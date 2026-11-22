using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RebootCamp.Services;

public enum ProgramKind
{
    /// <summary>Programme classique, installé avec un installeur (.exe, .msi).</summary>
    Desktop,
    /// <summary>Application Windows (Microsoft Store ou fournie avec Windows).</summary>
    WindowsApp,
}

/// <summary>Une ligne de l'onglet Désinstaller.</summary>
/// <param name="Source">
/// Où Windows range la fiche de ce programme : pour un programme classique, sa clé dans la liste des programmes
/// installés (lue, jamais modifiée) ; pour une application Windows, le nom complet de son paquet.
/// </param>
public sealed record InstalledProgram(
    string Name, string Publisher, string Version, long SizeBytes, DateTime? InstalledOn,
    ProgramKind Kind, string InstallLocation, string UninstallCommand, bool IsSilent, string Source,
    System.Windows.Media.ImageSource? Icon = null)
{
    /// <summary>« Éditeur  ·  version  ·  taille  ·  installé le … » : seulement ce qui est connu.</summary>
    public string Details => string.Join("  ·  ", new[]
    {
        Publisher,
        Version.Length > 0 ? $"version {Version}" : "",
        SizeBytes > 0 ? Format.Size(SizeBytes) : "",
        InstalledOn is { } date ? $"installé le {date:dd/MM/yyyy}" : "",
    }.Where(part => part.Length > 0));

    // Mention « ne pas désinstaller » / « à garder », s'il y en a une (voir UninstallAdvice).
    public string Advice => UninstallAdvice.For(this)?.Text ?? "";
    public Models.Status AdviceStatus => UninstallAdvice.For(this)?.Level ?? Models.Status.None;
}

