using System.IO.Compression;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Le rapport qu'un poste envoie quand quelque chose ne va pas.
///
/// Les journaux sont ce qui a permis de trouver le liseré blanc et le « Pipe is broken » —
/// mais ils étaient sur le poste de la boutique, sous la main. Sur celui d'un collègue,
/// personne ne va lire <c>D:\PhotoStudioData\logs</c>.
///
/// <b>Ce que ces essais protègent avant tout : qu'aucun SECRET ne parte.</b>
/// <c>mail.json</c> contient le mot de passe d'application de la boîte du magasin et
/// <c>dropbox.json</c> le jeton d'accès — les joindre les enverrait en clair, par courriel,
/// à chaque rapport.
/// </summary>
public class RapportDiagnosticTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "Rapport-" + Guid.NewGuid().ToString("N"));

    private string Logs => Path.Combine(_racine, "logs");
    private string Config => Path.Combine(_racine, "config");
    private string Archive => Path.Combine(_racine, "rapport.zip");

    public RapportDiagnosticTests()
    {
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Config);
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
    }

    private void Journal(string nom, string contenu, int ageEnJours = 0)
    {
        var chemin = Path.Combine(Logs, nom);
        File.WriteAllText(chemin, contenu);
        File.SetLastWriteTime(chemin, DateTime.Now.AddDays(-ageEnJours));
    }

    private static List<string> Entrees(string archive)
    {
        using var zip = ZipFile.OpenRead(archive);
        return [.. zip.Entries.Select(e => e.FullName)];
    }

    private static string Lire(string archive, string entree)
    {
        using var zip = ZipFile.OpenRead(archive);
        using var flux = zip.GetEntry(entree)!.Open();
        using var lecteur = new StreamReader(flux);
        return lecteur.ReadToEnd();
    }

    // ————— ce qui ne doit JAMAIS partir —————

    [Theory]
    [InlineData("mail.json")]
    [InlineData("dropbox.json")]
    [InlineData("wifi.json")]
    [InlineData("mail.json.bak")]
    [InlineData("dropbox.json.bak")]
    public void Les_reglages_qui_portent_un_secret_sont_ecartes(string nom)
    {
        Assert.True(RapportDiagnostic.EstSensible(nom), $"{nom} doit être tenu pour sensible");
    }

    [Theory]
    [InlineData("poste.json")]
    [InlineData("detourage.json")]
    [InlineData("consommables.json")]
    [InlineData("debits.json")]
    public void Les_reglages_sans_secret_sont_repris(string nom)
    {
        Assert.False(RapportDiagnostic.EstSensible(nom));
    }

    /// <summary>L'essai qui compte : sur un dossier réel, le mot de passe ne part pas.</summary>
    [Fact]
    public void Le_mot_de_passe_du_magasin_ne_se_retrouve_pas_dans_l_archive()
    {
        File.WriteAllText(Path.Combine(Config, "mail.json"),
            """{ "MotDePasseApplication": "abcd efgh ijkl mnop" }""");
        File.WriteAllText(Path.Combine(Config, "dropbox.json"),
            """{ "Jeton": "sl.u.SECRET-A-NE-PAS-ENVOYER" }""");
        File.WriteAllText(Path.Combine(Config, "poste.json"),
            """{ "DiLandRacine": "D:\\DiLand" }""");

        RapportDiagnostic.Fabriquer(Logs, Config, Archive);

        var entrees = Entrees(Archive);

        Assert.DoesNotContain("config/mail.json", entrees);
        Assert.DoesNotContain("config/dropbox.json", entrees);
        Assert.Contains("config/poste.json", entrees);
    }

    // ————— ce que le rapport emporte —————

    [Fact]
    public void Les_journaux_recents_sont_repris_et_les_vieux_laisses()
    {
        Journal("app-recent.log", "ligne du jour");
        Journal("app-hier.log", "ligne d'hier", ageEnJours: 2);
        Journal("app-vieux.log", "ligne d'un autre mois", ageEnJours: 40);

        var contenu = RapportDiagnostic.Fabriquer(Logs, Config, Archive);
        var entrees = Entrees(Archive);

        Assert.Contains("logs/app-recent.log", entrees);
        Assert.Contains("logs/app-hier.log", entrees);
        Assert.DoesNotContain("logs/app-vieux.log", entrees);
        Assert.Contains("app-recent.log", contenu.Fichiers);
    }

    /// <summary>
    /// La première question devant un défaut est toujours « sur quelle version ? ». Le
    /// rapport doit donc porter la version et le poste sans qu'on ait à les demander.
    /// </summary>
    [Fact]
    public void Le_rapport_dit_le_poste_et_la_version()
    {
        RapportDiagnostic.Fabriquer(Logs, Config, Archive);

        var entete = Lire(Archive, "rapport.txt");

        Assert.Contains(Environment.MachineName, entete);
        Assert.Contains("Version", entete);
        Assert.Contains("Imprimantes vues par Windows", entete);
    }

    /// <summary>Ce que l'opérateur a écrit doit s'y retrouver : c'est ce qu'on lit d'abord.</summary>
    [Fact]
    public void Le_mot_de_l_operateur_est_repris()
    {
        RapportDiagnostic.Fabriquer(Logs, Config, Archive,
            note: "Les 13x18 sortent avec un liseré depuis ce matin");

        Assert.Contains("liseré depuis ce matin", Lire(Archive, "rapport.txt"));
    }

    /// <summary>
    /// <b>Le journal du jour est ouvert par l'application elle-même.</b> Sans partage en
    /// écriture, le rapport échouerait sur le seul fichier qui compte — celui où vient de
    /// s'écrire le défaut qu'on veut signaler.
    /// </summary>
    [Fact]
    public void Un_journal_en_cours_d_ecriture_est_quand_meme_repris()
    {
        var chemin = Path.Combine(Logs, "app-ouvert.log");
        File.WriteAllText(chemin, "première ligne");

        using (var _ = new FileStream(chemin, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            RapportDiagnostic.Fabriquer(Logs, Config, Archive);
        }

        Assert.Contains("logs/app-ouvert.log", Entrees(Archive));
    }

    /// <summary>Un poste sans journal ne doit pas faire échouer l'envoi : le reste renseigne déjà.</summary>
    [Fact]
    public void Un_poste_sans_journal_produit_quand_meme_un_rapport()
    {
        var contenu = RapportDiagnostic.Fabriquer(Logs, Config, Archive);

        Assert.True(File.Exists(Archive));
        Assert.Contains("rapport.txt", Entrees(Archive));
        Assert.True(contenu.Octets > 0);
    }

    /// <summary>Deux rapports du même poste ne doivent pas porter le même nom.</summary>
    [Fact]
    public void Le_nom_propose_porte_le_poste_et_la_date()
    {
        var nom = RapportDiagnostic.NomPropose();

        Assert.Contains(Environment.MachineName, nom);
        Assert.EndsWith(".zip", nom);
    }

    // ————— le catalogue —————

    /// <summary>
    /// Le catalogue doit partir avec le rapport : c'est LE fichier qui décide où une
    /// commande s'imprime.
    ///
    /// Il manquait, et cela a coûté cher. Le 12/08/2026, le poste DESKTOP-KT88VDM
    /// n'imprimait ni sur sa DNP ni sur son DE100 ; son rapport portait les journaux et les
    /// réglages, mais pas <c>products.json</c>. Il a fallu déduire le catalogue d'une taille
    /// de rendu — 1205 × 1795 px, soit 102 × 152 mm, soit le produit d'amorçage — pour
    /// comprendre que tout partait dans « Microsoft Print to PDF ».
    /// </summary>
    [Fact]
    public void Le_catalogue_part_avec_le_rapport()
    {
        var catalogue = Path.Combine(_racine, "catalog");
        Directory.CreateDirectory(catalogue);
        File.WriteAllText(Path.Combine(catalogue, "products.json"),
            """[{"Code":"ID-FR-6","PrinterName":"Microsoft Print to PDF"}]""");

        var contenu = RapportDiagnostic.Fabriquer(Logs, Config, Archive, "", catalogue);

        Assert.Contains("catalog/products.json", Entrees(Archive));
        Assert.Contains("Microsoft Print to PDF", Lire(Archive, "catalog/products.json"));
        Assert.Contains("products.json", contenu.Fichiers);
    }

    /// <summary>
    /// Un poste sans catalogue lisible doit quand même envoyer son rapport : le catalogue
    /// est un plus, pas une condition.
    /// </summary>
    [Fact]
    public void Un_catalogue_absent_n_empeche_pas_le_rapport()
    {
        var contenu = RapportDiagnostic.Fabriquer(
            Logs, Config, Archive, "", Path.Combine(_racine, "catalogue-qui-n-existe-pas"));

        Assert.True(File.Exists(Archive));
        Assert.Contains("rapport.txt", Entrees(Archive));
        Assert.DoesNotContain("catalog/products.json", Entrees(Archive));
        Assert.True(contenu.Octets > 0);
    }
}
