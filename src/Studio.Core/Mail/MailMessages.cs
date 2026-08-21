using System.Text.Json;

namespace Studio.Core.Mail;

/// <summary>
/// Un mot prêt à joindre aux photos du client.
/// </summary>
/// <param name="Libelle">Ce que l'opérateur lit dans la liste déroulante.</param>
/// <param name="Texte">
/// Le mot lui-même. Il peut porter des ÉTIQUETTES que l'envoi remplace — <c>{nom}</c> par
/// le nom du client, <c>{magasin}</c> par celui de la boutique. Voir
/// <see cref="MailMessages.Appliquer"/>.
/// </param>
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

        // Celui-ci porte l'étiquette : c'est ainsi qu'un opérateur découvre qu'elle existe,
        // sans avoir à lire une aide. Voir Appliquer.
        new("Remerciement au nom",
            "Merci de votre confiance {nom}, et à très bientôt au studio."),

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

    /// <summary>
    /// Les étiquettes qu'un mot prédéfini peut porter, telles qu'on les montre à
    /// l'opérateur. L'ordre est celui de l'aide affichée sous la liste.
    /// </summary>
    public static IReadOnlyList<string> Etiquettes { get; } = ["{nom}", "{magasin}"];

    /// <summary>
    /// Remplace les étiquettes d'un mot prédéfini par ce que l'envoi connaît.
    ///
    /// <b>« Bonjour {nom}, voici vos photos »</b> — c'est ce qui a été demandé le
    /// 22/08/2026, et c'est tout : une phrase toute prête que l'opérateur choisit, avec le
    /// nom de la personne dedans. Le reste des textes n'a pas à devenir un langage de
    /// modèles.
    ///
    /// Les étiquettes s'écrivent en accolades et se lisent SANS TENIR COMPTE de la casse ni
    /// des accents : <c>{nom}</c>, <c>{Nom}</c>, <c>{prénom}</c> et <c>{prenom}</c> mènent
    /// toutes au même endroit. L'opérateur qui les tape de mémoire ne doit pas être puni
    /// d'un accent — il ne verra pas l'étiquette non remplacée, il verra le client la
    /// recevoir.
    ///
    /// ⚠ <b>Un nom absent efface l'étiquette ET l'espace qui la précède.</b> Sans cela,
    /// « Bonjour {nom}, » donnerait « Bonjour , » — une virgule qui flotte, dans un
    /// courriel déjà parti. Le champ étant facultatif, ce cas est le plus fréquent des deux.
    ///
    /// Une accolade laissée seule ou une étiquette inconnue reste telle quelle : on ne
    /// devine pas ce qu'un opérateur a voulu écrire, et un texte tronqué en silence serait
    /// pire que la même chose visible.
    /// </summary>
    /// <param name="texte">Le mot prédéfini, avec ses étiquettes.</param>
    /// <param name="nom">Nom ou prénom du client. Vide = l'étiquette disparaît.</param>
    /// <param name="magasin">Nom de la boutique. Vide = l'étiquette disparaît.</param>
    public static string Appliquer(string? texte, string? nom, string? magasin = null)
    {
        if (string.IsNullOrWhiteSpace(texte)) return "";

        var resultat = texte;

        foreach (var (etiquettes, valeur) in new[]
                 {
                     (new[] { "nom", "prenom", "prénom", "client" }, nom),
                     (new[] { "magasin", "boutique" }, magasin),
                 })
        {
            var propre = (valeur ?? "").Trim();

            foreach (var etiquette in etiquettes)
            {
                // L'ESPACE VOISIN PART AVEC L'ÉTIQUETTE quand il n'y a rien à mettre :
                // celui d'avant en cours de phrase (« Bonjour {nom}, » → « Bonjour, »),
                // celui d'après en tête de phrase (« {nom} Merci » → « Merci »). Sans quoi
                // le client reçoit une virgule qui flotte ou une phrase qui commence par un
                // blanc — dans un courriel déjà parti.
                if (propre.Length == 0)
                {
                    resultat = Remplacer(resultat, " {" + etiquette + "}", "");
                    resultat = Remplacer(resultat, "{" + etiquette + "} ", "");
                }

                resultat = Remplacer(resultat, "{" + etiquette + "}", propre);
            }
        }

        return resultat;
    }

    /// <summary>Remplacement insensible à la casse, celle de l'étiquette n'ayant aucun sens.</summary>
    private static string Remplacer(string texte, string cherche, string par) =>
        texte.Replace(cherche, par, StringComparison.OrdinalIgnoreCase);

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
