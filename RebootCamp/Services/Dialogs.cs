namespace RebootCamp.Services;

/// <summary>Ce qu'une fenêtre de confirmation affiche.</summary>
/// <param name="Icon">Caractère de la police d'icônes Windows.</param>
/// <param name="Hint">Précision en gris sous le message (peut être vide).</param>
/// <param name="IsWarning">Vrai pour une mise en garde : l'icône et le cadre passent à l'orange.</param>
public sealed record ConfirmRequest(
    string Icon, string Title, string Message, string Hint, string ConfirmLabel, string CancelLabel, bool IsWarning = false);

/// <summary>
/// Permet aux ViewModels et aux services de poser une question à l'utilisateur sans connaître les fenêtres :
/// la fenêtre principale branche ici de quoi l'afficher. Sans fenêtre (tests), la réponse est toujours « non ».
/// </summary>
public static class Dialogs
{
    public static Func<ConfirmRequest, bool>? Show { get; set; }

    /// <returns>Vrai seulement si l'utilisateur a cliqué sur le bouton de confirmation.</returns>
    public static bool Confirm(ConfirmRequest request) => Show?.Invoke(request) ?? false;
}
