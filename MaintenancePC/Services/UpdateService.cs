using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MaintenancePC.Services;

public sealed record AppUpdate(Version Version, string DownloadUrl, string? Sha256, long Size);

/// <summary>
/// Mise à jour de l'appli elle-même, à partir des « Releases » d'un dépôt GitHub public.
/// Pour publier une version : créer une Release dont l'étiquette est le numéro (v1.2.0) et y joindre le .exe.
/// </summary>
public static class UpdateService
{
    /// <summary>
    /// Dépôt GitHub au format « utilisateur/depot ». Vide = pas de vérification des mises à jour.
    /// </summary>
    public static readonly string Repository = "";

    public static bool IsConfigured => Repository.Length > 0;

    public static Version CurrentVersion { get; } = typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    /// <summary>Interroge GitHub. Renvoie la nouvelle version si elle est plus récente que celle-ci, sinon null.</summary>
    public static async Task<AppUpdate?> CheckAsync(string repository, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));

        string json = await FileDownloader.Http.GetStringAsync(
            $"https://api.github.com/repos/{repository}/releases/latest", timeout.Token);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        string tag = (root.GetProperty("tag_name").GetString() ?? "").TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var version)) return null;
        if (Normalize(version) <= Normalize(CurrentVersion)) return null;

        // Le fichier à télécharger est le .exe joint à la Release.
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            string name = asset.GetProperty("name").GetString() ?? "";
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!url.StartsWith($"https://github.com/{repository}/releases/download/", StringComparison.OrdinalIgnoreCase)) continue;

            // GitHub fournit l'empreinte du fichier sous la forme « sha256:abcd… ».
            string? digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            string? sha256 = digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null;
            return new AppUpdate(version, url, sha256, asset.GetProperty("size").GetInt64());
        }
        return null;
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// Télécharge la nouvelle version à côté de l'exécutable, vérifie son empreinte, puis échange les deux fichiers.
    /// Renvoie le chemin de l'exécutable à relancer.
    /// </summary>
    public static async Task<string> DownloadAndInstallAsync(
        AppUpdate update, IProgress<(double Percent, string Message)> progress, CancellationToken token)
    {
        string current = Environment.ProcessPath ?? throw new InvalidOperationException("Emplacement de l'application inconnu.");
        string downloaded = current + ".new";

        await FileDownloader.DownloadAsync(update.DownloadUrl, downloaded, progress, token);

        if (update.Sha256 is not null)
        {
            progress.Report((-1, "Vérification du fichier…"));
            string actual = await Task.Run(() =>
            {
                using var stream = File.OpenRead(downloaded);
                return Convert.ToHexString(SHA256.HashData(stream));
            }, token);
            if (!actual.Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(downloaded);
                throw new InvalidDataException("Le fichier téléchargé ne correspond pas à celui publié sur GitHub.");
            }
        }

        Swap(current, downloaded);
        Logger.Info($"Application mise à jour en version {update.Version}");
        return current;
    }

    /// <summary>
    /// Windows interdit d'écraser un programme en cours d'exécution, mais autorise à le renommer :
    /// l'ancienne version devient « .old » (supprimée au prochain lancement) et la nouvelle prend sa place.
    /// </summary>
    internal static void Swap(string current, string downloaded)
    {
        string old = current + ".old";
        File.Move(current, old, overwrite: true);
        try
        {
            File.Move(downloaded, current);
        }
        catch
        {
            File.Move(old, current);   // échec : on remet l'ancienne version en place
            throw;
        }
    }

    /// <summary>Au lancement : supprime l'ancienne version laissée par une mise à jour précédente.</summary>
    public static void CleanUpAfterUpdate()
    {
        try
        {
            if (Environment.ProcessPath is not { } current) return;
            foreach (string leftover in (string[])[current + ".old", current + ".new"])
                if (File.Exists(leftover)) File.Delete(leftover);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Encore verrouillé : ce sera pour le prochain lancement.
        }
    }

    public static void Restart(string executable) =>
        Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })?.Dispose();
}
