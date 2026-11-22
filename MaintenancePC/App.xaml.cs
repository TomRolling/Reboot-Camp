using System.Windows;

namespace MaintenancePC;

/// <summary>
/// Point d'entrée de l'application. Rien n'est lancé ici : pas de scan, pas de tâche de fond.
/// La fenêtre s'ouvre (StartupUri dans App.xaml) et attend que l'utilisateur clique.
/// </summary>
public partial class App : Application
{
}
