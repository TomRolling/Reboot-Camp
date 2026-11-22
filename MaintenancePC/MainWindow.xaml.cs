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
        // Windows 11 et Windows 10 récent utilisent l'attribut 20 ; les Windows 10 d'avant 2020 utilisaient le 19.
        // Si les deux échouent (Windows encore plus ancien), la barre de titre reste claire : sans gravité.
        if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref enabled, sizeof(int));
    }

    // Taille pour laquelle l'interface a été dessinée (zone utile de la fenêtre par défaut).
    private const double DesignWidth = 1024, DesignHeight = 641;
    private const double MinScale = 0.85, MaxScale = 2.2;

    /// <summary>
    /// Quand la fenêtre change de taille, on calcule un facteur d'échelle unique : la plus petite des deux
    /// proportions (largeur, hauteur), pour que rien ne déborde. Le « LayoutTransform » agrandit alors textes,
    /// boutons et marges ensemble, et WPF redistribue la place restante (les listes s'élargissent).
    /// </summary>
    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double scale = Math.Min(e.NewSize.Width / DesignWidth, e.NewSize.Height / DesignHeight);
        scale = Math.Clamp(scale, MinScale, MaxScale);
        RootScale.ScaleX = scale;
        RootScale.ScaleY = scale;
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
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
