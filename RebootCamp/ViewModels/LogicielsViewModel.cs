using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>Onglet Logiciels : met à jour les programmes installés (Chrome, Discord, VLC…) avec winget.</summary>
public sealed partial class LogicielsViewModel : PageViewModel
{
    // Ce que l'appli a retenu des tentatives précédentes (voir UpdateMemory) : les mises à jour déjà faites que
    // winget repropose à tort ne sont plus affichées, et celles qu'il ne peut pas faire sont mises à part.
    private readonly UpdateMemory _memory;

    public ObservableCollection<SoftwareUpdate> Items { get; } = [];
    public LogViewModel Log { get; } = new();

    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial bool ShowLog { get; set; }
    [ObservableProperty] public partial string Message { get; set; }
    [ObservableProperty] public partial string SelectionText { get; set; }

    /// <summary>Phrase qui dit quels logiciels ne sont plus proposés et pourquoi (vide s'il n'y en a pas).</summary>
    [ObservableProperty] public partial string HiddenNote { get; set; }

    /// <summary>Faux si winget est absent de ce PC.</summary>
    public bool WingetAvailable { get; private set; } = true;

    /// <summary>Les mises à jour réellement faisables : c'est ce nombre que l'accueil affiche.</summary>
    public int UpdateCount => Items.Count(i => i.CanSelect);

    /// <summary>Vrai tant qu'il reste au moins un logiciel à mettre à jour (sinon la barre d'action disparaît).</summary>
    public bool HasPendingUpdates => UpdateCount > 0;

    /// <param name="memory">Le carnet des tentatives précédentes (les tests en fournissent un vide).</param>
    public LogicielsViewModel(UpdateMemory? memory = null) : base(
        "Logiciels", "", "Analyser les logiciels",
        "Cherche les mises à jour de tes logiciels installés (navigateur, Discord, VLC…).",
        ringCaption: "les logiciels")
    {
        _memory = memory ?? new UpdateMemory();
        Message = "";
        SelectionText = "";
        HiddenNote = "";
    }

    /// <summary>Les logiciels cochés qui restent à mettre à jour : la seule définition de « la sélection ».</summary>
    private IEnumerable<SoftwareUpdate> Selected => Items.Where(i => i.IsSelected && i.CanSelect);

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

