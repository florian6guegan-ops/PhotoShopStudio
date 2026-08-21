using Studio.Core.Mail;

namespace Studio.Tests;

/// <summary>
/// Les étiquettes des mots prédéfinis : « Bonjour {nom}, voici vos photos ».
///
/// Demandé le 22/08/2026 pour l'envoi par Dropbox — « la possibilité de faire des messages
/// prédéfinis, avec le prénom ou le nom de la personne ». La liste existait déjà pour
/// l'envoi par courriel ; ce qui manquait, c'est le nom dedans.
/// </summary>
public class MessagesPredefinisTests
{
    [Fact]
    public void Le_nom_prend_la_place_de_letiquette()
    {
        Assert.Equal(
            "Bonjour Marie, voici vos photos.",
            MailMessages.Appliquer("Bonjour {nom}, voici vos photos.", "Marie"));
    }

    [Theory]
    [InlineData("{nom}")]
    [InlineData("{Nom}")]
    [InlineData("{NOM}")]
    [InlineData("{prenom}")]
    [InlineData("{prénom}")]
    [InlineData("{client}")]
    public void Toutes_les_facons_de_lecrire_menent_au_meme_endroit(string etiquette)
    {
        // l'opérateur la tape de mémoire : ni la casse ni l'accent ne doivent le punir —
        // il ne verrait pas l'étiquette intacte, il verrait le client la recevoir
        Assert.Equal("Bonjour Marie.", MailMessages.Appliquer($"Bonjour {etiquette}.", "Marie"));
    }

    /// <summary>
    /// <b>Le cas le plus fréquent</b> : le champ est facultatif, et la virgule qui suit
    /// l'étiquette ne doit pas se retrouver à flotter dans un courriel déjà parti.
    /// </summary>
    [Fact]
    public void Sans_nom_letiquette_et_son_espace_disparaissent()
    {
        Assert.Equal(
            "Bonjour, voici vos photos.",
            MailMessages.Appliquer("Bonjour {nom}, voici vos photos.", null));

        Assert.Equal(
            "Bonjour, voici vos photos.",
            MailMessages.Appliquer("Bonjour {nom}, voici vos photos.", "   "));
    }

    [Fact]
    public void Une_etiquette_en_tete_de_phrase_disparait_aussi()
    {
        // pas d'espace avant : l'étiquette part seule, sans manger le mot qui suit
        Assert.Equal("Merci !", MailMessages.Appliquer("{nom} Merci !", null));
    }

    [Fact]
    public void Le_magasin_a_son_etiquette()
    {
        Assert.Equal(
            "À bientôt chez Photo Concept.",
            MailMessages.Appliquer("À bientôt chez {magasin}.", null, "Photo Concept"));
    }

    [Fact]
    public void Le_nom_est_deshabille_de_ses_espaces()
    {
        Assert.Equal("Bonjour Marie.", MailMessages.Appliquer("Bonjour {nom}.", "  Marie  "));
    }

    [Fact]
    public void Plusieurs_etiquettes_dans_la_meme_phrase()
    {
        Assert.Equal(
            "Marie, merci de votre visite chez Photo Concept, Marie !",
            MailMessages.Appliquer("{nom}, merci de votre visite chez {magasin}, {prenom} !",
                "Marie", "Photo Concept"));
    }

    /// <summary>
    /// On ne devine pas ce qu'un opérateur a voulu écrire : une étiquette inconnue reste
    /// visible plutôt que d'être effacée en silence, ce qui tronquerait la phrase.
    /// </summary>
    [Fact]
    public void Une_etiquette_inconnue_reste_telle_quelle()
    {
        Assert.Equal(
            "Votre commande {numero} est prête.",
            MailMessages.Appliquer("Votre commande {numero} est prête.", "Marie"));
    }

    [Fact]
    public void Un_texte_vide_reste_vide()
    {
        Assert.Equal("", MailMessages.Appliquer(null, "Marie"));
        Assert.Equal("", MailMessages.Appliquer("   ", "Marie"));
    }

    [Fact]
    public void Un_message_sans_etiquette_ne_bouge_pas()
    {
        const string mot = "Merci de votre visite, et à bientôt au studio.";
        Assert.Equal(mot, MailMessages.Appliquer(mot, "Marie", "Photo Concept"));
    }

    /// <summary>Le modèle livré porte l'étiquette : c'est ainsi qu'on découvre qu'elle existe.</summary>
    [Fact]
    public void Un_modele_livre_montre_letiquette()
    {
        Assert.Contains(MailMessages.Defaults, m => m.Texte.Contains("{nom}"));
    }

    // — ce que le client lit vraiment —

    /// <summary>
    /// <b>La formule d'appel est la vraie réponse à la demande.</b> L'écran Dropbox passait
    /// <c>null</c> comme nom : le client lisait « Bonjour, » tout court alors que
    /// <see cref="PhotoMailer"/> savait écrire son nom depuis toujours.
    /// </summary>
    [Fact]
    public void Le_courriel_du_lien_appelle_le_client_par_son_nom()
    {
        var corps = Studio.Printing.PhotoMailer.ApercuDuLien(
            "https://www.dropbox.com/scl/fo/abc", photos: 12, nomClient: "Marie",
            mot: MailMessages.Appliquer("Merci de votre confiance {nom}, et à très bientôt.", "Marie"),
            joursDeValidite: 3, protege: false, magasin: "Photo Concept");

        Assert.StartsWith("Bonjour Marie,", corps);
        Assert.Contains("Vos 12 photos sont prêtes à télécharger", corps);
        Assert.Contains("Merci de votre confiance Marie, et à très bientôt.", corps);
        Assert.Contains("Photo Concept", corps);
    }

    /// <summary>Sans nom, la formule neutre — et aucune accolade ne fuit jusqu'au client.</summary>
    [Fact]
    public void Sans_nom_le_courriel_reste_correct()
    {
        var corps = Studio.Printing.PhotoMailer.ApercuDuLien(
            "https://www.dropbox.com/scl/fo/abc", photos: 1, nomClient: null,
            mot: MailMessages.Appliquer("Merci de votre confiance {nom}, et à très bientôt.", null),
            joursDeValidite: 3, protege: false, magasin: "Photo Concept");

        Assert.StartsWith("Bonjour,", corps);
        Assert.Contains("Merci de votre confiance, et à très bientôt.", corps);
        Assert.DoesNotContain("{", corps);
    }
}
