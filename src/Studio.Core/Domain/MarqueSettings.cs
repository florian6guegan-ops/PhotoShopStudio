using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Core.Domain;

/// <summary>
/// La marque du studio, telle qu'elle apparaît sur ce qui sort de l'atelier — pour l'instant
/// la bande basse des planches identité.
///
/// Elle est RÉGLABLE et non écrite dans le code : le logo est un fichier que la boutique
/// remplace sans recompiler, et la mention se réécrit le jour où la formulation change. Les
/// réglages vivent dans le dossier de DONNÉES, comme <see cref="Mail.MailSettings"/>.
/// </summary>
/// <param name="Mention">
/// Mention portée au centre de la bande. Deux lignes au plus, séparées par <c>\n</c> : la
/// première est mise en avant, la seconde la précise. Vide = pas de mention.
/// </param>
/// <param name="LogoPath">
/// Chemin complet de l'image de marque posée à droite de la bande. PNG à fond transparent
/// de préférence — un fond blanc se verra sur le papier. Vide ou fichier absent = pas de
/// logo, et la planche sort quand même.
/// </param>
/// <param name="QrTexte">
/// Ce que le code QR encode : l'adresse du site de la boutique, une fiche d'avis, un
/// contact. Vide = pas de code QR.
/// </param>
/// <param name="BandeActive">
/// Faux pour revenir à la planche d'avant — date seule dans la marge. La bande est ce qui
/// change le plus l'allure du tirage : il faut pouvoir la retirer sans passer par le code.
/// </param>
/// <param name="NomMagasin">
/// Le nom de la boutique, écrit EN PETIT à la suite de la mention — « PHOTOS CONFORMES ·
/// Photo Concept Maisons-Alfort ».
///
/// <b>Pourquoi il ne se met pas dans la mention.</b> Rien n'empêchait de le taper dedans,
/// mais il y aurait pris le corps et le gras de l'annonce : le nom du magasin est une
/// signature, pas une affirmation de conformité. Séparé, il s'écrit petit et gris, et la
/// mention reste ce qu'elle est même quand la boutique change de nom ou de propriétaire.
///
/// Vide = rien n'est signé, et la bande sort comme avant. Demandé le 18/08/2026, pour les
/// deux logiciels.
/// </param>
public sealed record MarqueSettings(
    string Mention = MarqueSettings.MentionParDefaut,
    string LogoPath = "",
    string QrTexte = "",
    bool BandeActive = true,
    string NomMagasin = "")
{
    /// <summary>Nom du fichier, dans le dossier de configuration.</summary>
    public const string FileName = "marque.json";

    /// <summary>
    /// La mention par défaut.
    ///
    /// Elle affirme la conformité aux normes, ce que la boutique est en droit de dire de
    /// ses tirages. Elle ne reprend NI la Marianne NI le nom d'un prestataire agréé : le
    /// premier est l'emblème de l'État, le second appartient à qui l'a déposé, et les
    /// afficher laisserait entendre une accréditation qui ne se donne pas toute seule.
    /// </summary>
    public const string MentionParDefaut =
        "PHOTOS CONFORMES\naux normes des documents officiels";

    /// <summary>
    /// Ce que la bande porte quand l'opérateur a coché « photos non conformes ».
    ///
    /// <b>Elle REMPLACE la mention réglée, elle ne s'y ajoute pas.</b> Une planche ne peut
    /// pas affirmer la conformité et la démentir sur la ligne suivante.
    ///
    /// <b>Pourquoi une planche sortirait exprès hors norme.</b> Une photo d'école, un
    /// souvenir au format identité, et surtout le client qui veut sa pose, son sourire ou
    /// ses lunettes contre l'avis du comptoir. La boutique tire ce qu'on lui demande — mais
    /// elle n'a pas à porter par écrit une conformité qu'elle sait fausse, et l'opérateur
    /// n'a pas à la démontrer de mémoire quand la mairie refuse la photo trois semaines plus
    /// tard. Demandé le 21/08/2026.
    ///
    /// Le NOM DU MAGASIN reste écrit à la suite, comme sur une planche conforme : la
    /// signature dit qui a tiré, pas ce que vaut le tirage.
    ///
    /// ⚠ Elle n'est pas réglable, à la différence de <see cref="MentionParDefaut"/>. Une
    /// boutique peut vouloir formuler sa promesse à sa façon ; l'avertissement qui la
    /// protège doit rester dans les mêmes termes partout, et lisible.
    /// </summary>
    public const string MentionNonConforme =
        "PHOTOS NON CONFORMES\naux normes des documents officiels";

    /// <summary>Vrai si la bande porte quelque chose de plus que la date.</summary>
    /// <remarks>Hors du fichier : c'est un calcul, voir <c>DropboxSettings.EstUtilisable</c>.</remarks>
    [JsonIgnore]
    public bool PorteQuelqueChose =>
        BandeActive
        && (!string.IsNullOrWhiteSpace(Mention)
            || !string.IsNullOrWhiteSpace(LogoPath)
            || !string.IsNullOrWhiteSpace(QrTexte)
            // le nom seul suffit à justifier la bande : une boutique peut vouloir signer
            // sans rien affirmer
            || !string.IsNullOrWhiteSpace(NomMagasin));

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge les réglages. Un fichier absent ou abîmé rend les réglages PAR DÉFAUT plutôt
    /// que de lever : une planche doit pouvoir sortir même si personne n'a rien réglé.
    /// </summary>
    public static MarqueSettings Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return new MarqueSettings();

        try
        {
            using var flux = File.OpenRead(chemin);
            return JsonSerializer.Deserialize<MarqueSettings>(flux, Options) ?? new MarqueSettings();
        }
        catch (Exception)
        {
            return new MarqueSettings();
        }
    }

    /// <summary>Enregistre les réglages, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, MarqueSettings settings)
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
