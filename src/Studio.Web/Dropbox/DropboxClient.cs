using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Web.Dropbox;

/// <summary>
/// Le strict nécessaire de l'API Dropbox v2 : téléverser un fichier et en partager le
/// dossier.
///
/// Écrit à la main plutôt qu'avec le SDK officiel : trois appels HTTP suffisent, là où le
/// paquet <c>Dropbox.Api</c> ajouterait une dépendance de plusieurs mégaoctets et sa propre
/// chaîne de versions à suivre. Les appels sont ceux de la documentation HTTP, et les
/// messages d'erreur sont traduits pour le comptoir.
/// </summary>
public sealed class DropboxClient : IDisposable
{
    /// <summary>Journal optionnel, branché par l'application.</summary>
    public static Action<string>? Log { get; set; }

    private readonly HttpClient _http;

    /// <param name="accessToken">Jeton d'accès, obtenu par <see cref="DropboxAuth.JetonDAccesAsync"/>.</param>
    public DropboxClient(string accessToken)
    {
        _http = new HttpClient
        {
            // le téléversement d'un dossier de photos se compte en minutes sur une ligne de
            // magasin : le délai par défaut de 100 secondes couperait au milieu
            Timeout = TimeSpan.FromMinutes(30),
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Taille au-delà de laquelle il faut passer par une session de téléversement.
    ///
    /// Dropbox refuse <c>files/upload</c> au-delà de 150 Mo. Le seuil est pris plus bas pour
    /// garder de la marge, et parce qu'une session découpe l'envoi : une coupure réseau ne
    /// fait alors perdre qu'un morceau.
    /// </summary>
    private const long SeuilSessionOctets = 100L * 1024 * 1024;

    /// <summary>Taille d'un morceau en session. Assez grand pour ne pas multiplier les allers-retours.</summary>
    private const int MorceauOctets = 8 * 1024 * 1024;

    /// <summary>
    /// Téléverse un fichier. Les dossiers manquants sont créés par Dropbox lui-même.
    /// </summary>
    /// <param name="cheminLocal">Fichier à envoyer.</param>
    /// <param name="cheminDistant">Chemin visé dans le Dropbox, barres obliques comprises.</param>
    public async Task TeleverserAsync(string cheminLocal, string cheminDistant, CancellationToken ct = default)
    {
        var taille = new FileInfo(cheminLocal).Length;

        if (taille > SeuilSessionOctets)
        {
            await TeleverserParSessionAsync(cheminLocal, cheminDistant, ct);
            return;
        }

        // « add » et non « overwrite » : deux envois du même dossier dans la même minute ne
        // doivent pas s'écraser l'un l'autre. Dropbox renomme le second (« photo (1).jpg »),
        // ce qui est exactement ce qu'on veut — un client à qui l'on renvoie ses photos ne
        // doit pas perdre les premières.
        var arg = JsonSerializer.Serialize(new
        {
            path = cheminDistant,
            mode = "add",
            autorename = true,
            mute = true,
        });

        await using var flux = File.OpenRead(cheminLocal);
        using var contenu = new StreamContent(flux);
        contenu.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var requete = new HttpRequestMessage(HttpMethod.Post,
            "https://content.dropboxapi.com/2/files/upload") { Content = contenu };
        requete.Headers.Add("Dropbox-API-Arg", EnTeteAscii(arg));

        using var reponse = await _http.SendAsync(requete, ct);
        await VerifierAsync(reponse, $"téléversement de {Path.GetFileName(cheminLocal)}", ct);
    }

    /// <summary>
    /// Téléversement en session, pour les fichiers que <c>files/upload</c> refuse.
    ///
    /// Trois temps, comme le veut l'API : ouvrir la session sur le premier morceau,
    /// appendre les suivants, clore en donnant le chemin. C'est la clôture seule qui crée
    /// le fichier — une session interrompue ne laisse rien de visible dans le Dropbox du
    /// client.
    /// </summary>
    private async Task TeleverserParSessionAsync(string cheminLocal, string cheminDistant, CancellationToken ct)
    {
        await using var flux = File.OpenRead(cheminLocal);
        var tampon = new byte[MorceauOctets];

        var lu = await flux.ReadAtLeastAsync(tampon, tampon.Length, throwOnEndOfStream: false, ct);
        var sessionId = await OuvrirLaSessionAsync(tampon.AsMemory(0, lu), ct);
        long decalage = lu;

        while ((lu = await flux.ReadAtLeastAsync(tampon, tampon.Length, throwOnEndOfStream: false, ct)) > 0)
        {
            await AppendreAsync(sessionId, decalage, tampon.AsMemory(0, lu), ct);
            decalage += lu;
        }

        await CloreLaSessionAsync(sessionId, decalage, cheminDistant, ct);
        Log?.Invoke($"Dropbox : {Path.GetFileName(cheminLocal)} envoyé en session ({decalage / 1024 / 1024} Mo).");
    }

    private async Task<string> OuvrirLaSessionAsync(ReadOnlyMemory<byte> morceau, CancellationToken ct)
    {
        using var reponse = await EnvoyerDuContenuAsync(
            "https://content.dropboxapi.com/2/files/upload_session/start",
            JsonSerializer.Serialize(new { close = false }), morceau, ct);

        await VerifierAsync(reponse, "ouverture de la session de téléversement", ct);

        var session = await reponse.Content.ReadFromJsonAsync<SessionOuverte>(cancellationToken: ct);
        return session?.SessionId
               ?? throw new InvalidOperationException("Dropbox n'a pas rendu d'identifiant de session.");
    }

    private async Task AppendreAsync(string sessionId, long decalage, ReadOnlyMemory<byte> morceau,
        CancellationToken ct)
    {
        var arg = JsonSerializer.Serialize(new
        {
            cursor = new { session_id = sessionId, offset = decalage },
            close = false,
        });

        using var reponse = await EnvoyerDuContenuAsync(
            "https://content.dropboxapi.com/2/files/upload_session/append_v2", arg, morceau, ct);

        await VerifierAsync(reponse, "envoi d'un morceau", ct);
    }

    private async Task CloreLaSessionAsync(string sessionId, long taille, string cheminDistant,
        CancellationToken ct)
    {
        var arg = JsonSerializer.Serialize(new
        {
            cursor = new { session_id = sessionId, offset = taille },
            commit = new { path = cheminDistant, mode = "add", autorename = true, mute = true },
        });

        using var reponse = await EnvoyerDuContenuAsync(
            "https://content.dropboxapi.com/2/files/upload_session/finish", arg, ReadOnlyMemory<byte>.Empty, ct);

        await VerifierAsync(reponse, "clôture de la session de téléversement", ct);
    }

    private async Task<HttpResponseMessage> EnvoyerDuContenuAsync(
        string url, string arg, ReadOnlyMemory<byte> corps, CancellationToken ct)
    {
        using var contenu = new ReadOnlyMemoryContent(corps);
        contenu.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var requete = new HttpRequestMessage(HttpMethod.Post, url) { Content = contenu };
        requete.Headers.Add("Dropbox-API-Arg", EnTeteAscii(arg));

        return await _http.SendAsync(requete, ct);
    }

    /// <summary>Crée le dossier s'il n'existe pas ; ne se plaint pas s'il existe déjà.</summary>
    public async Task CreerLeDossierAsync(string cheminDistant, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cheminDistant)) return;

        using var reponse = await _http.PostAsJsonAsync(
            "https://api.dropboxapi.com/2/files/create_folder_v2",
            new { path = cheminDistant, autorename = false }, ct);

        if (reponse.IsSuccessStatusCode) return;

        // Le dossier existe déjà : c'est le cas courant du dossier racine, et ce n'est pas
        // une erreur. Dropbox rend un 409 avec « conflict/folder ».
        var corps = await reponse.Content.ReadAsStringAsync(ct);
        if (reponse.StatusCode == HttpStatusCode.Conflict && corps.Contains("conflict")) return;

        throw new InvalidOperationException(Explication(reponse.StatusCode, corps, "création du dossier"));
    }

