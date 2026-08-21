using Studio.Printing.LargeFormat;

namespace Studio.Tests;

/// <summary>
/// Le format papier posé d'office pour un agrandissement.
///
/// <b>D'où ça vient.</b> Le 21/08/2026, un 30 × 40 demandé au Kremlin-Bicêtre est sorti à la
/// taille d'une A4 : la boîte d'impression s'ouvrait sur le format par défaut de la file, et
/// 48 % du tirage tombait hors de la feuille. Le pilote proposait pourtant « 30 x 40 cm ».
///
/// La liste ci-dessous est le relevé RÉEL de l'EPSON SC-P6000 de la boutique, cotes
/// comprises — c'est elle qui doit rendre « 30 x 40 cm », pas une liste inventée.
/// </summary>
public class ChoixDuFormatPapierTests
{
    /// <summary>L'Epson du Kremlin-Bicêtre, relevé le 21/08/2026.</summary>
    private static readonly LargeFormatPrinter.PapierOffert[] Epson =
    [
        new("A4 210 x 297 mm", 210.1, 296.9),
        new("A3 297 x 420 mm", 296.9, 420.1),
        new("A3+ / US B+ 329 x 483 mm", 328.9, 483.1),
        new("A2 420 x 594 mm", 420.1, 594.1),
        new("A1 594 x 841 mm", 594.1, 841),
        new("A0 841 x 1189 mm", 841, 1189),
        new("US B 11 x 17 p.", 279.4, 431.8),
        new("16 x 20 p.", 406.4, 508),
        new("30 x 40 cm", 300, 400),
        new("40 x 60 cm", 400, 599.9),
        new("60 x 90 cm", 599.9, 899.9),
        new("Personnalisée", 210.1, 296.9, Personnalise: true),
    ];

    [Fact]
    public void Un_30x40_trouve_le_papier_30x40()
    {
        // le défaut d'origine, en une ligne : c'est CE format qu'il fallait poser
        var choix = LargeFormatPrinter.Retenir(Epson, 300, 400);

        Assert.NotNull(choix);
        Assert.Equal("30 x 40 cm", choix.Nom);
        Assert.False(choix.Paysage);
    }

    [Fact]
    public void Un_30x40_ne_part_pas_sur_une_A4()
    {
        // l'A4 est le format par DÉFAUT du pilote : c'est elle qu'il ne faut plus retenir
        var choix = LargeFormatPrinter.Retenir(Epson, 300, 400);

        Assert.NotEqual("A4 210 x 297 mm", choix!.Nom);
    }

    [Fact]
    public void A_la_cote_exacte_le_dixieme_de_millimetre_ne_disqualifie_pas()
    {
        // un A3 se relit 296,9 × 420,1 : sans tolérance, une photo donnée à 297 × 420 serait
        // jugée trop grande pour son propre papier
        var choix = LargeFormatPrinter.Retenir(Epson, 297, 420);

        Assert.Equal("A3 297 x 420 mm", choix!.Nom);
    }

    [Fact]
    public void Le_plus_petit_qui_contient_lemporte()
    {
        // un 20 × 25 tient dans presque tout ; c'est l'A4 qui gâche le moins de papier
        var choix = LargeFormatPrinter.Retenir(Epson, 200, 250);

        Assert.Equal("A4 210 x 297 mm", choix!.Nom);
    }

    [Fact]
    public void Un_tirage_couche_couche_la_feuille()
    {
        // 40 × 30 : aucun format n'est plus large que haut, il faut passer en paysage
        var choix = LargeFormatPrinter.Retenir(Epson, 400, 300);

        Assert.NotNull(choix);
        Assert.True(choix.Paysage);
        Assert.Equal("30 x 40 cm", choix.Nom);

        // les cotes rendues sont celles de la feuille APRÈS rotation : c'est sur elles que
        // la boîte calcule le centrage
        Assert.Equal(400, choix.WidthMm, 1);
        Assert.Equal(300, choix.HeightMm, 1);
    }

    [Fact]
    public void Le_format_personnalise_est_ecarte()
    {
        // « Personnalisée » annonce ici 210 × 297, les cotes de la dernière saisie : elle
        // gagnerait au plus petit pour un tirage qui y tient, et sortirait n'importe quoi
        var choix = LargeFormatPrinter.Retenir(
            [new("Personnalisée", 210.1, 296.9, Personnalise: true)], 100, 150);

        Assert.Null(choix);
    }

    [Fact]
    public void Rien_dassez_grand_ne_choisit_rien()
    {
        // 100 × 150 cm : au-delà de la machine. On rend null, et l'appelant garde le format
        // par défaut du pilote plutôt que d'imposer un papier au hasard.
        Assert.Null(LargeFormatPrinter.Retenir(Epson, 1000, 1500));
    }

    [Fact]
    public void Un_tirage_sans_taille_ne_choisit_rien()
    {
        Assert.Null(LargeFormatPrinter.Retenir(Epson, 0, 400));
        Assert.Null(LargeFormatPrinter.Retenir(Epson, 300, -1));
    }

    [Fact]
    public void Un_A2_saisi_a_la_main_trouve_son_papier()
    {
        // les agrandissements hors catalogue passent par le même chemin : rien ne connaît
        // le produit, tout se joue sur les millimètres du fichier rendu
        var choix = LargeFormatPrinter.Retenir(Epson, 420, 594);

        Assert.Equal("A2 420 x 594 mm", choix!.Nom);
    }
}
