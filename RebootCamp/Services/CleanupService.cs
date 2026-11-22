using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using RebootCamp.Models;

namespace RebootCamp.Services;

public enum CleanupKind { Files, WindowsUpdateCache, DeliveryOptimization, RecycleBin, Dism }

/// <summary>
/// Un emplacement de la liste blanche.
/// </summary>
/// <param name="Root">Dossier autorisé. On ne supprime jamais en dehors.</param>
/// <param name="Pattern">Filtre sur le nom des fichiers (« * » = tous).</param>
/// <param name="Recursive">Vrai : tout le contenu du dossier. Faux : seulement les fichiers placés directement dedans.</param>
/// <param name="OldOnly">Vrai : seulement ce qui n'a pas été modifié depuis 24 h (protège les fichiers temporaires des programmes ouverts).</param>
public sealed record CleanupTarget(string Root, string Pattern = "*", bool Recursive = true, bool OldOnly = false);

public sealed record ScanResult(long Size, int FileCount, bool IsAvailable, string? Warning);

public sealed record CleanResult(long Freed, int Deleted, int Skipped, string? Note = null);

/// <summary>Une ligne de l'onglet Nettoyage.</summary>
public sealed partial class CleanupCategory : ObservableObject
{
    public string Name { get; }
    public string Description { get; }
    public string Icon { get; }
    public CleanupKind Kind { get; }
    public bool SelectedByDefault { get; }

    /// <summary>Calcule les dossiers concernés au moment du scan (les profils de navigateur peuvent changer).</summary>
    internal Func<List<CleanupTarget>> GetTargets { get; init; } = () => [];

    /// <summary>Nom du processus du navigateur (sans .exe) qui doit être fermé avant le nettoyage.</summary>
    internal string? BlockingProcess { get; init; }

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsAvailable { get; set; }
    [ObservableProperty] public partial long Size { get; set; }
    [ObservableProperty] public partial string SizeText { get; set; }
    [ObservableProperty] public partial string Detail { get; set; }
    [ObservableProperty] public partial Status Status { get; set; }

    /// <summary>Explication simple affichée au survol du « ? » : à quoi ça sert, et est-ce risqué.</summary>
    public string Help { get; internal set; } = "";

    public CleanupCategory(string name, string description, string icon, CleanupKind kind, bool selectedByDefault = true)
    {
        Name = name;
        Description = description;
        Icon = icon;
        Kind = kind;
        SelectedByDefault = selectedByDefault;
        Detail = description;
        SizeText = "";
    }
}

