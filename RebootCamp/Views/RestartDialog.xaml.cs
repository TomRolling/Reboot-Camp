using System.Windows;

namespace RebootCamp.Views;

/// <summary>
/// Question « Redémarrer maintenant / Plus tard ». ShowDialog() renvoie true seulement après un clic sur
/// « Redémarrer maintenant » ; Échap, Entrée et « Plus tard » ferment sans rien faire.
/// </summary>
public partial class RestartDialog : Window
{
    public RestartDialog(string reason)
    {
        InitializeComponent();
        ReasonText.Text = reason;
    }

    private void OnRestartNow(object sender, RoutedEventArgs e) => DialogResult = true;
}
