using Studio.Core.Cloud;
using Studio.Web.Dropbox;

namespace Studio.Tests;

/// <summary>
/// Envoi des photos au client par Dropbox : ce qui se décide SANS réseau — l'état de la
/// configuration, la normalisation des chemins, et les noms donnés au dossier et aux
/// fichiers.
///
/// Les appels à Dropbox ne sont pas simulés : ce qu'ils font est trivial (un POST) et ce
/// qui casse dans la vraie vie, ce sont les noms — un accent, une barre oblique, un dossier
/// racine écrit à la Windows.
/// </summary>
public class DropboxTransferTests
{
    // ----- état de la configuration -----

    [Fact]
    public void Sans_cle_l_envoi_n_est_pas_utilisable()
    {
        var reglages = new DropboxSettings(Actif: true);

        Assert.False(reglages.EstUtilisable);
        Assert.Contains("clé", reglages.CeQuiManque());
    }

    /// <summary>
    /// Une clé sans autorisation est le cas de l'installation à moitié faite : l'écran doit
    /// pouvoir dire d'appuyer sur « Connecter », et non « il manque la clé ».
    /// </summary>
    [Fact]
    public void Une_cle_sans_jeton_demande_l_autorisation()
    {
        var reglages = new DropboxSettings(AppKey: "abc123", Actif: true);

        Assert.False(reglages.EstUtilisable);
        Assert.True(reglages.AutorisationManquante);
        Assert.Contains("autorisation", reglages.CeQuiManque());
    }

    /// <summary>
    /// Le drapeau ne suffit pas : des réglages complets mais désactivés doivent le dire, et
    /// non laisser croire que ça marchera devant le client.
    /// </summary>
    [Fact]
    public void Des_reglages_complets_mais_eteints_ne_sont_pas_utilisables()
    {
        var reglages = new DropboxSettings(AppKey: "abc", RefreshToken: "jeton", Actif: false);

        Assert.False(reglages.EstUtilisable);
        Assert.Equal("", new DropboxSettings(AppKey: "abc", RefreshToken: "jeton", Actif: true).CeQuiManque());
    }

    // ----- chemins -----

    /// <summary>
    /// Dropbox veut une barre oblique DEVANT et aucune derrière. L'opérateur, lui, tape ce
    /// qui lui vient — y compris des antislashs, puisqu'il travaille sous Windows.
    /// </summary>
    [Theory]
    [InlineData("/Studio Photo", "/Studio Photo")]
    [InlineData("Studio Photo", "/Studio Photo")]
    [InlineData("/Studio Photo/", "/Studio Photo")]
    [InlineData("\\Studio Photo\\Clients", "/Studio Photo/Clients")]
    [InlineData("  /Studio Photo  ", "/Studio Photo")]
    public void La_racine_est_normalisee_comme_dropbox_l_attend(string saisi, string attendu) =>
        Assert.Equal(attendu, new DropboxSettings(DossierRacine: saisi).RacineNormalisee());