/// <summary>
/// Moteur de nettoyage. Règles de sécurité appliquées ici :
/// - liste blanche : seuls les dossiers déclarés dans <see cref="CreateCategories"/> sont touchés ;
/// - on ne suit jamais les liens/jonctions (un lien dans %TEMP% ne peut pas nous emmener ailleurs) ;
/// - un fichier verrouillé ou protégé est simplement ignoré ;
/// - en mode simulation, rien n'est supprimé : on compte seulement.
/// </summary>
public static class CleanupService
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string LocalAppData => UserContext.LocalAppData;
    private static readonly string ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

    private static readonly string DeliveryOptimizationCache = Path.Combine(
        Windows, @"ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");

    // ───────────────────────── Liste blanche ─────────────────────────

    public static List<CleanupCategory> CreateCategories()
    {
        var categories = new List<CleanupCategory>
        {
            new("Fichiers temporaires", "Fichiers temporaires de ton compte et de Windows, vieux de plus de 24 h.", "\uE8A5", CleanupKind.Files)
            {
                GetTargets = () =>
                [
                    new(UserContext.TempPath, OldOnly: true),
                    new(Path.Combine(Windows, "Temp"), OldOnly: true),
                ],
            },
            new("Cache Windows Update", "Mises à jour déjà téléchargées. Windows les retéléchargera si besoin.", "\uE895", CleanupKind.WindowsUpdateCache)
            {
                GetTargets = () => [new(Path.Combine(Windows, @"SoftwareDistribution\Download"))],
            },
            new("Optimisation de la distribution", "Morceaux de mises à jour gardés pour être partagés avec d'autres PC.", "\uE968", CleanupKind.DeliveryOptimization)
            {
                GetTargets = () => [new(DeliveryOptimizationCache)],
            },
            new("Rapports d'erreurs et vidages mémoire", "Rapports de plantage envoyés à Microsoft et fichiers de diagnostic.", "\uE9F9", CleanupKind.Files)
            {
                GetTargets = () =>
                [
                    new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportArchive")),
                    new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\ReportQueue")),
                    new(Path.Combine(ProgramData, @"Microsoft\Windows\WER\Temp")),
                    new(Path.Combine(LocalAppData, @"Microsoft\Windows\WER\ReportArchive")),
                    new(Path.Combine(LocalAppData, @"Microsoft\Windows\WER\ReportQueue")),
                    new(Path.Combine(LocalAppData, "CrashDumps")),
                    new(Path.Combine(Windows, "Minidump")),
                    new(Windows, "MEMORY.DMP", Recursive: false),
                ],
            },
            new("Cache des miniatures", "Aperçus des images et vidéos. Windows les recrée tout seul.", "\uE91B", CleanupKind.Files)
            {
                GetTargets = () => [new(Path.Combine(LocalAppData, @"Microsoft\Windows\Explorer"), "thumbcache_*.db", Recursive: false)],
            },
            new("Corbeille", "Tout ce qui se trouve dans la corbeille.", "\uE74D", CleanupKind.RecycleBin),
        };

        AddBrowser(categories, "Google Chrome", "chrome", () => ChromiumCaches(Path.Combine(LocalAppData, @"Google\Chrome\User Data")));
        AddBrowser(categories, "Microsoft Edge", "msedge", () => ChromiumCaches(Path.Combine(LocalAppData, @"Microsoft\Edge\User Data")));
        AddBrowser(categories, "Mozilla Firefox", "firefox", FirefoxCaches);

        categories.Add(new CleanupCategory(
            "Anciens composants Windows (avancé)",
            "Supprime les anciennes versions des composants via DISM. Peut durer plus de 10 minutes.",
            "\uE7B8", CleanupKind.Dism, selectedByDefault: false));

        foreach (var category in categories)
        {
            category.Help = category.Kind switch
            {
                CleanupKind.WindowsUpdateCache =>
                    "Sans risque. Ce sont des fichiers d'installation de mises à jour déjà installées. " +
                    "Si Windows en a encore besoin, il les retélécharge.",
                CleanupKind.DeliveryOptimization =>
                    "Sans risque. Windows garde des morceaux de mises à jour pour les envoyer à d'autres PC. Ton PC n'en a pas besoin.",
                CleanupKind.RecycleBin =>
                    "Attention : ce qui est dans la corbeille sera supprimé pour de bon. Vérifie qu'il n'y a rien à récupérer.",
                CleanupKind.Dism =>
                    "Sans risque mais long (plus de 10 minutes). Windows garde les anciennes versions de ses composants " +
                    "après chaque mise à jour ; cette option les supprime. On ne peut ensuite plus désinstaller les mises à jour déjà installées.",
                _ when category.BlockingProcess is not null =>
                    "Sans risque. Le cache est une copie des images et pages déjà vues, pour les afficher plus vite. " +
                    "Tes mots de passe, favoris, historique et sessions ne sont pas touchés. Les sites seront un peu plus lents la première fois.",
                _ when category.Name.StartsWith("Fichiers temporaires") =>
                    "Sans risque. Ce sont des fichiers de travail laissés par les programmes. " +
                    "Ceux de moins de 24 h sont gardés, au cas où un programme ouvert s'en sert encore.",
                _ when category.Name.StartsWith("Rapports") =>
                    "Sans risque. Ces fichiers ne servent qu'à diagnostiquer un plantage. À garder seulement si un technicien te les demande.",
                _ => "Sans risque. Windows reconstruit ces aperçus tout seul. Les dossiers d'images s'afficheront un peu plus lentement la première fois.",
            };
        }

        return categories;
    }

    private static void AddBrowser(List<CleanupCategory> categories, string name, string process, Func<List<CleanupTarget>> targets)
    {
        categories.Add(new CleanupCategory(
            $"Cache de {name}", "Cache uniquement : cookies, historique et mots de passe ne sont pas touchés.", "\uE774", CleanupKind.Files)
        {
            GetTargets = targets,
            BlockingProcess = process,
        });
    }

    /// <summary>Dossiers de cache de chaque profil d'un navigateur Chromium (Chrome, Edge).</summary>
    private static List<CleanupTarget> ChromiumCaches(string userData)
    {
        var targets = new List<CleanupTarget>();
        if (!Directory.Exists(userData)) return targets;

        foreach (string profile in Directory.GetDirectories(userData))
        {
            string name = Path.GetFileName(profile);
            if (name != "Default" && !name.StartsWith("Profile ", StringComparison.Ordinal)) continue;

            targets.Add(new(Path.Combine(profile, "Cache")));
            targets.Add(new(Path.Combine(profile, "Code Cache")));
            targets.Add(new(Path.Combine(profile, "GPUCache")));
        }
        return targets;
    }

    private static List<CleanupTarget> FirefoxCaches()
    {
        var targets = new List<CleanupTarget>();
        string profiles = Path.Combine(LocalAppData, @"Mozilla\Firefox\Profiles");
        if (!Directory.Exists(profiles)) return targets;

        foreach (string profile in Directory.GetDirectories(profiles))
            targets.Add(new(Path.Combine(profile, "cache2")));
        return targets;
    }

    // ───────────────────────── Analyse (ne supprime rien) ─────────────────────────

    public static ScanResult Scan(CleanupCategory category, CancellationToken token)
    {
        if (category.Kind == CleanupKind.Dism)
            return new ScanResult(0, 0, true, null);

        if (category.Kind == CleanupKind.RecycleBin)
        {
            if (UserContext.IsDifferentUser)
                return new ScanResult(0, 0, false, $"La corbeille de {UserContext.InteractiveUser} n'est pas accessible depuis le compte administrateur.");
            var (size, count) = QueryRecycleBin();
            return new ScanResult(size, count, true, null);
        }

        var targets = category.GetTargets();
        if (category.BlockingProcess is not null)
        {
            if (targets.Count == 0)
                return new ScanResult(0, 0, false, "Navigateur non installé.");
            if (IsRunning(category.BlockingProcess))
                return new ScanResult(0, 0, false, "Navigateur ouvert (peut-être en arrière-plan) : ferme-le puis relance l'analyse.");
        }

        long total = 0;
        int files = 0;
        foreach (var target in targets)
        {
            foreach (var file in Walk(target, token).Files)
            {
                total += SafeLength(file);
                files++;
            }
        }
        return new ScanResult(total, files, true, null);
    }

    // ───────────────────────── Nettoyage ─────────────────────────

    public static async Task<CleanResult> CleanAsync(
        CleanupCategory category, bool simulate, IProgress<string> output, CancellationToken token)
    {
        switch (category.Kind)
        {
            case CleanupKind.RecycleBin:
            {
                var (size, count) = QueryRecycleBin();
                if (!simulate && count > 0)
                {
                    int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SherbNoConfirmation | SherbNoProgressUi | SherbNoSound);
                    if (hr != 0) return new CleanResult(0, 0, count, "Windows a refusé de vider la corbeille.");
                }
                return new CleanResult(size, count, 0);
            }

            case CleanupKind.Dism:
            {
                if (simulate) return new CleanResult(0, 0, 0, "Non exécuté en simulation.");
                long before = SystemDriveFreeSpace();
                int code = await ProcessRunner.RunAsync(
                    ProcessRunner.SystemTool("dism.exe"), "/Online /Cleanup-Image /StartComponentCleanup",
                    output, token, ProcessRunner.OemEncoding);
                long freed = Math.Max(0, SystemDriveFreeSpace() - before);
                return new CleanResult(freed, 0, 0, code == 0 ? null : $"DISM a renvoyé le code {code}.");
            }

            case CleanupKind.DeliveryOptimization:
            {
                long size = await Task.Run(() => Scan(category, token).Size, token);
                if (simulate) return new CleanResult(size, 0, 0);
                // Méthode officielle : la commande PowerShell fournie par Windows (le service garde la main sur son cache).
                int code = await ProcessRunner.RunAsync(
                    ProcessRunner.SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NonInteractive -Command \"Delete-DeliveryOptimizationCache -Force\"",
                    output, token, ProcessRunner.OemEncoding);
                return code == 0 ? new CleanResult(size, 0, 0) : new CleanResult(0, 0, 0, $"La commande a renvoyé le code {code}.");
            }

            case CleanupKind.WindowsUpdateCache:
                return await Task.Run(() => CleanWindowsUpdateCache(category, simulate, token), token);

            default:
                if (category.BlockingProcess is not null && IsRunning(category.BlockingProcess))
                    return new CleanResult(0, 0, 0, "Navigateur ouvert : cache non touché.");
                return await Task.Run(() => DeleteTargets(category.GetTargets(), simulate, token), token);
        }
    }

    /// <summary>Arrête proprement le service Windows Update, vide son cache, puis le relance s'il tournait.</summary>
    private static CleanResult CleanWindowsUpdateCache(CleanupCategory category, bool simulate, CancellationToken token)
    {
        if (simulate) return DeleteTargets(category.GetTargets(), simulate: true, token);

        bool wasRunning;
        try
        {
            using var service = new ManagementObject("Win32_Service.Name='wuauserv'");
            service.Get();
            wasRunning = (string)service["State"] != "Stopped";
            if (wasRunning)
            {
                service.InvokeMethod("StopService", null);
                for (int i = 0; i < 60 && (string)service["State"] != "Stopped"; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Thread.Sleep(500);
                    service.Get();
                }
                if ((string)service["State"] != "Stopped")
                    return new CleanResult(0, 0, 0, "Le service Windows Update n'a pas voulu s'arrêter : cache non touché.");
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or COMException)
        {
            return new CleanResult(0, 0, 0, "Impossible d'arrêter le service Windows Update : " + ex.Message.Trim());
        }

        try
        {
            return DeleteTargets(category.GetTargets(), simulate: false, token);
        }
        finally
        {
            if (wasRunning)
            {
                try
                {
                    using var service = new ManagementObject("Win32_Service.Name='wuauserv'");
                    service.InvokeMethod("StartService", null);
                }
                catch (Exception ex)
                {
                    Logger.Error("Redémarrage du service Windows Update", ex);
                }
            }
        }
    }

    private static CleanResult DeleteTargets(List<CleanupTarget> targets, bool simulate, CancellationToken token)
    {
        long freed = 0;
        int deleted = 0, skipped = 0;

        foreach (var target in targets)
        {
            var (files, directories) = Walk(target, token);

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();

                // Ceinture et bretelles : on revérifie que le fichier est bien dans le dossier autorisé.
                if (!IsInside(target.Root, file.FullName)) { skipped++; continue; }

                long length = SafeLength(file);
                if (!simulate)
                {
                    try
                    {
                        if (file.IsReadOnly) file.IsReadOnly = false;
                        file.Delete();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
                    {
                        skipped++;   // fichier verrouillé ou protégé : on l'ignore
                        continue;
                    }
                }
                freed += length;
                deleted++;
            }

            if (simulate) continue;

            // Suppression des sous-dossiers devenus vides, du plus profond au moins profond.
            // Directory.Delete(chemin) sans « recursive » échoue si le dossier n'est pas vide : aucun risque.
            foreach (var directory in directories.OrderByDescending(d => d.FullName.Length))
            {
                if (!IsInside(target.Root, directory.FullName)) continue;
                try { Directory.Delete(directory.FullName); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }

        return new CleanResult(freed, deleted, skipped);
    }

    // ───────────────────────── Parcours sécurisé des dossiers ─────────────────────────

    /// <summary>
    /// Liste les fichiers (et sous-dossiers) d'un emplacement de la liste blanche,
    /// sans jamais entrer dans un lien symbolique ou une jonction.
    /// </summary>
    private static (List<FileInfo> Files, List<DirectoryInfo> Directories) Walk(CleanupTarget target, CancellationToken token)
    {
        var files = new List<FileInfo>();
        var directories = new List<DirectoryInfo>();

        var root = new DirectoryInfo(target.Root);
        if (!root.Exists || !IsSafeRoot(target)) return (files, directories);

        DateTime cutoff = DateTime.UtcNow.AddHours(-24);
        var options = new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None };
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var current = pending.Pop();

            try
            {
                foreach (var file in current.EnumerateFiles(target.Pattern, options))
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    if (target.OldOnly && file.LastWriteTimeUtc > cutoff) continue;
                    files.Add(file);
                }

                if (!target.Recursive) continue;

                foreach (var directory in current.EnumerateDirectories("*", options))
                {
                    if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // lien : on n'entre pas
                    // %TEMP%\.net contient les fichiers dont les applis .NET (dont celle-ci) ont besoin pour démarrer.
                    if (current == root && directory.Name.Equals(".net", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push(directory);
                    // Date lue AVANT toute suppression (supprimer un fichier modifie la date du dossier).
                    if (!target.OldOnly || directory.LastWriteTimeUtc <= cutoff)
                        directories.Add(directory);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Dossier inaccessible ou disparu entre-temps : on passe au suivant.
            }
        }

        return (files, directories);
    }

    /// <summary>Refuse de parcourir récursivement un dossier trop général (racine d'un disque, C:\Windows, profil…).</summary>
    private static bool IsSafeRoot(CleanupTarget target)
    {
        if (!target.Recursive) return target.Pattern != "*";   // non récursif : il faut un filtre précis

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.Root));
        string[] forbidden =
        [
            Windows,
            LocalAppData,
            ProgramData,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.SystemDirectory,
        ];
        return root.Split(Path.DirectorySeparatorChar).Length >= 3
               && !forbidden.Any(f => string.Equals(f, root, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsInside(string root, string path)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch { return 0; }
    }

    private static bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes) process.Dispose();
        return processes.Length > 0;
    }

    private static long SystemDriveFreeSpace()
    {
        try { return new DriveInfo(Path.GetPathRoot(Windows)!).AvailableFreeSpace; }
        catch { return 0; }
    }

    // ───────────────────────── Corbeille (API Windows shell32) ─────────────────────────

    private const uint SherbNoConfirmation = 0x1;
    private const uint SherbNoProgressUi = 0x2;
    private const uint SherbNoSound = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRbInfo
    {
        public uint cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref ShQueryRbInfo pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private static (long Size, int Count) QueryRecycleBin()
    {
        var info = new ShQueryRbInfo { cbSize = (uint)Marshal.SizeOf<ShQueryRbInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? (info.i64Size, (int)info.i64NumItems) : (0, 0);
    }
}
