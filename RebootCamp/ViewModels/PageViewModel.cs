using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
    private bool _guarding;

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
    /// <param name="installs">
    /// Vrai si l'opération installe quelque chose : tant qu'elle dure, un redémarrage du PC déclenché par un
    /// installeur est retenu (voir <see cref="RestartService"/>).
    /// </param>
    protected CancellationToken BeginOperation(string headline, string subText, CancellationToken external = default, bool installs = false)
    {
        if (installs && !_guarding)
        {
            _guarding = true;
            RestartService.BeginGuard();
        }
        _operation?.Dispose();
        _operation = AppLifetime.CreateOperation(external);
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

    protected void EndOperation()
    {
        IsBusy = false;
        Progress = -1;
        RingText = _idleRingText;
        RingCaption = _idleRingCaption;

        if (_guarding)
        {
            _guarding = false;
            _ = ReleaseGuardLaterAsync();
        }
        // Si l'opération a besoin d'un redémarrage, c'est maintenant qu'on pose la question.
        RestartService.ProposeIfNeeded();
    }

    /// <summary>Certains installeurs demandent le redémarrage juste après s'être terminés : on garde la retenue 15 secondes.</summary>
    private static async Task ReleaseGuardLaterAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15), AppLifetime.Token);
        }
        catch (OperationCanceledException)
        {
            // L'appli se ferme.
        }
        RestartService.EndGuard();
        RestartService.ProposeIfNeeded();   // un redémarrage retenu pendant ces 15 secondes
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!IsBusy) return;
        RingCaption = "Annulation…";
        _operation?.Cancel();
    }
}