    /// <summary>La racine du compte s'écrit par une chaîne VIDE, et non par « / ».</summary>
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("   ")]
    public void La_racine_du_compte_est_une_chaine_vide(string saisi) =>
        Assert.Equal("", new DropboxSettings(DossierRacine: saisi).RacineNormalisee());

    // ----- noms -----

    /// <summary>
    /// La date en tête, pour qu'un tri alphabétique du Dropbox donne l'ordre chronologique :
    /// c'est ainsi qu'on retrouve « l'envoi de mardi » sans se rappeler du nom du client.
    /// </summary>
    [Fact]
    public void Le_dossier_porte_la_date_avant_le_nom()
    {
        var nom = DropboxTransfer.NomDeDossier("Mariage Dupont");

        Assert.StartsWith(DateTime.Now.ToString("yyyy-MM-dd"), nom);
        Assert.EndsWith("Mariage Dupont", nom);
    }

    /// <summary>Sans nom de lot, la date se suffit à elle-même — pas de tiret qui pend.</summary>
    [Fact]
    public void Un_lot_sans_nom_ne_garde_que_la_date()
    {
        var nom = DropboxTransfer.NomDeDossier("   ");

        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd HHmm"), nom);
    }

    /// <summary>
    /// Les ACCENTS restent : un studio français nomme ses dossiers « Séance Dupont », et les
    /// remplacer donnerait au client un nom illisible. Ils voyagent sans problème une fois
    /// l'en-tête de l'API échappé.
    /// </summary>
    [Fact]
    public void Les_accents_sont_conserves()
    {
        Assert.Contains("Séance été", DropboxTransfer.NomDeDossier("Séance été"));
        Assert.Equal("communión.jpg", DropboxTransfer.NomDeFichier("communión.jpg"));
    }

    /// <summary>
    /// Ce que Dropbox refuse est remplacé, et non retiré : une barre oblique laissée telle
    /// quelle créerait un SOUS-DOSSIER là où on voulait un nom.
    /// </summary>
    [Theory]
    [InlineData("Dupont/Martin", "Dupont Martin")]
    [InlineData("Photo: 2026", "Photo  2026")]
    [InlineData("Client <test>", "Client  test ")]
    public void Les_caracteres_refuses_par_dropbox_sont_remplaces(string saisi, string attendu) =>
        Assert.EndsWith(attendu.Trim(), DropboxTransfer.NomDeDossier(saisi));

    /// <summary>Un nom qui finit par un point fait échouer la création côté Dropbox.</summary>
    [Fact]
    public void Un_nom_ne_finit_jamais_par_un_point() =>
        Assert.DoesNotContain(".", DropboxTransfer.NomDeDossier("Dupont...")[^1..]);

    /// <summary>L'extension est GARDÉE : sans elle, le client reçoit un fichier qui ne s'ouvre pas.</summary>
    [Fact]
    public void Le_fichier_garde_son_extension()
    {
        Assert.Equal("DSC_0042.jpg", DropboxTransfer.NomDeFichier("DSC_0042.jpg"));
        Assert.Equal("photo.jpg", DropboxTransfer.NomDeFichier("///.jpg"));
    }

    // ----- ménage automatique -----

    /// <summary>
    /// Le ménage ne reconnaît QUE les dossiers qu'il a lui-même créés. C'est le garde-fou :
    /// le studio range ce qu'il veut sous la racine, et rien de ce qui ne porte pas notre
    /// date en tête ne doit jamais être supprimé.
    /// </summary>
    [Theory]
    [InlineData("2026-08-04 1630 — Mariage Dupont")]
    [InlineData("2026-08-04 1630")]
    public void Le_menage_reconnait_ses_propres_dossiers(string nom)
    {
        var date = DropboxMenage.DateDuDossier(nom);

        Assert.NotNull(date);
        Assert.Equal(new DateTime(2026, 8, 4, 16, 30, 0), date);
    }

    /// <summary>
    /// Tout ce qui n'est pas de nous est ÉPARGNÉ, et pas seulement « probablement » : c'est
    /// ce qui sépare un ménage d'une perte de données. Un dossier d'archives rangé à la
    /// main sous la racine doit y rester.
    /// </summary>
    [Theory]
    [InlineData("Archives")]
    [InlineData("Mariage Durand")]
    [InlineData("04-08-2026 Dupont")]      // la date, mais pas notre format
    [InlineData("2026-13-45 9999")]        // notre format, mais pas une date
    [InlineData("2026-08-04")]             // notre date sans l'heure : trop court
    [InlineData("")]
    public void Le_menage_epargne_ce_qui_n_est_pas_de_lui(string nom) =>
        Assert.Null(DropboxMenage.DateDuDossier(nom));

    /// <summary>
    /// Le nom que l'envoi écrit doit être celui que le ménage sait relire. Les deux vivent
    /// dans des fichiers différents et rien ne les tient ensemble — sauf cette épreuve :
    /// une divergence ferait un ménage qui ne supprime plus jamais rien, en silence.
    /// </summary>
    [Fact]
    public void Le_menage_relit_ce_que_l_envoi_ecrit()
    {
        var nom = DropboxTransfer.NomDeDossier("Séance Dupont");
        var date = DropboxMenage.DateDuDossier(nom);

        Assert.NotNull(date);
        Assert.Equal(DateTime.Now.ToString("yyyy-MM-dd HHmm"), date.Value.ToString("yyyy-MM-dd HHmm"));
    }

    /// <summary>Sans rétention réglée, le ménage ne touche à rien — même bien configuré.</summary>
    [Fact]
    public async Task Une_retention_nulle_ne_supprime_rien()
    {
        var reglages = new DropboxSettings(
            AppKey: "abc", RefreshToken: "jeton", Actif: true, RetentionJours: 0);

        var bilan = await DropboxMenage.FaireLeMenageAsync(reglages);

        Assert.Equal(0, bilan.Supprimes);
    }

    /// <summary>
    /// Un ménage sur des réglages incomplets rend un bilan vide au lieu de lever : il tourne
    /// en tâche de fond au démarrage, et rien de ce qu'il fait ne doit remonter jusqu'à un
    /// opérateur qui est en train de servir quelqu'un.
    /// </summary>
    [Fact]
    public async Task Un_menage_non_configure_ne_leve_pas()
    {
        var bilan = await DropboxMenage.FaireLeMenageAsync(new DropboxSettings());

        Assert.Equal(new DropboxMenage.Bilan(0, 0, 0), bilan);
    }

    /// <summary>Trois jours, c'est le réglage voulu par la boutique (05/08/2026).</summary>
    [Fact]
    public void La_retention_vaut_trois_jours_par_defaut() =>
        Assert.Equal(3, new DropboxSettings().RetentionJours);

    // ----- refus attendus -----

    [Fact]
    public async Task Un_envoi_non_configure_est_refuse_avec_ce_qui_manque()
    {
        var erreur = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DropboxTransfer.EnvoyerAsync(new DropboxSettings(), ["photo.jpg"], "Test"));

        Assert.Contains("clé", erreur.Message);
    }

    /// <summary>
    /// Un lot vide est refusé AVANT tout appel réseau : créer un dossier puis un lien de
    /// partage sur rien du tout donnerait au client un lien vers un dossier vide.
    /// </summary>
    [Fact]
    public async Task Un_lot_vide_est_refuse_avant_d_appeler_dropbox()
    {
        var reglages = new DropboxSettings(AppKey: "abc", RefreshToken: "jeton", Actif: true);

        var erreur = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DropboxTransfer.EnvoyerAsync(reglages, [], "Test"));

        Assert.Contains("Aucune photo", erreur.Message);
    }

    // ----- permission manquante -----

    /// <summary>
    /// Le premier mur d'une installation neuve : on crée l'application, on connecte le
    /// compte, et on découvre au premier envoi qu'une case n'était pas cochée. Le message
    /// de Dropbox est long, en anglais, et n'indique PAS les deux gestes qui débloquent —
    /// appuyer sur Submit, puis reconnecter le compte.
    /// </summary>
    [Fact]
    public void Une_permission_manquante_dit_quoi_faire()
    {
        const string refus =
            "Error in call to API function \"files/create_folder_v2\": Your app (ID: 7950163) " +
            "is not permitted to access this endpoint because it does not have the required " +
            "scope 'files.content.write'. The owner of the app can enable the scope for the " +
            "app using the Permissions tab on the App Console.";

        var message = DropboxClient.PermissionManquante(refus);

        Assert.NotNull(message);
        Assert.Contains("files.content.write", message);   // la permission est nommée
        Assert.Contains("SUBMIT", message);                // le geste qu'on oublie
        Assert.Contains("Connecter le compte", message);   // celui qu'on ignore
    }

    /// <summary>
    /// Un refus ORDINAIRE ne doit pas être pris pour une permission manquante : renvoyer
    /// l'opérateur dans la console Dropbox pour un compte plein lui ferait perdre sa
    /// journée.
    /// </summary>
    [Theory]
    [InlineData("{\"error_summary\": \"insufficient_space/...\"}")]
    [InlineData("{\"error_summary\": \"expired_access_token/\"}")]
    [InlineData("")]
    public void Un_refus_ordinaire_n_est_pas_une_permission_manquante(string corps) =>
        Assert.Null(DropboxClient.PermissionManquante(corps));

    // ----- autorisation -----

    /// <summary>
    /// PKCE : chaque autorisation fabrique son propre aléa, et l'adresse porte le condensé
    /// et non l'aléa lui-même. Deux demandes d'affilée ne doivent rien avoir en commun.
    /// </summary>
    [Fact]
    public void Chaque_autorisation_a_son_propre_alea()
    {
        var une = DropboxAuth.Preparer("cle-de-test");
        var deux = DropboxAuth.Preparer("cle-de-test");

        Assert.NotEqual(une.CodeVerifier, deux.CodeVerifier);
        Assert.NotEqual(une.Url, deux.Url);
        Assert.DoesNotContain(une.CodeVerifier, une.Url);
    }

    /// <summary>
    /// <c>token_access_type=offline</c> est ce qui fait rendre un jeton DURABLE. Sans lui,
    /// l'autorisation ne vaudrait que quatre heures et serait à refaire à chaque envoi.
    /// </summary>
    [Fact]
    public void L_adresse_demande_un_jeton_durable_et_du_pkce()
    {
        var demande = DropboxAuth.Preparer("cle-de-test");

        Assert.Contains("token_access_type=offline", demande.Url);
        Assert.Contains("code_challenge_method=S256", demande.Url);
        Assert.Contains("client_id=cle-de-test", demande.Url);
    }

    [Fact]
    public void Une_autorisation_sans_cle_est_refusee() =>
        Assert.Throws<ArgumentException>(() => DropboxAuth.Preparer("  "));
}
