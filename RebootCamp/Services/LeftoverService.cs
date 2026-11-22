using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RebootCamp.Services;

/// <summary>Un dossier ou un raccourci laissé sur le disque par un programme désinstallé.</summary>
public sealed partial class Leftover : ObservableObject
{
    public string Path { get; }

    /// <summary>Ce que c'est, en clair (« Dossier du programme », « Réglages et données »…).</summary>
    public string Kind { get; }

    public long Size { get; }
    public string SizeText => Size > 0 ? Format.Size(Size) : "";
    public bool IsDirectory { get; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    internal Leftover(string path, string kind, long size, bool isDirectory)
    {
        Path = path;
        Kind = kind;
        Size = size;
        IsDirectory = isDirectory;
        IsSelected = true;
    }
}

/// <summary>Les endroits où un programme range habituellement ses fichiers. Les tests y mettent de faux dossiers.</summary>
public sealed record LeftoverRoots(
    string[] ProgramFolders, string[] DataFolders, string[] StartMenus, string[] Desktops, string[] Protected, string WindowsFolder)
{
    public static LeftoverRoots ForThisPc()
    {
        static string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        string local = UserContext.LocalAppData, roaming = UserContext.RoamingAppData;
        string profile = System.IO.Path.GetFullPath(System.IO.Path.Combine(local, @"..\.."));

        return new LeftoverRoots(
            ProgramFolders:
            [
                Folder(Environment.SpecialFolder.ProgramFiles),
                Folder(Environment.SpecialFolder.ProgramFilesX86),
                System.IO.Path.Combine(local, "Programs"),
            ],
            DataFolders: [Folder(Environment.SpecialFolder.CommonApplicationData), roaming, local],
            StartMenus:
            [
                Folder(Environment.SpecialFolder.CommonPrograms),
                System.IO.Path.Combine(roaming, @"Microsoft\Windows\Start Menu\Programs"),
            ],
            Desktops: [Folder(Environment.SpecialFolder.CommonDesktopDirectory), System.IO.Path.Combine(profile, "Desktop")],
            Protected: [profile, Environment.ProcessPath ?? ""],
            WindowsFolder: Folder(Environment.SpecialFolder.Windows));
    }
}

public sealed record LeftoverResult(int Deleted, int AtNextRestart, long Freed);

/// <summary>
/// Après une désinstallation : retrouve ce que le programme a laissé sur le disque, le montre, et ne supprime
/// que ce qui est coché. Règles de sécurité :
/// - on ne cherche que des dossiers portant le NOM EXACT du programme, à des endroits connus (Program Files,
///   ProgramData, AppData, menu Démarrer, Bureau), plus son dossier d'installation déclaré ;
/// - jamais un dossier général (Program Files lui-même, « Microsoft », « Common Files »…) ni un dossier dans
///   lequel un autre programme encore installé se trouve ;
/// - on n'entre jamais dans un lien ou une jonction : le lien est retiré, pas ce vers quoi il pointe ;
/// - le registre n'est pas touché. Un fichier verrouillé est confié à Windows, qui le supprime au redémarrage.
/// </summary>
public static partial class LeftoverService
{
    // Noms trop généraux pour désigner un seul programme : un dossier ainsi nommé n'est jamais proposé.
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "Common Files", "Intel", "AMD", "NVIDIA", "NVIDIA Corporation", "Google", "Adobe",
        "Apple", "Mozilla", "Steam", "Epic Games", "Packages", "Programs", "Temp", "Desktop", "Documents",
        "Downloads", "Games", "Jeux", "Program Files", "Program Files (x86)", "ProgramData", "AppData", "Users",
        "Windows NT", "WindowsApps", "Internet Explorer", "Windows Defender", "Package Cache", "Uninstall",
    };

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex ParenthesesRegex();

