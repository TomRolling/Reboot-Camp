using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using MaintenancePC.Services;
using MaintenancePC.ViewModels;

namespace MaintenancePC;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    /// <summary>Appelé quand la fenêtre Windows existe réellement : on peut alors assombrir sa barre de titre.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        int enabled = 1;
        // Si l'appel échoue (Windows trop ancien), la barre de titre reste claire : sans gravité.
        _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }

    /// <summary>Fermer la fenêtre = quitter complètement.</summary>
    protected override void OnClosed(EventArgs e)
    {
        AppLifetime.Shutdown();          // annule les tâches en cours et tue les processus enfants
        base.OnClosed(e);
        Application.Current.Shutdown();  // arrête l'application elle-même
    }

    // API Windows documentée (dwmapi.h) : attribut 20 = barre de titre sombre.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
