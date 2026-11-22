using RebootCamp.Services;

namespace RebootCamp.Tests;

/// <summary>Vérifie la traduction des codes de sortie de winget, en particulier ceux qui parlent de redémarrage.</summary>
public sealed class WingetTests
{
    [Theory]
    [InlineData(0u, WingetOutcome.Done)]
    [InlineData(0x8A15002B, WingetOutcome.AlreadyUpToDate)]    // déjà à jour
    [InlineData(0x8A150109, WingetOutcome.DoneAfterRestart)]   // redémarrer pour terminer l'installation
    [InlineData(0x8A15010B, WingetOutcome.DoneAfterRestart)]   // l'installeur veut redémarrer
    [InlineData(0x8A15010A, WingetOutcome.RestartThenRetry)]   // redémarrer puis réessayer : ce n'est pas une réussite
    [InlineData(0x8A15008E, WingetOutcome.Failed)]             // autre technologie d'installation
    [InlineData(1u, WingetOutcome.Failed)]
    public void Chaque_code_de_winget_est_bien_traduit(uint code, WingetOutcome expected)
    {
        Assert.Equal(expected, Winget.Interpret(unchecked((int)code)));
    }

    [Theory]
    [InlineData(0x8A150011)]   // fichier d'installation différent de celui attendu
    [InlineData(0x8A15008E)]   // autre technologie d'installation
    public void Les_mises_a_jour_que_winget_ne_peut_pas_faire_sont_expliquees_et_marquees_bloquees(uint code)
    {
        Assert.True(Winget.IsBlocked(unchecked((int)code)));
        Assert.NotNull(Winget.Explain(unchecked((int)code)));
    }

    [Fact]
    public void Un_logiciel_ouvert_est_explique_mais_peut_etre_retente()
    {
        int code = unchecked((int)0x8A150101);

        Assert.False(Winget.IsBlocked(code));
        Assert.Contains("ferme-le", Winget.Explain(code));
    }

    [Fact]
    public void Un_code_inconnu_n_a_pas_d_explication_inventee()
    {
        Assert.Null(Winget.Explain(1));
        Assert.False(Winget.IsBlocked(1));
    }
}
