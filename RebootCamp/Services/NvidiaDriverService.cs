using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace RebootCamp.Services;

public sealed record NvidiaDriver(
    string GpuName, string InstalledVersion, string LatestVersion, string DownloadUrl, string SizeText, string ReleaseDate)
{
    public bool IsNewer =>
        decimal.TryParse(LatestVersion, CultureInfo.InvariantCulture, out decimal latest)
        && decimal.TryParse(InstalledVersion, CultureInfo.InvariantCulture, out decimal installed)
        && latest > installed;
}

/// <summary>
/// Recherche et installe le dernier pilote graphique NVIDIA, uniquement depuis les serveurs de NVIDIA
/// (le même service que celui utilisé par la page de téléchargement de nvidia.com).
/// Le fichier n'est exécuté que si sa signature numérique « NVIDIA Corporation » est valide.
/// </summary>
public static class NvidiaDriverService
{
    private const string ProductListUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string LookupUrl =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php" +
        "?func=DriverManualLookup&psid={0}&pfid={1}&osID={2}&languageCode=1036&beta=0&isWHQL=1&dltype=-1&dch=1&upCRD=0&sort1=0&numberOfResults=1";

    // Identifiants de système utilisés par NVIDIA : Windows 10 64 bits = 57, Windows 11 = 135.
    private const int OsWindows10 = 57, OsWindows11 = 135;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    /// <summary>« 32.0.16.1742 » (version vue par Windows) → « 617.42 » (version affichée par NVIDIA).</summary>
    public static string ToNvidiaVersion(string windowsDriverVersion)
    {
        string[] parts = windowsDriverVersion.Split('.');
        if (parts.Length < 4) return "";
        string digits = parts[2] + parts[3];
        if (digits.Length < 5) return "";
        string last = digits[^5..];
        return $"{last[..3]}.{last[3..]}";
    }

