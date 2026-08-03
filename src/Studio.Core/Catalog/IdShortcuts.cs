using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Core.Catalog;

/// <summary>Ce vers quoi pointe un raccourci de l'écran « photo d'identité ».</summary>
public enum IdShortcutKind
{
    /// <summary>Une norme du référentiel des 274 documents (France, Espagne…).</summary>
    Document,

    /// <summary>
    /// Un produit du catalogue, tiré tel quel. C'est le cas de l'E-Photo : la photo part
    /// entière sur un 10×15, sans passer par le gabarit d'identité.
    /// </summary>
    Produit,
}

/// <summary>
/// Un raccourci de l'écran de choix du document.
/// </summary>
/// <param name="Kind">Document normé ou produit du catalogue.</param>
/// <param name="Cle">
/// Pour un document : « Pays|Type » tel qu'il figure au référentiel. Pour un produit :
/// son code catalogue.
/// </param>
/// <param name="Libelle">Ce que l'opérateur lit sur la tuile.</param>
public sealed record IdShortcut(IdShortcutKind Kind, string Cle, string Libelle);

/// <summary>
/// Les formats mis en avant sur l'écran de choix du document d'identité.
///
/// Le référentiel en compte 274, la boutique en tire deux toute la journée. Les afficher
/// tous à plat obligeait à chercher « France » dans une grille où il voisine avec le visa
/// tadjik ; les raccourcis les mettent devant, le reste passe derrière un bouton.
///
/// La liste est modifiable au Catalogue : ce qui se vend change avec la saison, et
/// recompiler l'application pour changer une tuile n'aurait pas de sens.
/// </summary>
public static class IdShortcuts
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record Fichier(List<IdShortcut> Raccourcis);

    /// <summary>Nom du fichier, dans le dossier catalogue.</summary>
    public const string FileName = "id-raccourcis.json";

    /// <summary>
    /// Ce que la boutique tire tous les jours, faute de fichier de configuration : la
    /// norme française et l'E-Photo, exactement les deux demandés le 03/08/2026.
    /// </summary>
    public static IReadOnlyList<IdShortcut> Defaults { get; } =
    [
        new(IdShortcutKind.Document, "France|Passeport / CNI", "France"),
        new(IdShortcutKind.Produit, "e-photo-dnp", "E-Photo"),
    ];

    /// <summary>Clé d'un document, telle qu'elle s'écrit dans le fichier.</summary>
    public static string DocumentKey(string pays, string document) => $"{pays}|{document}";

    /// <summary>
    /// Charge les raccourcis. Un fichier absent ou illisible rend les raccourcis par
    /// défaut : l'écran doit rester utilisable même si quelqu'un a abîmé le fichier.
    /// </summary>
    public static IReadOnlyList<IdShortcut> Load(string catalogDir)
    {
        var chemin = Path.Combine(catalogDir, FileName);
        if (!File.Exists(chemin)) return Defaults;

        try
        {
            using var flux = File.OpenRead(chemin);
            var fichier = JsonSerializer.Deserialize<Fichier>(flux, JsonOptions);
            var lus = fichier?.Raccourcis?
                .Where(r => !string.IsNullOrWhiteSpace(r.Cle))
                .ToList();

            // une liste vide est un choix légitime (« aucun raccourci »), un fichier
            // corrompu ne l'est pas : on ne retombe sur les défauts que dans le second cas
            return lus is null ? Defaults : lus;
        }
        catch (Exception)
        {
            return Defaults;
        }
    }

    /// <summary>Enregistre les raccourcis, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string catalogDir, IEnumerable<IdShortcut> raccourcis)
    {
        ArgumentNullException.ThrowIfNull(raccourcis);

        Directory.CreateDirectory(catalogDir);
        var chemin = Path.Combine(catalogDir, FileName);
        var json = JsonSerializer.Serialize(new Fichier(raccourcis.ToList()), JsonOptions);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