    /// <summary>
    /// Un lien de partage sur <paramref name="cheminDistant"/>, avec ce que le compte permet.
    ///
    /// <b>L'expiration et le mot de passe sont facultatifs par la force des choses</b> :
    /// Dropbox les réserve aux comptes Professional et Business, et les refuse sur un compte
    /// gratuit. Plutôt que d'échouer devant le client, on retente sans eux et on dit ce
    /// qu'on a obtenu — un lien qui marche vaut mieux qu'un envoi manqué.
    /// </summary>
    /// <returns>Le lien, et ce qui a réellement pu lui être appliqué.</returns>
    public async Task<LienPartage> PartagerAsync(
        string cheminDistant, int expirationJours, string? motDePasse, CancellationToken ct = default)
    {
        var protege = !string.IsNullOrWhiteSpace(motDePasse);
        var expire = expirationJours > 0;

        if (protege || expire)
        {
            var essai = await CreerLeLienAsync(cheminDistant, expirationJours, motDePasse, ct);
            if (essai is not null) return new LienPartage(essai, expire, protege);

            Log?.Invoke(
                "Dropbox : expiration et mot de passe refusés (compte gratuit) — " +
                "le lien est créé sans, et il sera permanent.");
        }

        var simple = await CreerLeLienAsync(cheminDistant, 0, null, ct)
                     ?? await LienExistantAsync(cheminDistant, ct)
                     ?? throw new InvalidOperationException(
                         "Dropbox n'a pas voulu créer de lien de partage pour ce dossier.");

        return new LienPartage(simple, Expire: false, Protege: false);
    }

