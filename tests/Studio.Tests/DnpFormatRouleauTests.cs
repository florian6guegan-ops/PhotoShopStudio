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
    /// En fonctionnement normal, c'est la machine qui parle : elle seule sait à quelle
    /// définition elle compose.
    /// </summary>
    [Fact]
    public void La_definition_de_la_machine_l_emporte()
    {
        Assert.Equal((600.0, 600.0),
            DnpDriver.DefinitionRetenue(machineH: 600, machineV: 600, fichierH: 300, fichierV: 300));
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
}
