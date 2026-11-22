using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>
/// ViewModel de la fenêtre principale : la liste des onglets et celui qui est affiché.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    public IReadOnlyList<PageViewModel> Pages { get; }

    /// <summary>Version lue dans l'exécutable (balise Version du .csproj), affichée en bas de la barre latérale.</summary>
    public string Version { get; } =
        typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

    // La barre latérale modifie cette propriété, et la zone de droite affiche la page choisie.
    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    public MainViewModel()
    {
        var pilotes = new PilotesViewModel();
        var logiciels = new LogicielsViewModel();
        var nettoyage = new NettoyageViewModel();
        var composants = new ComposantsViewModel();
        var accueil = new AccueilViewModel(pilotes, logiciels, nettoyage, composants, page => CurrentPage = page);
        var extras = new ExtrasViewModel(accueil.BuildScanSummary);

        Pages = [accueil, pilotes, logiciels, nettoyage, composants, extras];
        CurrentPage = accueil;
    }

    partial void OnCurrentPageChanged(PageViewModel value) => value?.OnNavigatedTo();

    // ───────────────────────── Mise à jour de l'appli ─────────────────────────

    private AppUpdate? _update;

    /// <summary>Vrai quand une version plus récente est publiée sur GitHub : un encart apparaît dans la barre latérale.</summary>
    [ObservableProperty] public partial bool UpdateAvailable { get; set; }
    [ObservableProperty] public partial bool UpdateInProgress { get; set; }
    [ObservableProperty] public partial string UpdateText { get; set; } = "";

    /// <summary>
    /// Appelé une fois à l'ouverture de la fenêtre : une seule petite question à GitHub (« quelle est la dernière version ? »).
    /// C'est la seule chose que l'appli fait d'elle-même. Sans réseau ou sans dépôt configuré, il ne se passe rien.
    /// </summary>
    public async Task CheckForUpdateAsync()
    {
        if (!UpdateService.IsConfigured) return;
        try
        {
            _update = await UpdateService.CheckAsync(UpdateService.Repository, AppLifetime.Token);
            if (_update is null) return;
            UpdateText = $"Version {_update.Version.ToString(3)} disponible";
            UpdateAvailable = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            // Pas de réseau ou GitHub injoignable : on réessaiera au prochain lancement.
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_update is null || UpdateInProgress) return;
        if (Pages.Any(p => p.IsBusy))
        {
            UpdateText = "Attends la fin de l'opération en cours";
            return;
        }

        UpdateInProgress = true;
        try
        {
            var progress = new Progress<(double Percent, string Message)>(p =>
                UpdateText = p.Percent >= 0 ? $"Téléchargement… {p.Percent:0} %" : p.Message);
            string executable = await UpdateService.DownloadAndInstallAsync(_update, progress, AppLifetime.Token);

            UpdateText = "Redémarrage…";
            UpdateService.Restart(executable);
            Application.Current.MainWindow?.Close();
        }
        catch (OperationCanceledException)
        {
            // L'appli se ferme.
        }
        catch (Exception ex)
        {
            Logger.Error("Mise à jour de l'application", ex);
            UpdateText = "Mise à jour impossible. Réessaie plus tard.";
            UpdateInProgress = false;
        }
    }
}
