using System.Windows;
using System.Windows.Media;
using RebootCamp.Services;

namespace RebootCamp.Views;

/// <summary>
/// Fenêtre de confirmation. ShowDialog() renvoie true seulement après un clic sur le bouton de confirmation ;
/// Échap, Entrée et le bouton d'annulation ferment sans rien faire.
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(ConfirmRequest request)
    {
        InitializeComponent();
        Title = request.Title;
        IconText.Text = request.Icon;
        TitleText.Text = request.Title;
        MessageText.Text = request.Message;
        HintText.Text = request.Hint;
        HintText.Visibility = request.Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = request.ConfirmLabel;
        CancelButton.Content = request.CancelLabel;

        if (request.IsWarning && TryFindResource("WarningBrush") is Brush warning)
        {
            IconText.Foreground = warning;
            Frame.BorderBrush = warning;
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;
}
