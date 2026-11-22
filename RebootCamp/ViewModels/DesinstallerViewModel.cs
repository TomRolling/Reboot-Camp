using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>
/// Onglet Désinstaller : liste les programmes (classiques d'un côté, applications Windows de l'autre),
/// lance leur désinstalleur officiel, puis propose de supprimer les dossiers et raccourcis qu'il a laissés.
/// Le registre n'est jamais modifié.
/// </summary>
public sealed partial class DesinstallerViewModel : PageViewModel
{
    private const int DesktopTab = 0, WindowsAppsTab = 1;

    private List<InstalledProgram> _desktop = [], _windowsApps = [];

    /// <summary>Les programmes de la catégorie affichée, filtrés par la recherche.</summary>
    public ObservableCollection<InstalledProgram> Programs { get; } = [];

    /// <summary>Ce que le dernier programme désinstallé a laissé sur le disque (vide = rien à montrer).</summary>
    public ObservableCollection<Leftover> Leftovers { get; } = [];

    public IReadOnlyList<SectionTab> Tabs { get; } = [new("Programmes"), new("Applications Windows")];

    [ObservableProperty] public partial int SelectedTab { get; set; }
    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial bool CreateRestorePoint { get; set; }
    [ObservableProperty] public partial string FilterText { get; set; }
    [ObservableProperty] public partial string ListMessage { get; set; }
    [ObservableProperty] public partial bool HasLeftovers { get; set; }
    [ObservableProperty] public partial string LeftoversTitle { get; set; }
    [ObservableProperty] public partial string LeftoversSelectionText { get; set; }

    public DesinstallerViewModel() : base(
        "Désinstaller", "", "Lister les programmes",
        "Affiche les programmes installés pour les désinstaller proprement, traces comprises. Rien n'est retiré sans ton accord.",
        ringText: "LISTER", ringCaption: "les programmes")
    {
        CreateRestorePoint = true;
        FilterText = "";
        ListMessage = "";
        LeftoversTitle = "";
        LeftoversSelectionText = "";
    }

    partial void OnSelectedTabChanged(int value) => ApplyFilter();
    partial void OnFilterTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var source = SelectedTab == WindowsAppsTab ? _windowsApps : _desktop;
        string filter = FilterText.Trim();

        Programs.Clear();
        foreach (var program in source)
        {
            if (filter.Length == 0
                || program.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                || program.Publisher.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
            {
                Programs.Add(program);
            }
        }

        ListMessage = Programs.Count > 0 ? ""
            : filter.Length > 0 ? "Aucun programme ne correspond à la recherche."
            : SelectedTab == WindowsAppsTab && UserContext.IsDifferentUser
                ? $"Les applications Windows de {UserContext.InteractiveUser} ne sont pas visibles depuis le compte administrateur."
            : "Rien à afficher dans cette catégorie.";
    }

    private void RefreshBadges()
    {
        Tabs[DesktopTab].Badge = _desktop.Count.ToString();
        Tabs[WindowsAppsTab].Badge = _windowsApps.Count.ToString();
    }

    // ───────────────────────── Liste ─────────────────────────

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RingAsync()
    {
        if (IsBusy) Cancel();
        else await RunScanAsync();
    }

