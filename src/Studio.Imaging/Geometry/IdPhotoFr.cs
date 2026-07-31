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

    public bool CrownOk => CrownMarginMm >= IdPhotoFr.CrownMarginMinMm
                           && CrownMarginMm <= IdPhotoFr.CrownMarginMaxMm;

    public bool CenteredOk => Math.Abs(CenterOffsetMm) <= IdPhotoFr.CenterToleranceMm;

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
    public const double TargetHeadMm = 34;
    public const double CrownMarginMinMm = 2;
    public const double CrownMarginMaxMm = 7;
    public const double TargetCrownMarginMm = 4;
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

        var headHeightMm = head.Height / crop.Height * spec.HeightMm;
        var crownMarginMm = (head.Y - crop.Y) / crop.Height * spec.HeightMm;
        var centerOffsetMm = (head.CenterX - (crop.X + crop.Width / 2)) / crop.Width * spec.WidthMm;
        return new IdCompliance(headHeightMm, crownMarginMm, centerOffsetMm, spec);
    }
}
