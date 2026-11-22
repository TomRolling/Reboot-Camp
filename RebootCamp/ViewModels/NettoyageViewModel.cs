using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.ViewModels;

public sealed partial class NettoyageViewModel : PageViewModel
{
    public ObservableCollection<CleanupCategory> Categories { get; }

    [ObservableProperty] public partial bool HasScanned { get; set; }

    /// <summary>Mode simulation : on fait tout comme en vrai, mais rien n'est supprimé.</summary>
    [ObservableProperty] public partial bool Simulation { get; set; }

    [ObservableProperty] public partial bool CreateRestorePoint { get; set; }
    [ObservableProperty] public partial string SelectionText { get; set; }
    [ObservableProperty] public partial string CleanLabel { get; set; }

    /// <summary>Espace récupérable trouvé par la dernière analyse (pour l'écran d'accueil).</summary>
    public long RecoverableBytes { get; private set; }

    public NettoyageViewModel() : base(
        "Nettoyage", "", "Analyser avant de nettoyer",
        "Calcule l'espace récupérable. Rien n'est supprimé sans ton accord.",
        ringCaption: "avant nettoyage")
    {
        CreateRestorePoint = true;
        SelectionText = "";
        CleanLabel = "Nettoyer";

        Categories = new ObservableCollection<CleanupCategory>(CleanupService.CreateCategories());
        foreach (var category in Categories)
        {
            category.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CleanupCategory.IsSelected) or nameof(CleanupCategory.Size) or nameof(CleanupCategory.IsAvailable))
                    UpdateSelection();
            };
        }
    }

    partial void OnSimulationChanged(bool value) => CleanLabel = value ? "Simuler" : "Nettoyer";

    private void UpdateSelection()
    {
        long total = Categories.Where(c => c.IsSelected).Sum(c => c.Size);
        int count = Categories.Count(c => c.IsSelected);
        SelectionText = count == 0 ? "Rien de sélectionné" : $"Sélection : {Format.Size(total)}";
        CleanCommand.NotifyCanExecuteChanged();   // le bouton « Nettoyer » se grise quand rien n'est coché
    }

    /// <summary>Les catégories cochées et nettoyables : la seule définition de « la sélection » pour le bouton.</summary>
    private IEnumerable<CleanupCategory> Selected => Categories.Where(c => c.IsSelected && c.IsAvailable);

    private bool CanClean() => Selected.Any();

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task RingAsync()
    {
        if (IsBusy) Cancel();
        else await RunScanAsync();
    }

    public async Task RunScanAsync(CancellationToken external = default)
    {
        if (IsBusy) return;
        var token = BeginOperation("Analyse avant nettoyage…", "Calcul de l'espace récupérable…", external);
        try
        {
            long total = 0;
            for (int i = 0; i < Categories.Count; i++)
            {
                var category = Categories[i];
                Report(i * 100.0 / Categories.Count, $"Analyse : {category.Name}…");
                category.Status = Status.Working;

                var result = await Task.Run(() => CleanupService.Scan(category, token), token);
                await Task.Delay(120, token);   // laisse le temps de voir quelle catégorie est analysée

                category.Status = Status.None;
                category.IsAvailable = result.IsAvailable;
                category.Size = result.Size;
                category.Detail = result.Warning ?? category.Description;
                category.SizeText = category.Kind == CleanupKind.Dism ? "?" : result.IsAvailable ? Format.Size(result.Size) : "";
                category.IsSelected = result.IsAvailable && category.SelectedByDefault && result.Size > 0;
                total += result.Size;
            }

            RecoverableBytes = total;
            HasScanned = true;
            UpdateSelection();
            Headline = total > 0 ? $"{Format.Size(total)} récupérables" : "Rien à nettoyer";
            SubText = "Coche ce que tu veux supprimer, puis clique sur Nettoyer.";
            Logger.Info($"Analyse du nettoyage : {Format.Size(total)} récupérables");
        }
        catch (OperationCanceledException)
        {
            foreach (var category in Categories) category.Status = Status.None;
            Headline = "Analyse annulée";
            SubText = "Clique sur le bouton pour recommencer.";
        }
        catch (Exception ex)
        {
            Headline = "L'analyse a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Analyse du nettoyage", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task CleanAsync()
    {
        var selected = Selected.ToList();
        if (IsBusy || selected.Count == 0) return;

        bool simulate = Simulation;
        var token = BeginOperation(simulate ? "Simulation en cours…" : "Nettoyage en cours…", "Préparation…");
        long freed = 0;
        try
        {
            if (CreateRestorePoint && !simulate)
            {
                SubText = "Création d'un point de restauration…";
                var (ok, message) = await RestorePointService.CreateAsync(
                    $"{AppInfo.Name} - avant nettoyage", RestorePointService.ModifySettings, token);
                if (!ok)
                {
                    Headline = "Nettoyage non lancé";
                    SubText = message + " Décoche la case pour nettoyer quand même.";
                    return;
                }
            }

            var output = new Progress<string>(line => SubText = line);
            for (int i = 0; i < selected.Count; i++)
            {
                var category = selected[i];
                Report(i * 100.0 / selected.Count, $"{(simulate ? "Simulation" : "Nettoyage")} : {category.Name}…");
                category.Status = Status.Working;

                var result = await CleanupService.CleanAsync(category, simulate, output, token);

                freed += result.Freed;
                category.Status = result.Note is null ? Status.Ok : Status.Warning;
                category.Detail = Describe(result, simulate);
                Logger.Info($"{(simulate ? "[simulation] " : "")}{category.Name} : {Format.Size(result.Freed)}, " +
                            $"{result.Deleted} fichier(s), {result.Skipped} ignoré(s). {result.Note}");

                if (!simulate)
                {
                    category.Size = 0;
                    category.SizeText = "";
                    category.IsSelected = false;
                }
            }

            Report(100, "Terminé.");
            Headline = simulate ? $"Simulation : {Format.Size(freed)} seraient libérés" : $"{Format.Size(freed)} libérés";
            SubText = simulate ? "Rien n'a été supprimé. Décoche « Mode simulation » pour nettoyer vraiment."
                : "Nettoyage terminé. Les fichiers en cours d'utilisation ont été laissés en place.";
            if (!simulate) RecoverableBytes = 0;
        }
        catch (OperationCanceledException)
        {
            foreach (var category in selected.Where(c => c.Status == Status.Working)) category.Status = Status.None;
            Headline = $"Nettoyage annulé ({Format.Size(freed)} libérés)";
            SubText = "Relance une analyse pour voir ce qu'il reste.";
        }
        catch (Exception ex)
        {
            Headline = "Le nettoyage a échoué";
            SubText = ex.Message.Trim();
            Logger.Error("Nettoyage", ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private static string Describe(CleanResult result, bool simulate)
    {
        if (result.Note is not null) return result.Note;

        string text = simulate ? $"{Format.Size(result.Freed)} seraient libérés" : $"{Format.Size(result.Freed)} libérés";
        if (result.Skipped > 0)
            text += (result.Skipped == 1 ? "  ·  1 fichier en cours d'utilisation ignoré" : $"  ·  {result.Skipped} fichiers en cours d'utilisation ignorés");
        return text;
    }
}
