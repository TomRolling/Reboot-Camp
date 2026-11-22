using RebootCamp.Services;

namespace RebootCamp.Tests;

/// <summary>Vérifie le carnet de l'onglet Logiciels (ce qui a déjà été fait, ce que winget ne peut pas faire).</summary>
public sealed class UpdateMemoryTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "RebootCampTests-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose() => File.Delete(_file);

    [Fact]
    public void Une_mise_a_jour_reussie_est_retenue_d_une_ouverture_a_l_autre()
    {
        new UpdateMemory(_file).Remember("Moonsworth.LunarClient", new UpdateNote("3.7.28", Blocked: false));

        var reopened = new UpdateMemory(_file);

        Assert.NotNull(reopened.Find("moonsworth.lunarclient", "3.7.28"));   // sans tenir compte des majuscules
    }

    [Fact]
    public void Une_note_ne_vaut_plus_quand_winget_propose_une_autre_version()
    {
        var memory = new UpdateMemory(_file);
        memory.Remember("Ankama.AnkamaLauncher", new UpdateNote("3.15.15", Blocked: true, "empreinte différente"));

        Assert.NotNull(memory.Find("Ankama.AnkamaLauncher", "3.15.15"));
        Assert.Null(memory.Find("Ankama.AnkamaLauncher", "3.16.0"));   // nouvelle version : on réessaiera
    }

    [Fact]
    public void Reafficher_oublie_les_reussites_mais_garde_les_blocages()
    {
        var memory = new UpdateMemory(_file);
        memory.Remember("A", new UpdateNote("1.0", Blocked: false));
        memory.Remember("B", new UpdateNote("2.0", Blocked: true, "raison", CanReinstall: true));

        memory.ForgetDone();

        Assert.Null(memory.Find("A", "1.0"));
        Assert.True(memory.Find("B", "2.0")!.CanReinstall);
    }

    [Fact]
    public void Un_fichier_abime_donne_un_carnet_vide_sans_planter()
    {
        File.WriteAllText(_file, "{ ceci n'est pas du json");

        Assert.Null(new UpdateMemory(_file).Find("A", "1.0"));
    }

    [Fact]
    public void Seule_une_installation_differente_peut_etre_debloquee_par_une_reinstallation()
    {
        Assert.True(Winget.CanReinstall(unchecked((int)0x8A15008E)));    // autre technologie d'installation
        Assert.False(Winget.CanReinstall(unchecked((int)0x8A150011)));   // fichier d'installation non vérifié
    }
}
