using Studio.Sources;

namespace Studio.Tests;

/// <summary>
/// Ce que l'écran de navigation dans l'arborescence doit savoir faire.
///
/// Le 01/08/2026, désigner un dossier parent revenait à lire tout le disque en dessous :
/// des dizaines de milliers de vignettes, et l'application tombait par manque de mémoire
/// dans le rendu WPF. Un dossier sans photos, lui, ne disait rien du tout. Ces deux cas
/// sont vérifiés ici.
/// </summary>
public class FolderBrowsingTests : IDisposable
{
    private readonly string _racine = Path.Combine(
        Path.GetTempPath(), "studio-tests-" + Guid.NewGuid().ToString("N"));

    public FolderBrowsingTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
    }

    private string Dossier(params string[] parties)
    {
        var chemin = Path.Combine(new[] { _racine }.Concat(parties).ToArray());
        Directory.CreateDirectory(chemin);
        return chemin;
    }

    private static void Photos(string dossier, int combien, string prefixe = "img")
    {
        for (var i = 0; i < combien; i++)
            File.WriteAllBytes(Path.Combine(dossier, $"{prefixe}{i:0000}.jpg"), [0xFF, 0xD8]);
    }

    [Fact]
    public void Un_dossier_sans_photos_rend_une_liste_vide_sans_lever()
    {
        var vide = Dossier("vide");

        Assert.Empty(PhotoScanner.Scan(vide, recursive: true));
        Assert.Empty(PhotoScanner.Scan(vide, recursive: false));
        Assert.Equal(0, PhotoScanner.Count(vide, recursive: true));
        Assert.Null(PhotoScanner.FirstPhoto(vide));
    }

    [Fact]
    public void Sans_recursion_les_photos_des_sous_dossiers_restent_dehors()
    {
        var parent = Dossier("parent");
        Photos(parent, 2);
        Photos(Dossier("parent", "enfant"), 5);

        Assert.Equal(2, PhotoScanner.Scan(parent, recursive: false).Count);
        Assert.Equal(7, PhotoScanner.Scan(parent, recursive: true).Count);
    }

    [Fact]
    public void Un_dossier_parent_sans_photos_directes_en_annonce_zero()
    {
        // le cas qui faisait tomber l'application : on choisit le parent, il n'a rien
        // lui-même, et tout est plus bas
        var parent = Dossier("catalogue");
        Photos(Dossier("catalogue", "2026-07"), 3);

        Assert.Equal(0, PhotoScanner.Count(parent, recursive: false));
        Assert.Equal(3, PhotoScanner.Count(parent, recursive: true));
    }

    [Fact]
    public void Le_plafond_arrete_le_parcours()
    {
        var gros = Dossier("gros");
        Photos(gros, 30);

        Assert.Equal(10, PhotoScanner.Scan(gros, recursive: true, max: 10).Count);
        Assert.Equal(10, PhotoScanner.Count(gros, recursive: true, max: 10));
    }

    [Fact]
    public void Les_photos_du_DCIM_passent_devant_quand_le_plafond_tombe()
    {
        // sur une carte d'appareil photo, si l'on ne peut en prendre que dix, ce sont les
        // photos du client qu'on prend, pas les images d'un dossier système du support
        var support = Dossier("support");
        Photos(Dossier("support", "autre"), 40, "zzz");
        Photos(Dossier("support", "DCIM", "100CANON"), 40, "IMG_");

        var trouvees = PhotoScanner.Scan(support, recursive: true, max: 10);

        Assert.Equal(10, trouvees.Count);
        Assert.All(trouvees, p => Assert.Contains("DCIM", p));
    }

    [Fact]
    public void Une_arborescence_profonde_se_parcourt_entierement()
    {
        // le parcours est itératif, pas récursif : une pile de dossiers ne doit pas
        // déborder la pile d'appels, qui elle ne se rattrape pas — le processus meurt
        var niveaux = Enumerable.Range(0, 50).Select(i => "n" + i).ToArray();
        var fond = Dossier(niveaux);
        Photos(fond, 1);

        Assert.Equal(1, PhotoScanner.Count(_racine, recursive: true));
    }

    [Fact]
    public void Un_fichier_vide_n_est_pas_une_photo()
    {
        // copie interrompue, carte défaillante : l'extension est bonne, le fichier ne
        // donnera aucun tirage. Il encombrait la planche en restant cochable.
        var dossier = Dossier("carte");
        Photos(dossier, 2);
        File.WriteAllBytes(Path.Combine(dossier, "tronquee.jpg"), []);

        Assert.Equal(2, PhotoScanner.Count(dossier, recursive: false));
        Assert.DoesNotContain(
            PhotoScanner.Scan(dossier, recursive: false),
            p => p.EndsWith("tronquee.jpg"));
    }

    [Fact]
    public void Les_fichiers_caches_ou_systeme_restent_dehors()
    {
        var dossier = Dossier("support");
        Photos(dossier, 1);

        var cache = Path.Combine(dossier, "apercu.jpg");
        File.WriteAllBytes(cache, [0xFF, 0xD8]);
        File.SetAttributes(cache, FileAttributes.Hidden);

        Assert.Equal(1, PhotoScanner.Count(dossier, recursive: false));
    }

    [Fact]
    public void Un_dossier_sans_rien_d_imprimable_se_compte_a_zero()
    {
        // c'est ce zéro qui fait disparaître le dossier de l'écran de parcours
        var dossier = Dossier("documents");
        File.WriteAllText(Path.Combine(dossier, "notes.txt"), "rien à imprimer");
        File.WriteAllText(Path.Combine(dossier, "budget.xlsx"), "rien à imprimer non plus");

        Assert.Equal(0, PhotoScanner.Count(dossier, recursive: true));
    }

    /// <summary>
    /// Un PDF EST imprimable depuis le 04/08/2026 : il est éclaté en une image par page
    /// (voir <c>PdfPages</c>). Un dossier qui n'en contient que doit donc rester visible —
    /// il l'était pour les photos, et disparaissait pour les documents.
    /// </summary>
    [Fact]
    public void Un_dossier_qui_ne_contient_qu_un_pdf_reste_visible()
    {
        var dossier = Dossier("scans");
        File.WriteAllText(Path.Combine(dossier, "notes.txt"), "rien à imprimer");
        File.WriteAllText(Path.Combine(dossier, "catalogue.pdf"), "%PDF-1.4");

        Assert.Equal(1, PhotoScanner.Count(dossier, recursive: true));
    }

    /// <summary>
    /// La vignette qui ILLUSTRE un dossier ne peut pas être un PDF : elle est décodée
    /// telle quelle, sans passer par le rendu des pages.
    /// </summary>
    [Fact]
    public void Un_pdf_n_illustre_jamais_un_dossier()
    {
        var dossier = Dossier("melange");
        File.WriteAllText(Path.Combine(dossier, "aaa.pdf"), "%PDF-1.4");   // premier par ordre alphabétique
        var photo = Path.Combine(dossier, "zzz.jpg");
        File.WriteAllBytes(photo, [0xFF, 0xD8]);

        Assert.Equal(photo, PhotoScanner.FirstPhoto(dossier));
    }

    [Fact]
    public void Les_sous_dossiers_sont_listes_tries_et_sans_les_caches()
    {
        Dossier("photos", "Zoo");
        Dossier("photos", "Anniversaire");
        var cache = Dossier("photos", ".cache");
        File.SetAttributes(cache, FileAttributes.Directory | FileAttributes.Hidden);

        var noms = FolderTree.SubFolders(Path.Combine(_racine, "photos")).Select(n => n.Name).ToList();

        Assert.Equal(new[] { "Anniversaire", "Zoo" }, noms);
    }

    [Fact]
    public void Le_chemin_se_decompose_en_etapes_de_la_racine_au_dossier()
    {
        var feuille = Dossier("a", "b", "c");

        var etapes = FolderTree.Breadcrumb(feuille);

        Assert.Equal(feuille, etapes[^1].Path);
        Assert.Equal(new[] { "a", "b", "c" }, etapes.TakeLast(3).Select(e => e.Name));
        Assert.True(etapes.Count > 3);   // la racine du disque et le chemin temporaire
    }

    [Fact]
    public void Le_parent_existe_partout_sauf_a_la_racine_du_disque()
    {
        var enfant = Dossier("parent", "enfant");

        Assert.Equal(Path.Combine(_racine, "parent"), FolderTree.Parent(enfant));
        Assert.Null(FolderTree.Parent(Path.GetPathRoot(_racine)!));
    }

    [Fact]
    public void La_vignette_d_un_dossier_vient_de_son_premier_sous_dossier_garni()
    {
        var parent = Dossier("mariage");
        Photos(Dossier("mariage", "01-mairie"), 2, "a");

        var apercu = PhotoScanner.FirstPhoto(parent);

        Assert.NotNull(apercu);
        Assert.EndsWith("a0000.jpg", apercu);
    }

    [Fact]
    public void Les_raccourcis_proposent_au_moins_un_disque()
    {
        var raccourcis = FolderTree.Shortcuts();

        Assert.NotEmpty(raccourcis);
        Assert.All(raccourcis, r => Assert.False(string.IsNullOrWhiteSpace(r.Label)));
    }
}
