using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using RebootCamp.Models;
using Microsoft.Win32;

namespace RebootCamp.Services;

/// <summary>Une ligne de l'onglet Composants.</summary>
public sealed partial class ComponentItem : ObservableObject
{
    public string Group { get; }
    public string Name { get; }

    /// <summary>Identifiant du paquet dans winget (tous vérifiés avec « winget search »).</summary>
    public string WingetId { get; }

    /// <summary>Renvoie la version installée, ou null si le composant est absent.</summary>
    internal Func<DetectionContext, string?> Detect { get; }

    [ObservableProperty] public partial Status Status { get; set; }
    [ObservableProperty] public partial string Detail { get; set; }
    [ObservableProperty] public partial bool IsInstalled { get; set; }
    [ObservableProperty] public partial string ActionLabel { get; set; }

    internal ComponentItem(string group, string name, string wingetId, Func<DetectionContext, string?> detect)
    {
        Group = group;
        Name = name;
        WingetId = wingetId;
        Detect = detect;
        Detail = "Pas encore vérifié";
        ActionLabel = "Installer";
    }
}

/// <summary>Données lues une seule fois par détection (la liste des programmes installés).</summary>
internal sealed record DetectionContext(List<(string Name, string Version)> VisualCppEntries);

public static class ComponentService
{
    // Code renvoyé par winget quand le paquet est déjà à jour (APPINSTALLER_CLI_ERROR_UPDATE_NOT_APPLICABLE).
    private const int WingetNoUpgrade = unchecked((int)0x8A15002B);

    public static List<ComponentItem> CreateItems()
    {
        var items = new List<ComponentItem>
        {
            new("DirectX", "DirectX End-User Runtime (anciennes DLL pour les jeux)", "Microsoft.DirectX", _ => DetectDirectX()),
        };

        foreach (string year in (string[])["2005", "2008", "2010", "2012", "2013"])
        {
            foreach (string arch in (string[])["x64", "x86"])
            {
                items.Add(new("Visual C++", $"Visual C++ {year} ({arch})", $"Microsoft.VCRedist.{year}.{arch}",
                    context => DetectOldVisualCpp(context, year, arch)));
            }
        }
        items.Add(new("Visual C++", "Visual C++ 2015-2022 (x64)", "Microsoft.VCRedist.2015+.x64", _ => DetectVisualCpp14("x64")));
        items.Add(new("Visual C++", "Visual C++ 2015-2022 (x86)", "Microsoft.VCRedist.2015+.x86", _ => DetectVisualCpp14("x86")));

        items.Add(new(".NET", ".NET Desktop Runtime 8", "Microsoft.DotNet.DesktopRuntime.8", _ => DetectDesktopRuntime(8)));
        items.Add(new(".NET", ".NET Desktop Runtime 10", "Microsoft.DotNet.DesktopRuntime.10", _ => DetectDesktopRuntime(10)));
        items.Add(new(".NET", ".NET Framework 4.8", "Microsoft.DotNet.Framework.Runtime", _ => DetectNetFramework()));

        return items;
    }

    // ───────────────────────── Détection (lecture seule) ─────────────────────────

    /// <summary>Renvoie, pour chaque composant, sa version installée (ou null s'il est absent).</summary>
    public static Dictionary<ComponentItem, string?> DetectAll(IEnumerable<ComponentItem> items, CancellationToken token)
    {
        var context = new DetectionContext(ReadVisualCppEntries());
        var result = new Dictionary<ComponentItem, string?>();
        foreach (var item in items)
        {
            token.ThrowIfCancellationRequested();
            try { result[item] = item.Detect(context); }
            catch { result[item] = null; }
        }
        return result;
    }

