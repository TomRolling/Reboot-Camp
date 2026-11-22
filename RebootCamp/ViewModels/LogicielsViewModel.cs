using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>Onglet Logiciels : met à jour les programmes installés (Chrome, Discord, VLC…) avec winget.</summary>
public sealed partial class LogicielsViewModel : PageViewModel
{
    // Logiciels mis à jour depuis l'ouverture de l'appli : on ne les repropose pas, même si winget
    // met un moment à remarquer la nouvelle version (ou si le logiciel doit redémarrer pour l'afficher).
    private readonly HashSet<string> _updatedThisSession = new(StringComparer.OrdinalIgnoreCase);

    // Logiciels que winget n'a pas pu mettre à jour sur ce PC (avec l'explication) : une nouvelle analyse les
    // montre encore, mais décochés et avec leur explication, pour ne pas relancer un échec certain.
    private readonly Dictionary<string, string> _blockedThisSession = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<SoftwareUpdate> Items { get; } = [];
    public LogViewModel Log { get; } = new();

    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial bool ShowLog { get; set; }
    [ObservableProperty] public partial string Message { get; set; }
    [ObservableProperty] public partial string SelectionText { get; set; }

    /// <summary>Faux si winget est absent de ce PC.</summary>
    public bool WingetAvailable { get; private set; } = true;

    public int UpdateCount => Items.Count(i => i.Status != Status.Ok);

    /// <summary>Vrai tant qu'il reste au moins un logiciel à mettre à jour (sinon la barre d'action disparaît).</summary>
    public bool HasPendingUpdates => UpdateCount > 0;

    public LogicielsViewModel() : base(
        "Logiciels", "", "Analyser les logiciels",
        "Cherche les mises à jour de tes logiciels installés (navigateur, Discord, VLC…).",
        ringCaption: "les logiciels")
    {
        Message = "";
        SelectionText = "";
    }

    /// <summary>Les logiciels cochés qui restent à mettre à jour : la seule définition de « la sélection ».</summary>
    private IEnumerable<SoftwareUpdate> Selected => Items.Where(i => i.IsSelected && i.Status != Status.Ok);

    private void UpdateSelection()
    {
        int count = Selected.Count();
        SelectionText = count == 0 ? "Rien de sélectionné" : PilotesViewModel.Plural(count, "logiciel sélectionné", "logiciels sélectionnés");
        UpgradeCommand.NotifyCanExecuteChanged();   // le bouton « Mettre à jour » se grise quand rien n'est coché
    }

    private bool CanUpgrade() => Selected.Any();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RingAsync()
    {
        if (IsBusy) Cancel();
        else await RunScanAsync();
    }

    public async Task RunScanAsync(CancellationToken external = default)
    {
        if (IsBusy) return;
        var token = BeginOperation("Analyse des logiciels…", "Recherche des mises à jour de logiciels…", external);
        try
        {
            var updates = await SoftwareUpdateService.ListAsync(token);

            Items.Clear();
            OnPropertyChanged(nameof(HasPendingUpdates));
            WingetAvailable = updates is not null;
            if (updates is null)
            {
                Headline = "winget est introuvable";
                SubText = "Installe « Programme d'installation d'application » depuis le Microsoft Store, puis relance l'analyse.";
                Message = "";
                HasScanned = false;
                return;
            }

            updates.RemoveAll(u => _updatedThisSession.Contains(u.Id));
            foreach (var update in updates)
            {
                if (_blockedThisSession.TryGetValue(update.Id, out string? reason))
                {
                    update.IsSelected = false;
                    update.Status = Status.Warning;
                    update.Detail = reason;
                }
                update.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(SoftwareUpdate.IsSelected) or nameof(SoftwareUpdate.Status)) UpdateSelection();
                };
                Items.Add(update);
            }

            HasScanned = true;
            UpdateSelection();
            Message = updates.Count == 0 ? "Tous les logiciels connus de winget sont à jour." : "";
            Headline = updates.Count == 0
                ? "Tes logiciels sont à jour"
                : PilotesViewModel.Plural(updates.Count, "logiciel à mettre à jour", "logiciels à mettre à jour");
            SubText = updates.Count == 0
                ? "Seuls les logiciels connus de winget sont vérifiés."
                : "Décoche ceux que tu ne veux pas toucher, puis clique sur « Mettre à jour ».";
            OnPropertyChanged(nameof(UpdateCount)); OnPropertyChanged(nameof(HasPendingUpdates));
            Logger.Info($"Analyse des logiciels : {updates.Count} mise(s) à jour");
        }
        catch (OperationCanceledException)
        {
            Headline = "Analyse annulée";
            SubText = "Clique sur le bouton pour recommencer.";
        }
        catch (Exception ex)
        {
            Headline = "L'analyse a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Analyse des logiciels", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        bool select = Items.Any(i => !i.IsSelected);
        foreach (var item in Items) item.IsSelected = select;
    }

    [RelayCommand(CanExecute = nameof(CanUpgrade))]
    private async Task UpgradeAsync()
    {
        var selected = Selected.ToList();
        if (IsBusy || selected.Count == 0) return;

        var token = BeginOperation("Mise à jour des logiciels…", "Préparation…");
        ShowLog = true;
        Log.Clear();
        var output = new Progress<string>(Log.Add);
        int succeeded = 0, failed = 0, blocked = 0;
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var item = selected[i];
                Report(i * 100.0 / selected.Count, $"Mise à jour : {item.Name}…");
                item.Status = Status.Working;
                item.Detail = "Mise à jour en cours…";

                var (ok, message, isBlocked) = await SoftwareUpdateService.UpgradeAsync(item, output, token);

                // « Bloqué » : winget ne peut pas faire cette mise à jour ici, réessayer ne changera rien.
                // La ligne passe à l'orange avec l'explication, et se décoche pour ne pas être relancée par erreur.
                item.Status = ok ? Status.Ok : isBlocked ? Status.Warning : Status.Error;
                item.Detail = message;
                if (ok) { succeeded++; _updatedThisSession.Add(item.Id); }
                else if (isBlocked) blocked++;
                else failed++;
                if (ok || isBlocked) item.IsSelected = false;
                if (isBlocked) _blockedThisSession[item.Id] = message;
            }

            Report(100, "Terminé.");
            string done = PilotesViewModel.Plural(succeeded, "logiciel mis à jour", "logiciels mis à jour");
            int notDone = failed + blocked;
            Headline = notDone == 0 ? $"Terminé : {done}" : $"Terminé : {done}, {notDone} non mis à jour";
            SubText = notDone == 0 ? "Tout s'est bien passé."
                : failed == 0 ? "winget ne peut pas mettre à jour ceux marqués en orange : l'explication est sous leur nom."
                : "L'explication est sous le nom de chaque logiciel ; le détail est dans le journal en bas de l'écran.";
            MarkDone(allGood: notDone == 0);
            OnPropertyChanged(nameof(UpdateCount)); OnPropertyChanged(nameof(HasPendingUpdates));
        }
        catch (OperationCanceledException)
        {
            foreach (var item in selected.Where(i => i.Status == Status.Working))
            {
                item.Status = Status.Warning;
                item.Detail = "Mise à jour annulée";
            }
            Headline = "Mise à jour annulée";
            SubText = PilotesViewModel.Plural(succeeded, "logiciel mis à jour", "logiciels mis à jour") + " avant l'annulation.";
        }
        catch (Exception ex)
        {
            Headline = "La mise à jour a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Mise à jour de logiciels", ex);
        }
        finally
        {
            EndOperation();
        }
    }
}
