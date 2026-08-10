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
    /// <b>Le défaut de Créteil.</b> Une planche 10×15 rendue à 300 ppp fait 6,15 × 4,13
    /// pouces ; sur un rouleau 15×20, il faut réclamer la découpe, sinon la machine sort la
    /// feuille entière et l'autre moitié part à la poubelle.
    /// </summary>
    [Theory]
    [InlineData(6.15, 4.13)]  // planche d'identité en paysage
    [InlineData(4.13, 6.15)]  // la même en portrait
    public void Un_10x15_sur_un_rouleau_15x20_se_fait_couper(double largeur, double hauteur)
    {
        Assert.Equal(
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
}
