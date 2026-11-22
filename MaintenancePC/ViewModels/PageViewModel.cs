using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MaintenancePC.Services;

namespace MaintenancePC.ViewModels;

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

    protected PageViewModel(string title, string icon, string headline, string subText)
    {
        Title = title;
        Icon = icon;
        Headline = headline;
        SubText = subText;
        Progress = -1;
        RingText = "ANALYSER";
        RingCaption = "";
    }

    /// <summary>Appelé quand l'utilisateur ouvre cet onglet.</summary>
    public virtual void OnNavigatedTo() { }

    /// <summary>Démarre une opération longue : l'anneau s'anime et le bouton devient « Annuler ».</summary>
    protected CancellationToken BeginOperation(string headline, string subText, CancellationToken external = default)
    {
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
        RingText = "ANALYSER";
        RingCaption = "";
    }

    [RelayCommand]
    public void Cancel()
    {
        if (!IsBusy) return;
        RingCaption = "Annulation…";
        _operation?.Cancel();
    }
}
