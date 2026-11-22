using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

public sealed partial class ComposantsViewModel : PageViewModel
{
    public ObservableCollection<ComponentItem> Items { get; }
    public LogViewModel Log { get; } = new();

    [ObservableProperty] public partial bool HasScanned { get; set; }
    [ObservableProperty] public partial bool ShowLog { get; set; }

    /// <summary>Message affiché quand winget est absent (vide sinon).</summary>
    [ObservableProperty] public partial string WingetWarning { get; set; }

    public int MissingCount => Items.Count(i => HasScanned && !i.IsInstalled);

    public ComposantsViewModel() : base(
        "Composants", "", "Vérifier les composants",
        "Vérifie DirectX, Visual C++ et .NET, les composants dont les jeux et logiciels ont besoin.",
        ringText: "VÉRIFIER", ringCaption: "les composants")
    {
        WingetWarning = "";
        Items = new ObservableCollection<ComponentItem>(ComponentService.CreateItems());
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

            Report(50, "Lecture des composants installés…");
            var detected = await Task.Run(() => ComponentService.DetectAll(Items, token), token);
            foreach (var (item, version) in detected) Apply(item, version);

            Report(100, "Vérification terminée.");
            HasScanned = true;
            UpdateHeadline();
            Logger.Info($"Vérification des composants : {MissingCount} manquant(s), winget {winget ?? "absent"}");
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

    private static void Apply(ComponentItem item, string? version)
    {
        item.IsInstalled = version is not null;
        item.Status = version is not null ? Status.Ok : Status.Warning;
        item.Detail = version is null ? "Non installé" : version.Length == 0 ? "Installé" : $"Installé  ·  {version}";
        item.ActionLabel = version is null ? "Installer" : "Mettre à jour";
    }

    private void UpdateHeadline()
    {
        Headline = MissingCount == 0
            ? "Tous les composants sont installés"
            : PilotesViewModel.Plural(MissingCount, "composant manquant", "composants manquants");
        SubText = MissingCount == 0
            ? "Tu peux quand même les mettre à jour un par un."
            : "Clique sur « Installer ce qui manque » pour tout installer d'un coup.";
        OnPropertyChanged(nameof(MissingCount));
    }

    [RelayCommand]
    private Task InstallMissingAsync() => InstallAsync(Items.Where(i => !i.IsInstalled).ToList());

    [RelayCommand]
    private Task InstallOneAsync(ComponentItem item) => InstallAsync([item]);

    private async Task InstallAsync(List<ComponentItem> items)
    {
        if (IsBusy || items.Count == 0 || !HasScanned || WingetWarning.Length > 0) return;

        var token = BeginOperation("Installation des composants…", "Préparation…", installs: true);
        ShowLog = true;
        Log.Clear();
        var output = new Progress<string>(Log.Add);
        int failures = 0;
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
                    Apply(item, version);
                    if (version is null)
                    {
                        failures++;
                        item.Detail = "winget annonce une réussite, mais le composant n'apparaît toujours pas sur le PC";
                    }
                    else
                    {
                        item.Detail = version.Length > 0 ? $"{message}  ·  {version}" : message;
                    }
                }
                else
                {
                    failures++;
                    item.Status = Status.Error;
                    item.Detail = message + "  ·  détails dans le journal ci-dessous";
                }
            }

            Report(100, "Terminé.");
            UpdateHeadline();
            if (failures > 0)
            {
                Headline = PilotesViewModel.Plural(failures, "installation en échec", "installations en échec");
                SubText = "Regarde le journal en bas de l'écran pour comprendre pourquoi.";
            }
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
