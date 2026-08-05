using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// La reconnaissance des imprimantes du laboratoire.
///
/// <b>Pourquoi.</b> Les machines étaient désignées en dur — « SC-P800 » cherché dans le nom
/// pour les agrandissements. Or Windows nomme celle de la boutique
/// <c>EPSONFECE59 (SC-P800 Series)</c> : cela marchait par chance. Chez un collègue équipé
/// d'une P700, d'une DS-RX1 ou d'une Citizen, plus rien n'était trouvé, et rien ne le
/// disait.
///
/// Les noms de cet essai sont ceux RELEVÉS sur le poste de la boutique le 05/08/2026.
/// </summary>
public class DetectionImprimantesTests
{
    // ————— les machines réelles de la boutique —————

    [Theory]
    [InlineData("EPSONFECE59 (SC-P800 Series)", RoleImprimante.GrandFormat)]
    [InlineData("DP-DS620", RoleImprimante.Sublimation)]
    [InlineData("FUJIFILM DE100", RoleImprimante.Minilab)]
    [InlineData("FUJIFILM DE100-2", RoleImprimante.Minilab)]
    public void Les_machines_de_la_boutique_sont_reconnues(string nom, RoleImprimante attendu)
    {
        Assert.Equal(attendu, DetectionImprimantes.RoleDe(nom));
    }

    /// <summary>
    /// <b>Le photocopieur de bureau n'est pas un traceur.</b> L'iR-ADV sort du A3 sur
    /// papier ordinaire ; proposer un agrandissement dessus ferait perdre un tirage. C'est
    /// pourquoi on ne retient jamais une marque entière, seulement ses gammes photo.
    /// </summary>
    [Theory]
    [InlineData("iR-ADV C5535 III")]
    [InlineData("Microsoft Print to PDF")]
    [InlineData("Microsoft XPS Document Writer")]
    [InlineData("OneNote for Windows 10")]
    [InlineData("Fax")]
    [InlineData("Send to Sawgrass Print Utility")]
    [InlineData("SAWGRASS SG500")]
    public void Ce_qui_n_est_pas_du_laboratoire_est_ecarte(string nom)
    {
        Assert.Equal(RoleImprimante.Aucun, DetectionImprimantes.RoleDe(nom));
    }

    // ————— les machines qu'on n'a pas sous les yeux —————

    /// <summary>C'est tout l'objet du changement : reconnaître par FAMILLE, pas par modèle.</summary>
    [Theory]
    [InlineData("EPSON SC-P700 Series", RoleImprimante.GrandFormat)]
    [InlineData("Epson SureColor P900", RoleImprimante.GrandFormat)]
    [InlineData("Canon imagePROGRAF PRO-1000", RoleImprimante.GrandFormat)]
    [InlineData("HP DesignJet Z9", RoleImprimante.GrandFormat)]
    [InlineData("DS-RX1HS", RoleImprimante.Sublimation)]
    [InlineData("DNP QW410", RoleImprimante.Sublimation)]
    [InlineData("Citizen CX-02", RoleImprimante.Sublimation)]
    [InlineData("Mitsubishi CP-D70DW", RoleImprimante.Sublimation)]
    [InlineData("Sinfonia S2145", RoleImprimante.Sublimation)]
    public void Les_autres_modeles_de_la_famille_sont_reconnus(string nom, RoleImprimante attendu)
    {
        Assert.Equal(attendu, DetectionImprimantes.RoleDe(nom));
    }

    /// <summary>Le nom vient de Windows : on ne maîtrise ni la casse ni le décor autour.</summary>
    [Theory]
    [InlineData("epsonfece59 (sc-p800 series)")]
    [InlineData("EPSONFECE59 (SC-P800 SERIES)")]
    public void La_casse_n_entre_pas_en_ligne_de_compte(string nom)
    {
        Assert.Equal(RoleImprimante.GrandFormat, DetectionImprimantes.RoleDe(nom));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_nom_vide_ne_leve_pas(string nom)
    {
        Assert.Equal(RoleImprimante.Aucun, DetectionImprimantes.RoleDe(nom));
        Assert.Equal("", DetectionImprimantes.MotifDe(nom));
    }

    /// <summary>La machine reconnue doit pouvoir DIRE pourquoi : c'est ce qu'affichent les paramètres.</summary>
    [Fact]
    public void La_detection_explique_ce_qu_elle_a_reconnu()
    {
        Assert.Equal("DNP DS620", DetectionImprimantes.MotifDe("DP-DS620"));
        Assert.Equal("Fujifilm DE100", DetectionImprimantes.MotifDe("FUJIFILM DE100"));
        Assert.Equal("", DetectionImprimantes.MotifDe("iR-ADV C5535 III"));
    }

    // ————— le réglage a le dernier mot —————

    /// <summary>
    /// Un réglage qui ne désigne plus rien — machine débranchée, file renommée — ne doit
    /// pas être rendu tel quel : l'impression échouerait en nommant une machine absente.
    /// On retombe sur la détection, qui verra la nouvelle.
    /// </summary>
    [Fact]
    public void Un_reglage_qui_ne_designe_plus_rien_est_ignore()
    {
        var choisie = DetectionImprimantes.Choisir(
            RoleImprimante.GrandFormat, "Imprimante démontée il y a trois ans");

        Assert.NotEqual("Imprimante démontée il y a trois ans", choisie);
    }

    /// <summary>Sans réglage ni machine du rôle, on rend null — et l'appelant le dit.</summary>
    [Fact]
    public void Sans_rien_a_proposer_on_rend_null_plutot_qu_une_machine_au_hasard()
    {
        // aucun poste n'a d'imprimante tenant ce rôle imaginaire
        var choisie = DetectionImprimantes.Choisir((RoleImprimante)999, null);

        Assert.Null(choisie);
    }
}
