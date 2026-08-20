using Studio.App.Infrastructure;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// Comment une planche reprise retrouve ses photos dans la bande.
///
/// <b>Ce que ces essais protègent :</b> une photo rouverte depuis l'historique des trente
/// jours revient AVEC son travail. La fiche garde le nom du client (« IMG_1234.jpg ») mais
/// désigne la copie de travail (« IMG_1234-ab12cd34.jpg ») ; la bande, remplie depuis les
/// chemins, porte donc le nom de la copie. Chercher l'un avec l'autre ne trouve rien — et ne
/// rien trouver ne se VOIT PAS : la planche revient simplement vide de cadrage, de repères,
/// de fond blanc et de corrections, sans erreur à l'écran ni une ligne au journal.
///
/// C'est-à-dire exactement ce que l'historique existe pour épargner.
/// </summary>
public class RepriseDeLaPlancheTests
{
    private static PhotoIdentiteEnAttente Fiche(
        string nomDuClient, string? nomSurLeDisque = null, double axe = 0.5) => new()
    {
        FileName = nomDuClient,
        NomSurLeDisque = nomSurLeDisque,
        AxeVisage = axe,
    };

    // ----- le nom à garder dans la fiche -----

    [Fact]
    public void Le_nom_sur_le_disque_est_celui_de_la_copie_de_travail()
    {
        var nom = RepriseDeLaPlanche.NomSurLeDisque(
            "IMG_1234.jpg", @"D:\PhotoStudioData\cache\travail\20260819\IMG_1234-ab12cd34.jpg");

        Assert.Equal("IMG_1234-ab12cd34.jpg", nom);
    }

    [Fact]
    public void Rien_a_garder_quand_le_fichier_lu_est_celui_du_client()
    {
        // la planche mise de côté est reprise sur le support du client : les deux noms
        // se confondent, et la fiche n'a pas à porter la trace d'un problème qui n'est
        // pas le sien
        Assert.Null(RepriseDeLaPlanche.NomSurLeDisque("IMG_1234.jpg", @"E:\DCIM\100CANON\IMG_1234.jpg"));
    }

    [Fact]
    public void La_casse_ne_fait_pas_un_nom_different()
    {
        Assert.Null(RepriseDeLaPlanche.NomSurLeDisque("Img_1234.JPG", @"E:\DCIM\IMG_1234.jpg"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sans_chemin_il_n_y_a_rien_a_garder(string? chemin)
    {
        Assert.Null(RepriseDeLaPlanche.NomSurLeDisque("IMG_1234.jpg", chemin));
    }

    // ----- l'index de la reprise -----

    [Fact]
    public void Une_photo_d_historique_se_retrouve_par_le_nom_de_sa_copie()
    {
        // LE DÉFAUT : la bande porte le nom de la copie, la fiche celui du client
        var index = RepriseDeLaPlanche.ParNom([Fiche("IMG_1234.jpg", "IMG_1234-ab12cd34.jpg", axe: 0.42)]);

        Assert.True(index.TryGetValue("IMG_1234-ab12cd34.jpg", out var garde));
        Assert.Equal(0.42, garde!.AxeVisage);
    }

    [Fact]
    public void Elle_se_retrouve_aussi_par_le_nom_du_client()
    {
        // la même fiche doit rester reprenable depuis le support, si le client le rapporte
        var index = RepriseDeLaPlanche.ParNom([Fiche("IMG_1234.jpg", "IMG_1234-ab12cd34.jpg")]);

        Assert.True(index.ContainsKey("IMG_1234.jpg"));
    }

    [Fact]
    public void Une_planche_mise_de_cote_se_retrouve_par_le_nom_du_client()
    {
        var index = RepriseDeLaPlanche.ParNom([Fiche("IMG_1234.jpg", axe: 0.31)]);

        Assert.True(index.TryGetValue("IMG_1234.jpg", out var garde));
        Assert.Equal(0.31, garde!.AxeVisage);
    }

    [Fact]
    public void Le_nom_du_client_l_emporte_sur_le_nom_de_copie_d_une_autre_photo()
    {
        // ⚠ deux clients apportent souvent un IMG_1234.jpg. Si la copie de l'une porte le
        // nom du fichier de l'autre, c'est le nom du CLIENT qui fait foi : reprendre le
        // cadrage d'une inconnue sortirait le visage de quelqu'un d'autre.
        var index = RepriseDeLaPlanche.ParNom([
            Fiche("IMG_1234.jpg", axe: 0.20),
            Fiche("IMG_9999.jpg", "IMG_1234.jpg", axe: 0.80),
        ]);

        Assert.Equal(0.20, index["IMG_1234.jpg"].AxeVisage);
        Assert.Equal(0.80, index["IMG_9999.jpg"].AxeVisage);
    }

    [Fact]
    public void Deux_copies_du_meme_nom_gardent_la_premiere()
    {
        var index = RepriseDeLaPlanche.ParNom([
            Fiche("A.jpg", "commun.jpg", axe: 0.10),
            Fiche("B.jpg", "commun.jpg", axe: 0.90),
        ]);

        Assert.Equal(0.10, index["commun.jpg"].AxeVisage);
    }

    [Fact]
    public void La_casse_ne_fait_pas_perdre_une_photo()
    {
        var index = RepriseDeLaPlanche.ParNom([Fiche("IMG_1234.JPG", "IMG_1234-ab12cd34.JPG")]);

        Assert.True(index.ContainsKey("img_1234.jpg"));
        Assert.True(index.ContainsKey("img_1234-ab12cd34.jpg"));
    }

    [Fact]
    public void Une_fiche_sans_nom_n_entre_pas_dans_l_index()
    {
        // un fichier abîmé ou une vieille fiche : elle ne doit pas capturer la clé vide
        var index = RepriseDeLaPlanche.ParNom([Fiche(""), Fiche("IMG_1234.jpg")]);

        Assert.Single(index);
        Assert.True(index.ContainsKey("IMG_1234.jpg"));
    }

    [Fact]
    public void Sans_photo_l_index_est_vide_et_ne_leve_pas()
    {
        Assert.Empty(RepriseDeLaPlanche.ParNom(null));
        Assert.Empty(RepriseDeLaPlanche.ParNom([]));
    }
}