    /// <param name="Url">L'adresse à donner au client.</param>
    /// <param name="Expire">Vrai si la date d'expiration a bien été acceptée.</param>
    /// <param name="Protege">Vrai si le mot de passe a bien été accepté.</param>
    public sealed record LienPartage(string Url, bool Expire, bool Protege);

    /// <returns>Le lien, ou null si Dropbox a refusé POUR CAUSE DE RÉGLAGES (compte gratuit).</returns>
    private async Task<string?> CreerLeLienAsync(
        string cheminDistant, int expirationJours, string? motDePasse, CancellationToken ct)
    {
        var reglages = new Dictionary<string, object> { ["access"] = "viewer" };

        if (expirationJours > 0)
            reglages["expires"] = DateTime.UtcNow.AddDays(expirationJours)
                .ToString("yyyy-MM-ddTHH:mm:ssZ");

        if (!string.IsNullOrWhiteSpace(motDePasse))
        {
            reglages["require_password"] = true;
            reglages["link_password"] = motDePasse;
        }

        using var reponse = await _http.PostAsJsonAsync(
            "https://api.dropboxapi.com/2/sharing/create_shared_link_with_settings",
            new { path = cheminDistant, settings = reglages }, ct);

        if (reponse.IsSuccessStatusCode)
        {
            var lien = await reponse.Content.ReadFromJsonAsync<LienCree>(cancellationToken: ct);
            if (!string.IsNullOrWhiteSpace(lien?.Url)) return lien.Url;
        }

        var corps = await reponse.Content.ReadAsStringAsync(ct);

        // Réglages hors offre, ou lien déjà existant : deux cas où l'on peut retomber sur
        // ses pieds. Tout le reste est une vraie panne, qu'il ne faut pas masquer.
        if (corps.Contains("not_allowed") || corps.Contains("settings_error")
            || corps.Contains("shared_link_already_exists"))
            return null;

        throw new InvalidOperationException(
            Explication(reponse.StatusCode, corps, "création du lien de partage"));
    }

    /// <summary>Le lien déjà posé sur ce dossier, s'il y en a un.</summary>
    private async Task<string?> LienExistantAsync(string cheminDistant, CancellationToken ct)
    {
        using var reponse = await _http.PostAsJsonAsync(
            "https://api.dropboxapi.com/2/sharing/list_shared_links",
            new { path = cheminDistant, direct_only = true }, ct);

        if (!reponse.IsSuccessStatusCode) return null;

        var liste = await reponse.Content.ReadFromJsonAsync<ListeDeLiens>(cancellationToken: ct);
        return liste?.Links?.FirstOrDefault()?.Url;
    }

    /// <summary>Un dossier trouvé dans le Dropbox du studio.</summary>
    /// <param name="Nom">Nom seul, sans le chemin.</param>
    /// <param name="Chemin">Chemin complet, tel que Dropbox le rend.</param>
    public sealed record DossierDistant(string Nom, string Chemin);

