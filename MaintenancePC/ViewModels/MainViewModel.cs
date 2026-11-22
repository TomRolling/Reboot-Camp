using CommunityToolkit.Mvvm.ComponentModel;

namespace MaintenancePC.ViewModels;

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
}
