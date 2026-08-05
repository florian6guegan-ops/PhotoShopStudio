using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Studio.Web.Dropbox;

/// <summary>
/// Autorisation d'un compte Dropbox, en PKCE et sans secret d'application.
///
/// <b>Pourquoi PKCE.</b> Une application de bureau ne peut RIEN garder de confidentiel :
/// tout ce qu'on écrirait dans le programme se lit dans le fichier livré. PKCE règle cela
/// en remplaçant le secret par un aléa fabriqué à chaque autorisation, dont seul le
/// condensé part sur le réseau. C'est le flux que Dropbox recommande pour ce cas, et il
/// permet de n'avoir aucun secret dans le dépôt.
///
/// <b>Pourquoi le code se recopie à la main.</b> Dropbox sait rendre le code SUR SA PAGE
/// plutôt que de rediriger vers une adresse. Cela évite d'ouvrir un port en écoute sur le
/// poste — que le pare-feu du magasin bloquerait — et d'inscrire une URL de redirection
/// dans la console Dropbox. L'opérateur autorise, recopie le code, et c'est fait une fois
/// pour toutes : le jeton de rafraîchissement obtenu ne périme pas.
/// </summary>
public static class DropboxAuth
{
    /// <summary>Journal optionnel, branché par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// L'aléa d'une autorisation en cours. Il naît avec l'adresse ouverte dans le
    /// navigateur et sert à échanger le code : les deux vont ensemble.
    /// </summary>
    /// <param name="Url">Adresse à ouvrir dans le navigateur.</param>
    /// <param name="CodeVerifier">L'aléa, à représenter au moment de l'échange.</param>
    public sealed record Demande(string Url, string CodeVerifier);

    /// <summary>
    /// Prépare l'autorisation : l'adresse à ouvrir, et l'aléa à garder pour l'échange.
    /// </summary>
    public static Demande Preparer(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
            throw new ArgumentException("La clé de l'application Dropbox est vide.", nameof(appKey));

        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // token_access_type=offline est ce qui fait rendre un JETON DE RAFRAÎCHISSEMENT en
        // plus du jeton d'accès. Sans lui, l'autorisation ne vaudrait que quatre heures et
        // il faudrait la refaire à chaque envoi — autant dire que la fonction serait
        // inutilisable au comptoir.
        var url =
            "https://www.dropbox.com/oauth2/authorize" +
            $"?client_id={Uri.EscapeDataString(appKey)}" +
            "&response_type=code" +
            "&token_access_type=offline" +
            "&code_challenge_method=S256" +
            $"&code_challenge={challenge}";

        return new Demande(url, verifier);
    }

    /// <summary>
    /// Échange le code recopié par l'opérateur contre un jeton de rafraîchissement.
    /// </summary>
    /// <returns>Le jeton de rafraîchissement, à conserver dans les réglages.</returns>
    public static async Task<string> EchangerAsync(
        string appKey, string code, string codeVerifier, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Le code d'autorisation est vide.", nameof(code));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var reponse = await http.PostAsync("https://api.dropboxapi.com/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code.Trim(),
                ["grant_type"] = "authorization_code",
                ["client_id"] = appKey,
                ["code_verifier"] = codeVerifier,
            }), ct);

        if (!reponse.IsSuccessStatusCode)
        {
            var corps = await reponse.Content.ReadAsStringAsync(ct);
            Log?.Invoke($"Dropbox : échange du code refusé ({(int)reponse.StatusCode}) — {corps}");
            throw new InvalidOperationException(Explication(corps));
        }

        var jetons = await reponse.Content.ReadFromJsonAsync<ReponseJeton>(cancellationToken: ct);

        if (string.IsNullOrWhiteSpace(jetons?.RefreshToken))
            throw new InvalidOperationException(
                "Dropbox n'a pas rendu de jeton de rafraîchissement. Recommencez l'autorisation " +
                "depuis le bouton « Connecter » : le code ne sert qu'une fois et il expire vite.");

        Log?.Invoke("Dropbox : compte autorisé, jeton de rafraîchissement obtenu.");
        return jetons.RefreshToken;
    }

    /// <summary>
    /// Ce que Dropbox reproche, en clair pour l'opérateur.
    ///
    /// Le corps rendu est du JSON technique (<c>{"error": "invalid_grant"}</c>) : le poser
    /// tel quel devant quelqu'un qui vient de recopier un code ne l'aide pas à comprendre
    /// qu'il doit simplement recommencer.
    /// </summary>
    private static string Explication(string corps) => corps switch
    {
        var c when c.Contains("invalid_grant") =>
            "Ce code n'est plus valable. Il ne sert qu'une fois et n'est bon que quelques minutes : " +
            "reprenez au bouton « Connecter ».",
        var c when c.Contains("invalid_client") =>
            "La clé de l'application Dropbox est refusée. Vérifiez-la sur dropbox.com/developers/apps.",
        _ => $"Dropbox a refusé l'autorisation : {corps}",
    };

    /// <summary>
    /// Un nouveau jeton d'ACCÈS à partir du jeton de rafraîchissement.
    ///
    /// Le jeton d'accès vit quatre heures ; on n'en garde donc aucun sur le disque et on en
    /// redemande un à chaque envoi. C'est un aller-retour de quelques dizaines de
    /// millisecondes, à côté d'un téléversement qui se compte en secondes.
    /// </summary>
    public static async Task<string> JetonDAccesAsync(
        string appKey, string refreshToken, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var reponse = await http.PostAsync("https://api.dropboxapi.com/oauth2/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = appKey,
            }), ct);

        if (!reponse.IsSuccessStatusCode)
        {
            var corps = await reponse.Content.ReadAsStringAsync(ct);
            Log?.Invoke($"Dropbox : rafraîchissement refusé ({(int)reponse.StatusCode}) — {corps}");

            throw new InvalidOperationException(
                corps.Contains("invalid_grant")
                    ? "L'autorisation Dropbox n'est plus valable — le compte a peut-être révoqué " +
                      "l'application. Reconnectez-le depuis Paramètres."
                    : $"Dropbox refuse la connexion : {corps}");
        }

        var jetons = await reponse.Content.ReadFromJsonAsync<ReponseJeton>(cancellationToken: ct);

        return jetons?.AccessToken
               ?? throw new InvalidOperationException("Dropbox n'a pas rendu de jeton d'accès.");
    }

    private sealed record ReponseJeton(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken);

    /// <summary>Base64 « URL », sans remplissage : ce que PKCE exige.</summary>
    private static string Base64Url(byte[] octets) =>
        Convert.ToBase64String(octets).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