/// <summary>
/// Liste les programmes installés et lance leur désinstallation « officielle » : le désinstalleur fourni par
/// le programme lui-même, ou la commande de Windows pour les applications du Store.
/// Le registre n'est que LU (c'est là que Windows tient la liste des programmes) : rien n'y est écrit ni supprimé.
/// </summary>
public static partial class UninstallService
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int MsiRebootRequired = 3010, MsiRebootInitiated = 1641;   // codes documentés de Windows Installer

    private static string PowerShellPath => ProcessRunner.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe");

    [GeneratedRegex(@"^\{[0-9A-Fa-f\-]{36}\}$")]
    private static partial Regex GuidRegex();

    [GeneratedRegex(@"^[\w.\-]+$")]
    private static partial Regex PackageNameRegex();

    // ───────────────────────── Liste des programmes classiques ─────────────────────────

    /// <summary>Les trois endroits où Windows note les programmes installés (64 bits, 32 bits, compte de l'utilisateur).</summary>
    private static IEnumerable<(string Id, Func<RegistryKey> Open)> Roots()
    {
        yield return ("HKLM64", () => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64));
        yield return ("HKLM32", () => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32));
        yield return ("HKCU", UserContext.OpenUserRegistry);
    }

    public static List<InstalledProgram> ListDesktopPrograms()
    {
        var programs = new List<InstalledProgram>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (rootId, open) in Roots())
        {
            try
            {
                using var root = open();
                using var uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null) continue;

                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    if (key is null || Read(key, rootId, keyName) is not { } program) continue;
                    // Un même programme peut apparaître en 64 et en 32 bits : on ne garde que la première fiche.
                    if (seen.Add($"{program.Name}|{program.Version}")) programs.Add(program);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
            {
                Logger.Error($"Lecture des programmes installés ({rootId})", ex);
            }
        }

        return [.. programs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private static InstalledProgram? Read(RegistryKey key, string rootId, string keyName)
    {
        string name = Text(key, "DisplayName");
        if (name.Length == 0) return null;

        // Composants internes de Windows, mises à jour et correctifs : ce ne sont pas des programmes à désinstaller ici.
        if (key.GetValue("SystemComponent") is int and 1) return null;
        if (Text(key, "ParentKeyName").Length > 0) return null;
        if (Text(key, "ReleaseType") is "Update" or "Hotfix" or "Security Update") return null;

        bool isMsi = key.GetValue("WindowsInstaller") is int and 1 && GuidRegex().IsMatch(keyName);
        string quiet = Text(key, "QuietUninstallString"), normal = Text(key, "UninstallString");
        if (!isMsi && quiet.Length == 0 && normal.Length == 0) return null;

        // Windows Installer : désinstallation sans question, avec une simple barre de progression, sans redémarrage.
        string command = isMsi ? $"msiexec.exe /x {keyName} /passive /norestart"
            : quiet.Length > 0 ? quiet
            : normal;

        long size = key.GetValue("EstimatedSize") is int kilobytes && kilobytes > 0 ? kilobytes * 1024L : 0;
        DateTime? installedOn = DateTime.TryParseExact(Text(key, "InstallDate"), "yyyyMMdd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

        string location = Text(key, "InstallLocation").Trim('"').TrimEnd('\\');
        return new InstalledProgram(name, Text(key, "Publisher"), Text(key, "DisplayVersion"), size, installedOn,
            ProgramKind.Desktop, location,
            command, IsSilent: isMsi || quiet.Length > 0, Source: $"{rootId}|{keyName}",
            Icon: IconService.ForProgram(Text(key, "DisplayIcon"), location));
    }

    private static string Text(RegistryKey key, string value) => (key.GetValue(value) as string)?.Trim() ?? "";

    /// <summary>Vrai tant que Windows liste encore ce programme (son désinstalleur retire sa fiche quand il a fini).</summary>
    public static bool IsStillInstalled(InstalledProgram program)
    {
        if (program.Kind != ProgramKind.Desktop) return false;
        string[] parts = program.Source.Split('|', 2);
        try
        {
            using var root = Roots().First(r => r.Id == parts[0]).Open();
            using var key = root.OpenSubKey($@"{UninstallKey}\{parts[1]}");
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;   // dans le doute, on ne cherche pas de « restes » d'un programme peut-être encore là
        }
    }

    // ───────────────────────── Liste des applications Windows ─────────────────────────

    /// <summary>
    /// Applications du Microsoft Store et applications fournies avec Windows que l'utilisateur peut retirer.
    /// On ne garde que celles qui ont une entrée dans le menu Démarrer : c'est de là que vient leur vrai nom
    /// (« Calculatrice » plutôt que « Microsoft.WindowsCalculator »), et cela écarte les composants techniques.
    /// </summary>
    public static async Task<List<InstalledProgram>> ListWindowsAppsAsync(CancellationToken token)
    {
        const string script =
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
            "$names = @{}; Get-StartApps | ForEach-Object { if ($_.AppID -match '^(.+?)!') { $names[$Matches[1]] = $_.Name } }; " +
            "@(Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.NonRemovable -and $_.SignatureKind -ne 'System' " +
            "-and $names.ContainsKey($_.PackageFamilyName) } | ForEach-Object { [pscustomobject]@{ " +
            "Display = $names[$_.PackageFamilyName]; Full = $_.PackageFullName; Publisher = $_.Publisher; " +
            "Version = [string]$_.Version; Location = $_.InstallLocation } }) | ConvertTo-Json -Compress";

        var json = new StringBuilder();
        try
        {
            int code = await ProcessRunner.RunAsync(PowerShellPath, $"-NoProfile -NonInteractive -Command \"{script}\"",
                new DirectProgress<string>(line => { lock (json) json.Append(line); }), token, Encoding.UTF8);
            if (code != 0 || json.Length == 0) return [];

            using var document = JsonDocument.Parse(json.ToString());
            // Une seule application : PowerShell renvoie un objet au lieu d'une liste.
            var items = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList()
                : [document.RootElement];

            var apps = new List<InstalledProgram>();
            foreach (var item in items)
            {
                string full = Json(item, "Full"), name = Json(item, "Display");
                if (name.Length == 0 || !PackageNameRegex().IsMatch(full)) continue;
                apps.Add(new InstalledProgram(name, PublisherName(Json(item, "Publisher")), Json(item, "Version"), 0, null,
                    ProgramKind.WindowsApp, Json(item, "Location"), UninstallCommand: "", IsSilent: true, Source: full,
                    Icon: IconService.ForWindowsApp(Json(item, "Location"))));
            }
            return [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex) when (ex is Win32Exception or JsonException)
        {
            Logger.Error("Liste des applications Windows", ex);
            return [];
        }
    }

    private static string Json(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>« CN=Microsoft Corporation, O=… » → « Microsoft Corporation ». Un identifiant technique donne « ».</summary>
    internal static string PublisherName(string distinguishedName)
    {
        var match = Regex.Match(distinguishedName, @"CN=(""(?<q>[^""]+)""|(?<p>[^,]+))");
        string name = (match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["p"].Value).Trim();
        return Guid.TryParse(name, out _) ? "" : name;
    }

    // ───────────────────────── Désinstallation ─────────────────────────

    /// <summary>
    /// Découpe une commande de désinstallation en « programme » + « options ». Les programmes l'écrivent de trois
    /// façons : chemin entre guillemets, chemin sans guillemets (même avec des espaces), ou « MsiExec.exe /I{…} ».
    /// </summary>
    internal static (string File, string Arguments) SplitCommand(string command)
    {
        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        string file, arguments;

        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end < 0) return (text.Trim('"'), "");
            file = text[1..end];
            arguments = text[(end + 1)..].Trim();
        }
        else
        {
            // Sans guillemets : le programme s'arrête à la fin du premier « .exe ».
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int cut = exe >= 0 ? exe + 4 : text.IndexOf(' ');
            if (cut < 0 || cut >= text.Length) return (text, "");
            file = text[..cut];
            arguments = text[cut..].Trim();
        }

        // « MsiExec.exe /I{code} » ouvrirait l'écran « Modifier / Réparer » : pour désinstaller, c'est /X.
        if (System.IO.Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            file = ProcessRunner.SystemTool("msiexec.exe");
            arguments = Regex.Replace(arguments, @"/I\s*(?=\{)", "/X", RegexOptions.IgnoreCase);
            if (!arguments.Contains("/norestart", StringComparison.OrdinalIgnoreCase)) arguments += " /norestart";
        }
        return (file, arguments);
    }

    /// <summary>
    /// Lance la désinstallation et attend qu'elle soit réellement finie. Annuler n'interrompt pas le désinstalleur
    /// (l'arrêter en plein milieu laisserait le programme à moitié retiré) : on cesse seulement de l'attendre.
    /// </summary>
    /// <exception cref="OperationCanceledException">Si l'utilisateur a cessé d'attendre.</exception>
    public static async Task<(bool Ok, string Message)> UninstallAsync(
        InstalledProgram program, IProgress<string> status, CancellationToken token)
    {
        Logger.Info($"Désinstallation demandée : {program.Name} ({program.Kind})");

        // Un désinstalleur peut vouloir redémarrer le PC : comme pour les installations, on le retient.
        using var guard = RestartService.Guard();

        return program.Kind == ProgramKind.WindowsApp
            ? await RemoveWindowsAppAsync(program, token)
            : await RunUninstallerAsync(program, status, token);
    }

    private static async Task<(bool, string)> RemoveWindowsAppAsync(InstalledProgram program, CancellationToken token)
    {
        if (!PackageNameRegex().IsMatch(program.Source)) return (false, "Nom d'application inattendu : rien n'a été fait.");

        var error = new StringBuilder();
        string script =
            "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
            $"try {{ Remove-AppxPackage -Package '{program.Source}' -ErrorAction Stop }} catch {{ Write-Output $_.Exception.Message; exit 1 }}";
        try
        {
            int code = await ProcessRunner.RunAsync(PowerShellPath, $"-NoProfile -NonInteractive -Command \"{script}\"",
                new DirectProgress<string>(line => { lock (error) error.AppendLine(line); }), token, Encoding.UTF8);

            Logger.Info($"Remove-AppxPackage {program.Source} : code {code}");
            return code == 0
                ? (true, "")
                : (false, "Windows a refusé de retirer cette application : " + error.ToString().Trim());
        }
        catch (Win32Exception)
        {
            return (false, "PowerShell est introuvable sur ce PC : impossible de retirer une application Windows.");
        }
    }

    private static async Task<(bool, string)> RunUninstallerAsync(InstalledProgram program, IProgress<string> status, CancellationToken token)
    {
        var (file, arguments) = SplitCommand(program.UninstallCommand);
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = false })
                      ?? throw new Win32Exception("Le désinstalleur n'a pas démarré.");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            Logger.Error($"Lancement du désinstalleur de {program.Name}", ex);
            return (false, $"Le désinstalleur du programme est introuvable ou n'a pas pu démarrer ({ex.Message.Trim()}).");
        }

        using (process)
        {
            status.Report(program.IsSilent
                ? "Désinstallation en cours…"
                : "Suis les étapes dans la fenêtre du désinstalleur qui vient de s'ouvrir.");
            await process.WaitForExitAsync(token);

            if (process.ExitCode is MsiRebootRequired or MsiRebootInitiated)
                RestartService.Need($"« {program.Name} » a besoin d'un redémarrage du PC pour finir d'être désinstallé.");
            Logger.Info($"Désinstalleur de {program.Name} terminé, code {process.ExitCode}");
        }

        // Beaucoup de désinstalleurs passent la main à une copie d'eux-mêmes puis s'arrêtent aussitôt :
        // le vrai signal de fin, c'est que Windows ne liste plus le programme.
        var waiting = Stopwatch.StartNew();
        while (IsStillInstalled(program))
        {
            if (waiting.Elapsed > TimeSpan.FromSeconds(4))
                status.Report("Termine la désinstallation dans la fenêtre du programme. Si tu l'as annulée, clique sur le bouton rond.");
            if (waiting.Elapsed > TimeSpan.FromMinutes(30))
                return (false, "Le programme est toujours installé.");
            await Task.Delay(1000, token);
        }
        return (true, "");
    }
}
