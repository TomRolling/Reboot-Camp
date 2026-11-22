using System.IO;

namespace MaintenancePC.Services;

/// <summary>
/// Sauvegarde des pilotes installés avec l'outil officiel de Windows « pnputil /export-driver ».
/// En cas de souci après une mise à jour, on peut réinstaller l'ancien pilote depuis ce dossier
/// (Gestionnaire de périphériques > Mettre à jour le pilote > Parcourir mon poste de travail).
/// </summary>
public static class DriverBackupService
{
    public static string RootFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MaintenancePC", "SauvegardePilotes");

    public static async Task<(bool Ok, string Message)> ExportAsync(IProgress<string> output, CancellationToken token)
    {
        string target = Path.Combine(RootFolder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm"));
        try
        {
            Directory.CreateDirectory(target);
            int code = await ProcessRunner.RunAsync(
                ProcessRunner.SystemTool("pnputil.exe"), $"/export-driver * \"{target}\"", output, token, ProcessRunner.OemEncoding);

            if (code != 0)
                return (false, $"La sauvegarde des pilotes a échoué (pnputil, code {code}).");

            // On ne garde que la sauvegarde la plus récente (chacune pèse de 1 à 3 Go).
            foreach (string old in Directory.GetDirectories(RootFolder))
            {
                if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(old, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            Logger.Info($"Pilotes sauvegardés dans {target}");
            return (true, $"Pilotes sauvegardés dans {target}");
        }
        catch (OperationCanceledException)
        {
            try { Directory.Delete(target, recursive: true); } catch { }
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Logger.Error("Sauvegarde des pilotes", ex);
            return (false, "La sauvegarde des pilotes a échoué : " + ex.Message);
        }
    }
}
