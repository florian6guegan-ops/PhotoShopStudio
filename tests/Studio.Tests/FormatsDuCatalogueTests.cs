using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Dnp;

namespace Studio.Tests;

/// <summary>
/// « QU'IL AFFICHE LES FORMATS DU CATALOGUE, PAS CEUX GÉNÉRIQUES DU ROULEAU » — 19/08/2026.
///
/// L'écran « État des machines » comptait les tirages restants dans la nomenclature du pilote
/// de DiLand : <c>15xS</c>, <c>15xL</c>, <c>15x23</c>, <c>15x40</c>. Personne ne vend ça, et le
/// 13×18 — qui sort très bien d'un rouleau de 152 mm avec ses bandes blanches — n'y figurait
/// même pas.
/// </summary>
public class FormatsDuCatalogueTests
{
    private static Product Minilab(string nom, double l, double h, string? machine = null) => new()
    {
        Code = nom, Name = nom, WidthMm = l, HeightMm = h,
        Output = ProductOutput.FujiMinilab, MinilabMachineId = machine, Enabled = true,
    };

    private static Product Dnp(string nom, double l, double h, string file = "DP-DS620") => new()
    {
        Code = nom, Name = nom, WidthMm = l, HeightMm = h,
        Output = ProductOutput.Printer, PrinterName = file, Enabled = true,
    };

    /// <summary>Le catalogue de la boutique, réduit à ce qui décide ici.</summary>
    private static readonly Product[] Catalogue =
    [
        Minilab("10x15", 102, 152),
        Minilab("Bord blanc 10x15", 102, 152),
        Minilab("13x18", 127, 180),
        Minilab("15x20", 152, 203),
        Minilab("20x30", 203, 307),
    ];

    // ————— le minilab —————

    /// <summary>
    /// Un rouleau de 152 mm et dix mètres de papier. Le 10×15 se couche — 102 mm consommés,
    /// donc environ 98 tirages — et le 13×18 EST de la partie, ce que la liste du pilote ne
    /// disait pas.
    /// </summary>
    [Fact]
    public void Le_rouleau_de_152_rend_les_formats_qu_on_vend()
    {
        var formats = FormatsDuCatalogue.SurLeMinilab(Catalogue, 'A', 152, 10_000);
        var noms = formats.Select(f => f.Nom).ToList();

        Assert.Contains("10x15", noms);
        Assert.Contains("13x18", noms);
        Assert.Contains("15x20", noms);

        // 20×30 ne tient pas : son petit côté fait 203 mm.
        Assert.DoesNotContain("20x30", noms);

        Assert.Equal(98, formats.Single(f => f.Nom == "10x15").Restants);
    }

    /// <summary>
    /// Deux produits de mêmes cotes ne font qu'un rang : « 10×15 » et « Bord blanc 10×15 »
    /// consomment le même papier, et le catalogue en compte vingt-six sur le minilab.
    /// </summary>
    [Fact]
    public void Deux_produits_de_meme_taille_ne_font_qu_une_ligne()
    {
        var formats = FormatsDuCatalogue.SurLeMinilab(Catalogue, 'A', 152, 10_000);

        Assert.Single(formats, f => f.Restants == 98);
        Assert.DoesNotContain(formats, f => f.Nom.StartsWith("Bord blanc", StringComparison.Ordinal));
    }

    /// <summary>
    /// C'est le format NU qui nomme la ligne, pas la variante. Sur le catalogue de la
    /// boutique, l'ordre du fichier met « Bord blanc 21×29,7 » devant « 21×29,7 » : l'écran
    /// annonçait une largeur de marge là où on attend un format.
    /// </summary>
    [Fact]
    public void Le_format_nu_nomme_la_ligne_avant_sa_variante_a_bord_blanc()
    {
        var bordBlanc = Minilab("Bord blanc 21x29,7", 210, 297);
        bordBlanc.BorderMm = 5;

        Product[] catalogue = [bordBlanc, Minilab("21x29,7", 210, 297)];

        var formats = FormatsDuCatalogue.SurLeMinilab(catalogue, 'A', 210, 10_000);

        Assert.Equal("21x29,7", Assert.Single(formats).Nom);
    }

    /// <summary>
    /// La photo se pose EN TRAVERS du rouleau dès qu'elle y tient : un 10×15 sur du 203 mm
    /// ne mange que 102 mm, et c'est ce que fait la machine (relevé d'Arcueil, 13/08/2026 :
    /// « 203×102 mm »). Compter le grand côté sous-estimerait d'un tiers.
    /// </summary>
    [Theory]
    [InlineData(102, 152)]  // rouleau juste assez large : le tirage part en LONGUEUR
    [InlineData(152, 102)]  // le 152 accueille le grand côté : le tirage se couche
    [InlineData(203, 102)]
    public void Un_10x15_se_couche_des_que_le_rouleau_est_assez_large(
        int largeurRouleau, int attendu)
    {
        Assert.Equal(attendu, FormatsDuCatalogue.LongueurConsommeeMm(102, 152, largeurRouleau));
    }

