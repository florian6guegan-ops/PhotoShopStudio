using System.Text.Json;

namespace Studio.Core.Mail;

/// <summary>
/// Un mot prêt à joindre aux photos du client.
/// </summary>
/// <param name="Libelle">Ce que l'opérateur lit dans la liste déroulante.</param>
/// <param name="Texte">Le mot lui-même, tel qu'il partira dans le courriel.</param>
public sealed record MessagePredefini(string Libelle, string Texte);

/// <summary>
/// Les mots prédéfinis proposés à l'envoi des photos par courriel.
///
/// Ils vivent dans un fichier À PART de <see cref="MailSettings"/>, et ce n'est pas un
/// détail : les deux écrans qui les touchent ne sont pas les mêmes. L'écran Paramètres
/// réécrit <c>mail.json</c> en entier à chaque enregistrement — y loger les messages les
/// effacerait dès qu'on toucherait au mot de passe.
/// </summary>
public static class MailMessages
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Fichier(List<MessagePredefini> Messages);

    /// <summary>Nom du fichier, dans le dossier de configuration.</summary>
    public const string FileName = "mail-messages.json";

    /// <summary>
    /// Ce que la boutique écrit le plus souvent, faute de fichier. Ce sont des points de
    /// départ : l'opérateur les modifie dans Paramètres, et le mot reste retouchable à la
    /// main sur l'écran d'envoi.
    /// </summary>
    public static IReadOnlyList<MessagePredefini> Defaults { get; } =
    [
        new("Photos d'identité",
            "Vos photos d'identité sont conformes aux normes en vigueur. " +
            "Pour une démarche en ligne, utilisez la version légère jointe à ce message."),

        new("Remerciement",
            "Merci de votre visite, et à bientôt au studio."),

        new("Tirages à retirer",
            "Vos tirages sont prêts et vous attendent au magasin, aux heures d'ouverture habituelles."),
    ];

    /// <summary>
    /// Charge les messages. Un fichier absent rend les messages par défaut ; une liste
    /// vide est un choix légitime (« aucun message prédéfini ») et se respecte.
    /// </summary>
    public static IReadOnlyList<MessagePredefini> Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return Defaults;

        try
        {
            using var flux = File.OpenRead(chemin);
            var fichier = JsonSerializer.Deserialize<Fichier>(flux, Options);
            var lus = fichier?.Messages?
                .Where(m => !string.IsNullOrWhiteSpace(m.Libelle))
                .ToList();

            return lus is null ? Defaults : lus;
        }
        catch (Exception)
        {
            return Defaults;
        }
    }

    /// <summary>Enregistre les messages, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, IEnumerable<MessagePredefini> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        Directory.CreateDirectory(configDir);
        var chemin = Path.Combine(configDir, FileName);
        var json = JsonSerializer.Serialize(new Fichier(messages.ToList()), Options);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
