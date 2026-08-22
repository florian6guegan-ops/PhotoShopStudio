using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Le profil couleur de la DNP : ce qu'il touche, et ce qu'il lit.
///
/// La question vient du comptoir, le 18/08/2026 : « j'ai l'impression que le profil n'est pas
/// le même pour DiLand et Studio ». Il ne l'était pas — mais surtout, il n'était pas le même
/// d'un PRODUIT à l'autre de la même machine : la planche d'identité portait
/// <c>DS620-R0.icc</c>, l'E-Photo et le 10×15 n'avaient rien du tout, sur le même rouleau.
/// C'est ce désaccord-là que ces essais surveillent.
/// </summary>
public class ProfilCouleurMachineTests
{
    private static Product Dnp(string code, string? icc = null, string file = "DP-DS620") =>
        new() { Code = code, Name = code, PrinterName = file, IccProfile = icc };

    // ----- ce que le réglage touche -----

    [Theory]
    [InlineData("DP-DS620")]
    [InlineData("DS620")]
    [InlineData("ds620")]      // le pilote ne garantit pas la casse
    [InlineData("DS820")]
    [InlineData("QW410")]
    public void Les_files_de_la_gamme_dnp_sont_reconnues(string file) =>
        Assert.True(ImprimanteDnp.EstUneDnp(file));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("EPSON SC-P800")]
    [InlineData("Microsoft Print to PDF")]
    public void Les_autres_files_ne_le_sont_pas(string file) =>
        Assert.False(ImprimanteDnp.EstUneDnp(file));

    [Fact]
    public void Seuls_les_produits_de_la_dnp_sont_retenus()
    {
        var tous = new[]
        {
            Dnp("ID-FR-6"),
            Dnp("e-photo-dnp"),
            new Product { Code = "agrandissement", PrinterName = "EPSON SC-P800" },
            new Product { Code = "10x15-fuji", Output = ProductOutput.FujiMinilab, PrinterName = "" },
        };

        Assert.Equal(["ID-FR-6", "e-photo-dnp"],
            ProfilCouleurMachine.Produits(tous, MachineCouleur.Dnp).Select(p => p.Code));
    }

    /// <summary>
    /// Une file qui commence par « DS6 » mais qui sort en FICHIER n'est pas une imprimante :
    /// c'est le cas du poste qui reprend ses agrandissements dans Photoshop.
    /// </summary>
    [Fact]
    public void Un_produit_qui_sort_en_fichier_est_ecarte()
    {
        var tous = new[] { new Product { Code = "retouche", Output = ProductOutput.ManualFile, PrinterName = "DS620" } };

        Assert.Empty(ProfilCouleurMachine.Produits(tous, MachineCouleur.Dnp));
    }

    // ----- le minilab Fuji -----

    private static Product Fuji(string code, string? icc = null) =>
        new() { Code = code, Name = code, Output = ProductOutput.FujiMinilab, PrinterName = "", IccProfile = icc };

    /// <summary>
    /// Le DE100 se reconnaît à sa SORTIE et non à un nom de file : ses produits n'en ont
    /// pas — le minilab ne se pilote pas par le spouleur Windows, ses files y sont branchées
    /// sur le port « nul ».
    /// </summary>
    [Fact]
    public void Seuls_les_produits_du_minilab_sont_retenus()
    {
        var tous = new[]
        {
            Fuji("10x15"), Fuji("13x18"),
            Dnp("ID-FR-6"),
            new Product { Code = "agrandissement", Output = ProductOutput.ManualFile, PrinterName = "EPSON SC-P800" },
        };

        Assert.Equal(["10x15", "13x18"],
            ProfilCouleurMachine.Produits(tous, MachineCouleur.MinilabFuji).Select(p => p.Code));
    }

    /// <summary>
    /// Les deux machines ne se marchent pas dessus : régler la couleur du minilab ne doit
    /// pas toucher la planche d'identité, qui sort d'une autre machine et d'un autre papier.
    /// </summary>
    [Fact]
    public void Regler_le_minilab_ne_touche_pas_la_dnp()
    {
        var planche = Dnp("ID-FR-6", "DS620-R0.icc");
        var tirage = Fuji("10x15");
        var tous = new[] { planche, tirage };

        ProfilCouleurMachine.Appliquer(
            ProfilCouleurMachine.Produits(tous, MachineCouleur.MinilabFuji), "DE100 Glossy.icc");

        Assert.Equal("DE100 Glossy.icc", tirage.IccProfile);
        Assert.Equal("DS620-R0.icc", planche.IccProfile);
    }

    /// <summary>
    /// L'état RÉEL du poste avant ce réglage, relevé le 22/08/2026 dans
    /// <c>products.json</c> : vingt-six produits DE100, pas un seul profil. C'est un accord —
    /// tout le monde en sRGB présumé — et l'écran doit donc proposer d'en poser un, pas
    /// signaler un désaccord.
    /// </summary>
    [Fact]
    public void Le_minilab_sans_aucun_profil_est_un_accord()
    {
        var etat = ProfilCouleurMachine.Lire([Fuji("10x15"), Fuji("13x18"), Fuji("20x30")]);

        Assert.True(etat.Accord);
        Assert.Null(etat.Profil);
    }

    /// <summary>
    /// Les deux machines sont décrites, et différemment : un écran qui dirait « la DNP » en
    /// réglant le DE100 enverrait l'opérateur changer la couleur de la mauvaise machine.
    /// </summary>
    [Fact]
    public void Chaque_machine_a_ses_propres_mots()
    {
        Assert.NotEqual(MachineCouleur.Dnp.Titre, MachineCouleur.MinilabFuji.Titre);
        Assert.NotEqual(MachineCouleur.Dnp.Sortie, MachineCouleur.MinilabFuji.Sortie);
        Assert.Contains("DE100", MachineCouleur.MinilabFuji.Titre);
        Assert.Contains("DNP", MachineCouleur.Dnp.Titre);
    }

    // ----- ce qu'il lit -----

    [Fact]
    public void Sans_produit_dnp_il_n_y_a_rien_a_dire()
    {
        var etat = ProfilCouleurMachine.Lire([]);

        Assert.Null(etat.Profil);
        Assert.True(etat.Accord);
    }

    [Fact]
    public void Le_meme_profil_partout_est_un_accord()
    {
        var etat = ProfilCouleurMachine.Lire([Dnp("a", "DS620.icc"), Dnp("b", "DS620.icc")]);

        Assert.Equal("DS620.icc", etat.Profil);
        Assert.True(etat.Accord);
    }

    /// <summary>Aucun profil nulle part est un accord aussi — celui du pilote.</summary>
    [Fact]
    public void Aucun_profil_partout_est_un_accord()
    {
        var etat = ProfilCouleurMachine.Lire([Dnp("a"), Dnp("b")]);

        Assert.Null(etat.Profil);
        Assert.True(etat.Accord);
    }

    /// <summary>
    /// L'état RÉEL des trois postes avant ce réglage : la planche avec profil, l'E-Photo
    /// sans. C'est un désaccord, et l'écran doit le dire — deux couleurs sortent de la même
    /// machine.
    /// </summary>
    [Fact]
    public void Un_produit_sans_profil_a_cote_d_un_produit_avec_est_un_desaccord()
    {
        var etat = ProfilCouleurMachine.Lire([Dnp("ID-FR-6", "DS620-R0.icc"), Dnp("e-photo-dnp")]);

        Assert.False(etat.Accord);
        Assert.Null(etat.Profil);
    }

    [Fact]
    public void Deux_profils_differents_sont_un_desaccord() =>
        Assert.False(ProfilCouleurMachine.Lire([Dnp("a", "DS620.icc"), Dnp("b", "DS620_SD.icc")]).Accord);

    /// <summary>
    /// Le profil d'une FINITION l'emporte sur celui du produit — c'est ce que fait
    /// l'orchestrateur à l'impression. La lecture doit suivre la même règle, sans quoi
    /// l'écran annoncerait une couleur que la machine ne sort pas.
    /// </summary>
    [Fact]
    public void La_finition_couvre_le_profil_du_produit()
    {
        var produit = Dnp("ID-FR-6", "DS620.icc");
        produit.Finishes = [new FinishOption { Name = "Brillant", IccProfile = "DS620_Metallic.icc" }];

        Assert.Equal("DS620_Metallic.icc", ProfilCouleurMachine.Lire([produit]).Profil);
    }

    [Fact]
    public void Une_finition_sans_profil_laisse_celui_du_produit()
    {
        var produit = Dnp("ID-FR-6", "DS620.icc");
        produit.Finishes = [new FinishOption { Name = "Brillant", DevmodeFile = "brillant.bin" }];

        Assert.Equal("DS620.icc", ProfilCouleurMachine.Lire([produit]).Profil);
    }

    // ----- ce qu'il pose -----

    [Fact]
    public void Le_profil_est_pose_sur_tous_les_produits()
    {
        var produits = new[] { Dnp("ID-FR-6", "DS620-R0.icc"), Dnp("e-photo-dnp"), Dnp("10x15-dnp") };

        var changes = ProfilCouleurMachine.Appliquer(produits, "DS620.icc");

        Assert.All(produits, p => Assert.Equal("DS620.icc", p.IccProfile));
        Assert.Equal(3, changes.Count);
        Assert.True(ProfilCouleurMachine.Lire(produits).Accord);
    }

    /// <summary>
    /// Ce qui n'a pas bougé n'est pas annoncé comme modifié : l'écran dit combien de
    /// produits il a touchés, et un chiffre faux ferait douter du reste.
    /// </summary>
    [Fact]
    public void Seuls_les_produits_reellement_changes_sont_rendus()
    {
        var produits = new[] { Dnp("a", "DS620.icc"), Dnp("b") };

        var changes = ProfilCouleurMachine.Appliquer(produits, "DS620.icc");

        Assert.Equal(["b"], changes.Select(p => p.Code));
    }

    [Fact]
    public void Choisir_aucun_profil_efface_ce_qui_etait_pose()
    {
        var produits = new[] { Dnp("a", "DS620.icc") };

        var changes = ProfilCouleurMachine.Appliquer(produits, null);

        Assert.Null(produits[0].IccProfile);
        Assert.Single(changes);
    }

    /// <summary>Un nom vide vaut « aucun » : le JSON reste lisible, sans chaîne vide.</summary>
    [Fact]
    public void Un_nom_vide_vaut_aucun_profil()
    {
        var produits = new[] { Dnp("a", "DS620.icc") };

        ProfilCouleurMachine.Appliquer(produits, "   ");

        Assert.Null(produits[0].IccProfile);
    }

    /// <summary>
    /// <b>Le piège.</b> Un profil de finition couvre celui du produit : le poser sans effacer
    /// la finition donnerait un réglage qui paraît pris et ne sort pas de la machine.
    /// </summary>
    [Fact]
    public void Le_profil_d_une_finition_est_efface_pour_que_le_choix_prenne()
    {
        var produit = Dnp("ID-FR-6", "DS620-R0.icc");
        produit.Finishes =
        [
            new FinishOption { Name = "Brillant", IccProfile = "DS620_Metallic.icc" },
            new FinishOption { Name = "Mat", DevmodeFile = "mat.bin" },
        ];

        ProfilCouleurMachine.Appliquer([produit], "DS620.icc");

        Assert.All(produit.Finishes, f => Assert.Null(f.IccProfile));
        Assert.Equal("DS620.icc", ProfilCouleurMachine.Lire([produit]).Profil);
    }
}
