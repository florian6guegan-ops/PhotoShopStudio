using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// L'alimentation d'une imprimante AU RYTHME OÙ ELLE SORT LE PAPIER.
///
/// Le défaut corrigé : <c>PrintPages</c> remettait toute la commande au spouleur aussi vite
/// qu'il l'acceptait — onze tirages en cinq secondes sur la commande 04-024 du 04/08/2026.
/// Sur six cents photos, une panne d'encre à la troisième laissait quand même partir les
/// cinq cent quatre-vingt-dix-sept autres, et il n'existait plus aucun moyen de savoir où
/// reprendre.
/// </summary>
public class CadenceSpouleurTests
{
    /// <summary>Une file qui rend, à chaque lecture, l'état suivant de la liste.</summary>
    private static CadenceSpouleur Cadence(params PlaceEnFile[] lectures)
    {
        var rang = 0;
        return new CadenceSpouleur(
            () => lectures[Math.Min(rang++, lectures.Length - 1)],
            _ => { })
        {
            PlafondEnFile = 3,
        };
    }

    private static PlaceEnFile File(int pages) => new(PeutEnvoyer: true, Panne: "", PagesEnFile: pages);

    private static PlaceEnFile Panne(string quoi, int pages = 0) => new(false, quoi, pages);

    // — la file laisse passer —

    [Fact]
    public void Une_file_vide_laisse_partir_la_page_tout_de_suite()
    {
        var place = Cadence(File(0)).Attendre(plafond: 3);

        Assert.True(place.PeutEnvoyer);
        Assert.False(place.EnPanne);
    }

    /// <summary>
    /// Le plafond n'est pas zéro : la machine ne doit jamais attendre après nous, sinon
    /// chaque tirage coûte un aller-retour de lecture et la cadence s'effondre.
    /// </summary>
    [Fact]
    public void Une_file_sous_le_plafond_laisse_partir_la_page()
    {
        Assert.True(Cadence(File(3)).Attendre(plafond: 3).PeutEnvoyer);
    }

    /// <summary>
    /// LE point de la correction : au-delà du plafond, on attend que la machine sorte du
    /// papier avant de lui en donner plus.
    /// </summary>
    [Fact]
    public void Une_file_pleine_fait_patienter_jusqu_a_ce_qu_elle_descende()
    {
        var lectures = 0;
        var file = new[] { 9, 7, 5, 2 };

        var cadence = new CadenceSpouleur(
            () => File(file[Math.Min(lectures++, file.Length - 1)]),
            _ => { })
        { PlafondEnFile = 3 };

        var place = cadence.Attendre(plafond: 3);

        Assert.True(place.PeutEnvoyer);
        Assert.Equal(2, place.PagesEnFile);
        Assert.Equal(4, lectures); // elle a bien relu jusqu'à ce que la file descende
    }

    // — la panne —

    /// <summary>
    /// Une panne l'emporte sur tout : inutile d'attendre une file qui ne descendra pas, et
    /// surtout il ne faut pas lui remettre une page de plus — c'est précisément ce qui
    /// laissait cinq cent quatre-vingt-dix-sept photos dans le spouleur d'une machine à
    /// court d'encre.
    /// </summary>
    [Fact]
    public void Une_panne_arrete_l_alimentation_sur_le_champ()
    {
        var place = Cadence(Panne("Ruban épuisé.", pages: 12)).Attendre(plafond: 3);

        Assert.True(place.EnPanne);
        Assert.Equal("Ruban épuisé.", place.Panne);
    }

    /// <summary>
    /// Elle est vue même quand la file avait de la place : c'est le cas du ruban qui
    /// s'épuise entre deux photos, machine par ailleurs disponible.
    /// </summary>
    [Fact]
    public void Une_panne_sur_une_file_vide_arrete_aussi()
    {
        Assert.True(Cadence(Panne("Plus de papier.")).Attendre(plafond: 3).EnPanne);
    }

    // — la machine qui ne suit plus —

