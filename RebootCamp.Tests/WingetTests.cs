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
}
