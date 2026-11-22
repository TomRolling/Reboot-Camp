using RebootCamp.Services;

namespace RebootCamp.Tests;

/// <summary>Vérifie la mise en forme du nom du PC et de la marque de la carte mère (onglet Extras et rapport).</summary>
public sealed class SystemInfoServiceTests
{
    [Theory]
    [InlineData("System Product Name")]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("Default string")]
    [InlineData("")]
    public void Les_textes_par_defaut_des_cartes_meres_sont_reconnus(string model)
    {
        Assert.True(SystemInfoService.IsPlaceholder(model));
    }

    [Fact]
    public void Un_vrai_modele_est_affiche_tel_quel()
    {
        Assert.False(SystemInfoService.IsPlaceholder("VivoBook 15 X1504"));
        Assert.Equal("ASUSTeK COMPUTER INC. VivoBook 15 X1504", SystemInfoService.ComputerName("ASUSTeK COMPUTER INC.", "VivoBook 15 X1504"));
    }

    [Theory]
    [InlineData("ASUSTeK COMPUTER INC.", "ASUS")]
    [InlineData("Micro-Star International Co., Ltd.", "MSI")]
    [InlineData("Gigabyte Technology Co., Ltd.", "Gigabyte")]
    [InlineData("ASRock", "ASRock")]
    [InlineData("Dell Inc.", "Dell")]
    public void La_marque_est_raccourcie(string manufacturer, string expected)
    {
        Assert.Equal(expected, SystemInfoService.ShortBrand(manufacturer));
    }
}
