using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace MaintenancePC.Services;

/// <summary>Téléchargement d'un fichier avec progression et annulation. Utilisé pour les pilotes et les mises à jour de l'appli.</summary>
public static class FileDownloader
{
    public static HttpClient Http { get; } = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    /// <summary>Dossier de téléchargement de l'appli, vidé de ses anciens fichiers à chaque utilisation.</summary>
    public static string PrepareDownloadFolder()
    {
        string folder = Path.Combine(AppInfo.LocalFolder, "downloads");
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.GetFiles(folder))
        {
            try { File.Delete(old); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* encore utilisé : on le laisse */ }
        }
        return folder;
    }

    /// <param name="referer">Certains sites (AMD) n'acceptent le téléchargement que s'il vient de leur propre page.</param>
    /// <param name="sizeFactor">Place libre exigée, en multiple de la taille du fichier (un installeur se décompresse).</param>
    public static async Task DownloadAsync(string url, string targetFile, IProgress<(double Percent, string Message)> progress,
        CancellationToken token, string? referer = null, int sizeFactor = 2)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null) request.Headers.Referrer = new Uri(referer);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        long free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(targetFile))!).AvailableFreeSpace;
        if (total > 0 && free < total * sizeFactor)
            throw new IOException($"Pas assez de place sur le disque (il faut environ {Format.Size(total * sizeFactor)} libres).");

        try
        {
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            var buffer = new byte[1 << 20];
            long received = 0;
            var lastReport = Stopwatch.StartNew();
            int read;
            while ((read = await source.ReadAsync(buffer, token)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token);
                received += read;
                if (lastReport.ElapsedMilliseconds > 250)
                {
                    lastReport.Restart();
                    double percent = total > 0 ? received * 100.0 / total : -1;
                    progress.Report((percent, total > 0
                        ? $"Téléchargement : {Format.Size(received)} sur {Format.Size(total)}"
                        : $"Téléchargement : {Format.Size(received)}"));
                }
            }
        }
        catch
        {
            // Téléchargement interrompu : on ne laisse pas un fichier incomplet derrière nous.
            try { File.Delete(targetFile); } catch { }
            throw;
        }
    }
}
