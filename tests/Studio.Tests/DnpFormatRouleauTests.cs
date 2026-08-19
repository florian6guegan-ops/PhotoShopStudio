using Studio.Printing.Devices.Dnp;

namespace Studio.Tests;

/// <summary>
/// Le format du rouleau chargé, et ce qu'on réclame à la machine pour tirer dessus.
///
/// <b>Ce que ces essais empêchent de revenir.</b> Le 10/08/2026, Créteil a sorti ses
/// planches d'identité sur une demi-feuille pendant toute une journée. Deux fautes
/// enchaînées : Studio croyait avoir un rouleau 10×15 alors qu'il tirait sur du 15×20, et
/// il n'a jamais réclamé la découpe qui aurait fait tenir deux tirages par feuille — ce que
/// DiLand fait depuis toujours sur la même machine.
/// </summary>
public class DnpFormatRouleauTests
{
    // ————— lire le rouleau —————

    /// <summary>
    /// Les deux codes relevés sur les machines de la boutique. Ils se ressemblent — un seul
    /// chiffre les sépare — et c'est ce qui avait trompé la première version : elle ne
    /// gardait que les trois premiers, identiques, et concluait au même format pour les deux.
    /// </summary>
    [Theory]
    [InlineData("00301", 400, DnpMediaSize.Size6x4)]
    [InlineData("00310", 200, DnpMediaSize.Size6x8)]
    public void Les_codes_releves_sur_les_machines_sont_reconnus(
        string code, int capacite, DnpMediaSize attendu)
    {
        Assert.Equal(attendu, DnpDriver.LireLeFormat(code, capacite));
    }

    /// <summary>
    /// <b>C'est LA mesure qui a départagé les deux boutiques.</b> Le code média, l'étiquette
    /// du rouleau et le souvenir de l'exploitant se contredisaient ; la capacité, elle, ne
    /// ment pas — une DS620 tire 400 fois sur un 10×15 et 200 fois sur un 15×20.
    /// </summary>
    [Theory]
    [InlineData(400, DnpMediaSize.Size6x4)]
    [InlineData(200, DnpMediaSize.Size6x8)]
    public void Un_code_inconnu_se_rattrape_sur_la_capacite(int capacite, DnpMediaSize attendu)
    {
        Assert.Equal(attendu, DnpDriver.LireLeFormat("99999", capacite));
    }

    /// <summary>
    /// Ne rien affirmer vaut mieux que se tromper : c'est une erreur d'affichage d'un côté,
    /// une feuille gâchée de l'autre.
    /// </summary>
    [Theory]
    [InlineData("", 0)]
    [InlineData(null, 0)]
    [InlineData("inconnu", 12)]
    public void Sans_indice_fiable_on_ne_conclut_pas(string? code, int capacite)
    {
        Assert.Equal(DnpMediaSize.None, DnpDriver.LireLeFormat(code, capacite));
    }

    // ————— que réclamer à la machine —————

    /// <summary>
    /// <b>Le défaut de Créteil.</b> Sur un rouleau 15×20, un tirage 10×15 doit être réclamé
    /// comme tel, sinon la machine sort la feuille entière et l'autre moitié part à la
    /// poubelle.
    /// </summary>
    [Theory]
    [InlineData(6.15, 4.13)]  // planche d'identité en paysage
    [InlineData(4.13, 6.15)]  // la même en portrait
    public void Un_10x15_sur_un_rouleau_15x20_se_reclame_en_10x15(double largeur, double hauteur)
    {
        Assert.Equal(
            DnpMediaSize.Size6x4,
            DnpDriver.TailleDeTirage(DnpMediaSize.Size6x8, largeur, hauteur));
    }

    /// <summary>
    /// <b>JAMAIS <see cref="DnpMediaSize.Size6x4x2"/>, qui veut dire « voici une PAIRE ».</b>
    ///
    /// Cette valeur a bloqué Créteil le 10/08/2026 : la machine accepte l'envoi —
    /// <c>SendImageData</c> rend 1, tout paraît réussi — puis n'imprime rien, gardant la
    /// première moitié de la feuille en attendant la seconde image. Studio envoie ses
    /// tirages un par un.
    ///
    /// Que <see cref="DnpMediaSize.Size6x4"/> suffise se lit dans les compteurs de DiLand
    /// sur cette même machine : 138 feuilles (276 tirages 10×15) tombent à 275 après UNE
    /// planche. Un seul tirage consommé, une seule image envoyée, coupée.
    /// </summary>
    [Theory]
    [InlineData(6.15, 4.13)]
    [InlineData(4.13, 6.15)]
    public void On_ne_reclame_jamais_le_mode_par_paire(double largeur, double hauteur)
    {
        Assert.NotEqual(
            DnpMediaSize.Size6x4x2,
            DnpDriver.TailleDeTirage(DnpMediaSize.Size6x8, largeur, hauteur));
    }