    [GeneratedRegex(@"\s+v?\d+(\.\d+)*[a-z]?$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingVersionRegex();

    /// <summary>
    /// Les noms de dossier possibles pour un programme : son nom affiché, et le même sans version ni parenthèses
    /// (« 7-Zip 24.08 (x64) » → « 7-Zip »).
    /// </summary>
    internal static List<string> CandidateNames(string displayName)
    {
        var names = new List<string>();
        void Add(string value)
        {
            value = value.Trim().TrimEnd('.');
            if (value.Length >= 3 && !GenericNames.Contains(value) && value.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0
                && !names.Contains(value, StringComparer.OrdinalIgnoreCase))
                names.Add(value);
        }

        Add(displayName);
        string shorter = ParenthesesRegex().Replace(displayName, "");
        Add(shorter);
        Add(TrailingVersionRegex().Replace(shorter.Trim(), ""));
        return names;
    }

    /// <param name="stillInstalled">Les programmes encore présents : leurs dossiers ne doivent pas être touchés.</param>
    public static List<Leftover> Find(InstalledProgram program, IReadOnlyCollection<InstalledProgram> stillInstalled,
        LeftoverRoots roots, CancellationToken token)
    {
        var found = new List<Leftover>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = CandidateNames(program.Name);
        string publisher = program.Publisher.Trim().TrimEnd('.');
        bool usePublisher = publisher.Length >= 3 && publisher.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;

        void TryFolder(string path, string kind, bool declared = false)
        {
            token.ThrowIfCancellationRequested();
            if (path.Length == 0 || !Directory.Exists(path)) return;
            string full = Normalize(path);
            if (!IsSafeFolder(full, roots, stillInstalled, declared) || !seen.Add(full)) return;
            found.Add(new Leftover(full, kind, FolderSize(full, token), isDirectory: true));
        }

        void TryShortcut(string folder, string name)
        {
            string path = System.IO.Path.Combine(folder, name + ".lnk");
            if (File.Exists(path) && seen.Add(path)) found.Add(new Leftover(path, "Raccourci", SafeLength(path), isDirectory: false));
        }

        // 1. Le dossier d'installation que le programme avait déclaré à Windows.
        TryFolder(program.InstallLocation, "Dossier du programme", declared: true);

        foreach (string name in names)
        {
            // Un autre programme encore installé porte ce nom ou commence par lui (autre version, module du même
            // logiciel) : un dossier à ce nom peut être le sien. Dans le doute, on ne le propose pas.
            if (stillInstalled.Any(other => other.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))) continue;

            // 2. Un dossier à son nom (ou « Éditeur\Nom ») là où les programmes s'installent…
            foreach (string root in roots.ProgramFolders)
            {
                TryFolder(System.IO.Path.Combine(root, name), "Dossier du programme");
                if (usePublisher) TryFolder(System.IO.Path.Combine(root, publisher, name), "Dossier du programme");
            }
            // 3. …et là où ils rangent leurs réglages et leurs données.
            foreach (string root in roots.DataFolders)
            {
                TryFolder(System.IO.Path.Combine(root, name), "Réglages et données du programme");
                if (usePublisher) TryFolder(System.IO.Path.Combine(root, publisher, name), "Réglages et données du programme");
            }
            // 4. Menu Démarrer et Bureau.
            foreach (string menu in roots.StartMenus)
            {
                TryFolder(System.IO.Path.Combine(menu, name), "Dossier du menu Démarrer");
                TryShortcut(menu, name);
            }
            foreach (string desktop in roots.Desktops) TryShortcut(desktop, name);
        }

        return found;
    }

    /// <summary>Vrai si ce dossier peut être proposé à la suppression sans risque pour Windows ni pour un autre programme.</summary>
    /// <param name="declared">Vrai pour le dossier d'installation déclaré par le programme (il peut être n'importe où).</param>
    internal static bool IsSafeFolder(string fullPath, LeftoverRoots roots, IReadOnlyCollection<InstalledProgram> stillInstalled, bool declared)
    {
        string path = Normalize(fullPath);
        string? parent = System.IO.Path.GetDirectoryName(path);
        if (parent is null) return false;                                    // racine d'un disque
        if (GenericNames.Contains(System.IO.Path.GetFileName(path))) return false;

        string[] knownRoots = [.. roots.ProgramFolders, .. roots.DataFolders, .. roots.StartMenus];

        // Ni un des dossiers généraux, ni un dossier qui en contient un, ni un dossier protégé (Windows, profil, l'appli).
        foreach (string special in knownRoots.Concat(roots.Protected))
            if (special.Length > 0 && IsSameOrInside(special, path)) return false;

        // Jamais rien à l'intérieur du dossier de Windows, même si un programme prétend s'y être installé.
        if (roots.WindowsFolder.Length > 0 && IsSameOrInside(path, roots.WindowsFolder)) return false;

        // Un dossier « deviné » par son nom doit être dans un des endroits connus (deux niveaux au plus : Éditeur\Nom).
        // Un dossier déclaré par le programme peut être ailleurs (D:\Jeux\MonJeu), mais pas directement à la racine d'un disque.
        bool insideKnownRoot = knownRoots.Any(root => root.Length > 0 && IsSameOrInside(path, root));
        if (!insideKnownRoot && (!declared || System.IO.Path.GetDirectoryName(parent) is null)) return false;

        // Un autre programme encore installé vit dedans : on n'y touche pas.
        if (stillInstalled.Any(other => other.InstallLocation.Length > 0 && IsSameOrInside(other.InstallLocation, path))) return false;

        try
        {
            return !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static string Normalize(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    /// <summary>Vrai si « path » est « container » lui-même ou se trouve dedans.</summary>
    private static bool IsSameOrInside(string path, string container)
    {
        string inner = Normalize(path), outer = Normalize(container);
        return inner.Equals(outer, StringComparison.OrdinalIgnoreCase)
               || inner.StartsWith(outer + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly EnumerationOptions NoLinks = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.None,
        RecurseSubdirectories = false,
    };

    private static long FolderSize(string folder, CancellationToken token)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(folder);
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var current = new DirectoryInfo(pending.Pop());
                foreach (var file in current.EnumerateFiles("*", NoLinks)) total += SafeLength(file.FullName);
                foreach (var directory in current.EnumerateDirectories("*", NoLinks))
                    if (!directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) pending.Push(directory.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Dossier illisible : il compte pour zéro.
            }
        }
        return total;
    }

    private static long SafeLength(string file)
    {
        try { return new FileInfo(file).Length; }
        catch { return 0; }
    }

    // ───────────────────────── Suppression ─────────────────────────

    /// <summary>Supprime les éléments cochés. Ce qui est verrouillé sera supprimé par Windows au prochain redémarrage.</summary>
    public static LeftoverResult Delete(IEnumerable<Leftover> leftovers, CancellationToken token)
    {
        int deleted = 0, delayed = 0;
        long freed = 0;

        foreach (var leftover in leftovers.Where(l => l.IsSelected))
        {
            token.ThrowIfCancellationRequested();
            bool complete = leftover.IsDirectory
                ? DeleteFolder(leftover.Path, ref delayed)
                : DeleteFile(leftover.Path, ref delayed);

            if (complete) { deleted++; freed += leftover.Size; }
            Logger.Info($"Trace {(complete ? "supprimée" : "en partie reportée au redémarrage")} : {leftover.Path}");
        }
        return new LeftoverResult(deleted, delayed, freed);
    }

    /// <returns>Vrai si le dossier a entièrement disparu tout de suite.</returns>
    private static bool DeleteFolder(string folder, ref int delayed)
    {
        bool complete = true;
        try
        {
            var info = new DirectoryInfo(folder);
            if (!info.Exists) return true;

            // Un lien ou une jonction : on retire le lien, jamais ce qu'il y a derrière.
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                foreach (var file in info.EnumerateFiles("*", NoLinks)) complete &= DeleteFile(file.FullName, ref delayed);
                foreach (var directory in info.EnumerateDirectories("*", NoLinks)) complete &= DeleteFolder(directory.FullName, ref delayed);
            }

            if (complete)
            {
                Directory.Delete(folder);   // sans « recursive » : échoue si le dossier n'est pas vide, donc sans risque
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            complete = false;
        }

        // Il reste quelque chose dedans (ou le dossier est verrouillé) : Windows le retirera après les fichiers, au redémarrage.
        DeleteAtNextRestart(folder);
        return false;
    }

    private static bool DeleteFile(string file, ref int delayed)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return true;
            if (info.IsReadOnly) info.IsReadOnly = false;
            info.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            if (DeleteAtNextRestart(file)) delayed++;
            return false;
        }
    }

    /// <summary>
    /// Demande à Windows de supprimer ce fichier lui-même au prochain démarrage, avant que quoi que ce soit ne
    /// puisse le verrouiller (fonction documentée MoveFileEx avec MOVEFILE_DELAY_UNTIL_REBOOT). C'est Windows qui
    /// tient cette liste d'attente ; l'appli ne se relance pas au démarrage et ne fait rien d'autre.
    /// </summary>
    private static bool DeleteAtNextRestart(string path) => MoveFileEx(path, null, MoveFileDelayUntilReboot);

    private const int MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string existingFileName, string? newFileName, int flags);
}
