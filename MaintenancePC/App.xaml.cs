using System.Text;
using System.Windows;
using MaintenancePC.Services;

namespace MaintenancePC;

/// <summary>
/// Point d'entrée de l'application. Rien n'est lancé ici : pas de scan, pas de tâche de fond.
/// La fenêtre s'ouvre (StartupUri dans App.xaml) et attend que l'utilisateur clique.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Avant tout le reste : récupère les dossiers de l'ancien nom de l'appli, s'il y en a.
        AppInfo.MigrateOldFolders();

        // Nécessaire pour lire la sortie de DISM (page de codes « OEM » 850, absente de .NET par défaut).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Filet de sécurité : une erreur imprévue est notée dans le journal au lieu de fermer l'appli.
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Erreur imprévue", args.Exception);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
