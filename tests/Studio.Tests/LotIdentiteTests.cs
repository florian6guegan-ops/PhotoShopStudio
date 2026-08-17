using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// LA BANDE DE GAUCHE N'EST PAS LE LOT.
///
/// Studio Photo Identité s'ouvre directement sur la carte du client : la bande porte donc
/// toute la carte, parce que l'opérateur doit y chercher la bonne photo. Aucune n'a été
/// demandée pour autant.
///
/// Le défaut d'Arcueil, 17/08/2026 : l'impression fabriquait une planche pour CHAQUE photo
/// de la bande, en remontant à un exemplaire celles qui étaient à zéro. Toucher
/// « Imprimer » sortait la carte entière.
/// </summary>
public class LotIdentiteTests
{
    // — ce qu'une photo porte en arrivant dans la bande —

    /// <summary>
    /// Le cas d'Arcueil : la bande vient d'une carte mémoire. Rien n'a été choisi, donc
    /// rien ne part sur du papier tant que l'opérateur n'a pas ouvert une photo.
    /// </summary>
    [Fact]
    public void Une_photo_venue_d_une_carte_n_est_pas_du_lot()
    {
        var quantite = LotIdentite.QuantiteDeDepart(choisieDavance: false);

        Assert.Equal(0, quantite);
        Assert.False(LotIdentite.EstRetenue(quantite));
    }

    /// <summary>
    /// L'autre parcours, celui du Studio complet : l'opérateur a désigné ses photos à
    /// l'écran de sélection. Les exclure les ferait toutes disparaître du lot — c'est la
    /// régression qu'il ne faut pas introduire en corrigeant la première.
    /// </summary>
    [Fact]
    public void Une_photo_choisie_a_l_ecran_de_selection_est_du_lot()
    {
        var quantite = LotIdentite.QuantiteDeDepart(choisieDavance: true);

        Assert.Equal(1, quantite);
        Assert.True(LotIdentite.EstRetenue(quantite));
    }

    // — ce qui se passe quand on ouvre une photo —

    /// <summary>Ouvrir une photo, c'est la choisir : elle entre dans le lot.</summary>
    [Fact]
    public void Ouvrir_une_photo_jamais_ouverte_la_fait_entrer_dans_le_lot()
    {
        Assert.Equal(1, LotIdentite.QuantiteALOuverture(quantiteEnregistree: 0, dejaOuverte: false));
    }

    /// <summary>
    /// Une photo ouverte PAR ERREUR en parcourant la carte doit pouvoir en ressortir : on
    /// descend son compteur à zéro. Sans ce cas, le zéro reviendrait à un dès qu'on regarde
    /// une autre photo puis qu'on revient — et la photo repartirait sur du papier.
    /// </summary>
    [Fact]
    public void Un_zero_pose_a_la_main_tient_quand_on_rouvre_la_photo()
    {
        Assert.Equal(0, LotIdentite.QuantiteALOuverture(quantiteEnregistree: 0, dejaOuverte: true));
    }

    /// <summary>Une quantité déjà réglée est respectée, dans les deux cas.</summary>
    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    [InlineData(20, true)]
    public void Une_quantite_deja_reglee_est_respectee(int quantite, bool dejaOuverte)
    {
        Assert.Equal(quantite, LotIdentite.QuantiteALOuverture(quantite, dejaOuverte));
    }

    // — le lot lui-même —

    /// <summary>
    /// LE CAS D'ARCUEIL, de bout en bout : quatre-vingts photos sur la carte, une seule
    /// ouverte. Une planche doit sortir, pas quatre-vingts.
    /// </summary>
    [Fact]
    public void Une_carte_de_quatre_vingts_photos_dont_une_ouverte_ne_donne_qu_une_planche()
    {
        var bande = Enumerable.Range(0, 80)
            .Select(_ => LotIdentite.QuantiteDeDepart(choisieDavance: false))
            .ToList();

        // l'opérateur ouvre la photo du client, la 57e
        bande[56] = LotIdentite.QuantiteALOuverture(bande[56], dejaOuverte: false);

        var retenues = bande.Count(LotIdentite.EstRetenue);
        var planches = bande.Where(LotIdentite.EstRetenue).Sum();

        Assert.Equal(1, retenues);
        Assert.Equal(1, planches);
    }

    /// <summary>
    /// Et le lot compte bien les EXEMPLAIRES : deux clients, l'un veut deux planches.
    /// </summary>
    [Fact]
    public void Le_lot_compte_les_exemplaires_des_seules_photos_retenues()
    {
        var bande = new[] { 0, 2, 0, 1, 0 };

        Assert.Equal(2, bande.Count(LotIdentite.EstRetenue));
        Assert.Equal(3, bande.Where(LotIdentite.EstRetenue).Sum());
    }
}
