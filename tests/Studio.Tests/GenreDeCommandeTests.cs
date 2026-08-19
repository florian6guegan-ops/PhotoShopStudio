using Studio.App.Infrastructure;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// L'ONGLET « TIRAGES PHOTO » NE DOIT MONTRER QUE DES TIRAGES.
///
/// Signalé le 19/08/2026 : « l'onglet tirages photos ne devrait afficher que les tirages, et
/// non tout ce qui concerne photos d'identité ». La planche était bien écartée — elle porte un
/// <c>Sheet</c> —, mais deux lignes du parcours identité passaient au travers : l'E-PHOTO, qui
/// est un 10×15 ordinaire aux yeux du catalogue, et l'ENVOI PAR COURRIEL, qui n'imprime rien
/// du tout. L'E-Photo représentait quatre des vingt-cinq dernières commandes de la boutique.
/// </summary>
public class GenreDeCommandeTests
{
    private const string CodeEPhoto = "e-photo-dnp";

    /// <summary>Le catalogue de la boutique, réduit à ce qui décide ici.</summary>
    private static Product? Catalogue(string code) => code switch
    {
        "10x15" => new Product { Code = "10x15", Name = "10x15", Output = ProductOutput.Printer },
        CodeEPhoto => new Product { Code = CodeEPhoto, Name = "E-Photo", Output = ProductOutput.Printer },
        "ID-FR-6" => new Product { Code = "ID-FR-6", Name = "Planche", Sheet = new SheetSpec() },
        "envoi-courriel" => new Product
        {
            Code = "envoi-courriel", Name = "Envoi par courriel", Output = ProductOutput.Email,
        },
        _ => null,
    };

    /// <summary>Ce que portent les raccourcis de l'écran d'identité de la boutique.</summary>
    private static readonly string[] RaccourcisProduits = [CodeEPhoto];

    private static OrderLine Ligne(string code, double? caseMm = null) => new()
    {
        ProductCode = code,
        Items = [new OrderItem { SheetCellWidthMm = caseMm }],
    };

    private static bool EstIdentite(OrderLine ligne) =>
        GenreDeCommande.EstIdentite(ligne, Catalogue, RaccourcisProduits);

    private static bool EstDesTirages(params OrderLine[] lignes) =>
        GenreDeCommande.EstDesTirages(
            new Envelope { Lines = [.. lignes] }, Catalogue, RaccourcisProduits);

    // ————— ce qui relève de l'identité —————

    [Fact]
    public void La_planche_releve_de_l_identite()
    {
        Assert.True(EstIdentite(Ligne("ID-FR-6")));
    }

    /// <summary>
    /// <b>Le cas qui manquait.</b> Rien dans le produit ne distingue l'E-Photo d'un 10×15 :
    /// même papier, même machine, photo entière. Ce qui la distingue, c'est qu'on y arrive
    /// par l'écran d'identité — et c'est ce que dit le raccourci.
    /// </summary>
    [Fact]
    public void L_e_photo_releve_de_l_identite_par_son_raccourci()
    {
        Assert.True(EstIdentite(Ligne(CodeEPhoto)));
    }

    /// <summary>
    /// L'envoi par courriel n'est pas seulement mal rangé : rien n'en sort. Il n'a sa place
    /// dans « Tirages photo » sous aucun angle, raccourci ou pas.
    /// </summary>
    [Fact]
    public void L_envoi_par_courriel_n_est_jamais_un_tirage()
    {
        Assert.True(EstIdentite(Ligne("envoi-courriel")));
    }

    /// <summary>
    /// Le repli des vieilles commandes : le produit a disparu du catalogue, mais l'article
    /// porte encore sa taille de case.
    /// </summary>
    [Fact]
    public void Une_case_mesuree_suffit_quand_le_produit_a_disparu()
    {
        Assert.True(EstIdentite(Ligne("produit-supprime", caseMm: 35)));
    }

    // ————— ce qui reste un tirage —————

    [Fact]
    public void Un_10x15_reste_un_tirage()
    {
        Assert.False(EstIdentite(Ligne("10x15")));
        Assert.True(EstDesTirages(Ligne("10x15")));
    }

    /// <summary>
    /// Sans raccourci produit configuré, la règle ne se met pas à inventer : l'E-Photo
    /// redevient un tirage ordinaire. C'est le comportement d'un poste qui n'a pas d'écran
    /// d'identité, et il ne faut pas qu'il perde ses tirages.
    /// </summary>
    [Fact]
    public void Sans_raccourci_l_e_photo_reste_un_tirage()
    {
        Assert.False(GenreDeCommande.EstIdentite(Ligne(CodeEPhoto), Catalogue, []));
    }

    // ————— l'enveloppe entière —————

    [Fact]
    public void Une_enveloppe_d_e_photo_seule_n_est_pas_dans_les_tirages()
    {
        Assert.False(EstDesTirages(Ligne(CodeEPhoto)));
    }

    /// <summary>
    /// La règle du 06/08/2026, inchangée : une enveloppe MIXTE reste hors des tirages, sans
    /// quoi la planche qu'on venait de ranger revient par la fenêtre. Elle paraît dans
    /// « Photos d'identité » et dans « Tout », entière.
    /// </summary>
    [Fact]
    public void Une_enveloppe_mixte_reste_hors_des_tirages()
    {
        Assert.False(EstDesTirages(Ligne("10x15"), Ligne("ID-FR-6")));
        Assert.False(EstDesTirages(Ligne("10x15"), Ligne(CodeEPhoto)));
    }

    /// <summary>Une enveloppe vide n'est un tirage de rien du tout.</summary>
    [Fact]
    public void Une_enveloppe_vide_n_est_pas_un_tirage()
    {
        Assert.False(EstDesTirages());
    }
}