    /// <summary>
    /// Une file qui ne descend plus du tout finit par rendre la main : la commande part en
    /// attente au lieu de bloquer l'écran pour toujours.
    /// </summary>
    [Fact]
    public void Une_file_qui_stagne_finit_par_rendre_la_main()
    {
        var horloge = DateTimeOffset.UtcNow;
        var cadence = new CadenceSpouleur(
            () => File(8),
            // chaque « attente » fait avancer le temps de deux minutes
            _ => horloge = horloge.AddMinutes(2))
        {
            PlafondEnFile = 3,
            AttenteMaximale = TimeSpan.FromMilliseconds(1),
        };

        var place = cadence.Attendre(plafond: 3);

        Assert.False(place.PeutEnvoyer);
        Assert.True(place.EnPanne);
        Assert.Contains("n'a rien sorti", place.Panne, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Une file qui DESCEND n'est jamais abandonnée</b>, si lente soit-elle. Ce n'est pas
    /// la durée totale qui compte — une commande de six cents photos passe légitimement une
    /// heure ici — mais le temps sans le moindre progrès.
    /// </summary>
    [Fact]
    public void Une_file_qui_descend_lentement_n_est_pas_abandonnee()
    {
        var restantes = 600;
        var lectures = 0;

        var cadence = new CadenceSpouleur(
            () => { lectures++; return File(restantes -= 1); },
            _ => { })
        {
            PlafondEnFile = 3,
            // Courte À DESSEIN : elle serait dépassée depuis longtemps si le compteur
            // d'attente ne repartait pas à zéro à chaque page sortie. C'est bien le temps
            // SANS progrès qui compte, pas la durée totale.
            AttenteMaximale = TimeSpan.FromMilliseconds(50),
        };

        var place = cadence.Attendre(plafond: 3);

        Assert.True(place.PeutEnvoyer);
        Assert.False(place.EnPanne);
        Assert.Equal(597, lectures); // elle a suivi la file de 599 jusqu'à 3
    }

    // — ce qui est SORTI —

    /// <summary>
    /// Le point de reprise doit compter ce que la MACHINE a sorti, pas ce que Windows a
    /// pris. La différence est exactement ce qu'on réimprimerait pour rien — ou pire, ce
    /// qu'on sauterait.
    /// </summary>
    [Fact]
    public void Les_pages_sorties_retranchent_ce_qui_reste_en_file()
    {
        Assert.Equal(597, Cadence(File(3)).PagesSorties(pagesRemises: 600));
    }

    /// <summary>
    /// Une file inconnue rend -1 : le compte ne doit pas passer au-dessus des pages
    /// remises, sans quoi la reprise sauterait des photos.
    /// </summary>
    [Fact]
    public void Une_file_illisible_ne_gonfle_pas_le_compte_des_sorties()
    {
        var sorties = Cadence(new PlaceEnFile(true, "", -1)).PagesSorties(pagesRemises: 10);

        Assert.Equal(10, sorties);
    }

    [Fact]
    public void Le_compte_des_sorties_ne_descend_jamais_sous_zero()
    {
        Assert.Equal(0, Cadence(File(50)).PagesSorties(pagesRemises: 10));
    }

    // — où reprendre —

    /// <summary>
    /// <b>On refait la dernière photo sortie.</b> Quand une machine s'arrête faute d'encre,
    /// celle qui était en cours sort pâle ou à moitié, et rien ne permet de le savoir
    /// depuis le logiciel. Demandé par l'exploitant le 04/08/2026.
    /// </summary>
    [Theory]
    [InlineData(600, 599)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    public void La_reprise_refait_la_derniere_photo_sortie(int sorties, int attendu)
    {
        Assert.Equal(attendu, CadenceSpouleur.ReprendreA(sorties));
    }

    /// <summary>Rien n'est sorti : on repart du début, sans reculer dans le vide.</summary>
    [Fact]
    public void Sans_photo_sortie_la_reprise_repart_du_debut()
    {
        Assert.Equal(0, CadenceSpouleur.ReprendreA(0));
    }
}
