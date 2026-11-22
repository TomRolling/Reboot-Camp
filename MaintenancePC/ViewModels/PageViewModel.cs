using CommunityToolkit.Mvvm.ComponentModel;

namespace MaintenancePC.ViewModels;

/// <summary>
/// Classe de base d'un onglet. La barre latérale affiche <see cref="Icon"/> et <see cref="Title"/>.
/// </summary>
/// <param name="title">Nom affiché dans la barre latérale et en haut de la page.</param>
/// <param name="icon">Caractère de la police d'icônes Windows (Segoe Fluent Icons / Segoe MDL2 Assets).</param>
/// <param name="description">Phrase courte expliquant à quoi sert l'onglet.</param>
public abstract class PageViewModel(string title, string icon, string description) : ObservableObject
{
    public string Title { get; } = title;
    public string Icon { get; } = icon;
    public string Description { get; } = description;
}

// Les cinq onglets. Ils sont vides pour l'instant : chaque phase remplira le sien
// (et il sera alors déplacé dans son propre fichier).

public sealed class AccueilViewModel() : PageViewModel(
    "Accueil", "", "Résumé de l'état du PC. Rien n'est analysé tant que tu ne le demandes pas.");

public sealed class PilotesViewModel() : PageViewModel(
    "Pilotes", "", "Liste des pilotes, périphériques en erreur et mises à jour via Windows Update.");

public sealed class NettoyageViewModel() : PageViewModel(
    "Nettoyage", "", "Suppression des fichiers temporaires et des caches, avec aperçu avant suppression.");

public sealed class ComposantsViewModel() : PageViewModel(
    "Composants", "", "DirectX, Visual C++ et .NET : détection et installation via winget.");

public sealed class ExtrasViewModel() : PageViewModel(
    "Extras", "", "Démarrage, infos système, santé du disque, réparation et point de restauration.");
