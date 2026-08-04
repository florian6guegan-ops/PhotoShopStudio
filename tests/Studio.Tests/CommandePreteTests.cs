using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Le courriel qui annonce au client que sa commande l'attend en magasin.
///
/// <b>Il ne livre rien.</b> Aucune pièce jointe : joindre les photos reviendrait à les
/// donner sans les vendre, alors que l'envoi des fichiers est une prestation à part,
/// facturée. Ces essais portent sur le TEXTE, qui est ce que le client lira.
/// </summary>
public class CommandePreteTests
{
    private static string Message(
        string numero = "04-042", string quoi = "24 × 10x15",
        string? nom = "Dupont", string? mot = null, string magasin = "Photoconcept") =>
        PhotoMailer.ApercuCommandePrete(numero, quoi, nom, mot, magasin);

    [Fact]
    public void Le_message_nomme_la_commande_et_son_contenu()
    {
        var texte = Message();

        Assert.Contains("04-042", texte, StringComparison.Ordinal);
        Assert.Contains("24 × 10x15", texte, StringComparison.Ordinal);
        Assert.Contains("prête", texte, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("magasin", texte, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Le_client_est_appele_par_son_nom_quand_on_le_connait()
    {
        Assert.Contains("Bonjour Dupont", Message(nom: "Dupont"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Sans nom — le cas d'une commande de borne — la formule reste neutre plutôt que de
    /// laisser « Bonjour , » avec sa virgule orpheline.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sans_nom_la_formule_reste_neutre(string? nom)
    {
        var texte = Message(nom: nom);

        Assert.Contains("Bonjour,", texte, StringComparison.Ordinal);
        Assert.DoesNotContain("Bonjour ,", texte, StringComparison.Ordinal);
    }

    [Fact]
    public void Le_mot_de_l_operateur_est_repris_tel_quel()
    {
        var texte = Message(mot: "Pensez à apporter votre carte de fidélité.");

        Assert.Contains("carte de fidélité", texte, StringComparison.Ordinal);
    }

    /// <summary>Sans mot ajouté, pas de ligne vide surnuméraire au milieu du message.</summary>
    [Fact]
    public void Sans_mot_le_message_reste_compact()
    {
        var texte = Message(mot: "   ");

        Assert.DoesNotContain("\n\n\n", texte, StringComparison.Ordinal);
    }

    [Fact]
    public void Le_message_est_signe_du_magasin()
    {
        Assert.Contains("Photoconcept", Message(), StringComparison.Ordinal);
    }

    /// <summary>Sans nom d'expéditeur configuré, la signature reste correcte.</summary>
    [Fact]
    public void Sans_nom_de_magasin_la_signature_reste_correcte()
    {
        var texte = Message(magasin: "");

        Assert.Contains("Le magasin", texte, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>L'aperçu et l'envoi partagent la MÊME méthode.</b> Ce test le verrouille : deux
    /// textes entretenus séparément finiraient par différer, et l'opérateur relirait autre
    /// chose que ce qui part chez le client.
    /// </summary>
    [Fact]
    public void L_apercu_est_stable_pour_les_memes_donnees()
    {
        Assert.Equal(Message(), Message());
    }
}
