using System.IO;

namespace RebootCamp.Services;

/// <summary>Le nom de l'appli et ses dossiers de données, à un seul endroit.</summary>
public static class AppInfo
{
    /// <summary>Nom affiché à l'écran.</summary>
    public const string Name = "Reboot Camp";

    /// <summary>Nom des dossiers de données (sans espace).</summary>
    public const string FolderName = "RebootCamp";

    /// <summary>Dossier privé de l'appli pour ce compte : journal et téléchargements.</summary>
    public static string LocalFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName);

    /// <summary>Dossier commun à tout le PC : sauvegarde des pilotes.</summary>
    public static string CommonFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), FolderName);
}
