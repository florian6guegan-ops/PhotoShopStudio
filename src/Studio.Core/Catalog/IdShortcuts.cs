using System.Text.Json;
using System.Text.Json.Serialization;
using Studio.Core.Domain;

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
/// <param name="Planche">
/// Ce que la tuile fabrique : la planche ordinaire, celle de la rentrée, ou la planche
/// accompagnée d'un 10×15. Voir <see cref="GenreDePlanche"/>.
///
/// <b>Le genre fait partie de l'identité de la tuile</b>, au même titre que le nombre :
/// trois tuiles peuvent viser la norme française et ne différer que par lui — ce sont trois
/// ventes différentes, à trois prix différents.
///
/// Absent des fichiers écrits avant la rentrée 2026 : le désérialiseur y lit donc
/// <see cref="GenreDePlanche.Standard"/>, qui est bien ce qu'ils décrivaient.
/// </param>
public sealed record IdShortcut(
    IdShortcutKind Kind, string Cle, string Libelle, int? Photos = null,
    GenreDePlanche Planche = GenreDePlanche.Standard);

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

    /// <param name="Version">
    /// Palier de raccourcis déjà proposé à ce poste. Absent des fichiers écrits avant cette
    /// notion, donc zéro — ce qui est exactement ce qu'on veut : ils n'ont rien reçu.
    /// </param>
    private sealed record Fichier(List<IdShortcut> Raccourcis, int Version = 0);

    /// <summary>Nom du fichier, dans le dossier catalogue.</summary>
    public const string FileName = "id-raccourcis.json";

    /// <summary>
    /// Les deux formats de la RENTRÉE, demandés le 20/08/2026.
    ///
    /// Ils tiennent dans les DEUX logiciels : la boutique en vend au comptoir comme le
    /// poste identité, et c'est la même famille qui les demande. On les pose donc une fois
    /// ici, plutôt que de les recopier dans les deux listes de défauts — c'est la règle du
    /// dépôt, les BOUTONS se doublent, ce qu'ils font, non.
    ///
    /// <b>Les postes qui avaient déjà réglé leurs raccourcis ne les voyaient jamais</b> :
    /// leur <c>id-raccourcis.json</c> l'emporte sur ces défauts, et il fallait donc ajouter
    /// les tuiles à la main sur chaque poste. Personne ne l'a fait — Arcueil tirait encore
    /// sans planche de rentrée le 22/08/2026, faute d'une tuile pour la demander.
    /// <see cref="CompleterLesManquants"/> les pose désormais une fois, par PALIER : le
    /// souci d'alors reste entier, un logiciel qui remet des tuiles qu'on a retirées est
    /// insupportable, et c'est exactement ce que le palier empêche.
    ///
    /// ⚠ DÉCLARÉ AVANT <see cref="Defaults"/>, et il doit le rester : les initialiseurs de
    /// champs statiques s'exécutent dans l'ordre du fichier, et une liste qui en épand une
    /// autre déclarée plus bas la lirait NULLE — la classe entière refusait alors de
    /// s'initialiser, module d'identité compris.
    /// </summary>
    public static IReadOnlyList<IdShortcut> FormatsDeRentree { get; } =
    [
        new(IdShortcutKind.Document, "France|Passeport / CNI", "Rentrée — 4 + 1 grande",
            PlancheDeRentree.IdentitesParDefaut, GenreDePlanche.Rentree),
        new(IdShortcutKind.Document, "France|Passeport / CNI", "Planche + une 10×15",
            null, GenreDePlanche.PlancheEtTirage),
    ];

    /// <summary>
    /// Ce que la boutique tire tous les jours, faute de fichier de configuration : la
    /// norme française, les deux formats de la rentrée, et l'E-Photo.
    /// </summary>
    public static IReadOnlyList<IdShortcut> Defaults { get; } =
    [
        new(IdShortcutKind.Document, "France|Passeport / CNI", "France"),
        .. FormatsDeRentree,
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
        .. FormatsDeRentree,
        new(IdShortcutKind.Produit, "e-photo-dnp", "E-Photo"),
    ];

    /// <summary>Clé d'un document, telle qu'elle s'écrit dans le fichier.</summary>
    public static string DocumentKey(string pays, string document) => $"{pays}|{document}";

    /// <summary>
    /// Palier de raccourcis livré par cette version. À MONTER quand on ajoute un raccourci
    /// aux défauts, et seulement alors.
    ///
    /// 1 = les formats de rentrée (<see cref="FormatsDeRentree"/>).
    /// </summary>
    public const int PalierDesDefauts = 1;

    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Ajoute au poste les raccourcis d'un palier qu'il n'a jamais vu, et rien de plus.
    ///
    /// <b>Un fichier existant ne recevait plus jamais rien.</b> <see cref="Load"/> ne pose
    /// les défauts que si le fichier est ABSENT : un poste installé avant l'arrivée des
    /// formats de rentrée les a donc attendus pour toujours. Arcueil s'en est aperçu le
    /// 22/08/2026 — la planche de rentrée existait dans le logiciel, mais aucune tuile ne
    /// permettait de la demander, et la mise à jour n'y changeait rien. C'est la même règle
    /// que celle de <c>CatalogueLivre</c>, juste pour le catalogue et fâcheuse ici : un
    /// raccourci n'a ni prix ni réglage pilote, il n'y a rien à écraser.
    ///
    /// <b>Une suppression volontaire est respectée</b>, et c'est tout l'objet du palier. On
    /// n'ajoute pas « ce qui manque » — sans quoi une tuile retirée par l'exploitant
    /// reviendrait à chaque démarrage, ce qui est le genre de logiciel qu'on déteste. On
    /// ajoute ce que ce poste n'a JAMAIS reçu, une seule fois, et on note le palier atteint.
    /// </summary>
    /// <returns>Les raccourcis effectivement ajoutés.</returns>
    public static IReadOnlyList<IdShortcut> CompleterLesManquants(
        string catalogDir, bool posteIdentite = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogDir);

        var chemin = Path.Combine(catalogDir, FileName);

        // Pas de fichier : Load rendra les défauts du jour, palier compris. Rien à faire,
        // et surtout rien à écrire — un poste neuf n'a pas besoin qu'on lui fabrique un
        // fichier pour qu'il ait ses tuiles.
        if (!File.Exists(chemin)) return [];

        Fichier? fichier;
        try
        {
            using var flux = File.OpenRead(chemin);
            fichier = JsonSerializer.Deserialize<Fichier>(flux, JsonOptions);
        }
        catch (Exception)
        {
            // fichier abîmé : Load retombe déjà sur les défauts, on ne l'écrase pas ici
            return [];
        }

        if (fichier?.Raccourcis is not { } presents) return [];
        if (fichier.Version >= PalierDesDefauts) return [];

        var defauts = posteIdentite ? DefautsIdentite : Defaults;

        // La comparaison porte sur ce qui FAIT le raccourci — sa cible et son genre — et
        // non sur le libellé, que l'exploitant a pu renommer.
        var deja = presents
            .Select(r => (r.Cle, r.Photos, r.Planche))
            .ToHashSet();

        var ajouts = defauts
            .Where(d => !deja.Contains((d.Cle, d.Photos, d.Planche)))
            .ToList();

        var complet = presents.Concat(ajouts).ToList();

        try
        {
            Enregistrer(catalogDir, complet, PalierDesDefauts);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Raccourcis d'identité : palier {PalierDesDefauts} non enregistré " +
                        $"({ex.Message}) — il sera reproposé au prochain démarrage.");
            return [];
        }

        if (ajouts.Count > 0)
            Log?.Invoke($"Raccourcis d'identité : {ajouts.Count} tuile(s) ajoutée(s) — " +
                        string.Join(", ", ajouts.Select(a => $"« {a.Libelle} »")) + ".");

        return ajouts;
    }

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

        // ⚠ ÉCRIT AU PALIER DU JOUR, et ce n'est pas anodin : quelqu'un qui arrange ses
        // tuiles dans Paramètres a forcément VU celles du palier courant, y compris celles
        // qu'il vient d'enlever. Réécrire un palier plus ancien les lui remettrait au
        // démarrage suivant — exactement ce que CompleterLesManquants existe pour éviter.
        Enregistrer(catalogDir, raccourcis, PalierDesDefauts);
    }

    /// <summary>
    /// L'écriture elle-même, palier compris — à côté puis remplacement, pour qu'une coupure
    /// ne laisse jamais un fichier à moitié écrit.
    /// </summary>
    private static void Enregistrer(
        string catalogDir, IEnumerable<IdShortcut> raccourcis, int palier)
    {
        Directory.CreateDirectory(catalogDir);
        var chemin = Path.Combine(catalogDir, FileName);
        var json = JsonSerializer.Serialize(new Fichier(raccourcis.ToList(), palier), JsonOptions);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
