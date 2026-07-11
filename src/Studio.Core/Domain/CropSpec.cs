namespace Studio.Core.Domain;

/// <summary>
/// Rectangle de recadrage en coordonnées normalisées (0..1), exprimé sur l'image
/// déjà orientée (EXIF appliqué + rotation utilisateur).
/// </summary>
public sealed record CropSpec(double X, double Y, double Width, double Height)
{
    public static CropSpec Full { get; } = new(0, 0, 1, 1);

    public bool IsFull => X == 0 && Y == 0 && Width == 1 && Height == 1;

    public bool IsValid =>
        Width > 0 && Height > 0 &&
        X >= 0 && Y >= 0 &&
        X + Width <= 1.0000001 && Y + Height <= 1.0000001;
}
