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
/// <param name="Photos">
/// Combien de photos poser sur la planche, quand le raccourci le décide. Null = la planche
/// PLEINE, c'est-à-dire ce que le papier peut porter du document visé — le comportement
/// d'origine, et celui de tous les raccourcis écrits avant le 17/08/2026.
///
/// <b>Pourquoi un raccourci porte un nombre.</b> La boutique vend deux planches françaises,
/// et ce n'est pas le format qui les distingue mais le NOMBRE : la planche pleine à huit, et
/// celle de six. Sans ce champ, l'opérateur choisissait « France » puis descendait le
/// compteur de deux crans, cinquante fois par jour, et l'oubliait parfois.
///
/// Ne vaut que pour un <see cref="IdShortcutKind.Document"/> : un produit tiré tel quel n'a
/// pas de planche.
/// </param>
public sealed record IdShortcut(
    IdShortcutKind Kind, string Cle, string Libelle, int? Photos = null);

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

    /// <summary>
    /// Les défauts de STUDIO PHOTO IDENTITÉ : les mêmes, plus la planche française de SIX.
    ///
    /// <b>Elle ne vaut que pour ce logiciel-là</b>, demandé le 17/08/2026. Le poste identité
    /// vend deux planches françaises, qui ne se distinguent pas par le format mais par le
    /// nombre ; le Studio complet, lui, fait des photos d'identité de temps en temps et n'a
    /// que faire d'une seconde tuile « France » sur son écran de choix.
    ///
    /// Un fichier <c>id-raccourcis.json</c> l'emporte sur ces deux listes : dès qu'un poste
    /// a réglé ses formats, c'est son fichier qui parle — les deux logiciels le partagent,
    /// comme ils partagent le catalogue.
    /// </summary>
    public static IReadOnlyList<IdShortcut> DefautsIdentite { get; } =
    [
        new(IdShortcutKind.Document, "France|Passeport / CNI", "France"),
        new(IdShortcutKind.Document, "France|Passeport / CNI", "France — planche de 6", 6),
        new(IdShortcutKind.Produit, "e-photo-dnp", "E-Photo"),
    ];

    /// <summary>Clé d'un document, telle qu'elle s'écrit dans le fichier.</summary>
    public static string DocumentKey(string pays, string document) => $"{pays}|{document}";

    /// <summary>
    /// Charge les raccourcis. Un fichier absent ou illisible rend les raccourcis par
    /// défaut : l'écran doit rester utilisable même si quelqu'un a abîmé le fichier.
    /// </summary>
    /// <param name="posteIdentite">
    /// Vrai dans Studio Photo Identité : les défauts comprennent alors la planche française
    /// de six. Ne change RIEN quand un fichier existe — voir <see cref="DefautsIdentite"/>.
    /// </param>
    public static IReadOnlyList<IdShortcut> Load(string catalogDir, bool posteIdentite = false)
    {
        var defauts = posteIdentite ? DefautsIdentite : Defaults;

        var chemin = Path.Combine(catalogDir, FileName);
        if (!File.Exists(chemin)) return defauts;

        try
        {
            using var flux = File.OpenRead(chemin);
            var fichier = JsonSerializer.Deserialize<Fichier>(flux, JsonOptions);
            var lus = fichier?.Raccourcis?
                .Where(r => !string.IsNullOrWhiteSpace(r.Cle))
                .ToList();

            // une liste vide est un choix légitime (« aucun raccourci »), un fichier
            // corrompu ne l'est pas : on ne retombe sur les défauts que dans le second cas
            return lus is null ? defauts : lus;
        }
        catch (Exception)
        {
            return defauts;
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
