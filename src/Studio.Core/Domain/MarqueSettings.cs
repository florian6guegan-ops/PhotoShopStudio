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
public sealed record MarqueSettings(
    string Mention = MarqueSettings.MentionParDefaut,
    string LogoPath = "",
    string QrTexte = "",
    bool BandeActive = true)
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

    /// <summary>Vrai si la bande porte quelque chose de plus que la date.</summary>
    /// <remarks>Hors du fichier : c'est un calcul, voir <c>DropboxSettings.EstUtilisable</c>.</remarks>
    [JsonIgnore]
    public bool PorteQuelqueChose =>
        BandeActive
        && (!string.IsNullOrWhiteSpace(Mention)
            || !string.IsNullOrWhiteSpace(LogoPath)
            || !string.IsNullOrWhiteSpace(QrTexte));

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
