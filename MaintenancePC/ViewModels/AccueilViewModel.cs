using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaintenancePC.Models;
using MaintenancePC.Services;

namespace MaintenancePC.ViewModels;

/// <summary>Une des trois cartes de résumé de l'accueil.</summary>
public sealed partial class SummaryCard : ObservableObject
{
    public string Title { get; }
    public string Icon { get; }
    public PageViewModel Target { get; }

    [ObservableProperty] public partial string Value { get; set; }
    [ObservableProperty] public partial string Detail { get; set; }
    [ObservableProperty] public partial Status Status { get; set; }

    public SummaryCard(PageViewModel target)
    {
        Title = target.Title;
        Icon = target.Icon;
        Target = target;
        Value = "—";
        Detail = "Pas encore analysé";
    }
}

public sealed partial class AccueilViewModel : PageViewModel
{
    private readonly PilotesViewModel _pilotes;
    private readonly NettoyageViewModel _nettoyage;
    private readonly ComposantsViewModel _composants;
    private readonly Action<PageViewModel> _navigate;

    public SummaryCard PilotesCard { get; }
    public SummaryCard NettoyageCard { get; }
    public SummaryCard ComposantsCard { get; }
    public IReadOnlyList<SummaryCard> Cards { get; }

    public AccueilViewModel(PilotesViewModel pilotes, NettoyageViewModel nettoyage, ComposantsViewModel composants,
        Action<PageViewModel> navigate) : base(
        "Accueil", "", "Analyser tout le PC",
        "Un clic pour vérifier les pilotes, l'espace récupérable et les composants. Rien n'est modifié.")
    {
        _pilotes = pilotes;
        _nettoyage = nettoyage;
        _composants = composants;
        _navigate = navigate;

        PilotesCard = new SummaryCard(pilotes);
        NettoyageCard = new SummaryCard(nettoyage);
        ComposantsCard = new SummaryCard(composants);
        Cards = [PilotesCard, NettoyageCard, ComposantsCard];
    }

    /// <summary>En revenant sur l'accueil, les cartes reprennent les résultats déjà obtenus dans les onglets.</summary>
    public override void OnNavigatedTo() => RefreshCards();

    [RelayCommand]
    private void Open(SummaryCard card) => _navigate(card.Target);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RingAsync()
    {
        if (IsBusy)
        {
            Cancel();
            return;
        }

        var token = BeginOperation("Analyse en cours…", "Analyse des pilotes…");
        try
        {
            // Chaque onglet fait sa propre analyse ; l'accueil ne fait que les enchaîner.
            Report(5, "Analyse des pilotes…");
            PilotesCard.Status = Status.Working;
            await _pilotes.RunScanAsync(token);
            RefreshCards();
            token.ThrowIfCancellationRequested();

            Report(45, "Calcul de l'espace récupérable…");
            NettoyageCard.Status = Status.Working;
            await _nettoyage.RunScanAsync(token);
            RefreshCards();
            token.ThrowIfCancellationRequested();

            Report(85, "Vérification des composants…");
            ComposantsCard.Status = Status.Working;
            await _composants.RunScanAsync(token);
            RefreshCards();
            token.ThrowIfCancellationRequested();

            Report(100, "Analyse terminée.");
            int toDo = Cards.Count(c => c.Status is Status.Warning or Status.Error);
            Headline = toDo == 0 ? "Ton PC est en forme" : toDo == 1 ? "1 point à regarder" : $"{toDo} points à regarder";
            SubText = toDo == 0 ? "Rien à faire pour l'instant." : "Clique sur une carte pour voir le détail.";
        }
        catch (OperationCanceledException)
        {
            RefreshCards();
            Headline = "Analyse annulée";
            SubText = "Clique sur le bouton pour recommencer.";
        }
        finally
        {
            EndOperation();
        }
    }

    private void RefreshCards()
    {
        if (_pilotes.HasScanned)
        {
            int updates = _pilotes.Updates.Count, errors = _pilotes.ErrorCount;
            PilotesCard.Value = updates.ToString();
            PilotesCard.Detail = (updates == 1 ? "mise à jour disponible" : "mises à jour disponibles")
                                 + (errors > 0 ? $"  ·  {errors} en erreur" : "");
            PilotesCard.Status = errors > 0 ? Status.Error : updates > 0 ? Status.Warning : Status.Ok;
        }
        else if (PilotesCard.Status == Status.Working) PilotesCard.Status = Status.None;

        if (_nettoyage.HasScanned)
        {
            long bytes = _nettoyage.RecoverableBytes;
            NettoyageCard.Value = Format.Size(bytes);
            NettoyageCard.Detail = "récupérables";
            NettoyageCard.Status = bytes > 500L * 1024 * 1024 ? Status.Warning : Status.Ok;
        }
        else if (NettoyageCard.Status == Status.Working) NettoyageCard.Status = Status.None;

        if (_composants.HasScanned)
        {
            int missing = _composants.MissingCount;
            ComposantsCard.Value = missing.ToString();
            ComposantsCard.Detail = missing == 1 ? "composant manquant" : "composants manquants";
            ComposantsCard.Status = missing > 0 ? Status.Warning : Status.Ok;
        }
        else if (ComposantsCard.Status == Status.Working) ComposantsCard.Status = Status.None;
    }
}
