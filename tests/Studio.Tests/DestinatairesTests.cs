using Studio.Core.Mail;

namespace Studio.Tests;

/// <summary>
/// Lecture d'une saisie d'adresses au comptoir. L'opérateur tape ce qu'on lui dicte, et le
/// séparateur n'est jamais le même — refuser la saisie pour ce motif ferait perdre du temps
/// devant le client.
/// </summary>
public class DestinatairesTests
{
    [Theory]
    [InlineData("a@x.fr;b@y.fr")]
    [InlineData("a@x.fr, b@y.fr")]
    [InlineData("a@x.fr b@y.fr")]
    [InlineData("a@x.fr\nb@y.fr")]
    [InlineData("  a@x.fr ;; b@y.fr  ")]
    public void Les_quatre_facons_de_separer_donnent_les_memes_adresses(string saisie)
    {
        Assert.Equal(["a@x.fr", "b@y.fr"], Destinataires.Analyser(saisie));
    }

    /// <summary>Une adresse tapée deux fois est une faute de frappe, pas deux envois.</summary>
    [Fact]
    public void Un_doublon_est_ecarte_sans_regarder_la_casse()
    {
        Assert.Equal(["Client@Exemple.fr"],
            Destinataires.Analyser("Client@Exemple.fr ; client@exemple.fr"));
    }

    [Fact]
    public void Une_saisie_vide_ne_donne_aucune_adresse()
    {
        Assert.Empty(Destinataires.Analyser(null));
        Assert.Empty(Destinataires.Analyser("   "));
    }

    /// <summary>L'ordre compte : la première adresse part en « À », les autres en copie cachée.</summary>
    [Fact]
    public void L_ordre_de_saisie_est_conserve()
    {
        Assert.Equal(["premier@x.fr", "second@y.fr", "tiers@z.fr"],
            Destinataires.Analyser("premier@x.fr;second@y.fr;tiers@z.fr"));
    }

    [Theory]
    [InlineData("client@exemple.fr", true)]
    [InlineData("prenom.nom@sous.domaine.co.uk", true)]
    [InlineData("client", false)]           // pas d'arobase
    [InlineData("@exemple.fr", false)]      // rien devant
    [InlineData("client@exemple", false)]   // pas de point dans le domaine
    [InlineData("client@.fr", false)]       // domaine qui commence par un point
    [InlineData("client@exemple.", false)]  // domaine qui finit par un point
    [InlineData("a@b@exemple.fr", false)]   // deux arobases : deux adresses collées
    public void Une_adresse_manifestement_fausse_est_reperee(string adresse, bool recevable)
    {
        Assert.Equal(recevable, Destinataires.Recevable(adresse));
    }

    /// <summary>
    /// Les fautes sont NOMMÉES : sur trois adresses dont une fausse, griser le bouton sans
    /// un mot ne dit pas laquelle reprendre.
    /// </summary>
    [Fact]
    public void Seules_les_adresses_douteuses_sont_signalees()
    {
        Assert.Equal(["oups"], Destinataires.Douteuses("bon@x.fr ; oups ; autre@y.fr"));
        Assert.Empty(Destinataires.Douteuses("bon@x.fr ; autre@y.fr"));
    }
}
