using Studio.Core.Domain;

namespace Studio.Imaging.Geometry;

/// <summary>Rectangle normalisé (0..1) sur l'image orientée — même repère que CropSpec.</summary>
public sealed record NormRect(double X, double Y, double Width, double Height)
{
    public double CenterX => X + Width / 2;
    public double Bottom => Y + Height;
}

/// <summary>Point normalisé (0..1) sur l'image orientée.</summary>
public sealed record NormPoint(double X, double Y);

/// <summary>
/// Écarts mesurés du cadrage identité par rapport au gabarit du document visé.
/// Sans document précisé, la norme française 35×45 s'applique.
/// </summary>
public sealed record IdCompliance(
    double HeadHeightMm,
    double CrownMarginMm,
    double CenterOffsetMm,
    IdDocumentSpec? Spec = null)
{
    private IdDocumentSpec Document => Spec ?? IdDocumentSpec.France;

    /// <summary>
    /// Faux quand le document ne précise aucune borne de visage : la conformité ne peut
    /// alors pas être jugée, et annoncer « conforme » serait mentir.
    /// </summary>
    public bool CanBeChecked => Document.HasHeadBounds;

    public bool HeadHeightOk =>
        !CanBeChecked || (HeadHeightMm >= Document.HeadMinMm && HeadHeightMm <= Document.HeadMaxMm);

    /// <summary>
    /// Marge au-dessus du crâne acceptable POUR CE DOCUMENT — les bornes suivent son
    /// format, elles ne sont plus les millimètres français appliqués à tous.
    /// Voir <see cref="IdDocumentSpec.CrownMarginMinMm"/>.
    /// </summary>
    public bool CrownOk => CrownMarginMm >= Document.CrownMarginMinMm
                           && CrownMarginMm <= Document.CrownMarginMaxMm;

    public bool CenteredOk => Math.Abs(CenterOffsetMm) <= Document.CenterToleranceMm;

    public bool Compliant => HeadHeightOk && CrownOk && CenteredOk;
}

/// <summary>
/// Gabarit photo d'identité française 35×45 mm : tête (menton → sommet du crâne)
/// de 32 à 36 mm, centrée, marge au-dessus du crâne. Fonctions pures.
/// </summary>
public static class IdPhotoFr
{
    public const double PhotoWidthMm = 35;
    public const double PhotoHeightMm = 45;
    public const double HeadMinMm = 32;
    public const double HeadMaxMm = 36;
    /// <summary>
    /// Hauteur de tête visée pour le CADRAGE — calée sur DiLand le 03/08/2026.
    ///
    /// Mesuré sur la MÊME photo source avec LE MÊME détecteur, DiLand rend une tête qui
    /// occupe 0,83 du cadre. Le rapport obtenu vaut exactement <c>TargetHeadMm / 45</c>
    /// (vérifié, sans bornage), d'où 0,83 × 45 ≈ 37,35.
    ///
    /// Cette cote est exprimée dans les unités de NOTRE ESTIMATEUR, qui lit haut : il
    /// déduit le sommet du crâne en gonflant de 28 % la boîte du visage
    /// (<see cref="EstimateHead"/>), et déborde donc sur les cheveux. Elle n'est pas
    /// comparable telle quelle aux 32–36 mm de la norme — c'est le rôle de
    /// <see cref="SurestimationDeLEstimateur"/> de faire le pont.
    /// </summary>
    public const double TargetHeadMm = 37.35;

    /// <summary>
    /// De combien <see cref="EstimateHead"/> surestime la tête réelle (menton → sommet du
    /// crâne) au sens de la norme.
    ///
    /// Il faut ce facteur pour que les deux mondes se parlent : le cadrage vise
    /// <see cref="TargetHeadMm"/> = 37,35 dans les unités de l'estimateur, alors que la
    /// conformité se juge sur les 32–36 mm de la norme. Sans correction, tout tirage calé
    /// sur DiLand serait annoncé « tête trop grande », et l'opérateur défairait un cadrage
    /// juste.
    ///
    /// La valeur est CALIBRÉE, pas mesurée sur des vrais crânes : DiLand sort ainsi depuis
    /// des années sans refus au guichet, on cale donc son rendu au milieu haut de la norme
    /// (37,35 → 35,0 mm), ce qui laisse du battement des deux côtés.
    /// </summary>
    public const double SurestimationDeLEstimateur = 37.35 / 35.0;
    public const double CrownMarginMinMm = 2;
    public const double CrownMarginMaxMm = 7;

    /// <summary>
    /// Marge visée au-dessus du crâne — CALÉE SUR DILAND, à la demande de Florian le
    /// 03/08/2026.
    ///
    /// Elle valait 4 mm. Comparaison faite entre un tirage DiLand et un tirage Studio du
    /// même jour, en passant le MÊME détecteur sur les deux : la hauteur de tête est
    /// identique (0,83 du cadre de part et d'autre), mais DiLand pose le sommet du crâne
    /// au ras du bord haut là où nous le laissions 2,25 mm plus bas. C'est ce décalage,
    /// et lui seul, qui faisait paraître nos photos « plus petites ».
    ///
    /// D'où 4 − 2,25 ≈ 1,75. La valeur est un CALAGE MESURÉ, pas une cote de la norme :
    /// l'écart entre le crâne estimé par la détection et le crâne réel absorbe la
    /// différence. Les bornes de conformité suivent automatiquement
    /// (<see cref="IdDocumentSpec.CrownMarginMinMm"/>), donc rien ne passe à l'orange.
    /// </summary>
    public const double TargetCrownMarginMm = 1.75;
    public const double CenterToleranceMm = 2;

