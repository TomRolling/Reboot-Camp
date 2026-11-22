using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace RebootCamp.Services;

/// <summary>
/// Pilotes AMD et Intel. Aucun des deux fabricants n'offre de service pour demander « quel est le dernier pilote ? »,
/// mais chacun fournit un outil officiel qui reconnaît le matériel et installe le bon pilote.
/// On télécharge cet outil chez le fabricant, on vérifie sa signature, et on le lance.
/// </summary>
public static partial class VendorToolService
{
    // Page officielle d'AMD : elle contient le lien (qui change à chaque version) de l'outil « Auto-Detect and Install ».
    private const string AmdPage = "https://www.amd.com/en/support/download/drivers.html";

    [GeneratedRegex(@"https://drivers\.amd\.com/drivers/installer/[^""'\s<>]+minimalsetup[^""'\s<>]+_web\.exe", RegexOptions.IgnoreCase)]
    private static partial Regex AmdToolLinkRegex();

    // Identifiant winget vérifié avec « winget search ».
    private const string IntelAssistantId = "Intel.IntelDriverAndSupportAssistant";

    /// <summary>
    /// Outil officiel AMD (cartes Radeon et chipsets Ryzen). Il s'ouvre dans sa propre fenêtre
    /// et guide l'utilisateur : on ne l'installe pas « en silence », faute d'option documentée pour cette version web.
    /// </summary>
    public static async Task<(bool Ok, string Message)> RunAmdToolAsync(
        IProgress<(double Percent, string Message)> progress, CancellationToken token)
    {
        try
        {
            progress.Report((-1, "Recherche de l'outil officiel sur amd.com…"));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(40));
            string html = await FileDownloader.Http.GetStringAsync(AmdPage, timeout.Token);

            var match = AmdToolLinkRegex().Match(html);
            if (!match.Success)
                return (false, "Le lien de l'outil AMD est introuvable sur amd.com. Passe par la page « Pilotes AMD ».");

            string url = match.Value;
            string file = Path.Combine(FileDownloader.PrepareDownloadFolder(), Path.GetFileName(new Uri(url).LocalPath));
            await FileDownloader.DownloadAsync(url, file, progress, token, referer: "https://www.amd.com/");

            progress.Report((-1, "Vérification de la signature AMD…"));
            string details = "";
            bool trusted = await Task.Run(() => SignatureVerifier.IsSignedBy(file, "Advanced Micro Devices", out details), token);
            if (!trusted)
            {
                Logger.Error($"Outil AMD refusé : {details}");
                try { File.Delete(file); } catch { }
                return (false, "Fichier refusé : sa signature numérique AMD n'est pas valide.");
            }

            Logger.Info($"Lancement de l'outil AMD : {Path.GetFileName(file)}");
            // Non enregistré dans AppLifetime : on ne tue jamais une installation de pilote en cours.
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = false })?.Dispose();
            return (true, "L'outil AMD est ouvert : suis ses étapes, il installe le bon pilote. L'écran peut clignoter, n'éteins pas le PC.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return (false, "amd.com n'a pas répondu. Réessaie plus tard ou passe par la page « Pilotes AMD ».");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or Win32Exception)
        {
            Logger.Error("Outil AMD", ex);
            return (false, "Impossible de récupérer l'outil AMD : " + ex.Message);
        }
    }

    /// <summary>
    /// Assistant officiel d'Intel (carte graphique, chipset, Wi-Fi, Bluetooth), installé avec winget.
    /// À savoir : cet assistant reste ensuite en arrière-plan ; on peut le désactiver dans Extras > Démarrage.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallIntelAssistantAsync(IProgress<string> output, CancellationToken token)
    {
        string arguments =
            $"install --id {IntelAssistantId} --exact --source winget --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity";
        try
        {
            Logger.Info($"winget install {IntelAssistantId}");
            int code = await ProcessRunner.RunAsync("winget", arguments, output, token);
            Logger.Info($"winget {IntelAssistantId} : code 0x{code:X8}");

            // 0 = installé ; 0x8A15002B = déjà installé et à jour.
            return code is 0 or unchecked((int)0x8A15002B)
                ? (true, "L'assistant Intel est installé. Ouvre « Intel Driver & Support Assistant » depuis le menu Démarrer : il liste les pilotes à mettre à jour.")
                : (false, $"L'installation de l'assistant Intel a échoué (code 0x{code:X8}).");
        }
        catch (Win32Exception)
        {
            return (false, "winget est introuvable : impossible d'installer l'assistant Intel.");
        }
    }
}