    public async Task RunScanAsync()
    {
        if (IsBusy) return;
        var token = BeginOperation("Lecture des programmes…", "Programmes installés…");
        try
        {
            Report(20, "Programmes installés…");
            _desktop = await Task.Run(UninstallService.ListDesktopPrograms, token);

            Report(60, "Applications Windows…");
            _windowsApps = await UninstallService.ListWindowsAppsAsync(token);

            Report(100, "Terminé.");
            ClearLeftovers();
            HasScanned = true;
            RefreshBadges();
            ApplyFilter();
            ShowCounts();
            Logger.Info($"Désinstaller : {_desktop.Count} programme(s), {_windowsApps.Count} application(s) Windows");
        }
        catch (OperationCanceledException)
        {
            Headline = "Lecture annulée";
            SubText = "Clique sur le bouton pour recommencer.";
        }
        catch (Exception ex)
        {
            Headline = "La lecture a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Liste des programmes installés", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private void ShowCounts()
    {
        Headline = PilotesViewModel.Plural(_desktop.Count + _windowsApps.Count, "programme installé", "programmes installés");
        SubText = "Clique sur « Désinstaller » à droite d'un programme. Les mentions en couleur signalent ce qu'il vaut mieux garder.";
    }

    // ───────────────────────── Désinstallation ─────────────────────────

    [RelayCommand]
    private async Task UninstallAsync(InstalledProgram program)
    {
        if (IsBusy || program is null) return;

        // Les programmes marqués « Ne pas désinstaller » demandent une confirmation plus appuyée.
        bool risky = program.AdviceStatus == Status.Error;
        bool confirmed = Dialogs.Confirm(new ConfirmRequest(
            Icon: risky ? "" : "",
            Title: $"Désinstaller {program.Name} ?",
            Message: risky
                ? $"{program.Advice}. Si tu le retires, d'autres choses peuvent cesser de fonctionner."
                : program.Kind == ProgramKind.WindowsApp
                    ? "L'application et ses données seront retirées par Windows."
                    : "Son désinstalleur va se lancer. Ensuite, Reboot Camp cherchera les dossiers et raccourcis qu'il a laissés et te les montrera avant de supprimer quoi que ce soit.",
            Hint: program.Advice.Length > 0 && !risky ? program.Advice + "." : "",
            ConfirmLabel: risky ? "Désinstaller quand même" : "Désinstaller",
            CancelLabel: "Annuler",
            IsWarning: risky));
        if (!confirmed) return;

        var token = BeginOperation($"Désinstallation de {program.Name}…", "Préparation…");
        ClearLeftovers();
        try
        {
            if (CreateRestorePoint)
            {
                SubText = "Création d'un point de restauration…";
                var (created, message) = await RestorePointService.CreateAsync(
                    $"{AppInfo.Name} - avant désinstallation de {program.Name}", RestorePointService.ModifySettings, token);
                if (!created)
                {
                    Headline = "Désinstallation non lancée";
                    SubText = message + " Décoche la case pour désinstaller quand même.";
                    return;
                }
            }

            var status = new Progress<string>(text => SubText = text);
            var (ok, error) = await UninstallService.UninstallAsync(program, status, token);
            if (!ok)
            {
                Headline = "Désinstallation non terminée";
                SubText = error;
                return;
            }

            _desktop.Remove(program);
            _windowsApps.Remove(program);
            RefreshBadges();
            ApplyFilter();
            Logger.Info($"Désinstallé : {program.Name}");

            // Les applications Windows sont retirées en entier par Windows : il n'y a de traces à chercher
            // que pour les programmes classiques.
            if (program.Kind == ProgramKind.Desktop)
            {
                SubText = "Recherche des traces restantes…";
                var remaining = _desktop.ToList();
                var found = await Task.Run(() => LeftoverService.Find(program, remaining, LeftoverRoots.ForThisPc(), token), token);
                foreach (var leftover in found)
                {
                    leftover.PropertyChanged += (_, _) => UpdateLeftoverSelection();
                    Leftovers.Add(leftover);
                }
            }

            if (Leftovers.Count > 0)
            {
                HasLeftovers = true;
                LeftoversTitle = $"Ce que {program.Name} a laissé sur le disque";
                UpdateLeftoverSelection();
                Headline = $"Terminé : {program.Name} est désinstallé";
                SubText = "Il reste des traces. Décoche ce que tu veux garder, puis clique sur « Supprimer la sélection ».";
            }
            else
            {
                Headline = $"Terminé : {program.Name} est désinstallé";
                SubText = "Aucune trace restante trouvée sur le disque.";
            }
            MarkDone();
        }
        catch (OperationCanceledException)
        {
            // On a seulement cessé d'attendre : le désinstalleur, lui, n'a pas été interrompu.
            Headline = "Désinstallation non terminée";
            SubText = UninstallService.IsStillInstalled(program)
                ? $"{program.Name} est toujours installé."
                : "Relance « Lister » pour mettre la liste à jour.";
        }
        catch (Exception ex)
        {
            Headline = "La désinstallation a échoué";
            SubText = ex.Message.Trim();
            Logger.Error($"Désinstallation de {program.Name}", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    // ───────────────────────── Traces restantes ─────────────────────────

    private IEnumerable<Leftover> SelectedLeftovers => Leftovers.Where(l => l.IsSelected);

    private void UpdateLeftoverSelection()
    {
        int count = SelectedLeftovers.Count();
        LeftoversSelectionText = count == 0 ? "Rien de sélectionné"
            : $"{PilotesViewModel.Plural(count, "élément", "éléments")}  ·  {Format.Size(SelectedLeftovers.Sum(l => l.Size))}";
        DeleteLeftoversCommand.NotifyCanExecuteChanged();
    }

    private void ClearLeftovers()
    {
        Leftovers.Clear();
        HasLeftovers = false;
    }

    /// <summary>« Tout garder » : on referme l'aperçu sans rien supprimer.</summary>
    [RelayCommand]
    private void KeepLeftovers()
    {
        ClearLeftovers();
        SubText = "Les traces ont été laissées en place.";
    }

    private bool CanDeleteLeftovers() => SelectedLeftovers.Any();

    [RelayCommand(CanExecute = nameof(CanDeleteLeftovers))]
    private async Task DeleteLeftoversAsync()
    {
        var selected = SelectedLeftovers.ToList();
        if (IsBusy || selected.Count == 0) return;

        var token = BeginOperation("Suppression des traces…", "Suppression en cours…");
        try
        {
            var result = await Task.Run(() => LeftoverService.Delete(selected, token), token);
            ClearLeftovers();

            Headline = $"Terminé : {Format.Size(result.Freed)} de traces supprimées";
            if (result.AtNextRestart > 0)
            {
                SubText = PilotesViewModel.Plural(result.AtNextRestart, "fichier verrouillé sera supprimé", "fichiers verrouillés seront supprimés")
                          + " par Windows au prochain redémarrage.";
                RestartService.Need("Des fichiers verrouillés ne pourront être supprimés par Windows qu'au redémarrage du PC.");
            }
            else
            {
                SubText = "Il ne reste rien de ce programme sur le disque.";
            }
            MarkDone();
        }
        catch (OperationCanceledException)
        {
            Headline = "Suppression interrompue";
            SubText = "Une partie des traces est peut-être encore là.";
        }
        catch (Exception ex)
        {
            Headline = "La suppression a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Suppression des traces", ex);
        }
        finally
        {
            EndOperation();
        }
    }
}
