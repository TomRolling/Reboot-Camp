using System.Diagnostics;
using RebootCamp.Services;

namespace RebootCamp.Tests;

/// <summary>
/// Vérifie les règles de sécurité du nettoyage sur de faux dossiers créés pour l'occasion.
/// Chaque test fabrique son « bac à sable » dans le dossier temporaire, lance le vrai code de l'appli dessus,
/// regarde ce qui reste, puis efface le bac à sable. Aucun vrai fichier du PC n'est touché.
/// </summary>
public sealed class CleanupServiceTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "RebootCampTests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Le dossier que le nettoyage a le droit de vider.</summary>
    private readonly string _allowed;

    /// <summary>Un dossier voisin, hors liste blanche : rien ne doit y disparaître.</summary>
    private readonly string _outside;

    public CleanupServiceTests()
    {
        _allowed = Directory.CreateDirectory(Path.Combine(_sandbox, "autorise")).FullName;
        _outside = Directory.CreateDirectory(Path.Combine(_sandbox, "ailleurs")).FullName;
    }

    public void Dispose()
    {
        // Les jonctions sont retirées en premier, pour ne jamais supprimer « à travers » un lien.
        foreach (string directory in Directory.GetDirectories(_sandbox, "*", SearchOption.AllDirectories))
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(directory);
        Directory.Delete(_sandbox, recursive: true);
    }

    // ───────────────────────── Outils des tests ─────────────────────────

    /// <summary>Crée un fichier de 10 octets, daté de trois jours par défaut (donc « vieux » pour le nettoyage).</summary>
    private static string CreateFile(string folder, string name, bool old = true)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[10]);
        if (old) File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-3));
        return path;
    }

    /// <summary>Une catégorie de nettoyage qui vise nos faux dossiers au lieu des vrais.</summary>
    private static CleanupCategory Category(params CleanupTarget[] targets) =>
        new("Test", "Test", "", CleanupKind.Files) { GetTargets = () => [.. targets] };

    private static Task<CleanResult> Clean(CleanupCategory category, bool simulate = false) =>
        CleanupService.CleanAsync(category, simulate, new Progress<string>(), CancellationToken.None);

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link), "La jonction de test n'a pas pu être créée.");
    }

    // ───────────────────────── Liste blanche ─────────────────────────

    [Fact]
    public async Task Supprime_le_contenu_du_dossier_autorise_et_rien_ailleurs()
    {
        string inside = CreateFile(_allowed, "a.tmp");
        string deep = CreateFile(Path.Combine(_allowed, "sous", "dossier"), "b.tmp");
        string neighbour = CreateFile(_outside, "important.docx");

        var result = await Clean(Category(new CleanupTarget(_allowed)));

        Assert.False(File.Exists(inside));
        Assert.False(File.Exists(deep));
        Assert.True(File.Exists(neighbour));
        Assert.True(Directory.Exists(_allowed));   // le dossier de la liste blanche lui-même reste en place
        Assert.Equal(2, result.Deleted);
        Assert.Equal(20, result.Freed);
    }

    [Fact]
    public async Task Un_dossier_au_nom_presque_identique_n_est_pas_touche()
    {
        // « autorise-bis » commence comme « autorise » : il ne doit pas être confondu avec lui.
        string lookalike = CreateFile(Path.Combine(_sandbox, "autorise-bis"), "garde.txt");
        CreateFile(_allowed, "a.tmp");

        await Clean(Category(new CleanupTarget(_allowed)));

        Assert.True(File.Exists(lookalike));
    }

    [Fact]
    public async Task Une_jonction_n_est_jamais_suivie()
    {
        string precious = CreateFile(_outside, "photos.zip");
        string junction = Path.Combine(_allowed, "lien");
        CreateJunction(junction, _outside);
        CreateFile(_allowed, "a.tmp");

        var result = await Clean(Category(new CleanupTarget(_allowed)));

        Assert.True(File.Exists(precious));        // le fichier derrière le lien est intact
        Assert.True(Directory.Exists(junction));   // et le lien lui-même n'est pas supprimé
        Assert.Equal(1, result.Deleted);
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.System)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    [InlineData(Environment.SpecialFolder.LocalApplicationData)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData)]
    public void Un_dossier_systeme_entier_est_refuse(Environment.SpecialFolder folder)
    {
        // On utilise l'analyse (qui ne supprime jamais rien) : si le garde-fou marche, elle ne trouve aucun fichier.
        var result = CleanupService.Scan(Category(new CleanupTarget(Environment.GetFolderPath(folder))), CancellationToken.None);

        Assert.Equal(0, result.FileCount);
    }

    [Fact]
    public void La_racine_d_un_disque_est_refusee()
    {
        string drive = Path.GetPathRoot(Environment.SystemDirectory)!;

        var result = CleanupService.Scan(Category(new CleanupTarget(drive)), CancellationToken.None);

        Assert.Equal(0, result.FileCount);
    }

    [Fact]
    public async Task Sans_recursion_il_faut_un_filtre_precis()
    {
        string file = CreateFile(_allowed, "a.tmp");

        // « tous les fichiers » + « non récursif » : combinaison refusée, rien n'est supprimé.
        var result = await Clean(Category(new CleanupTarget(_allowed, "*", Recursive: false)));

        Assert.True(File.Exists(file));
        Assert.Equal(0, result.Deleted);
    }

    [Fact]
    public async Task Un_filtre_ne_supprime_que_les_fichiers_qui_correspondent()
    {
        // Même réglage que le cache des miniatures : « thumbcache_*.db », sans descendre dans les sous-dossiers.
        string thumb = CreateFile(_allowed, "thumbcache_256.db");
        string other = CreateFile(_allowed, "iconcache.db");
        string nested = CreateFile(Path.Combine(_allowed, "sous"), "thumbcache_32.db");

        await Clean(Category(new CleanupTarget(_allowed, "thumbcache_*.db", Recursive: false)));

        Assert.False(File.Exists(thumb));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(nested));
    }

    // ───────────────────────── Règle des 24 heures ─────────────────────────

    [Fact]
    public async Task Les_fichiers_de_moins_de_24_h_sont_gardes()
    {
        string oldFile = CreateFile(_allowed, "vieux.tmp");
        string recent = CreateFile(_allowed, "recent.tmp", old: false);

        var result = await Clean(Category(new CleanupTarget(_allowed, OldOnly: true)));

        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(recent));
        Assert.Equal(1, result.Deleted);
    }

    [Fact]
    public async Task Un_dossier_qui_contient_encore_un_fichier_recent_reste_en_place()
    {
        string folder = Path.Combine(_allowed, "programme");
        CreateFile(folder, "vieux.tmp");
        string recent = CreateFile(folder, "recent.tmp", old: false);

        await Clean(Category(new CleanupTarget(_allowed, OldOnly: true)));

        Assert.True(Directory.Exists(folder));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task Le_dossier_point_net_du_dossier_temporaire_est_epargne()
    {
        // %TEMP%\.net contient ce dont les applis .NET (dont Reboot Camp) ont besoin pour démarrer.
        string runtime = CreateFile(Path.Combine(_allowed, ".net", "RebootCamp"), "coreclr.dll");

        await Clean(Category(new CleanupTarget(_allowed)));

        Assert.True(File.Exists(runtime));
    }

    // ───────────────────────── Simulation, analyse, fichiers verrouillés ─────────────────────────

    [Fact]
    public async Task La_simulation_compte_mais_ne_supprime_rien()
    {
        string file = CreateFile(_allowed, "a.tmp");
        string folder = Path.Combine(_allowed, "sous");
        string nested = CreateFile(folder, "b.tmp");

        var result = await Clean(Category(new CleanupTarget(_allowed)), simulate: true);

        Assert.True(File.Exists(file));
        Assert.True(File.Exists(nested));
        Assert.True(Directory.Exists(folder));
        Assert.Equal(2, result.Deleted);   // ce qui *serait* supprimé
        Assert.Equal(20, result.Freed);
    }

    [Fact]
    public void L_analyse_mesure_sans_rien_supprimer()
    {
        string file = CreateFile(_allowed, "a.tmp");

        var result = CleanupService.Scan(Category(new CleanupTarget(_allowed)), CancellationToken.None);

        Assert.True(File.Exists(file));
        Assert.Equal(1, result.FileCount);
        Assert.Equal(10, result.Size);
    }

    [Fact]
    public async Task Un_fichier_verrouille_est_ignore_sans_planter()
    {
        string locked = CreateFile(_allowed, "ouvert.tmp");
        string free = CreateFile(_allowed, "libre.tmp");

        CleanResult result;
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))   // simule un programme qui s'en sert
            result = await Clean(Category(new CleanupTarget(_allowed)));

        Assert.True(File.Exists(locked));
        Assert.False(File.Exists(free));
        Assert.Equal(1, result.Deleted);
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public async Task Un_dossier_absent_ne_fait_pas_planter()
    {
        var result = await Clean(Category(new CleanupTarget(Path.Combine(_sandbox, "nexiste", "pas"))));

        Assert.Equal(0, result.Deleted);
    }

    // ───────────────────────── La vraie liste blanche de l'appli ─────────────────────────

    [Fact]
    public void Les_navigateurs_ne_visent_que_des_dossiers_de_cache()
    {
        string[] cacheFolders = ["Cache", "Code Cache", "GPUCache", "cache2"];

        foreach (var category in CleanupService.CreateCategories().Where(c => c.BlockingProcess is not null))
            foreach (var target in category.GetTargets())
            {
                // Jamais le profil entier (cookies, historique, mots de passe) : uniquement un sous-dossier de cache.
                Assert.Contains(Path.GetFileName(target.Root), cacheFolders);
                Assert.True(target.Recursive);
            }
    }

    [Fact]
    public void Seuls_les_fichiers_temporaires_et_la_corbeille_peuvent_contenir_des_fichiers_personnels()
    {
        // Les deux dossiers temporaires sont les seuls endroits où un programme ouvert peut encore travailler :
        // ils doivent garder la protection « plus de 24 h ».
        var temp = CleanupService.CreateCategories().Single(c => c.Name == "Fichiers temporaires");

        Assert.All(temp.GetTargets(), target => Assert.True(target.OldOnly));
    }

    [Fact]
    public void L_option_longue_DISM_est_decochee_par_defaut()
    {
        var dism = CleanupService.CreateCategories().Single(c => c.Kind == CleanupKind.Dism);

        Assert.False(dism.SelectedByDefault);
    }
}
