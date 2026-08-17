using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// La désignation de « l'autre logiciel », socle de la bascule.
///
/// <b>La DNP d'Arcueil, le 14/08/2026 au soir.</b> Studio Photo et Studio Photo Identité
/// pilotent les machines par le même relais 32 bits, sur un tube nommé à instance unique : le
/// second à s'ouvrir se branche sur le relais du premier, et le jour où celui-là se ferme, il
/// l'emporte. L'autre garde une connexion morte et plus rien ne part — sans un mot.
///
/// D'où la règle « un seul des deux à la fois », puis la bascule : fermer l'autre et
/// continuer, d'un clic, au lieu de renvoyer l'opérateur le faire à la main.
///
/// <b>Ce qui est vérifié ici, et ce qui ne l'est pas.</b> Seule la désignation est testée —
/// elle est pure. <c>FermerLAutre</c> ne l'est PAS, et c'est délibéré : la suite tourne sur le
/// poste du comptoir, où Studio Photo est ouvert. Un test qui appellerait cette méthode
/// fermerait l'application de la boutique en pleine journée.
/// </summary>
public class UnSeulLogicielTests
{
    /// <summary>
    /// L'autre du Studio est Identité, et l'autre d'Identité est le Studio. C'est cette
    /// symétrie qui permet aux deux applications d'appeler la même séquence en ne donnant que
    /// leur propre nom.
    /// </summary>
    [Theory]
    [InlineData("Studio.App", "Studio.Identite")]
    [InlineData("Studio.Identite", "Studio.App")]
    public void Chacun_designe_l_autre(string moi, string attendu)
    {
        Assert.Equal(attendu, UnSeulLogiciel.LAutre(moi));
    }

    /// <summary>
    /// Appliquée deux fois, la désignation revient à son point de départ. Si elle cessait
    /// d'être symétrique, une des deux applications se croirait seule et la panne muette
    /// reviendrait par cette porte.
    /// </summary>
    [Theory]
    [InlineData("Studio.App")]
    [InlineData("Studio.Identite")]
    public void La_designation_est_symetrique(string moi)
    {
        Assert.Equal(moi, UnSeulLogiciel.LAutre(UnSeulLogiciel.LAutre(moi)));
    }

    /// <summary>
    /// La casse ne compte pas : le nom vient d'un appelant humain, pas du système.
    /// </summary>
    [Theory]
    [InlineData("studio.app", "Studio.Identite")]
    [InlineData("STUDIO.IDENTITE", "Studio.App")]
    public void La_casse_ne_change_rien(string moi, string attendu)
    {
        Assert.Equal(attendu, UnSeulLogiciel.LAutre(moi));
    }

    /// <summary>
    /// Les noms montrés à l'opérateur sont ceux de ses raccourcis, pas des noms
    /// d'exécutables : c'est lui qui doit reconnaître le logiciel à fermer.
    /// </summary>
    [Theory]
    [InlineData("Studio.App", "Studio Photo")]
    [InlineData("Studio.Identite", "Studio Photo Identité")]
    public void Le_nom_montre_est_celui_du_metier(string exe, string attendu)
    {
        Assert.Equal(attendu, UnSeulLogiciel.NomLisible(exe));
    }

    /// <summary>
    /// Bout à bout : ce que l'opérateur lira quand l'autre tourne. Le message nomme le
    /// logiciel à fermer, donc la chaîne désignation → nom lisible doit tenir.
    /// </summary>
    [Theory]
    [InlineData("Studio.App", "Studio Photo Identité")]
    [InlineData("Studio.Identite", "Studio Photo")]
    public void Le_message_nomme_bien_le_logiciel_a_fermer(string moi, string attendu)
    {
        Assert.Equal(attendu, UnSeulLogiciel.NomLisible(UnSeulLogiciel.LAutre(moi)));
    }
}
