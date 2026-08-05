using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Core.Cloud;

/// <summary>
/// Réglages de l'envoi des photos au client par Dropbox.
///
/// <b>Pourquoi Dropbox et pas « Dropbox Transfer ».</b> Transfer est une fonction de leur
/// site, sans API : rien ne permet d'en créer un depuis un programme, et la demande traîne
/// sur leur forum développeurs depuis des années. Les autres services de transfert ne font
/// pas mieux — WeTransfer a retiré son API publique en mai 2022, SwissTransfer n'en a
/// jamais eu, et celle de Smash est réservée aux offres payantes.
///
/// Ce qui EST gratuit et automatisable, c'est l'API Dropbox v2 : on téléverse dans un
/// dossier daté puis on crée un lien de partage. Le client reçoit un lien, télécharge son
/// dossier, et n'a pas de compte à créer. Deux options du lien — mot de passe et date
/// d'expiration — demandent en revanche un compte Professional ; sur un compte gratuit
/// elles sont refusées par le serveur, et le lien sort permanent (voir
/// <see cref="ExpirationJours"/>).
///
/// Les réglages vivent dans le dossier de DONNÉES et jamais dans le dépôt : celui-ci est
/// public sur GitHub, et un jeton poussé par mégarde ne se rattrape pas — il reste dans
/// l'historique.
/// </summary>
/// <param name="AppKey">
/// Clé de l'application Dropbox, créée une fois sur <c>dropbox.com/developers/apps</c>.
/// Ce n'est PAS un secret : le flux PKCE est fait pour les applications de bureau, où rien
/// de confidentiel ne peut être gardé. Il n'y a donc pas de « app secret » à stocker ici.
/// </param>
/// <param name="RefreshToken">
/// Jeton de rafraîchissement obtenu à l'autorisation. C'est LUI qui vaut mot de passe : il
/// ne périme pas et redonne un jeton d'accès à la demande.
/// </param>
/// <param name="DossierRacine">
/// Dossier de dépôt dans le Dropbox du studio. Chaque envoi y crée un sous-dossier daté.
/// </param>
/// <param name="ExpirationJours">
/// Jours avant l'expiration du lien. 0 = pas d'expiration. Refusé par Dropbox sur un compte
/// gratuit : l'envoi n'échoue pas pour autant, le lien sort simplement permanent.
/// </param>
/// <param name="MotDePasse">
/// Mot de passe du lien. Vide = aucun. Même réserve que l'expiration : compte payant requis.
/// </param>
/// <param name="RetentionJours">
/// Jours au bout desquels un dossier envoyé est SUPPRIMÉ du Dropbox du studio. 0 = jamais.
///
/// À ne pas confondre avec <see cref="ExpirationJours"/>, qui ne ferme que le lien et
/// demande un compte payant. Le ménage, lui, supprime les fichiers pour de bon, il ne coûte
/// rien, et c'est LUI qui empêche un compte de 2 Go de se remplir en trois semaines de
/// mariages. C'est aussi la seule chose qui marche sur un compte gratuit.
/// </param>
public sealed record DropboxSettings(
    string AppKey = "",
    string RefreshToken = "",
    string DossierRacine = "/Studio Photo",
    int ExpirationJours = 30,
    string MotDePasse = "",
    bool Actif = false,
    int RetentionJours = 3)
{
    /// <summary>Nom du fichier, dans le dossier de configuration.</summary>
    public const string FileName = "dropbox.json";

    /// <summary>
    /// Vrai si l'envoi est réellement utilisable. On ne se fie pas au seul drapeau
    /// <see cref="Actif"/> : un fichier à moitié rempli ne doit pas laisser croire que ça
    /// marchera devant le client.
    /// </summary>
    /// <remarks>
    /// Hors du fichier : c'est un CALCUL, et l'écrire ferait croire qu'on peut le forcer à
    /// la main. Sans setter, une valeur relue serait de toute façon ignorée.
    /// </remarks>
    [JsonIgnore]
    public bool EstUtilisable =>
        Actif && !string.IsNullOrWhiteSpace(AppKey) && !string.IsNullOrWhiteSpace(RefreshToken);

    /// <summary>Vrai si l'application est déclarée mais que personne n'a encore autorisé le compte.</summary>
    [JsonIgnore]
    public bool AutorisationManquante =>
        !string.IsNullOrWhiteSpace(AppKey) && string.IsNullOrWhiteSpace(RefreshToken);

    /// <summary>Ce qui manque pour envoyer, en clair. Vide si tout est là.</summary>
    public string CeQuiManque()
    {
        if (EstUtilisable) return "";

        if (string.IsNullOrWhiteSpace(AppKey))
            return "la clé de l'application Dropbox";
        if (string.IsNullOrWhiteSpace(RefreshToken))
            return "l'autorisation du compte Dropbox (bouton « Connecter »)";

        return "l'envoi par Dropbox est désactivé";
    }

    /// <summary>
    /// Le dossier racine, normalisé comme Dropbox l'attend : une barre oblique au début,
    /// aucune à la fin, la racine du compte s'écrivant en revanche par une chaîne VIDE.
    /// </summary>
    public string RacineNormalisee()
    {
        var chemin = (DossierRacine ?? "").Trim().Replace('\\', '/').TrimEnd('/');
        if (chemin.Length == 0) return "";
        return chemin.StartsWith('/') ? chemin : "/" + chemin;
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge les réglages. Un fichier absent ou abîmé rend des réglages VIDES plutôt que
    /// de lever : l'application doit démarrer même si personne n'a rien configuré.
    /// </summary>
    public static DropboxSettings Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return new DropboxSettings();

        try
        {
            using var flux = File.OpenRead(chemin);
            return JsonSerializer.Deserialize<DropboxSettings>(flux, Options) ?? new DropboxSettings();
        }
        catch (Exception)
        {
            return new DropboxSettings();
        }
    }

    /// <summary>Enregistre les réglages, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, DropboxSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(configDir);
        var chemin = Path.Combine(configDir, FileName);
        var json = JsonSerializer.Serialize(settings, Options);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