    /// <summary>Un rouleau plus étroit que le grand côté : le tirage part en longueur.</summary>
    [Fact]
    public void Un_15x20_sur_du_152_consomme_ses_203_mm()
    {
        Assert.Equal(203, FormatsDuCatalogue.LongueurConsommeeMm(152, 203, 152));
    }

    /// <summary>
    /// Un produit épinglé sur la machine B ne compte pas pour la A. Un produit sans machine
    /// désignée compte pour les deux : c'est le cas de la quasi-totalité du catalogue.
    /// </summary>
    [Fact]
    public void Un_produit_epingle_ailleurs_ne_compte_pas()
    {
        Product[] catalogue = [Minilab("Lustré 10x15", 102, 152, machine: "B")];

        Assert.Empty(FormatsDuCatalogue.SurLeMinilab(catalogue, 'A', 152, 10_000));
        Assert.Single(FormatsDuCatalogue.SurLeMinilab(catalogue, 'B', 152, 10_000));
    }

    /// <summary>Papier inconnu, rien à annoncer — surtout pas des zéros.</summary>
    [Fact]
    public void Sans_rouleau_connu_on_n_annonce_rien()
    {
        Assert.Empty(FormatsDuCatalogue.SurLeMinilab(Catalogue, 'A', 0, 10_000));
    }

    // ————— la DNP —————

    /// <summary>
    /// <b>LE COMPTEUR DE LA DS620 PARLE EN FEUILLES.</b> Sur un rouleau 15×20, une planche
    /// d'identité 10×15 est coupée en deux : 138 feuilles, ce sont 276 planches. L'écran
    /// annonçait la moitié de ce qui restait — et c'est la même règle que celle qui décide de
    /// la découpe à l'impression, pas une autre.
    /// </summary>
    [Fact]
    public void Sur_un_rouleau_6x8_une_planche_10x15_compte_double()
    {
        Product[] catalogue = [Dnp("Planche 10x15", 156.1, 105)];

        var formats = FormatsDuCatalogue.SurLaDnp(catalogue, null, DnpMediaSize.Size6x8, 138);

        Assert.Equal(276, Assert.Single(formats).Restants);
    }

    /// <summary>Un 15×20 occupe la feuille entière : une feuille, un tirage.</summary>
    [Fact]
    public void Un_15x20_sur_un_rouleau_6x8_ne_compte_pas_double()
    {
        Product[] catalogue = [Dnp("15x20 DNP", 152, 203)];

        var formats = FormatsDuCatalogue.SurLaDnp(catalogue, null, DnpMediaSize.Size6x8, 138);

        Assert.Equal(138, Assert.Single(formats).Restants);
    }

    /// <summary>
    /// Le rouleau chargé ne porte pas tout : un 15×20 ne sort pas d'un rouleau 6×4, et
    /// l'annoncer ferait espérer un tirage impossible.
    ///
    /// ⚠ La planche, elle, en sort — alors qu'elle fait 156,1 × 105 mm contre 152,4 × 101,6
    /// pour le 6×4. Elle DÉBORDE exprès, pour qu'aucun liseré blanc ne subsiste après la
    /// coupe : « tenir » se juge au fond perdu près, sans quoi le format que la boutique tire
    /// tous les jours serait déclaré intirable.
    /// </summary>
    [Fact]
    public void Ce_qui_ne_tient_pas_sur_le_rouleau_n_est_pas_annonce()
    {
        Product[] catalogue = [Dnp("15x20 DNP", 152, 203), Dnp("Planche 10x15", 156.1, 105)];

        var noms = FormatsDuCatalogue
            .SurLaDnp(catalogue, null, DnpMediaSize.Size6x4, 200)
            .Select(f => f.Nom)
            .ToList();

        Assert.Equal(["Planche 10x15"], noms);
    }

    /// <summary>
    /// Quand la file Windows est connue — machine vue par le spouleur seul —, on s'y tient :
    /// un produit qui vise une autre imprimante n'a rien à faire dans ce compte.
    /// </summary>
    [Fact]
    public void Une_file_nommee_ecarte_les_produits_d_une_autre_imprimante()
    {
        Product[] catalogue =
        [
            Dnp("Planche 10x15", 156.1, 105, file: "DP-DS620"),
            Dnp("Planche voisine", 156.1, 105, file: "DP-DS620 (kodak)"),
        ];

        var formats = FormatsDuCatalogue.SurLaDnp(catalogue, "DP-DS620", DnpMediaSize.Size6x8, 100);

        Assert.Equal("Planche 10x15", Assert.Single(formats).Nom);
    }

    /// <summary>Un rouleau que la machine n'a pas nommé : on ne compte rien plutôt que faux.</summary>
    [Fact]
    public void Sans_rouleau_reconnu_la_dnp_n_annonce_rien()
    {
        Product[] catalogue = [Dnp("Planche 10x15", 156.1, 105)];

        Assert.Empty(FormatsDuCatalogue.SurLaDnp(catalogue, null, DnpMediaSize.None, 138));
    }
}