    /// <summary>
    /// Les SOUS-DOSSIERS de <paramref name="cheminDistant"/>, fichiers exclus.
    ///
    /// La pagination est suivie jusqu'au bout : Dropbox rend deux mille entrées au plus par
    /// appel, et un Dropbox de studio en compte davantage au bout de quelques mois. S'en
    /// tenir à la première page ferait un ménage qui oublie les plus vieux — exactement
    /// ceux qu'il faut retirer.
    /// </summary>
    public async Task<IReadOnlyList<DossierDistant>> ListerLesDossiersAsync(
        string cheminDistant, CancellationToken ct = default)
    {
        var dossiers = new List<DossierDistant>();

        using var premiere = await _http.PostAsJsonAsync(
            "https://api.dropboxapi.com/2/files/list_folder",
            new { path = cheminDistant, recursive = false, limit = 2000 }, ct);

        // Racine absente : il n'y a rien à ranger, et ce n'est pas une panne — c'est l'état
        // d'un studio qui n'a encore rien envoyé.
        if (premiere.StatusCode == HttpStatusCode.Conflict) return dossiers;

        await VerifierAsync(premiere, "lecture du dossier", ct);
        var page = await premiere.Content.ReadFromJsonAsync<PageDeDossier>(cancellationToken: ct);

        while (page is not null)
        {
            foreach (var entree in page.Entries ?? [])
                if (entree.Tag == "folder" && entree.Name is not null && entree.PathLower is not null)
                    dossiers.Add(new DossierDistant(entree.Name, entree.PathLower));

            if (!page.HasMore || string.IsNullOrEmpty(page.Cursor)) break;

            using var suite = await _http.PostAsJsonAsync(
                "https://api.dropboxapi.com/2/files/list_folder/continue",
                new { cursor = page.Cursor }, ct);

            await VerifierAsync(suite, "lecture du dossier (suite)", ct);
            page = await suite.Content.ReadFromJsonAsync<PageDeDossier>(cancellationToken: ct);
        }

        return dossiers;
    }

    /// <summary>
    /// Supprime un dossier et tout ce qu'il contient.
    ///
    /// Dropbox le met dans SA corbeille : le studio peut le récupérer depuis le site
    /// pendant trente jours. Ce n'est donc pas irrémédiable, mais cela reste une
    /// suppression — l'appelant doit savoir ce qu'il vise.
    /// </summary>
    public async Task SupprimerAsync(string cheminDistant, CancellationToken ct = default)
    {
        using var reponse = await _http.PostAsJsonAsync(
            "https://api.dropboxapi.com/2/files/delete_v2", new { path = cheminDistant }, ct);

        // déjà parti — quelqu'un l'a supprimé depuis le site entre-temps : le but est atteint
        if (reponse.StatusCode == HttpStatusCode.Conflict) return;

        await VerifierAsync(reponse, $"suppression de {cheminDistant}", ct);
    }

    /// <summary>
    /// Le compte au bout du jeton, pour que Paramètres puisse dire à qui l'on est connecté.
    /// </summary>
    public async Task<string> NomDuCompteAsync(CancellationToken ct = default)
    {
        // cet appel n'attend AUCUN argument, et Dropbox exige alors un corps vide et non
        // « null » : c'est le seul de l'API à se comporter ainsi
        using var vide = new StringContent("", Encoding.UTF8);
        vide.Headers.ContentType = null;

        using var reponse = await _http.PostAsync(
            "https://api.dropboxapi.com/2/users/get_current_account", vide, ct);

        await VerifierAsync(reponse, "lecture du compte", ct);

        var compte = await reponse.Content.ReadFromJsonAsync<Compte>(cancellationToken: ct);
        return compte?.Name?.DisplayName ?? compte?.Email ?? "compte Dropbox";
    }

    private static async Task VerifierAsync(HttpResponseMessage reponse, string quoi, CancellationToken ct)
    {
        if (reponse.IsSuccessStatusCode) return;

        var corps = await reponse.Content.ReadAsStringAsync(ct);
        throw new InvalidOperationException(Explication(reponse.StatusCode, corps, quoi));
    }

