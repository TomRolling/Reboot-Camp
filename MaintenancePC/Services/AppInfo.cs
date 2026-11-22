using System.IO;

namespace MaintenancePC.Services;

/// <summary>
/// Le nom de l'appli, à un seul endroit. (Le code garde son ancien nom interne « MaintenancePC » dans les espaces
/// de noms et les dossiers du projet : cela ne se voit nulle part pour l'utilisateur.)
/// </summary>
public static class AppInfo
{
    /// <summary>Nom affiché à l'écran.</summary>
    public const string Name = "Reboot Camp";

    /// <summary>Nom des dossiers de données (sans espace).</summary>
    public const string FolderName = "RebootCamp";

    private const string OldFolderName = "MaintenancePC";

    /// <summary>Dossier privé de l'appli pour ce compte : journal et téléchargements.</summary>
    public static string LocalFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>Dossier commun à tout le PC : sauvegarde des pilotes.</summary>
    public static string CommonFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);

    /// <summary>
    /// L'appli s'appelait « MaintenancePC » : au premier lancement sous son nouveau nom, on déplace ses anciens dossiers
    /// (journal, sauvegarde de pilotes) au lieu de les laisser traîner.
    /// </summary>
    public static void MigrateOldFolders()
    {
        foreach (string current in (string[])[LocalFolder, CommonFolder])
        {
            try
            {
                string old = Path.Combine(Path.GetDirectoryName(current)!, OldFolderName);
                if (Directory.Exists(old) && !Directory.Exists(current)) Directory.Move(old, current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Dossier utilisé ou protégé : on repart simplement d'un dossier neuf.
            }
        }
    }
}
