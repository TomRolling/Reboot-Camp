using RebootCamp.Services;

namespace RebootCamp.Tests;

/// <summary>Vérifie la recherche des icônes de programmes (sans rien afficher).</summary>
public sealed class IconServiceTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "RebootCampTests-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Theory]
    [InlineData(@"C:\App\app.exe,2", @"C:\App\app.exe", 2)]
    [InlineData("\"C:\\Program Files\\App\\app.exe\",0", @"C:\Program Files\App\app.exe", 0)]
    [InlineData(@"C:\App\icone.ico", @"C:\App\icone.ico", 0)]
    [InlineData(@"C:\App\lib.dll,-101", @"C:\App\lib.dll", -101)]
    public void L_emplacement_d_une_icone_est_separe_de_son_numero(string location, string path, int index)
    {
        Assert.Equal((path, index), IconService.SplitIconLocation(location));
    }

    [Fact]
    public void Le_logo_sans_fond_prevu_pour_les_listes_est_prefere()
    {
        foreach (string name in (string[])["Logo.scale-100.png", "Logo.scale-200.png", "Logo.targetsize-48_altform-unplated.png", "Logo.targetsize-16.png"])
            File.WriteAllBytes(Path.Combine(_folder, name), [0]);

        string? best = IconService.BestLogoFile(Path.Combine(_folder, "Logo.png"));

        Assert.EndsWith("Logo.targetsize-48_altform-unplated.png", best);
    }

    [Fact]
    public void Un_logo_d_un_autre_nom_n_est_pas_confondu()
    {
        File.WriteAllBytes(Path.Combine(_folder, "LogoLarge.scale-200.png"), [0]);

        Assert.Null(IconService.BestLogoFile(Path.Combine(_folder, "Logo.png")));
    }

    [Fact]
    public void Un_fichier_absent_ou_d_un_autre_type_ne_donne_pas_d_icone()
    {
        Assert.Null(IconService.ForFile(Path.Combine(_folder, "absent.exe")));
        Assert.Null(IconService.ForFile(@"C:\Windows\win.ini"));
        Assert.Null(IconService.ForFile(""));
    }
}
