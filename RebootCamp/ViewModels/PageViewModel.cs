using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>
/// Classe de base d'un onglet. Elle porte ce que tous les onglets ont en commun :
/// le titre/icône de la barre latérale, et l'état du gros bouton rond (occupé, pourcentage, textes).
/// </summary>
public abstract partial class PageViewModel : ObservableObject
{
    private CancellationTokenSource? _operation;

    public string Title { get; }

    /// <summary>Caractère de la police d'icônes Windows (Segoe Fluent Icons / Segoe MDL2 Assets).</summary>
    public string Icon { get; }

    // [ObservableProperty] génère le code qui prévient l'interface à chaque changement de valeur.
    [ObservableProperty] public partial bool IsBusy { get; set; }

    /// <summary>Avancement de 0 à 100, ou -1 quand on ne sait pas (l'anneau tourne simplement).</summary>
    [ObservableProperty] public partial double Progress { get; set; }

    [ObservableProperty] public partial string Headline { get; set; }
    [ObservableProperty] public partial string SubText { get; set; }
    [ObservableProperty] public partial string RingText { get; set; }
    [ObservableProperty] public partial string RingCaption { get; set; }

    // Ce que le bouton rond affiche au repos : un verbe et, en petit dessous, ce qu'il concerne.
    // Chaque onglet a le sien, pour qu'on sache toujours sur quelle page on est.
    private readonly string _idleRingText, _idleRingCaption;

    /// <summary>
    /// Résultat de la dernière action (nettoyage, installation, mise à jour) : tant qu'il n'est pas « None »,
    /// le bouton rond passe au vert (ou à l'orange) avec une coche et « TERMINÉ », pour qu'on voie que c'est fini.
    /// </summary>
    [ObservableProperty] public partial Status Outcome { get; set; }

    private Status _pendingOutcome;
    private bool _nested;

    protected PageViewModel(string title, string icon, string headline, string subText,
        string ringText = "ANALYSER", string ringCaption = "")
    {
        Title = title;
        Icon = icon;
        Headline = headline;
        SubText = subText;
        Progress = -1;
        RingText = _idleRingText = ringText;
        RingCaption = _idleRingCaption = ringCaption;
    }

    /// <summary>Appelé quand l'utilisateur ouvre cet onglet.</summary>
    public virtual void OnNavigatedTo() { }

    /// <summary>Démarre une opération longue : l'anneau s'anime et le bouton devient « Annuler ».</summary>
    protected CancellationToken BeginOperation(string headline, string subText, CancellationToken external = default)
    {
        _operation?.Dispose();
        _operation = AppLifetime.CreateOperation(external);
        _nested = external.CanBeCanceled;   // lancée par l'accueil (« Analyser tout ») : c'est lui qui signalera la fin
        _pendingOutcome = Status.None;
        Outcome = Status.None;
        Headline = headline;
        IsBusy = true;
        Progress = -1;
        RingText = "···";
        RingCaption = "Annuler";
        SubText = subText;
        return _operation.Token;
    }

    protected void Report(double percent, string subText)
    {
        Progress = percent;
        RingText = $"{percent:0} %";
        SubText = subText;
    }

    /// <summary>
    /// À appeler quand une action est allée au bout (pas pour une simple analyse, ni après une annulation) :
    /// à la fin de l'opération, le bouton rond affichera « TERMINÉ ».
    /// </summary>
    /// <param name="allGood">Faux si une partie a échoué : le bouton passe à l'orange au lieu du vert.</param>
    protected void MarkDone(bool allGood = true) => _pendingOutcome = allGood ? Status.Ok : Status.Warning;

    protected void EndOperation()
    {
        IsBusy = false;
        Progress = -1;
        Outcome = _pendingOutcome;
        bool done = Outcome != Status.None;
        RingText = done ? "TERMINÉ" : _idleRingText;
        RingCaption = done ? "nouvelle analyse" : _idleRingCaption;

        // Si la fenêtre n'est pas au premier plan, son bouton clignote dans la barre des tâches.
        if (!_nested) Attention.FlashIfInBackground();

        // Si l'opération a besoin d'un redémarrage, c'est maintenant qu'on pose la question.
        RestartService.ProposeIfNeeded();
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!IsBusy) return;
        RingCaption = "Annulation…";
        _operation?.Cancel();
    }
}