    /// <summary>
    /// Ce que Dropbox reproche, en clair.
    ///
    /// Les codes qui comptent au comptoir sont ceux qui appellent une ACTION : le compte
    /// est plein, la connexion a expiré, on va trop vite. Les autres partent au journal avec
    /// leur corps brut, qui sert au diagnostic.
    /// </summary>
    private static string Explication(HttpStatusCode code, string corps, string quoi)
    {
        Log?.Invoke($"Dropbox : {quoi} — {(int)code} {corps}");

        if (corps.Contains("insufficient_space"))
            return "Le Dropbox du studio est plein : faites de la place, puis recommencez.";

        // La permission manquante arrive elle aussi en 401, et se confondait donc avec une
        // connexion périmée : l'écran conseillait « reconnectez le compte », ce qui ne
        // change RIEN tant que la case n'est pas cochée dans la console Dropbox. C'est le
        // premier mur d'une installation neuve — constaté le 05/08/2026.
        if (PermissionManquante(corps) is { } manquante) return manquante;

        return code switch
        {
            HttpStatusCode.Unauthorized =>
                "La connexion Dropbox a expiré. Reconnectez le compte depuis Paramètres.",
            HttpStatusCode.TooManyRequests =>
                "Dropbox demande de ralentir (trop d'envois d'affilée). Réessayez dans une minute.",
            HttpStatusCode.PaymentRequired =>
                "Cette option demande un compte Dropbox payant.",
            _ => $"Dropbox a refusé « {quoi} » : {corps}",
        };
    }

    /// <summary>
    /// La marche à suivre quand Dropbox refuse pour PERMISSION MANQUANTE, ou null si le
    /// refus a une autre cause.
    ///
    /// Le message d'origine est long, en anglais, et se termine sur « the owner of the app
    /// can enable the scope… » — vrai, mais il ne dit pas les deux choses qui comptent :
    /// il faut appuyer sur <b>Submit</b> après avoir coché, et il faut ensuite
    /// <b>reconnecter le compte</b>. Un jeton déjà délivré ne gagne jamais une permission
    /// après coup ; sans la reconnexion, on recoche, on réessaie, et on retombe sur la
    /// même erreur sans comprendre.
    /// </summary>
    internal static string? PermissionManquante(string corps)
    {
        if (!corps.Contains("required scope") && !corps.Contains("missing_scope")) return null;

        // le nom de la permission est entre apostrophes simples dans le message de Dropbox
        var nom = "";
        var debut = corps.IndexOf('\'');
        if (debut >= 0)
        {
            var fin = corps.IndexOf('\'', debut + 1);
            if (fin > debut) nom = corps[(debut + 1)..fin];
        }

        return
            $"Votre application Dropbox n'a pas la permission « {(nom.Length > 0 ? nom : "requise")} ».\n\n" +
            "Sur dropbox.com/developers/apps, ouvrez votre application → onglet Permissions, " +
            "cochez account_info.read, files.metadata.read, files.content.write, sharing.read " +
            "et sharing.write, puis appuyez sur SUBMIT en bas de la page.\n\n" +
            "Revenez ensuite ici et refaites « Connecter le compte » : un jeton déjà délivré " +
            "ne gagne pas les nouvelles permissions, il faut le redemander.";
    }

    /// <summary>
    /// L'en-tête <c>Dropbox-API-Arg</c> ne voyage qu'en ASCII : tout caractère au-delà doit
    /// être échappé en <c>\uXXXX</c>.
    ///
    /// Sans cela, un simple accent dans un nom de fichier — « Séance Dupont » — fait rejeter
    /// la requête par le client HTTP avant même qu'elle ne parte, et l'envoi échoue sur ce
    /// qui est le cas ORDINAIRE d'un studio français.
    /// </summary>
    private static string EnTeteAscii(string json)
    {
        var sortie = new StringBuilder(json.Length);
        foreach (var c in json)
        {
            if (c < 128) sortie.Append(c);
            else sortie.Append("\\u").Append(((int)c).ToString("x4"));
        }
        return sortie.ToString();
    }

    private sealed record PageDeDossier(
        [property: JsonPropertyName("entries")] List<EntreeDeDossier>? Entries,
        [property: JsonPropertyName("cursor")] string? Cursor,
        [property: JsonPropertyName("has_more")] bool HasMore);

    private sealed record EntreeDeDossier(
        [property: JsonPropertyName(".tag")] string? Tag,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("path_lower")] string? PathLower);

    private sealed record SessionOuverte(
        [property: JsonPropertyName("session_id")] string? SessionId);

    private sealed record LienCree([property: JsonPropertyName("url")] string? Url);

    private sealed record ListeDeLiens(
        [property: JsonPropertyName("links")] List<LienCree>? Links);

    private sealed record Compte(
        [property: JsonPropertyName("name")] NomDeCompte? Name,
        [property: JsonPropertyName("email")] string? Email);

    private sealed record NomDeCompte(
        [property: JsonPropertyName("display_name")] string? DisplayName);
}
