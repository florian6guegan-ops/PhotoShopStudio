using Studio.Core.Mail;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Le courriel qui porte le lien de téléchargement (voir <c>DropboxTransfer</c>).
///
/// On vérifie le TEXTE que le client lira, parce que c'est lui qui décide s'il télécharge
/// à temps — et parce qu'une promesse fausse sur l'expiration coûte un rappel au magasin.
/// </summary>
public class CourrielDuLienTests
{
    private const string Lien = "https://www.dropbox.com/scl/fo/exemple";

    private static string Corps(
        int photos = 12, string? nom = null, string? mot = null,
        int? expiration = null, bool protege = false) =>
        PhotoMailer.ApercuDuLien(Lien, photos, nom, mot, expiration, protege, "Photoconcept");

    [Fact]
    public void Le_lien_figure_dans_le_message() => Assert.Contains(Lien, Corps());

    [Fact]
    public void Le_nombre_de_photos_est_annonce() => Assert.Contains("12 photos", Corps());

    /// <summary>Une seule photo ne s'annonce pas au pluriel : ça se voit, et ça fait négligé.</summary>
    [Fact]
    public void Une_seule_photo_est_au_singulier()
    {
        var corps = Corps(photos: 1);

        Assert.Contains("Votre photo est prête", corps);
        Assert.DoesNotContain("1 photos", corps);
    }

    [Fact]
    public void Le_nom_du_client_est_repris_quand_il_est_connu()
    {
        Assert.Contains("Bonjour Madame Dupont,", Corps(nom: "Madame Dupont"));
        Assert.Contains("Bonjour,", Corps(nom: null));
    }

    /// <summary>
    /// L'expiration est annoncée AVEC sa date : « 30 jours » oblige le client à compter,
    /// et un lien mort découvert trois semaines plus tard fait rappeler le magasin.
    /// </summary>
    [Fact]
    public void L_expiration_est_annoncee_avec_sa_date()
    {
        var corps = Corps(expiration: 30);

        Assert.Contains("30 jours", corps);
        Assert.Contains(DateTime.Now.AddDays(30).ToString("dd/MM/yyyy"), corps);
    }

    /// <summary>
    /// Sans aucune échéance connue, on ne promet RIEN et on invite à enregistrer : c'est la
    /// seule chose honnête à dire.
    /// </summary>
    [Fact]
    public void Sans_echeance_on_invite_a_enregistrer()
    {
        var corps = Corps(expiration: null);

        Assert.DoesNotContain("jours", corps);
        Assert.Contains("enregistrer", corps);
    }

    /// <summary>Une échéance à un jour ne s'écrit pas « 1 jours ».</summary>
    [Fact]
    public void Une_echeance_d_un_jour_est_au_singulier()
    {
        var corps = Corps(expiration: 1);

        Assert.Contains("valable 1 jour ", corps);
        Assert.DoesNotContain("1 jours", corps);
    }

    /// <summary>Une expiration à zéro vaut « pas d'expiration », pas « expire dans 0 jour ».</summary>
    [Fact]
    public void Une_expiration_nulle_ne_promet_pas_zero_jour() =>
        Assert.DoesNotContain("0 jours", Corps(expiration: 0));

    /// <summary>
    /// <b>Le mot de passe du lien n'est JAMAIS dans le message.</b> Mettre la serrure et la
    /// clé dans la même enveloppe ne protège de rien : le mot de passe se donne de vive
    /// voix au comptoir. Le message signale seulement qu'il en faut un.
    /// </summary>
    [Fact]
    public void Le_mot_de_passe_n_est_jamais_ecrit()
    {
        var corps = Corps(protege: true);

        Assert.Contains("mot de passe qui vous a été communiqué", corps);
        // la signature de la methode ne prend meme pas le mot de passe : rien ne peut fuir
        Assert.DoesNotContain("secret", corps, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sans_protection_aucun_mot_de_passe_n_est_mentionne() =>
        Assert.DoesNotContain("mot de passe", Corps(protege: false));

    [Fact]
    public void Le_mot_de_l_operateur_est_repris() =>
        Assert.Contains("Merci de votre visite", Corps(mot: "Merci de votre visite"));

    [Fact]
    public void Le_nom_du_magasin_signe_le_message() => Assert.EndsWith("Photoconcept", Corps());

    // ----- refus attendus -----

    /// <summary>
    /// Un envoi non configuré est refusé AVEC ce qui manque : l'opérateur vient de
    /// téléverser plusieurs centaines de mégaoctets, il doit savoir quoi corriger.
    /// </summary>
    [Fact]
    public void Un_courriel_non_configure_est_refuse_en_clair()
    {
        var erreur = Assert.Throws<InvalidOperationException>(() =>
            PhotoMailer.EnvoyerLeLien(new MailSettings(), ["client@exemple.fr"], Lien, 3));

        Assert.Contains("Paramètres", erreur.Message);
    }

    /// <summary>Sans destinataire, on ne construit même pas le message.</summary>
    [Fact]
    public void Un_envoi_sans_destinataire_est_refuse()
    {
        var reglages = new MailSettings(
            Expediteur: "studio@exemple.fr", MotDePasseApplication: "x", Actif: true);

        var erreur = Assert.Throws<InvalidOperationException>(() =>
            PhotoMailer.EnvoyerLeLien(reglages, [], Lien, 3));

        Assert.Contains("Aucune adresse", erreur.Message);
    }

    [Fact]
    public void Un_lien_vide_est_refuse() =>
        Assert.Throws<ArgumentException>(() =>
            PhotoMailer.EnvoyerLeLien(new MailSettings(), ["client@exemple.fr"], "  ", 3));
}
