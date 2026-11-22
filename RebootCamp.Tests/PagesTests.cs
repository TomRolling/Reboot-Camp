using RebootCamp.ViewModels;

namespace RebootCamp.Tests;

/// <summary>Garde-fous sur les onglets de la barre latérale.</summary>
public sealed class PagesTests
{
    [Fact]
    public void Chaque_onglet_a_un_titre_et_une_icone()
    {
        // Les icônes sont des caractères invisibles de la police d'icônes Windows : faciles à effacer sans le voir.
        foreach (var page in new MainViewModel().Pages)
        {
            Assert.False(string.IsNullOrWhiteSpace(page.Title));
            Assert.True(page.Icon.Length == 1 && page.Icon[0] >= '' && page.Icon[0] <= '',
                $"L'onglet « {page.Title} » n'a pas d'icône.");
        }
    }

    [Fact]
    public void Chaque_onglet_a_son_propre_libelle_sur_le_bouton_rond()
    {
        var labels = new MainViewModel().Pages
            .Where(p => p is not ExtrasViewModel)   // Extras n'a pas de bouton rond
            .Select(p => $"{p.RingText} {p.RingCaption}")
            .ToList();

        Assert.Equal(labels.Count, labels.Distinct().Count());
    }
}