    /// <summary>
    /// Maisons-Alfort tire juste depuis toujours : rouleau 10×15, tirage 10×15, rien à
    /// couper. Ce correctif ne doit RIEN y changer.
    /// </summary>
    [Fact]
    public void Un_10x15_sur_un_rouleau_10x15_ne_change_pas()
    {
        Assert.Equal(
            DnpMediaSize.Size6x4,
            DnpDriver.TailleDeTirage(DnpMediaSize.Size6x4, 6.15, 4.13));
    }

    /// <summary>
    /// Un vrai 15×20 sur son rouleau se tire entier. Le couper reviendrait à sortir deux
    /// moitiés de la photo du client.
    /// </summary>
    [Fact]
    public void Un_15x20_sur_son_rouleau_ne_se_coupe_pas()
    {
        Assert.Equal(
            DnpMediaSize.Size6x8,
            DnpDriver.TailleDeTirage(DnpMediaSize.Size6x8, 6.15, 8.2));
    }

    /// <summary>
    /// Rouleau inconnu : on ne réclame rien de particulier et la machine garde son réglage.
    /// Deviner ici ferait sortir n'importe quoi sur une machine qu'on ne connaît pas.
    /// </summary>
    [Fact]
    public void Sur_un_rouleau_inconnu_on_ne_reclame_rien()
    {
        Assert.Equal(
            DnpMediaSize.None,
            DnpDriver.TailleDeTirage(DnpMediaSize.None, 6.15, 4.13));
    }

    // ————— avec quelle définition on mesure le tirage —————

    /// <summary>
    /// <b>LES 15 × 20 ALÉATOIRES DE KODAKIDPC, 18/08/2026.</b>
    ///
    /// La définition annoncée par la DNP n'est pas une propriété de la machine : c'est le
    /// RÉGLAGE posé pour le travail suivant, et le logiciel voisin (IDMaker, sur ce poste)
    /// le change quand il veut. Six planches identiques de 1844 × 1240 sont parties le même
    /// après-midi, les unes coupées, les autres sur une feuille entière — sans que la
    /// machine se taise une seule fois.
    ///
    /// C'est donc la TRAME qui décide : elle, on sait comment on l'a faite.
    /// </summary>
    [Fact]
    public void La_definition_du_fichier_l_emporte_sur_le_reglage_de_la_machine()
    {
        Assert.Equal((300.0, 300.0),
            DnpDriver.DefinitionRetenue(machineH: 600, machineV: 600, fichierH: 300, fichierV: 300));
    }

    /// <summary>
    /// Le tirage exact du 18/08/2026 à 18:03 : machine réglée à 600 ppp, planche d'identité
    /// de 1844 × 1240 rendue à 300, rouleau 6×8. Elle DOIT se réclamer en 6×4 — sinon la
    /// moitié de la feuille part à la poubelle, et la trame est pivotée par-dessus le marché.
    /// </summary>
    [Fact]
    public void Une_planche_se_coupe_meme_si_la_machine_est_reglee_a_600()
    {
        var (h, v) = DnpDriver.DefinitionRetenue(600, 600, fichierH: 300, fichierV: 300);

        Assert.Equal(
            DnpMediaSize.Size6x4,
            DnpDriver.TailleDeTirage(DnpMediaSize.Size6x8, 1844 / h, 1240 / v));
        Assert.False(DnpDriver.DoitPivoter(DnpMediaSize.Size6x4, 1844, 1240));
    }

    /// <summary>
    /// Un produit rendu à 600 ppp se mesure à 600 : la règle suit le fichier, pas un
    /// nombre écrit en dur. Sa trame 6×4 fait alors 3688 × 2480.
    /// </summary>
    [Fact]
    public void Un_fichier_rendu_a_600_se_mesure_a_600()
    {
        var (h, v) = DnpDriver.DefinitionRetenue(300, 300, fichierH: 600, fichierV: 600);

        Assert.Equal((600.0, 600.0), (h, v));
        Assert.Equal(
            DnpMediaSize.Size6x4,
            DnpDriver.TailleDeTirage(DnpMediaSize.Size6x8, 3688 / h, 2480 / v));
    }

    /// <summary>
    /// <b>LA DEMI-FEUILLE PERDUE DE KODAKIDPC, 17/08/2026.</b>
    ///
    /// Quand la machine se tait — elle sort de veille, le port est occupé —, l'appelant
    /// renonçait à la découpe et réclamait le rouleau entier : une planche d'identité 6×4
    /// sortait sur une feuille 6×8 à moitié blanche. Relevé sur la MÊME image de 1844 × 1240
    /// à quatre minutes d'écart : « format demandé Size6x4 » à 16:17, « Size6x8 » à 16:22.
    ///
    /// Le fichier, lui, sait toujours : Studio le rend à 300 ppp et l'écrit dedans.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]        // la machine ne répond rien
    [InlineData(-1, -1)]      // ni n'importe quoi
    [InlineData(12, 12)]      // ni une valeur absurde
    [InlineData(300, 0)]      // ni une seule des deux
    public void Machine_muette_on_prend_la_definition_du_fichier(double h, double v)
    {
        Assert.Equal((300.0, 300.0),
            DnpDriver.DefinitionRetenue(h, v, fichierH: 300, fichierV: 300));
    }

