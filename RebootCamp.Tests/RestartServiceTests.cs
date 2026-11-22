using RebootCamp.Services;

namespace RebootCamp.Tests;

/// <summary>Vérifie que les codes de winget qui parlent de redémarrage sont bien reconnus (et seulement eux).</summary>
public sealed class RestartServiceTests
{
    [Theory]
    [InlineData(0x8A150109)]   // redémarrer pour terminer l'installation
    [InlineData(0x8A15010A)]   // redémarrer puis réessayer
    [InlineData(0x8A15010B)]   // l'installeur veut redémarrer
    public void Les_codes_de_redemarrage_de_winget_sont_reconnus(uint code)
    {
        Assert.True(RestartService.IsRebootCode(unchecked((int)code)));
    }

    [Theory]
    [InlineData(0u)]           // réussite simple
    [InlineData(0x8A15002B)]   // déjà à jour
    [InlineData(0x8A15008E)]   // autre technologie d'installation
    public void Les_autres_codes_ne_demandent_pas_de_redemarrage(uint code)
    {
        Assert.False(RestartService.IsRebootCode(unchecked((int)code)));
    }

    [Fact]
    public void Redemarrer_puis_reessayer_n_est_pas_une_reussite()
    {
        Assert.False(RestartService.IsSuccessPendingReboot(unchecked((int)0x8A15010A)));
        Assert.True(RestartService.IsSuccessPendingReboot(unchecked((int)0x8A150109)));
    }
}
