using System.IO;
using System.Security;

namespace MaintenancePC.Services;

public sealed record FolderSize(string Name, string Path, long Size, string Note)
{
    public string SizeText => Format.Size(Size);

    /// <summary>Longueur de la barre, par rapport au plus gros élément de la liste (0 à 100).</summary>
    public double Percent { get; init; }
}

public sealed record DiskUsageResult(List<FolderSize> Folders, List<FolderSize> UserFolders, List<FolderSize> BigFiles);

/// <summary>« Qu'est-ce qui prend de la place ? » Mesure les dossiers du disque système. Lecture seule.</summary>
public static class DiskUsageService
{
    private const int MaxRows = 12;

    public static DiskUsageResult Scan(IProgress<string> progress, CancellationToken token)
    {
        string systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        string profile = Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetDirectoryName(UserContext.LocalAppData))!);

        var topSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var userSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var bigFiles = new PriorityQueue<(string Path, long Size), long>();   // garde les plus gros fichiers
        var options = new System.IO.EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.None };

        // Chaque dossier à visiter retient à quel dossier « de premier niveau » il appartient.
        var pending = new Stack<(DirectoryInfo Directory, string Top, string? UserFolder)>();
        long rootFiles = 0;

        try
        {
            var root = new DirectoryInfo(systemRoot);
            foreach (var file in root.EnumerateFiles("*", options)) rootFiles += SafeLength(file);
            foreach (var directory in root.EnumerateDirectories("*", options))
            {
                if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                pending.Push((directory, directory.FullName, null));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }

        string lastReported = "";
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (current, top, userFolder) = pending.Pop();

            if (top != lastReported && current.FullName == top)
            {
                lastReported = top;
                progress.Report($"Mesure de {top}…");
            }

            try
            {
                long size = 0;
                foreach (var file in current.EnumerateFiles("*", options))
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    long length = SafeLength(file);
                    size += length;

                    if (length > 100L * 1024 * 1024)
                    {
                        bigFiles.Enqueue((file.FullName, length), length);
                        if (bigFiles.Count > MaxRows) bigFiles.Dequeue();   // retire le plus petit
                    }
                }
                topSizes[top] = topSizes.GetValueOrDefault(top) + size;
                if (userFolder is not null) userSizes[userFolder] = userSizes.GetValueOrDefault(userFolder) + size;

                foreach (var directory in current.EnumerateDirectories("*", options))
                {
                    if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;   // on ne suit pas les liens

                    // Les sous-dossiers directs du profil (Documents, Téléchargements, AppData…) ont leur propre ligne.
                    string? childUserFolder = userFolder
                        ?? (string.Equals(current.FullName, profile, StringComparison.OrdinalIgnoreCase) ? directory.FullName : null);
                    pending.Push((directory, top, childUserFolder));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                // Dossier protégé : on l'ignore.
            }
        }

        var folders = topSizes.Select(p => new FolderSize(Path.GetFileName(p.Key), p.Key, p.Value, Explain(p.Key))).ToList();
        if (rootFiles > 0)
            folders.Add(new FolderSize("Fichiers système à la racine", systemRoot, rootFiles, "Mémoire virtuelle et mise en veille prolongée : gérés par Windows."));

        var files = new List<FolderSize>();
        while (bigFiles.Count > 0)
        {
            var (path, size) = bigFiles.Dequeue();
            files.Add(new FolderSize(Path.GetFileName(path), path, size, Path.GetDirectoryName(path) ?? ""));
        }

        return new DiskUsageResult(
            Rank(folders),
            Rank(userSizes.Select(p => new FolderSize(Path.GetFileName(p.Key), p.Key, p.Value, "")).ToList()),
            Rank(files));
    }

    private static List<FolderSize> Rank(List<FolderSize> items)
    {
        var top = items.OrderByDescending(i => i.Size).Take(MaxRows).ToList();
        long max = top.Count > 0 ? Math.Max(top[0].Size, 1) : 1;
        return [.. top.Select(i => i with { Percent = i.Size * 100.0 / max })];
    }

    private static string Explain(string path) => Path.GetFileName(path).ToLowerInvariant() switch
    {
        "windows.old" => "Ancienne version de Windows. Supprimable depuis Paramètres > Système > Stockage > Fichiers temporaires.",
        "windows" => "Le système. Ne rien supprimer à la main : utilise l'onglet Nettoyage.",
        "users" or "utilisateurs" => "Tes fichiers personnels et ceux des autres comptes (détail ci-dessous).",
        "program files" or "program files (x86)" => "Tes logiciels. Désinstalle ceux que tu n'utilises plus depuis Paramètres > Applications.",
        "programdata" => "Données des logiciels. Ne rien supprimer à la main.",
        _ => "",
    };

    private static long SafeLength(FileInfo file)
    {
        try { return file.Length; }
        catch { return 0; }
    }
}
