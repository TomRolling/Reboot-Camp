using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

/// <summary>
/// Onglet Composants : DirectX, Visual C++ et .NET. Pour chacun on sait s'il est absent, installé mais ancien,
/// ou à jour ; on coche ce qu'on veut et un seul bouton installe ou met à jour la sélection.
/// </summary>
public sealed partial class ComposantsViewModel : PageViewModel
{
    public ObservableCollection<ComponentItem> Items { get; }
    public LogViewModel Log { get; } = new();

    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial bool ShowLog { get; set; }
    [ObservableProperty] public partial string SelectionText { get; set; }

    /// <summary>Message affiché quand winget est absent (vide sinon).</summary>
    [ObservableProperty] public partial string WingetWarning { get; set; }

    public int MissingCount => Items.Count(i => HasScanned && !i.IsInstalled);

    /// <summary>Composants installés dont une version plus récente existe.</summary>
    public int UpdateCount => Items.Count(i => HasScanned && i.HasUpdate);

    /// <summary>Vrai tant qu'il reste quelque chose à installer ou à mettre à jour (sinon la barre d'action disparaît).</summary>
    public bool HasPendingActions => MissingCount + UpdateCount > 0;

    public ComposantsViewModel() : base(
        "Composants", "", "Vérifier les composants",
        "Vérifie DirectX, Visual C++ et .NET, les composants dont les jeux et logiciels ont besoin.",
        ringText: "VÉRIFIER", ringCaption: "les composants")
    {
        WingetWarning = "";
        SelectionText = "";
        Items = new ObservableCollection<ComponentItem>(ComponentService.CreateItems());
        foreach (var item in Items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ComponentItem.IsSelected) or nameof(ComponentItem.NeedsAction)) UpdateSelection();
            };
        }
    }

    /// <summary>Les composants cochés pour lesquels il y a quelque chose à faire.</summary>
    private IEnumerable<ComponentItem> Selected => Items.Where(i => i.IsSelected && i.NeedsAction);

    private void UpdateSelection()
    {
        int count = Selected.Count();
        SelectionText = count == 0 ? "Rien de sélectionné" : PilotesViewModel.Plural(count, "composant sélectionné", "composants sélectionnés");
        InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RingAsync()
    {
        if (IsBusy) Cancel();
        else await RunScanAsync();
    }

    public async Task RunScanAsync(CancellationToken external = default)
    {
        if (IsBusy) return;
        var token = BeginOperation("Vérification des composants…", "Recherche de DirectX, Visual C++ et .NET…", external);
        try
        {
            Report(10, "Recherche de winget…");
            string? winget = await ComponentService.GetWingetVersionAsync(token);
            WingetWarning = winget is null
                ? "winget est introuvable : l'installation est impossible. Installe « Programme d'installation d'application » " +
                  "depuis le Microsoft Store (ou https://aka.ms/getwinget), puis relance la vérification."
                : "";

            Report(35, "Lecture des composants installés…");
            var detected = await Task.Run(() => ComponentService.DetectAll(Items, token), token);

            // C'est winget qui sait si une version plus récente existe (la même liste que l'onglet Logiciels).
            Report(60, "Recherche de versions plus récentes…");
            var newer = winget is null ? null : await SoftwareUpdateService.ListAsync(token);
            var versions = (newer ?? []).ToDictionary(u => u.Id, u => u.Versions, StringComparer.OrdinalIgnoreCase);

            foreach (var (item, version) in detected)
            {
                Apply(item, version, versions.GetValueOrDefault(item.WingetId), freshnessKnown: newer is not null);
                item.IsSelected = item.NeedsAction;
            }

            Report(100, "Vérification terminée.");
            HasScanned = true;
            UpdateHeadline();
            Logger.Info($"Vérification des composants : {MissingCount} manquant(s), {UpdateCount} à mettre à jour, winget {winget ?? "absent"}");
        }
        catch (OperationCanceledException)
        {
            Headline = "Vérification annulée";
            SubText = "Clique sur le bouton pour recommencer.";
        }
        catch (Exception ex)
        {
            Headline = "La vérification a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Vérification des composants", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <param name="version">Version installée, ou null si le composant est absent.</param>
    /// <param name="newerVersions">Texte « ancienne → nouvelle » si winget connaît une version plus récente, sinon null.</param>
    /// <param name="freshnessKnown">Faux si winget n'a pas pu dire ce qui est à jour : on affiche alors seulement « Installé ».</param>
    private static void Apply(ComponentItem item, string? version, string? newerVersions, bool freshnessKnown)
    {
        item.IsInstalled = version is not null;
        item.HasUpdate = version is not null && newerVersions is not null;

        if (version is null)
        {
            item.Status = Status.Error;
            item.Detail = "Manquant";
        }
        else if (item.HasUpdate)
        {
            item.Status = Status.Warning;
            item.Detail = $"Mise à jour disponible  ·  {newerVersions}";
        }
        else
        {
            item.Status = Status.Ok;
            string state = freshnessKnown ? "À jour" : "Installé";
            item.Detail = version.Length == 0 ? state : $"{state}  ·  {version}";
        }
    }

    private void UpdateHeadline()
    {
        int missing = MissingCount, updates = UpdateCount;
        string missingText = PilotesViewModel.Plural(missing, "composant manquant", "composants manquants");
        string updatesText = PilotesViewModel.Plural(updates, "à mettre à jour", "à mettre à jour");

        Headline = (missing, updates) switch
        {
            (0, 0) => "Tous les composants sont à jour",
            (_, 0) => missingText,
            (0, _) => PilotesViewModel.Plural(updates, "composant à mettre à jour", "composants à mettre à jour"),
            _ => $"{missingText}, {updatesText}",
        };
        SubText = missing + updates == 0
            ? "Rien à faire : tout ce dont les jeux et logiciels ont besoin est là."
            : "Décoche ce que tu ne veux pas toucher, puis clique sur « Installer la sélection ».";

        OnPropertyChanged(nameof(MissingCount));
        OnPropertyChanged(nameof(UpdateCount));
        OnPropertyChanged(nameof(HasPendingActions));
        UpdateSelection();
    }

    [RelayCommand]
    private void SelectAll()
    {
        var actionable = Items.Where(i => i.NeedsAction).ToList();
        bool select = actionable.Any(i => !i.IsSelected);
        foreach (var item in actionable) item.IsSelected = select;
    }

    private bool CanInstallSelected() => Selected.Any();

    [RelayCommand(CanExecute = nameof(CanInstallSelected))]
    private async Task InstallSelectedAsync()
    {
        var items = Selected.ToList();
        if (IsBusy || items.Count == 0 || !HasScanned || WingetWarning.Length > 0) return;

        var token = BeginOperation("Installation des composants…", "Préparation…");
        ShowLog = true;
        Log.Clear();
        var output = new Progress<string>(Log.Add);
        int succeeded = 0, failures = 0;
        try
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                Report(i * 100.0 / items.Count, $"Installation : {item.Name}…");
                item.Status = Status.Working;
                item.Detail = "Installation en cours…";

                var (ok, message) = await ComponentService.InstallAsync(item, output, token);

                if (ok)
                {
                    // On ne croit pas winget sur parole : on revérifie que le composant est réellement là.
                    string? version = await Task.Run(() => ComponentService.DetectAll([item], token)[item], token);
                    Apply(item, version, newerVersions: null, freshnessKnown: true);
                    if (version is null)
                    {
                        failures++;
                        item.Detail = "winget annonce une réussite, mais le composant n'apparaît toujours pas sur le PC";
                    }
                    else
                    {
                        succeeded++;
                        item.IsSelected = false;
                        item.Detail = version.Length > 0 ? $"{message}  ·  {version}" : message;
                    }
                }
                else
                {
                    failures++;
                    item.Status = Status.Error;
                    item.Detail = message;
                }
            }

            Report(100, "Terminé.");
            UpdateHeadline();
            string done = PilotesViewModel.Plural(succeeded, "composant installé ou mis à jour", "composants installés ou mis à jour");
            Headline = failures == 0 ? $"Terminé : {done}" : $"Terminé : {done}, {failures} en échec";
            SubText = failures == 0 ? "Tout s'est bien passé."
                : "L'explication est sous le nom de chaque composant ; le détail est dans le journal en bas de l'écran.";
            MarkDone(allGood: failures == 0);
        }
        catch (OperationCanceledException)
        {
            foreach (var item in items.Where(i => i.Status == Status.Working))
            {
                item.Status = Status.Warning;
                item.Detail = "Installation annulée";
            }
            Headline = "Installation annulée";
            SubText = "winget a été arrêté.";
        }
        catch (Exception ex)
        {
            Headline = "L'installation a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Installation de composants", ex);
        }
        finally
        {
            EndOperation();
        }
    }
}
