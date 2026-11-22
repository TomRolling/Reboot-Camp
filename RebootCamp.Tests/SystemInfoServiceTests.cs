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
        Assert.Equal("ASUSTeK COMPUTER INC. VivoBook 15 X1504",
            SystemInfoService.ComputerName("ASUSTeK COMPUTER INC.", "VivoBook 15 X1504", "", ""));
    }

    [Fact]
    public void Un_PC_assemble_affiche_sa_carte_mere()
    {
        Assert.Equal("PC assemblé (carte mère ASUS ROG STRIX Z790-E GAMING WIFI)",
            SystemInfoService.ComputerName("ASUS", "System Product Name", "ASUSTeK COMPUTER INC.", "ROG STRIX Z790-E GAMING WIFI"));
    }

    [Fact]
    public void Un_PC_assemble_sans_carte_mere_connue_reste_lisible()
    {
        Assert.Equal("PC assemblé", SystemInfoService.ComputerName("System manufacturer", "System Product Name", "", "Default string"));
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

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2782", true)]
    [InlineData(@"ROOT\DISPLAY\0000", false)]
    public void Seules_les_cartes_branchees_sur_le_bus_PCI_sont_de_vraies_cartes(string pnpDeviceId, bool expected)
    {
        Assert.Equal(expected, HardwareService.IsPhysicalCard(pnpDeviceId));
    }
}
