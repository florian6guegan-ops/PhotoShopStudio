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
/// <param name="TargetHeadOverrideMm">
/// Hauteur de visage visée, quand le milieu des bornes ne donne pas le rendu voulu.
/// Voir <see cref="TargetHeadMm"/>.
/// </param>
public sealed record IdDocumentSpec(
    string Country,
    string Document,
    double WidthMm,
    double HeightMm,
    double HeadMinMm,
    double HeadMaxMm,
    double? CrownMarginMm = null,
    double? TargetHeadOverrideMm = null,
    string? CountryEn = null,
    string? DocumentEn = null)
{
    /// <summary>
    /// Le pays et le type tels que le RÉFÉRENTIEL les écrit — en anglais, fautes de frappe
    /// comprises. Null pour la norme française de la boutique, qui n'en vient pas.
    ///
    /// Ils ne s'affichent nulle part : ils servent à retrouver un document que les
    /// raccourcis d'un poste ont enregistré avant que l'écran ne parle français, et à ce
    /// qu'une recherche sur « spain » trouve encore l'Espagne. Voir
    /// <see cref="TraductionIdentite"/>.
    /// </summary>
    public string CleOrigine => $"{CountryEn ?? Country}|{DocumentEn ?? Document}";

    /// <summary>Libellé destiné à l'opérateur.</summary>
    public string Label => $"{Country} — {Document} ({WidthMm:0.#} × {HeightMm:0.#} mm)";

    /// <summary>
    /// Hauteur de visage visée : le milieu des bornes, sauf calage explicite.
    ///
    /// Le milieu des bornes (34 mm pour la France) donnait une tête 5 % plus petite que
    /// celle de DiLand — comparaison faite le 03/08/2026 sur LA MÊME photo source avec LE
    /// MÊME détecteur : 0,79 du cadre contre 0,83. C'est l'écart que Florian voyait sur
    /// les tirages côte à côte, et il a tranché en faveur de DiLand.
    ///
    /// Le calage ne vaut que pour les documents où l'on dispose d'une référence — la
    /// France. Ailleurs, faute de tirage DiLand à comparer, le milieu des bornes reste
    /// le choix le plus défendable.
    /// </summary>
    /// <remarks>
    /// La valeur est exprimée dans les unités de l'ESTIMATEUR, comme
    /// <see cref="IdPhotoFr.TargetHeadMm"/> : le milieu des bornes est donc converti au
    /// passage. Sans cette conversion, le cadrage idéal des 273 autres documents serait
    /// jugé trop petit par <see cref="IdPhotoFr.Check"/>, qui ramène toujours à la norme —
    /// c'est exactement le défaut qu'un test a rattrapé le 03/08/2026.
    /// </remarks>
    public double TargetHeadMm => TargetHeadOverrideMm
                                  ?? (HasHeadBounds ? (HeadMinMm + HeadMaxMm) / 2 : HeightMm * 0.75)
                                     * IdPhotoFr.SurestimationDeLEstimateur;

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

    /// <summary>
    /// Marge minimale admise au-dessus du crâne.
    ///
    /// Les tolérances suivent le FORMAT et non des millimètres figés. Elles l'étaient :
    /// 2 à 7 mm, les valeurs françaises, appliquées aux 274 documents. Sur un passeport
    /// arménien 50 × 50, la marge idéale vaut 9 mm — le cadrage parfait était donc déclaré
    /// non conforme, le gabarit restait orange et l'écran conseillait de « monter le
    /// cadre », ce qui le rendait vraiment faux. 121 documents sur 274 étaient dans ce cas
    /// (constaté le 03/08/2026).
    ///
    /// Le rapport est celui de la norme française — 2 mm en dessous et 3 mm au-dessus de
    /// la cible, sur une hauteur de 45 mm — de sorte que la France garde exactement ses
    /// valeurs d'avant.
    /// </summary>
    public double CrownMarginMinMm =>
        Math.Max(0, TargetCrownMarginMm - HeightMm * (2.0 / IdPhotoFr.PhotoHeightMm));

    /// <summary>Marge maximale admise au-dessus du crâne. Voir <see cref="CrownMarginMinMm"/>.</summary>
    public double CrownMarginMaxMm =>
        TargetCrownMarginMm + HeightMm * (3.0 / IdPhotoFr.PhotoHeightMm);

    /// <summary>
    /// Écart de centrage admis, proportionnel à la largeur du document — 2 mm sur les
    /// 35 mm français. Voir <see cref="CrownMarginMinMm"/>.
    /// </summary>
    public double CenterToleranceMm => WidthMm * (2.0 / IdPhotoFr.PhotoWidthMm);

    /// <summary>Le document est-il exploitable pour un cadrage ?</summary>
    public bool IsUsable => WidthMm > 0 && HeightMm > 0;

    /// <summary>
    /// Norme française 35 × 45, visage de 32 à 36 mm — le cas courant de la boutique.
    ///
    /// La hauteur visée est calée sur DiLand (<see cref="IdPhotoFr.TargetHeadMm"/>) et
    /// reste dans les bornes de la norme.
    /// </summary>
    public static IdDocumentSpec France { get; } =
        new("France", "Passeport / CNI", IdPhotoFr.PhotoWidthMm, IdPhotoFr.PhotoHeightMm,
            IdPhotoFr.HeadMinMm, IdPhotoFr.HeadMaxMm, IdPhotoFr.TargetCrownMarginMm,
            IdPhotoFr.TargetHeadMm);
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

        // TRADUIT DÈS LE CHARGEMENT : le référentiel parle anglais, les écrans parlent au
        // comptoir. Traduire ici plutôt qu'à l'affichage évite d'y penser dans chacun des
        // cinq écrans qui montrent un document — et l'un d'eux aurait fini par l'oublier.
        //
        // ⚠ LE NOM D'ORIGINE EST GARDÉ, et il le faut : c'est la clé sous laquelle les
        // raccourcis d'un poste sont déjà enregistrés (« Spain|Passport »). Voir
        // FindByKey et Search, qui répondent aux deux.
        return fichier.Documents
            .Select(e => new IdDocumentSpec(
                TraductionIdentite.Pays(e.Pays), TraductionIdentite.Document(e.Document),
                e.LargeurMm, e.HauteurMm, e.VisageHauteurMm, e.VisageHauteurMaxMm,
                CountryEn: e.Pays, DocumentEn: e.Document))
            .Where(d => d.IsUsable)
            .OrderBy(d => d.Country, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(d => d.Document, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Retrouve un document par sa clé « Pays|Type », celle qu'écrivent les raccourcis.
    ///
    /// La norme française de la boutique (<see cref="IdDocumentSpec.France"/>) répond en
    /// plus du référentiel : elle porte le même 35 × 45 mais avec la marge de crâne fixée
    /// à 4 mm, là où les entrées « France / ID Card », « France / Passport » et
    /// « France / Visa » du fichier DiLand la laissent estimer. C'est elle qu'on veut
    /// quand un raccourci dit « France », et sans ce rattrapage la tuile disparaîtrait
    /// sans rien dire — le fichier ne contient aucun « Passeport / CNI ».
    /// </summary>
    public static IdDocumentSpec? FindByKey(IEnumerable<IdDocumentSpec> documents, string? cle)
    {
        if (string.IsNullOrWhiteSpace(cle)) return null;

        var morceaux = cle.Split('|', 2);
        if (morceaux.Length < 2) return null;

        var pays = morceaux[0].Trim();
        var document = morceaux[1].Trim();

        // Le nom FRANÇAIS d'abord, celui d'ORIGINE ensuite : un poste dont les raccourcis
        // ont été réglés avant la traduction porte encore « Spain|Passport » dans son
        // id-raccourcis.json, et sa tuile disparaîtrait sans ce second essai.
        bool Correspond(IdDocumentSpec d) =>
            (d.Country.Equals(pays, StringComparison.OrdinalIgnoreCase) &&
             d.Document.Equals(document, StringComparison.OrdinalIgnoreCase))
            || ((d.CountryEn ?? "").Equals(pays, StringComparison.OrdinalIgnoreCase) &&
                (d.DocumentEn ?? "").Equals(document, StringComparison.OrdinalIgnoreCase));

        return Correspond(IdDocumentSpec.France)
            ? IdDocumentSpec.France
            : documents.FirstOrDefault(Correspond);
    }

    /// <summary>
    /// Le référentiel précédé de la norme française de la boutique — celle que l'on
    /// propose en premier parce que c'est celle qu'on tire tous les jours.
    /// </summary>
    public static IReadOnlyList<IdDocumentSpec> AvecNormeBoutique(IEnumerable<IdDocumentSpec> documents) =>
        [IdDocumentSpec.France, .. documents];

    /// <summary>
    /// Documents dont le pays ou le type contient le texte cherché. Une recherche vide
    /// renvoie tout : sur 274 entrées, l'opérateur tape deux lettres plutôt que de faire
    /// défiler.
    /// </summary>
    public static IEnumerable<IdDocumentSpec> Search(IEnumerable<IdDocumentSpec> documents, string? texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return documents;

        // On cherche AUSSI dans le nom d'origine : l'écran affiche « Espagne », mais le
        // formulaire du client, lui, dit souvent « Spain » — et l'opérateur tape ce qu'il
        // a sous les yeux.
        var recherche = texte.Trim();
        return documents.Where(d =>
            d.Country.Contains(recherche, StringComparison.CurrentCultureIgnoreCase)
            || d.Document.Contains(recherche, StringComparison.CurrentCultureIgnoreCase)
            || (d.CountryEn ?? "").Contains(recherche, StringComparison.CurrentCultureIgnoreCase)
            || (d.DocumentEn ?? "").Contains(recherche, StringComparison.CurrentCultureIgnoreCase));
    }
}