            var hidden = new List<string>();
            foreach (var update in updates)
            {
                var note = _memory.Find(update.Id, update.Available);
                if (note is { Blocked: false })
                {
                    // Cette version a déjà été installée avec succès et winget la repropose quand même :
                    // il lit mal le numéro de version du logiciel. La relancer ne changerait rien.
                    hidden.Add(update.Name);
                    continue;
                }
                if (note is { Blocked: true }) update.MarkBlocked(note.Reason, note.CanReinstall);

                update.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName is nameof(SoftwareUpdate.IsSelected) or nameof(SoftwareUpdate.CanSelect)) UpdateSelection();
                };
            }

            // Les mises à jour faisables d'abord, celles que winget ne peut pas faire à la fin.
            foreach (var update in updates.Where(u => !hidden.Contains(u.Name)).OrderBy(u => u.IsBlocked)) Items.Add(update);

            HiddenNote = hidden.Count == 0 ? ""
                : $"{PilotesViewModel.Plural(hidden.Count, "logiciel n'est plus proposé", "logiciels ne sont plus proposés")} " +
                  $"({string.Join(", ", hidden)}) : la dernière version est déjà installée, mais winget lit mal son numéro de version.";

            HasScanned = true;
            UpdateSelection();
            ShowScanResult();
            Logger.Info($"Analyse des logiciels : {UpdateCount} mise(s) à jour, {Items.Count(i => i.IsBlocked)} bloquée(s), {hidden.Count} masquée(s)");
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

    private void ShowScanResult()
    {
        int updates = UpdateCount, blocked = Items.Count(i => i.IsBlocked);
        Message = Items.Count == 0 ? "Tous les logiciels connus de winget sont à jour." : "";
        Headline = updates > 0 ? PilotesViewModel.Plural(updates, "logiciel à mettre à jour", "logiciels à mettre à jour")
            : blocked > 0 ? "Rien à mettre à jour pour l'instant"
            : "Tes logiciels sont à jour";
        SubText = updates > 0 ? "Décoche ceux que tu ne veux pas toucher, puis clique sur « Mettre à jour »."
            : blocked > 0 ? $"{PilotesViewModel.Plural(blocked, "logiciel a", "logiciels ont")} une mise à jour que winget ne peut pas faire ici : l'explication est sous son nom."
            : "Seuls les logiciels connus de winget sont vérifiés.";
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasPendingUpdates));
    }

    [RelayCommand]
    private void SelectAll()
    {
        var selectable = Items.Where(i => i.CanSelect).ToList();
        bool select = selectable.Any(i => !i.IsSelected);
        foreach (var item in selectable) item.IsSelected = select;
    }

    /// <summary>« Réafficher » : oublie les mises à jour déjà faites et relance l'analyse, au cas où l'une d'elles serait à refaire.</summary>
    [RelayCommand]
    private async Task ShowHiddenAsync()
    {
        if (IsBusy) return;
        _memory.ForgetDone();
        await RunScanAsync();
    }

    /// <summary>
    /// Bouton d'une ligne bloquée. « Réessayer » relance la mise à jour normale (l'éditeur ou winget a peut-être
    /// corrigé le problème) ; « Désinstaller puis réinstaller » remplace l'ancienne version par la nouvelle.
    /// </summary>
    [RelayCommand]
    private async Task FixBlockedAsync(SoftwareUpdate item)
    {
        if (IsBusy || item is null || !item.IsBlocked) return;

        bool reinstall = item.CanReinstall;
        if (reinstall && !Dialogs.Confirm(new ConfirmRequest("", $"Désinstaller puis réinstaller {item.Name} ?",
                "La version actuelle va être désinstallée, puis la nouvelle installée à sa place. " +
                "Entre les deux, le logiciel est absent du PC : s'il sert à un jeu ou à un périphérique, ferme-les d'abord.",
                "Si la nouvelle version ne s'installe pas, il faudra la télécharger sur le site de l'éditeur.",
                ConfirmLabel: "Désinstaller puis réinstaller", CancelLabel: "Annuler", IsWarning: true)))
            return;

        var token = BeginOperation($"Mise à jour de {item.Name}…", "Préparation…");
        ShowLog = true;
        Log.Clear();
        var output = new Progress<string>(Log.Add);
        try
        {
            item.IsBlocked = false;
            item.Status = Status.Working;
            item.Detail = reinstall ? "Désinstallation de l'ancienne version…" : "Nouvel essai…";

            var result = reinstall
                ? await SoftwareUpdateService.ReinstallAsync(item, output, token)
                : await SoftwareUpdateService.UpgradeAsync(item, output, token);
            Apply(item, result);

            Headline = result.Ok ? $"Terminé : {item.Name} est à jour" : $"{item.Name} n'a pas pu être mis à jour";
            SubText = result.Ok ? "Tout s'est bien passé." : "L'explication est sous son nom ; le détail est dans le journal en bas de l'écran.";
            if (result.Ok) MarkDone();
            OnPropertyChanged(nameof(UpdateCount)); OnPropertyChanged(nameof(HasPendingUpdates));
        }
        catch (OperationCanceledException)
        {
            item.Status = Status.Warning;
            item.Detail = "Mise à jour annulée";
            Headline = "Mise à jour annulée";
            SubText = "Relance une analyse pour voir où en est ce logiciel.";
        }
        catch (Exception ex)
        {
            Headline = "La mise à jour a échoué";
            SubText = ex.Message.Trim();
            Logger.Error($"Mise à jour de {item.Name}", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Reporte le résultat d'une tentative sur la ligne, et le note dans le carnet pour les prochaines analyses.</summary>
    private void Apply(SoftwareUpdate item, SoftwareUpdateService.UpgradeResult result)
    {
        if (result.Ok)
        {
            item.Status = Status.Ok;
            item.Detail = result.Message;
            item.IsSelected = false;
            _memory.Remember(item.Id, new UpdateNote(item.Available, Blocked: false));
        }
        else if (result.Blocked)
        {
            // winget ne peut pas faire cette mise à jour ici : la ligne passe à part, avec l'explication.
            item.MarkBlocked(result.Message, result.CanReinstall);
            _memory.Remember(item.Id, new UpdateNote(item.Available, Blocked: true, result.Message, result.CanReinstall));
        }
        else
        {
            item.Status = Status.Error;
            item.Detail = result.Message;
            _memory.Forget(item.Id);
        }
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

                var result = await SoftwareUpdateService.UpgradeAsync(item, output, token);
                Apply(item, result);

                if (result.Ok) succeeded++;
                else if (result.Blocked) blocked++;
                else failed++;
            }

            Report(100, "Terminé.");
            string done = PilotesViewModel.Plural(succeeded, "logiciel mis à jour", "logiciels mis à jour");
            int notDone = failed + blocked;
            Headline = notDone == 0 ? $"Terminé : {done}" : $"Terminé : {done}, {notDone} non mis à jour";
            SubText = notDone == 0 ? "Tout s'est bien passé."
                : failed == 0 ? "winget ne peut pas mettre à jour ceux marqués en orange : l'explication et la marche à suivre sont sous leur nom."
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
