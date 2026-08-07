using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Le catalogue posé au premier démarrage d'un poste neuf.
///
/// <b>Le défaut de l'installation du 07/08/2026.</b> Le poste de Créteil a démarré sur les
/// cinq produits d'amorçage — dont quatre pointent sur « Microsoft Print to PDF » — alors
/// que le vrai catalogue existait, versionné, dans <c>catalog\boutique\</c>. Il ne partait
/// simplement pas dans l'archive publiée. Résultat : un logiciel qui s'ouvre, qui affiche
/// des tirages, et dont rien ne sort des machines pourtant présentes.
/// </summary>
public class CatalogueLivreTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "StudioCatalogue-" + Guid.NewGuid().ToString("N"));

    private string Livre => Path.Combine(_racine, "livre");
    private string Donnees => Path.Combine(_racine, "data", "catalog");

    public CatalogueLivreTests()
    {
        Directory.CreateDirectory(Livre);
        Directory.CreateDirectory(Donnees);
    }

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private void PoserLeLivre(params string[] codes)
    {
        var produits = codes.Select(code => new Product
        {
            Code = code,
            Name = code,
            WidthMm = 102,
            HeightMm = 152,
            PrinterChannel = "Minilab DE100",
            Output = ProductOutput.FujiMinilab,
            Price = 0.60m,
        });

        ProductCatalog.Save(Path.Combine(Livre, "products.json"), produits);
    }

    [Fact]
    public void Un_poste_neuf_recoit_le_catalogue_livre()
    {
        PoserLeLivre("10x15", "13x18", "20x30");

        Assert.True(CatalogueLivre.PoserSiAbsent(Donnees, Livre));

        var pose = ProductCatalog.Load(Path.Combine(Donnees, "products.json"));
        Assert.Equal(3, pose.All.Count);
        Assert.NotNull(pose.Find("10x15"));
    }

    /// <summary>
    /// <b>Le point le plus important du lot.</b> Un poste qui tourne a des prix, des
    /// formats et des réglages pilote qui lui appartiennent : une mise à jour qui les
    /// écraserait ferait bien plus de dégâts que l'absence qu'on corrige ici.
    /// </summary>
    [Fact]
    public void Un_catalogue_deja_present_n_est_jamais_ecrase()
    {
        PoserLeLivre("10x15", "13x18");

        var sien = Path.Combine(Donnees, "products.json");
        ProductCatalog.Save(sien, [new Product { Code = "le-mien", Name = "Le mien", Price = 9.99m }]);

        Assert.False(CatalogueLivre.PoserSiAbsent(Donnees, Livre));

        var apres = ProductCatalog.Load(sien);
        Assert.Single(apres.All);
        Assert.NotNull(apres.Find("le-mien"));
    }

    /// <summary>
    /// Les réglages pilote capturés au dialogue suivent le catalogue : sans eux, la planche
    /// d'identité sort avec les réglages par défaut de la DS620, et ils ne se recapturent
    /// qu'avec l'imprimante sous la main.
    /// </summary>
    [Fact]
    public void Les_reglages_pilote_suivent_le_catalogue()
    {
        PoserLeLivre("ID-FR-6");
        File.WriteAllBytes(Path.Combine(Livre, "devmode-ID-FR-6.bin"), [1, 2, 3, 4]);

        Assert.True(CatalogueLivre.PoserSiAbsent(Donnees, Livre));

        var devmode = Path.Combine(Donnees, "devmode-ID-FR-6.bin");
        Assert.True(File.Exists(devmode), "le DEVMODE n'a pas suivi");
        Assert.Equal(4, new FileInfo(devmode).Length);
    }

    /// <summary>
    /// Sans catalogue livré — une archive d'avant cette correction — on rend faux, et
    /// l'appelant retombe sur les produits d'amorçage. Surtout pas d'exception : c'est le
    /// démarrage de l'application.
    /// </summary>
    [Fact]
    public void Sans_catalogue_livre_on_ne_pose_rien()
    {
        Assert.False(CatalogueLivre.PoserSiAbsent(Donnees, Livre));
        Assert.False(File.Exists(Path.Combine(Donnees, "products.json")));
    }

    /// <summary>
    /// <b>Un catalogue livré illisible ne doit pas empêcher de démarrer.</b> Une archive
    /// mal décompressée passerait le simple test de présence du fichier, et ferait échouer
    /// le démarrage juste après — là où plus rien ne peut le rattraper.
    /// </summary>
    [Fact]
    public void Un_catalogue_livre_illisible_ne_bloque_pas_le_demarrage()
    {
        File.WriteAllText(Path.Combine(Livre, "products.json"), "{ ceci n'est pas du JSON");

        Assert.False(CatalogueLivre.PoserSiAbsent(Donnees, Livre));
        Assert.False(File.Exists(Path.Combine(Donnees, "products.json")));
    }

    /// <summary>Un catalogue livré vide ne vaut pas mieux que pas de catalogue du tout.</summary>
    [Fact]
    public void Un_catalogue_livre_vide_est_refuse()
    {
        ProductCatalog.Save(Path.Combine(Livre, "products.json"), []);

        Assert.False(CatalogueLivre.PoserSiAbsent(Donnees, Livre));
    }

    /// <summary>Le dossier de données peut ne pas exister encore : on le crée.</summary>
    [Fact]
    public void Le_dossier_de_donnees_est_cree_au_besoin()
    {
        PoserLeLivre("10x15");
        var neuf = Path.Combine(_racine, "jamais-vu", "catalog");

        Assert.True(CatalogueLivre.PoserSiAbsent(neuf, Livre));
        Assert.True(File.Exists(Path.Combine(neuf, "products.json")));
    }

    // ————— la reprise des postes déjà installés —————

    /// <summary>
    /// <b>Le cas de Créteil.</b> Un poste installé avant la correction porte déjà un
    /// catalogue : celui d'amorçage, que la version précédente lui a fabriqué faute de
    /// mieux. Sans reprise, il le garderait pour toujours et la correction ne servirait
    /// qu'aux installations neuves.
    /// </summary>
    [Fact]
    public void Un_catalogue_d_amorcage_est_repris()
    {
        PoserLeLivre("10x15", "13x18", "20x30");
        ProductCatalog.Save(Path.Combine(Donnees, "products.json"), ProductCatalog.CreateDefaultProducts());

        Assert.True(CatalogueLivre.PoserSiAbsent(Donnees, Livre));

        var apres = ProductCatalog.Load(Path.Combine(Donnees, "products.json"));
        Assert.Equal(3, apres.All.Count);
        Assert.All(apres.All, p => Assert.Equal(ProductOutput.FujiMinilab, p.Output));
    }

    /// <summary>On ne remplace jamais un fichier sans filet, même celui-là.</summary>
    [Fact]
    public void Le_catalogue_d_amorcage_repris_est_conserve_a_cote()
    {
        PoserLeLivre("10x15");
        ProductCatalog.Save(Path.Combine(Donnees, "products.json"), ProductCatalog.CreateDefaultProducts());

        CatalogueLivre.PoserSiAbsent(Donnees, Livre);

        var sauvegardes = Directory.GetFiles(Donnees, "products.amorcage-*.json");
        Assert.Single(sauvegardes);
        Assert.NotEmpty(ProductCatalog.Load(sauvegardes[0]).All);
    }

    /// <summary>
    /// Dès qu'un produit a été ajouté, le fichier appartient à quelqu'un : on n'y touche
    /// plus. C'est la limite qui protège un poste qui s'est construit son catalogue à
    /// partir de l'amorçage.
    /// </summary>
    [Fact]
    public void Un_amorcage_auquel_on_a_ajoute_un_produit_n_est_plus_repris()
    {
        PoserLeLivre("10x15", "13x18");

        var siens = ProductCatalog.CreateDefaultProducts();
        siens.Add(new Product { Code = "a-moi", Name = "À moi", Price = 3m });
        ProductCatalog.Save(Path.Combine(Donnees, "products.json"), siens);

        Assert.False(CatalogueLivre.PoserSiAbsent(Donnees, Livre));
        Assert.NotNull(ProductCatalog.Load(Path.Combine(Donnees, "products.json")).Find("a-moi"));
    }

    /// <summary>
    /// Un prix retouché ne fait pas d'un catalogue d'amorçage un catalogue à soi : il ne
    /// sait toujours imprimer que sur « Microsoft Print to PDF ».
    /// </summary>
    [Fact]
    public void Un_prix_retouche_n_empeche_pas_la_reprise()
    {
        PoserLeLivre("10x15");

        var siens = ProductCatalog.CreateDefaultProducts();
        siens[0].Price = 0.65m;
        ProductCatalog.Save(Path.Combine(Donnees, "products.json"), siens);

        Assert.True(CatalogueLivre.PoserSiAbsent(Donnees, Livre));
    }

    // ————— le point d'entrée du démarrage —————
    //
    // Ces essais-là manquaient, et c'est ce qui a laissé passer la 1.3.2 : PoserSiAbsent
    // était juste et vérifiée, mais le démarrage l'appelait derrière un « && » qui la
    // court-circuitait dès qu'un catalogue existait. Une méthode juste, mal appelée, se
    // comporte exactement comme une méthode fausse.

    /// <summary>
    /// <b>Le défaut de la 1.3.2, en un essai.</b> Le poste de Créteil, mis à jour à 23:06,
    /// a gardé ses cinq produits d'amorçage : la reprise n'était jamais atteinte.
    /// </summary>
    [Fact]
    public void Au_demarrage_un_catalogue_d_amorcage_est_repris()
    {
        PoserLeLivre("10x15", "13x18", "20x30");
        ProductCatalog.Save(Path.Combine(Donnees, "products.json"), ProductCatalog.CreateDefaultProducts());

        CatalogueLivre.AssurerUnCatalogue(Donnees, Livre);

        var apres = ProductCatalog.Load(Path.Combine(Donnees, "products.json"));
        Assert.Equal(3, apres.All.Count);
        Assert.All(apres.All, p => Assert.Equal(ProductOutput.FujiMinilab, p.Output));
    }

    [Fact]
    public void Au_demarrage_un_poste_neuf_recoit_le_catalogue_livre()
    {
        PoserLeLivre("10x15", "13x18");

        CatalogueLivre.AssurerUnCatalogue(Donnees, Livre);

        Assert.Equal(2, ProductCatalog.Load(Path.Combine(Donnees, "products.json")).All.Count);
    }

    /// <summary>
    /// Sans catalogue livré — une archive d'avant la correction — le poste doit tout de
    /// même démarrer : les produits d'amorçage restent le dernier recours.
    /// </summary>
    [Fact]
    public void Au_demarrage_sans_catalogue_livre_l_amorcage_prend_le_relais()
    {
        CatalogueLivre.AssurerUnCatalogue(Donnees, Livre);

        var pose = ProductCatalog.Load(Path.Combine(Donnees, "products.json"));
        Assert.NotEmpty(pose.All);
        Assert.Equal(
            ProductCatalog.CreateDefaultProducts().Select(p => p.Code).ToHashSet(),
            pose.All.Select(p => p.Code).ToHashSet());
    }

    /// <summary>Le catalogue d'un poste qui tourne n'est jamais touché au démarrage.</summary>
    [Fact]
    public void Au_demarrage_le_catalogue_du_poste_est_intact()
    {
        PoserLeLivre("10x15", "13x18");
        ProductCatalog.Save(
            Path.Combine(Donnees, "products.json"),
            [new Product { Code = "le-mien", Name = "Le mien", Price = 9.99m }]);

        CatalogueLivre.AssurerUnCatalogue(Donnees, Livre);

        var apres = ProductCatalog.Load(Path.Combine(Donnees, "products.json"));
        Assert.Single(apres.All);
        Assert.NotNull(apres.Find("le-mien"));
    }

    /// <summary>
    /// L'épreuve de vérité : le catalogue RÉEL du dépôt, celui que Publier.ps1 recopie.
    /// Il doit se poser et porter les produits de la boutique — pas cinq lignes d'amorçage.
    /// </summary>
    [Fact]
    public void Le_catalogue_reel_du_depot_est_installable()
    {
        var depot = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "catalog", "boutique");

        if (!File.Exists(Path.Combine(depot, "products.json")))
            return; // essais lancés hors du dépôt

        Assert.True(CatalogueLivre.PoserSiAbsent(Donnees, depot));

        var pose = ProductCatalog.Load(Path.Combine(Donnees, "products.json"));
        Assert.True(pose.All.Count > 20, $"catalogue anormalement pauvre : {pose.All.Count} produits");

        // le poste doit savoir tirer sur le minilab dès le premier démarrage
        Assert.Contains(pose.All, p => p.Output == ProductOutput.FujiMinilab);
    }
}