    /// <summary>
    /// Le cas complet : machine muette, fichier sans définition utilisable. On retient
    /// 300 ppp — celle de toutes les DNP de la boutique — plutôt que de renoncer.
    /// </summary>
    [Fact]
    public void Machine_muette_et_fichier_muet_on_retient_300()
    {
        Assert.Equal((DnpDriver.DefinitionParDefaut, DnpDriver.DefinitionParDefaut),
            DnpDriver.DefinitionRetenue(0, 0, 0, 0));
    }

    /// <summary>
    /// Et le bout du bout, celui qui compte vraiment : une planche d'identité de
    /// 1844 × 1240 sur un rouleau 6×8, machine muette, DOIT se réclamer en 6×4. C'est
    /// exactement le tirage de 16:22 qui a gâché une demi-feuille.
    /// </summary>
    [Fact]
    public void Une_planche_identite_sur_rouleau_6x8_se_coupe_meme_si_la_machine_se_tait()
    {
        var (h, v) = DnpDriver.DefinitionRetenue(0, 0, fichierH: 300, fichierV: 300);

        Assert.Equal(
            DnpMediaSize.Size6x4,
            DnpDriver.TailleDeTirage(DnpMediaSize.Size6x8, 1844 / h, 1240 / v));
    }

    // ————— dans quel sens la trame part à la machine —————

    /// <summary>
    /// <b>L'E-PHOTO PORTRAIT SORTIE COUPÉE EN PAYSAGE</b>, commande 18-006 du 18/08/2026.
    ///
    /// La machine n'oriente rien : elle attend une trame dont la LARGEUR est celle du
    /// rouleau. Un produit portrait — l'E-Photo, 105 × 156,1 mm — se rend en 1240 × 1844, et
    /// remis tel quel à un 6×4 il est lu en travers puis rogné.
    /// </summary>
    [Fact]
    public void Une_trame_portrait_sur_un_6x4_doit_pivoter()
    {
        Assert.True(DnpDriver.DoitPivoter(DnpMediaSize.Size6x4, 1240, 1844));
    }

    /// <summary>
    /// ⚠ Et la planche d'identité, elle, ne doit PAS bouger : elle est en 156,1 × 105, donc
    /// rendue en 1844 × 1240 — déjà dans le sens de la trame. Elle sort juste depuis des
    /// semaines, et ce correctif ne doit rien y changer.
    /// </summary>
    [Fact]
    public void Une_planche_identite_sur_un_6x4_ne_bouge_pas()
    {
        Assert.False(DnpDriver.DoitPivoter(DnpMediaSize.Size6x4, 1844, 1240));
    }

    /// <summary>
    /// Le 6×8 est un format DEBOUT : c'est l'inverse. Une trame couchée doit y pivoter, une
    /// trame debout non — sans quoi le correctif casserait le rouleau d'Arcueil.
    /// </summary>
    [Theory]
    [InlineData(1844, 2492, false)]   // debout sur un 6x8 : rien à faire
    [InlineData(2492, 1844, true)]    // couchée sur un 6x8 : à pivoter
    public void Le_6x8_est_un_format_debout(int largeur, int hauteur, bool attendu)
    {
        Assert.Equal(attendu, DnpDriver.DoitPivoter(DnpMediaSize.Size6x8, largeur, hauteur));
    }

    /// <summary>
    /// Un format CARRÉ n'a pas de sens à défendre, et une image carrée non plus : on ne
    /// pivote pas pour rien.
    /// </summary>
    [Fact]
    public void Un_format_ou_une_image_carres_ne_pivotent_pas()
    {
        Assert.False(DnpDriver.DoitPivoter(DnpMediaSize.Size6x6, 1240, 1844));
        Assert.False(DnpDriver.DoitPivoter(DnpMediaSize.Size6x4, 1500, 1500));
    }

    /// <summary>
    /// ⚠ Format INCONNU : on ne pivote pas. Deviner le sens de la trame sur une machine
    /// qu'on n'a jamais vue coûterait une feuille à chaque tirage.
    /// </summary>
    [Theory]
    [InlineData(DnpMediaSize.None)]
    [InlineData(DnpMediaSize.PostcardRewind)]
    public void Sur_un_format_inconnu_on_ne_pivote_pas(DnpMediaSize taille)
    {
        Assert.False(DnpDriver.DoitPivoter(taille, 1240, 1844));
    }

    /// <summary>Une image sans dimensions ne fait rien lever.</summary>
    [Fact]
    public void Une_image_vide_ne_pivote_pas()
    {
        Assert.False(DnpDriver.DoitPivoter(DnpMediaSize.Size6x4, 0, 0));
    }
}
