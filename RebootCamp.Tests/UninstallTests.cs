using System.Diagnostics;
using RebootCamp.Models;
using RebootCamp.Services;

namespace RebootCamp.Tests;

/// <summary>
/// Vérifie l'onglet Désinstaller sans jamais rien désinstaller : découpage des commandes, mentions « à garder »,
/// et surtout les règles de sécurité de la recherche de traces, sur de faux dossiers créés pour l'occasion.
/// </summary>
public sealed class UninstallTests : IDisposable
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "RebootCampTests-" + Guid.NewGuid().ToString("N"));
    private readonly string _programFiles, _appData, _startMenu, _desktop, _windows, _elsewhere;
    private readonly LeftoverRoots _roots;

    public UninstallTests()
    {
        string Make(string name) => Directory.CreateDirectory(Path.Combine(_sandbox, name)).FullName;
        _programFiles = Make("Program Files");
        _appData = Make("AppData");
        _startMenu = Make("Menu");
        _desktop = Make("Bureau");
        _windows = Make("Windows");
        _elsewhere = Make("Jeux");
        _roots = new LeftoverRoots([_programFiles], [_appData], [_startMenu], [_desktop],
            Protected: [Path.Combine(_sandbox, "Profil")], WindowsFolder: _windows);
    }

    public void Dispose()
    {
        foreach (string directory in Directory.GetDirectories(_sandbox, "*", SearchOption.AllDirectories))
            if (Directory.Exists(directory) && File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                Directory.Delete(directory);
        Directory.Delete(_sandbox, recursive: true);
    }

    private static InstalledProgram Program(string name, string location = "", string publisher = "") =>
        new(name, publisher, "1.0", 0, null, ProgramKind.Desktop, location, "", false, "HKLM64|" + name);

    private static InstalledProgram WindowsApp(string package) =>
        new("App", "", "1.0", 0, null, ProgramKind.WindowsApp, "", "", true, package + "_1.0.0.0_x64__8wekyb3d8bbwe");

    private static string CreateFile(string folder, string name)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[10]);
        return path;
    }

    private List<Leftover> Find(InstalledProgram program, params InstalledProgram[] stillInstalled) =>
        LeftoverService.Find(program, stillInstalled, _roots, CancellationToken.None);

    // ───────────────────────── Commandes de désinstallation ─────────────────────────

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\unins000.exe\" /SILENT", "C:\\Program Files\\App\\unins000.exe", "/SILENT")]
    [InlineData("C:\\Program Files\\Mon App\\uninstall.exe /S", "C:\\Program Files\\Mon App\\uninstall.exe", "/S")]
    [InlineData("\"C:\\App\\remove.exe\"", "C:\\App\\remove.exe", "")]
    public void Une_commande_est_decoupee_en_programme_et_options(string command, string file, string arguments)
    {
        Assert.Equal((file, arguments), UninstallService.SplitCommand(command));
    }

    [Fact]
    public void Une_commande_msiexec_desinstalle_au_lieu_d_ouvrir_l_ecran_de_modification()
    {
        var (file, arguments) = UninstallService.SplitCommand("MsiExec.exe /I{11111111-2222-3333-4444-555555555555}");

        Assert.EndsWith("msiexec.exe", file, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("/X{11111111-2222-3333-4444-555555555555}", arguments);
        Assert.Contains("/norestart", arguments);   // jamais de redémarrage décidé par l'installeur
    }

    [Theory]
    [InlineData("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond", "Microsoft Corporation")]
    [InlineData("CN=\"NVIDIA Corp, Inc.\", O=NVIDIA", "NVIDIA Corp, Inc.")]
    [InlineData("CN=8D2BB5B5-0A0A-4F0B-9F0C-0123456789AB", "")]
    public void Le_nom_de_l_editeur_d_une_application_Windows_est_lisible(string raw, string expected)
    {
        Assert.Equal(expected, UninstallService.PublisherName(raw));
    }

    // ───────────────────────── Mentions « à garder » ─────────────────────────

    [Theory]
    [InlineData("Microsoft.WindowsStore", Status.Error)]
    [InlineData("Microsoft.DesktopAppInstaller", Status.Error)]
    [InlineData("NVIDIACorp.NVIDIAControlPanel", Status.Warning)]
    [InlineData("Microsoft.GamingServices", Status.Warning)]
    public void Les_applications_Windows_sensibles_ont_une_mention(string package, Status level)
    {
        var app = WindowsApp(package);

        Assert.Equal(level, app.AdviceStatus);
        Assert.NotEmpty(app.Advice);
    }

    [Fact]
    public void Une_application_ordinaire_n_a_pas_de_mention()
    {
        Assert.Equal(Status.None, WindowsApp("Microsoft.WindowsCalculator").AdviceStatus);
        Assert.Equal(Status.None, Program("VLC media player").AdviceStatus);
        Assert.Equal(Status.None, Program("Microsoft .NET SDK 10.0.404 (x64)").AdviceStatus);   // outil de développement
        Assert.Equal(Status.Warning, Program("Microsoft .NET Runtime - 8.0.34 (x64)").AdviceStatus);
    }

    [Theory]
    [InlineData("Microsoft Edge WebView2 Runtime", Status.Error)]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44", Status.Warning)]
    [InlineData("NVIDIA Graphics Driver 621.24", Status.Warning)]
    public void Les_programmes_dont_d_autres_dependent_ont_une_mention(string name, Status level)
    {
        Assert.Equal(level, Program(name).AdviceStatus);
    }

    // ───────────────────────── Noms de dossier possibles ─────────────────────────

    [Fact]
    public void Le_nom_sans_version_ni_parentheses_est_aussi_essaye()
    {
        Assert.Equal(["7-Zip 24.08 (x64)", "7-Zip 24.08", "7-Zip"], LeftoverService.CandidateNames("7-Zip 24.08 (x64)"));
    }

    [Theory]
    [InlineData("Microsoft")]
    [InlineData("Windows")]
    [InlineData("ab")]
    public void Un_nom_trop_general_ou_trop_court_ne_designe_aucun_dossier(string name)
    {
        Assert.Empty(LeftoverService.CandidateNames(name));
    }

    // ───────────────────────── Recherche des traces ─────────────────────────

    [Fact]
    public void Les_dossiers_au_nom_du_programme_sont_trouves_aux_endroits_connus()
    {
        CreateFile(Path.Combine(_programFiles, "SuperApp"), "app.dll");
        CreateFile(Path.Combine(_appData, "SuperApp"), "reglages.ini");
        CreateFile(Path.Combine(_startMenu, "SuperApp"), "SuperApp.lnk");
        CreateFile(_desktop, "SuperApp.lnk");
        CreateFile(Path.Combine(_programFiles, "AutreApp"), "autre.dll");

        var found = Find(Program("SuperApp 2.1"));

        Assert.Equal(4, found.Count);
        Assert.DoesNotContain(found, l => l.Path.Contains("AutreApp"));
        Assert.All(found, l => Assert.True(l.IsSelected));
    }

    [Fact]
    public void Le_dossier_d_installation_declare_est_trouve_meme_ailleurs()
    {
        string location = Path.Combine(_elsewhere, "Studio", "MonJeu");
        CreateFile(location, "jeu.exe");

        var found = Find(Program("Mon Jeu", location));

        Assert.Single(found);
        Assert.Equal(location, found[0].Path);
        Assert.Equal(10, found[0].Size);
    }

    [Fact]
    public void Un_dossier_general_n_est_jamais_propose()
    {
        // Le programme prétend être installé dans « Program Files » lui-même, puis dans le dossier de Windows.
        Assert.Empty(Find(Program("Mechant", _programFiles)));
        Assert.Empty(Find(Program("Mechant", Path.Combine(_windows, "System32"))));
        Assert.Empty(Find(Program("Mechant", Path.GetPathRoot(_sandbox)!)));
    }

    [Fact]
    public void Un_dossier_declare_directement_a_la_racine_d_un_disque_n_est_pas_propose()
    {
        // « D:\Jeux » : trop large pour être le dossier d'un seul programme.
        string root = Path.GetPathRoot(_sandbox)!;
        var fake = new LeftoverRoots([_programFiles], [_appData], [_startMenu], [_desktop], [], _windows);

        Assert.False(LeftoverService.IsSafeFolder(Path.Combine(root, "Jeux"), fake, [], declared: true));
    }

    [Fact]
    public void Un_dossier_qui_contient_un_autre_programme_encore_installe_est_epargne()
    {
        string shared = Path.Combine(_elsewhere, "Editeur", "Suite");
        CreateFile(shared, "commun.dll");
        string other = Path.Combine(shared, "Module");
        CreateFile(other, "module.exe");

        var found = Find(Program("Suite", shared), Program("Un autre logiciel", other));

        Assert.Empty(found);
    }

    [Fact]
    public void Un_dossier_au_nom_partage_avec_une_autre_version_installee_est_epargne()
    {
        CreateFile(Path.Combine(_programFiles, "Python"), "python.exe");

        // « Python 3.13 » est toujours là : le dossier « Python » peut être le sien.
        var found = Find(Program("Python 3.14"), Program("Python 3.13"));

        Assert.Empty(found);
    }

    // ───────────────────────── Suppression des traces ─────────────────────────

    [Fact]
    public void Seul_ce_qui_est_coche_est_supprime()
    {
        string folder = Path.Combine(_programFiles, "SuperApp");
        CreateFile(Path.Combine(folder, "sous"), "a.dll");
        string data = Path.Combine(_appData, "SuperApp");
        CreateFile(data, "sauvegarde.dat");

        var found = Find(Program("SuperApp"));
        found.Single(l => l.Path == data).IsSelected = false;   // on garde les données
        var result = LeftoverService.Delete(found, CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(Path.Combine(data, "sauvegarde.dat")));
        Assert.Equal(1, result.Deleted);
        Assert.Equal(0, result.AtNextRestart);
    }

    [Fact]
    public void Une_jonction_dans_un_dossier_supprime_ne_touche_pas_ce_qu_il_y_a_derriere()
    {
        string folder = Path.Combine(_programFiles, "SuperApp");
        CreateFile(folder, "app.dll");
        string precious = CreateFile(Path.Combine(_elsewhere, "Photos"), "vacances.jpg");
        string junction = Path.Combine(folder, "lien");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{Path.GetDirectoryName(precious)}\"")
               { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!)
            process.WaitForExit();
        Assert.True(Directory.Exists(junction), "La jonction de test n'a pas pu être créée.");

        LeftoverService.Delete(Find(Program("SuperApp")), CancellationToken.None);

        Assert.False(Directory.Exists(folder));
        Assert.True(File.Exists(precious));
    }
}
