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

    // [ObservableProperty] génère le code qui prévient l'interface quand la valeur change.
    // La barre latérale modifie cette propriété, et la zone de droite affiche la page choisie.
    [ObservableProperty]
    public partial PageViewModel CurrentPage { get; set; }

    public MainViewModel()
    {
        Pages =
        [
            new AccueilViewModel(),
            new PilotesViewModel(),
            new NettoyageViewModel(),
            new ComposantsViewModel(),
            new ExtrasViewModel(),
        ];
        CurrentPage = Pages[0];
    }
}
