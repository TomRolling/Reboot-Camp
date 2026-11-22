using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>Une des cartes de résumé de l'accueil.</summary>
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
    private readonly LogicielsViewModel _logiciels;
    private readonly NettoyageViewModel _nettoyage;
    private readonly ComposantsViewModel _composants;
    private readonly Action<PageViewModel> _navigate;

    public SummaryCard PilotesCard { get; }
    public SummaryCard LogicielsCard { get; }
    public SummaryCard NettoyageCard { get; }
    public SummaryCard ComposantsCard { get; }
    public IReadOnlyList<SummaryCard> Cards { get; }

    /// <summary>Avertissement affiché quand l'appli tourne sous un autre compte que celui de la session (vide sinon).</summary>
    [ObservableProperty] public partial string AccountWarning { get; set; }

    public AccueilViewModel(PilotesViewModel pilotes, LogicielsViewModel logiciels, NettoyageViewModel nettoyage,
        ComposantsViewModel composants, Action<PageViewModel> navigate) : base(
        "Accueil", "", "Analyser tout le PC",
        "Un clic pour vérifier les pilotes, les logiciels, l'espace récupérable et les composants. Rien n'est modifié.",
        ringCaption: "tout le PC")
    {
        _pilotes = pilotes;
        _logiciels = logiciels;
        _nettoyage = nettoyage;
        _composants = composants;
        _navigate = navigate;
        AccountWarning = "";

        PilotesCard = new SummaryCard(pilotes);
        LogicielsCard = new SummaryCard(logiciels);
        NettoyageCard = new SummaryCard(nettoyage);
        ComposantsCard = new SummaryCard(composants);
        Cards = [PilotesCard, LogicielsCard, NettoyageCard, ComposantsCard];
    }

    /// <summary>En revenant sur l'accueil, les cartes reprennent les résultats déjà obtenus dans les onglets.</summary>
    public override void OnNavigatedTo()
    {
        RefreshCards();
        _ = CheckAccountAsync();
    }

    private async Task CheckAccountAsync()
    {
        if (AccountWarning.Length > 0 || !await Task.Run(() => UserContext.IsDifferentUser)) return;
        AccountWarning =
            $"L'appli tourne avec le compte administrateur « {UserContext.ElevatedUser} ». " +
            $"Le nettoyage et le démarrage visent bien le compte « {UserContext.InteractiveUser} », sauf sa corbeille.";
    }

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

        var token = BeginOperation("Analyse de tout le PC…", "Analyse des pilotes…");
        try
        {

            // Chaque onglet fait sa propre analyse ; l'accueil ne fait que les enchaîner.
            var steps = new (SummaryCard Card, double Percent, string Text, Func<CancellationToken, Task> Run)[]
            {
                (PilotesCard, 5, "Analyse des pilotes…", _pilotes.RunScanAsync),
                (LogicielsCard, 40, "Recherche des mises à jour de logiciels…", _logiciels.RunScanAsync),
                (NettoyageCard, 65, "Calcul de l'espace récupérable…", _nettoyage.RunScanAsync),
                (ComposantsCard, 85, "Vérification des composants…", _composants.RunScanAsync),
            };
            foreach (var (card, percent, text, run) in steps)
            {
                Report(percent, text);
                card.Status = Status.Working;
                await run(token);
                RefreshCards();
                token.ThrowIfCancellationRequested();
            }

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
            PilotesCard.Detail = (updates == 1 ? "mise à jour" : "mises à jour") + (errors > 0 ? $"  ·  {errors} en erreur" : "");
            PilotesCard.Status = errors > 0 ? Status.Error : updates > 0 ? Status.Warning : Status.Ok;
        }
        else if (PilotesCard.Status == Status.Working) PilotesCard.Status = Status.None;

        if (_logiciels.HasScanned)
        {
            int updates = _logiciels.UpdateCount;
            LogicielsCard.Value = updates.ToString();
            LogicielsCard.Detail = updates == 1 ? "mise à jour" : "mises à jour";
            LogicielsCard.Status = updates > 0 ? Status.Warning : Status.Ok;
        }
        else if (!_logiciels.WingetAvailable)
        {
            LogicielsCard.Detail = "winget introuvable";
            LogicielsCard.Status = Status.None;
        }
        else if (LogicielsCard.Status == Status.Working) LogicielsCard.Status = Status.None;

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
            ComposantsCard.Detail = missing == 1 ? "manquant" : "manquants";
            ComposantsCard.Status = missing > 0 ? Status.Warning : Status.Ok;
        }
        else if (ComposantsCard.Status == Status.Working) ComposantsCard.Status = Status.None;
    }

    /// <summary>Lignes de résumé des analyses déjà faites, pour le rapport exporté depuis Extras.</summary>
    public IEnumerable<string> BuildScanSummary()
    {
        if (_pilotes.HasScanned)
        {
            yield return $"Pilotes : {_pilotes.Updates.Count} mise(s) à jour disponible(s), {_pilotes.ErrorCount} périphérique(s) en erreur";
            foreach (var update in _pilotes.Updates) yield return $"  - à mettre à jour : {update.Title}";
            foreach (var problem in _pilotes.Problems) yield return $"  - {problem.Name} : {problem.Explanation}";
        }
        if (_logiciels.HasScanned)
        {
            yield return $"Logiciels : {_logiciels.UpdateCount} mise(s) à jour disponible(s)";
            foreach (var item in _logiciels.Items) yield return $"  - {item.Name} ({item.Versions})";
        }
        if (_nettoyage.HasScanned)
            yield return $"Nettoyage : {Format.Size(_nettoyage.RecoverableBytes)} récupérables";
        if (_composants.HasScanned)
        {
            yield return $"Composants : {_composants.MissingCount} manquant(s)";
            foreach (var item in _composants.Items.Where(i => !i.IsInstalled)) yield return $"  - manquant : {item.Name}";
        }
    }
}