    /// <summary>
    /// Tête complète estimée depuis la boîte visage YuNet (haut du front → menton) :
    /// le crâne et les cheveux débordent vers le haut d'environ 28 % de la boîte.
    /// </summary>
    public static NormRect EstimateHead(NormRect faceBox) =>
        new(faceBox.X, faceBox.Y - 0.28 * faceBox.Height, faceBox.Width, 1.28 * faceBox.Height);

    /// <summary>
    /// Largeur de tête présumée, en proportion de sa hauteur. Elle ne sert qu'à donner
    /// une emprise au rectangle : le cadrage n'utilise que le centre et la hauteur.
    /// </summary>
    private const double HeadWidthRatio = 0.75;

    /// <summary>
    /// Tête définie par deux repères posés par l'opérateur : le sommet du crâne et le bas
    /// du menton. C'est la méthode de DiLand, et la plus fiable — la détection
    /// automatique se trompe sur les cheveux volumineux, les couvre-chefs et les bébés,
    /// alors que ces deux points-là ne se discutent pas.
    /// </summary>
    /// <param name="crown">Sommet du crâne, cheveux compris.</param>
    /// <param name="chin">Bas du menton.</param>
    public static NormRect HeadFromMarkers(NormPoint crown, NormPoint chin)
    {
        ArgumentNullException.ThrowIfNull(crown);
        ArgumentNullException.ThrowIfNull(chin);

        // les repères peuvent être posés dans n'importe quel ordre : on remet d'aplomb
        var haut = Math.Min(crown.Y, chin.Y);
        var bas = Math.Max(crown.Y, chin.Y);
        var hauteur = bas - haut;

        if (hauteur <= 0)
            throw new ArgumentException(
                "Le sommet du crâne et le menton sont au même endroit : impossible d'en déduire une taille de visage.",
                nameof(chin));

        // l'axe du visage passe entre les deux repères : c'est lui qui sert au centrage
        var centreX = (crown.X + chin.X) / 2;
        var largeur = hauteur * HeadWidthRatio;

        return new NormRect(centreX - largeur / 2, haut, largeur, hauteur);
    }

    /// <summary>
    /// Cadre 35×45 déduit des deux repères, prêt à imprimer.
    /// </summary>
    public static CropSpec CropFromMarkers(NormPoint crown, NormPoint chin, int imageWidth, int imageHeight) =>
        ComputeCrop(HeadFromMarkers(crown, chin), imageWidth, imageHeight);

    /// <summary>
    /// Cadre 35×45 idéal pour la tête donnée : tête à 34 mm, crâne à 4 mm du bord haut,
    /// centré sur la tête. Le résultat est borné à l'image (la conformité peut donc
    /// être dégradée sur une photo trop serrée — Check le mesurera).
    /// </summary>
    public static CropSpec ComputeCrop(NormRect head, int imageWidth, int imageHeight) =>
        ComputeCrop(head, imageWidth, imageHeight, IdDocumentSpec.France);

    /// <summary>
    /// Cadre idéal pour le document visé : visage à la hauteur visée, crâne à la marge
    /// prévue, centré. Chaque pays a ses cotes — un passeport espagnol fait 26 × 32 mm
    /// là où le français fait 35 × 45.
    /// </summary>
    public static CropSpec ComputeCrop(NormRect head, int imageWidth, int imageHeight, IdDocumentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (imageWidth <= 0 || imageHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imageWidth));
        if (!spec.IsUsable)
            throw new ArgumentException($"Document sans cotes exploitables : {spec.Label}", nameof(spec));

        var cropH = head.Height * (spec.HeightMm / spec.TargetHeadMm);
        // proportions du document exprimées en coordonnées normalisées
        var cropW = cropH * (spec.WidthMm / spec.HeightMm) * imageHeight / imageWidth;
        var top = head.Y - cropH * (spec.TargetCrownMarginMm / spec.HeightMm);
        return CropMath.ClampToBounds(new CropSpec(head.CenterX - cropW / 2, top, cropW, cropH));
    }

    /// <summary>Mesure le cadrage actuel contre le gabarit (mm sur le tirage final).</summary>
    public static IdCompliance Check(CropSpec crop, NormRect head) =>
        Check(crop, head, IdDocumentSpec.France);

    /// <summary>Mesure le cadrage contre le gabarit du document visé.</summary>
    public static IdCompliance Check(CropSpec crop, NormRect head, IdDocumentSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (crop.Height <= 0 || crop.Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(crop));

        // La hauteur est ramenée aux unités de la NORME avant d'être jugée : l'estimateur
        // lit haut (voir SurestimationDeLEstimateur), et la comparer telle quelle aux
        // 32–36 mm déclarerait « tête trop grande » un cadrage pourtant calé sur DiLand.
        var headHeightMm = head.Height / crop.Height * spec.HeightMm / SurestimationDeLEstimateur;

        // La marge de crâne, elle, n'est PAS corrigée : elle se mesure entre deux traits
        // du même repère (le haut du cadre et le sommet estimé), pas contre une cote
        // anatomique. La corriger la fausserait.
        var crownMarginMm = (head.Y - crop.Y) / crop.Height * spec.HeightMm;
        var centerOffsetMm = (head.CenterX - (crop.X + crop.Width / 2)) / crop.Width * spec.WidthMm;
        return new IdCompliance(headHeightMm, crownMarginMm, centerOffsetMm, spec);
    }
}
