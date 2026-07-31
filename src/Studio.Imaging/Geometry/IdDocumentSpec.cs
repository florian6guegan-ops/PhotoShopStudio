using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Imaging.Geometry;

/// <summary>
/// Norme d'un document d'identité : format du tirage et bornes admises pour la hauteur
/// du visage, du menton au sommet du crâne.
///
/// Les valeurs viennent du référentiel de DiLand, qui couvre 274 documents. Chaque pays
/// a les siennes : un passeport espagnol fait 26 × 32 mm là où le français fait 35 × 45.
/// </summary>
/// <param name="Country">Pays émetteur.</param>
/// <param name="Document">Type de document (passeport, carte d'identité, visa…).</param>
/// <param name="WidthMm">Largeur du tirage.</param>
/// <param name="HeightMm">Hauteur du tirage.</param>
/// <param name="HeadMinMm">Hauteur de visage minimale admise.</param>
/// <param name="HeadMaxMm">Hauteur de visage maximale admise.</param>
/// <param name="CrownMarginMm">
/// Marge visée au-dessus du crâne. Null = estimée à partir du format. La norme française
/// la fixe à 4 mm ; le référentiel de DiLand ne la donne pas pour les autres pays.
/// </param>
public sealed record IdDocumentSpec(
    string Country,
    string Document,
    double WidthMm,
    double HeightMm,
    double HeadMinMm,
    double HeadMaxMm,
    double? CrownMarginMm = null)
{
    /// <summary>Libellé destiné à l'opérateur.</summary>
    public string Label => $"{Country} — {Document} ({WidthMm:0.#} × {HeightMm:0.#} mm)";

    /// <summary>Hauteur de visage visée : le milieu des bornes.</summary>
    public double TargetHeadMm => HasHeadBounds ? (HeadMinMm + HeadMaxMm) / 2 : HeightMm * 0.75;

    /// <summary>
    /// Vrai si le document précise des bornes de visage. Une trentaine de documents du
    /// référentiel n'en donnent aucune : on cadre alors sur une proportion usuelle,
    /// et la conformité ne peut pas être contrôlée.
    /// </summary>
    public bool HasHeadBounds => HeadMinMm > 0 && HeadMaxMm > HeadMinMm;

    /// <summary>
    /// Marge visée au-dessus du crâne : celle du document si elle est connue, sinon une
    /// estimation proportionnelle — un peu moins de la moitié de l'espace restant, le
    /// menton demandant plus de place que le crâne.
    /// </summary>
    public double TargetCrownMarginMm => CrownMarginMm ?? (HeightMm - TargetHeadMm) * 0.45;

    /// <summary>Le document est-il exploitable pour un cadrage ?</summary>
    public bool IsUsable => WidthMm > 0 && HeightMm > 0;

    /// <summary>Norme française 35 × 45, visage de 32 à 36 mm — le cas courant de la boutique.</summary>
    public static IdDocumentSpec France { get; } =
        new("France", "Passeport / CNI", IdPhotoFr.PhotoWidthMm, IdPhotoFr.PhotoHeightMm,
            IdPhotoFr.HeadMinMm, IdPhotoFr.HeadMaxMm, IdPhotoFr.TargetCrownMarginMm);
}

/// <summary>
/// Référentiel des documents d'identité, chargé depuis
/// <c>catalog/diland-id-documents.json</c>.
/// </summary>
public static class IdDocumentCatalog
{
    private sealed record Fichier([property: JsonPropertyName("Documents")] List<Entree> Documents);

    private sealed record Entree(
        string Pays,
        string Document,
        double LargeurMm,
        double HauteurMm,
        double VisageHauteurMm,
        double VisageHauteurMaxMm);

    /// <summary>Charge le référentiel. Les documents inexploitables sont écartés.</summary>
    public static IReadOnlyList<IdDocumentSpec> Load(string jsonPath)
    {
        using var flux = File.OpenRead(jsonPath);
        var fichier = JsonSerializer.Deserialize<Fichier>(flux,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (fichier?.Documents is null) return [];

        return fichier.Documents
            .Select(e => new IdDocumentSpec(e.Pays, e.Document, e.LargeurMm, e.HauteurMm,
                e.VisageHauteurMm, e.VisageHauteurMaxMm))
            .Where(d => d.IsUsable)
            .OrderBy(d => d.Country, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Document, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Documents dont le pays ou le type contient le texte cherché. Une recherche vide
    /// renvoie tout : sur 274 entrées, l'opérateur tape deux lettres plutôt que de faire
    /// défiler.
    /// </summary>
    public static IEnumerable<IdDocumentSpec> Search(IEnumerable<IdDocumentSpec> documents, string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return documents;

        var recherche = texte.Trim();
        return documents.Where(d =>
            d.Country.Contains(recherche, StringComparison.CurrentCultureIgnoreCase)
            || d.Document.Contains(recherche, StringComparison.CurrentCultureIgnoreCase));
    }
}