    /// <summary>Renvoie le dernier pilote pour cette carte, ou null si la carte n'est pas reconnue par NVIDIA.</summary>
    public static async Task<NvidiaDriver?> CheckAsync(GraphicsCard card, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));

        // 1) Trouver l'identifiant NVIDIA de la carte à partir de son nom exact.
        string xml = await Http.GetStringAsync(ProductListUrl, timeout.Token);
        string wanted = Normalize(card.Name);
        var product = XDocument.Parse(xml).Descendants("LookupValue")
            .FirstOrDefault(e => Normalize((string?)e.Element("Name") ?? "") == wanted);
        if (product is null) return null;

        string productId = (string?)product.Element("Value") ?? "";
        string seriesId = (string?)product.Attribute("ParentID") ?? (string?)product.Element("ParentID") ?? "";
        if (productId.Length == 0 || seriesId.Length == 0) return null;

        // 2) Demander le dernier pilote certifié (WHQL) pour cette carte et cette version de Windows.
        int os = Environment.OSVersion.Version.Build >= 22000 ? OsWindows11 : OsWindows10;
        string json = await Http.GetStringAsync(string.Format(CultureInfo.InvariantCulture, LookupUrl, seriesId, productId, os), timeout.Token);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("IDS", out var ids) || ids.GetArrayLength() == 0) return null;
        var info = ids[0].GetProperty("downloadInfo");

        string url = info.GetProperty("DownloadURL").GetString() ?? "";
        if (!IsNvidiaDownloadUrl(url)) return null;

        return new NvidiaDriver(
            card.Name,
            ToNvidiaVersion(card.DriverVersion),
            info.GetProperty("Version").GetString() ?? "",
            url,
            // NVIDIA renvoie « 1.04 GB » : on l'écrit à la française.
            info.TryGetProperty("DownloadURLFileSize", out var size)
                ? (size.GetString() ?? "").Replace(" GB", " Go").Replace(" MB", " Mo").Replace('.', ',')
                : "",
            info.TryGetProperty("ReleaseDateTime", out var date) ? Uri.UnescapeDataString(date.GetString() ?? "") : "");
    }

    private static string Normalize(string name)
    {
        name = name.Trim();
        if (name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) name = name[7..];
        return name.ToUpperInvariant();
    }

    /// <summary>On n'accepte qu'une adresse https sur download.nvidia.com (ou un de ses miroirs régionaux).</summary>
    private static bool IsNvidiaDownloadUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("download.nvidia.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".download.nvidia.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Télécharge, vérifie puis installe le pilote en mode silencieux.
    /// L'annulation est possible pendant le téléchargement. Une fois l'installation lancée, elle va jusqu'au bout
    /// (même si on ferme l'appli) : interrompre l'installation d'un pilote graphique laisserait l'écran sans pilote.
    /// </summary>
    public static async Task<(bool Ok, string Message)> DownloadAndInstallAsync(
        NvidiaDriver driver, IProgress<(double Percent, string Message)> progress, CancellationToken token)
    {
        if (!IsNvidiaDownloadUrl(driver.DownloadUrl))
            return (false, "Adresse de téléchargement refusée (ce n'est pas un serveur NVIDIA).");

        string folder = Path.Combine(AppInfo.LocalFolder, "downloads");
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, Path.GetFileName(new Uri(driver.DownloadUrl).LocalPath));

        try
        {
            // ── Téléchargement ──
            using (var response = await Http.GetAsync(driver.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0;

                long free = new DriveInfo(Path.GetPathRoot(folder)!).AvailableFreeSpace;
                if (total > 0 && free < total * 3)
                    return (false, $"Pas assez de place sur le disque (il faut environ {Format.Size(total * 3)} libres).");

                await using var source = await response.Content.ReadAsStreamAsync(token);
                await using var target = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
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
                        progress.Report((percent, $"Téléchargement : {Format.Size(received)} sur {Format.Size(total)}"));
                    }
                }
            }

            // ── Vérification de la signature ──
            progress.Report((-1, "Vérification de la signature NVIDIA…"));
            bool trusted = await Task.Run(() => SignatureVerifier.IsSignedBy(file, "NVIDIA Corporation", out _), token);
            if (!trusted)
            {
                Logger.Error($"Pilote NVIDIA refusé : signature invalide ({file})");
                return (false, "Fichier refusé : sa signature numérique NVIDIA n'est pas valide.");
            }

            // ── Installation silencieuse (options officielles de l'installeur NVIDIA) ──
            token.ThrowIfCancellationRequested();
            progress.Report((-1, "Installation du pilote NVIDIA… L'écran peut clignoter, n'éteins pas le PC."));
            Logger.Info($"Installation du pilote NVIDIA {driver.LatestVersion}");

            // Volontairement non enregistré dans AppLifetime : on ne tue jamais une installation de pilote en cours.
            using var installer = Process.Start(new ProcessStartInfo(file, "-s -noreboot") { UseShellExecute = false })
                                  ?? throw new InvalidOperationException("L'installeur n'a pas démarré.");
            await installer.WaitForExitAsync(CancellationToken.None);
            int code = installer.ExitCode;
            Logger.Info($"Installeur NVIDIA terminé, code {code}");

            // On vérifie le résultat réel : quelle version Windows voit-il maintenant ?
            string now = await Task.Run(() => HardwareService.GetGraphicsCards()
                .Where(c => c.Vendor == "NVIDIA").Select(c => ToNvidiaVersion(c.DriverVersion)).FirstOrDefault() ?? "");

            if (now == driver.LatestVersion)
                return (true, $"Pilote NVIDIA {now} installé. Redémarre le PC pour terminer.");
            return code == 0
                ? (true, $"Installation terminée. Redémarre le PC : la nouvelle version s'affichera ensuite (actuelle : {now}).")
                : (false, $"L'installeur NVIDIA a renvoyé le code {code}. Version actuelle : {now}.");
        }
        catch (HttpRequestException ex)
        {
            return (false, "Téléchargement impossible : " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { /* fichier encore utilisé : il sera écrasé la prochaine fois */ }
        }
    }
}