    private static string? DetectDirectX()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        bool x64 = File.Exists(Path.Combine(windows, "System32", "d3dx9_43.dll"));
        bool x86 = File.Exists(Path.Combine(windows, "SysWOW64", "d3dx9_43.dll"));
        return x64 && x86 ? "d3dx9_43.dll présent" : null;
    }

    /// <summary>Programmes « Microsoft Visual C++ … Redistributable » listés dans le registre (clés de désinstallation).</summary>
    private static List<(string Name, string Version)> ReadVisualCppEntries()
    {
        var entries = new List<(string, string)>();
        foreach (var view in (RegistryView[])[RegistryView.Registry64, RegistryView.Registry32])
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var uninstall = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (uninstall is null) continue;

            foreach (string keyName in uninstall.GetSubKeyNames())
            {
                using var key = uninstall.OpenSubKey(keyName);
                if (key?.GetValue("DisplayName") is string name
                    && name.StartsWith("Microsoft Visual C++", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Add((name, key.GetValue("DisplayVersion") as string ?? ""));
                }
            }
        }
        return entries;
    }

    private static string? DetectOldVisualCpp(DetectionContext context, string year, string arch)
    {
        string? best = null;
        foreach (var (name, version) in context.VisualCppEntries)
        {
            if (!name.Contains($"C++ {year}", StringComparison.OrdinalIgnoreCase)) continue;

            // Le nom contient « x64 » pour la version 64 bits ; la version 32 bits contient « x86 » ou rien du tout (2005).
            bool isX64 = name.Contains("x64", StringComparison.OrdinalIgnoreCase);
            if (isX64 != (arch == "x64")) continue;

            if (best is null || CompareVersions(version, best) > 0) best = version;
        }
        return best ?? DetectSideBySideRuntime(year, arch);
    }

    /// <summary>
    /// Les versions 2005 et 2008 s'installent dans C:\Windows\WinSxS, et Windows en fournit parfois lui-même une copie
    /// plus récente. Dans ce cas il n'y a pas de ligne « programme installé », mais les DLL sont bien là :
    /// inutile de reproposer l'installation.
    /// </summary>
    private static string? DetectSideBySideRuntime(string year, string arch)
    {
        string? assembly = year switch { "2005" => "microsoft.vc80.crt", "2008" => "microsoft.vc90.crt", _ => null };
        if (assembly is null) return null;

        string winSxS = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
        string prefix = arch == "x64" ? "amd64" : "x86";
        try
        {
            // Nom du dossier : amd64_microsoft.vc80.crt_<clé>_<version>_none_<empreinte>
            return Directory.GetDirectories(winSxS, $"{prefix}_{assembly}_*")
                .Select(d => Path.GetFileName(d).Split('_'))
                .Where(parts => parts.Length >= 4)
                .Select(parts => parts[3])
                .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
                .Select(v => $"{v} (fourni par Windows)")
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? DetectVisualCpp14(string arch)
    {
        // La clé x86 se trouve dans la vue 32 bits du registre, la clé x64 dans la vue 64 bits.
        var view = arch == "x64" ? RegistryView.Registry64 : RegistryView.Registry32;
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
        using var key = hklm.OpenSubKey($@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}");
        if (key?.GetValue("Installed") is not int installed || installed != 1) return null;
        return (key.GetValue("Version") as string ?? "").TrimStart('v');
    }

    private static string? DetectDesktopRuntime(int major)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\shared\Microsoft.WindowsDesktop.App");
        if (!Directory.Exists(folder)) return null;

        return Directory.GetDirectories(folder, $"{major}.*")
            .Select(Path.GetFileName)
            .OfType<string>()
            .OrderByDescending(v => v, Comparer<string>.Create(CompareVersions))
            .FirstOrDefault();
    }

    private static string? DetectNetFramework()
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
        // Valeurs « Release » documentées par Microsoft : 528040 = 4.8, 533320 = 4.8.1.
        return key?.GetValue("Release") is int release
            ? release >= 533320 ? "4.8.1" : release >= 528040 ? "4.8" : null
            : null;
    }

    private static int CompareVersions(string a, string b) =>
        Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)
            ? va.CompareTo(vb)
            : string.CompareOrdinal(a, b);

    // ───────────────────────── winget ─────────────────────────

    /// <summary>Renvoie la version de winget, ou null s'il n'est pas installé.</summary>
    public static async Task<string?> GetWingetVersionAsync(CancellationToken token)
    {
        try
        {
            string? version = null;
            var output = new DirectProgress<string>(line => version ??= line.Trim());
            int code = await ProcessRunner.RunAsync("winget", "--version", output, token);
            return code == 0 ? version ?? "installé" : null;
        }
        catch (Win32Exception)
        {
            return null;   // winget.exe introuvable
        }
    }

    /// <summary>Installe le paquet, ou le met à jour s'il est déjà là (comportement par défaut de « winget install »).</summary>
    public static async Task<(bool Ok, string Message)> InstallAsync(ComponentItem item, IProgress<string> output, CancellationToken token)
    {
        string arguments =
            $"install --id {item.WingetId} --exact --source winget --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity";

        output.Report($"> winget {arguments}");
        Logger.Info($"winget install {item.WingetId}");

        int code = await ProcessRunner.RunAsync("winget", arguments, output, token);

        Logger.Info($"winget {item.WingetId} : code 0x{code:X8}");
        if (RestartService.IsRebootCode(code))
        {
            RestartService.Need($"« {item.Name} » a besoin d'un redémarrage du PC pour terminer son installation.");
            return RestartService.IsSuccessPendingReboot(code)
                ? (true, "Installé, actif après un redémarrage")
                : (false, "Redémarre le PC, puis relance l'installation.");
        }
        return code switch
        {
            0 => (true, "Installé"),
            WingetNoUpgrade => (true, "Déjà à jour"),
            _ => (false, $"Échec (code 0x{code:X8})"),
        };
    }
}
