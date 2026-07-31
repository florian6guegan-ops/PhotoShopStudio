namespace Studio.Printing.Devices.Fuji;

/// <summary>
/// Un format tirable sur le DE100. <paramref name="ShortSideMm"/> correspond à la largeur
/// du rouleau nécessaire ; <paramref name="LengthMm"/> est ce que le tirage consomme en
/// longueur de papier.
/// </summary>
/// <param name="Name">Nom commercial, tel qu'affiché à l'opérateur.</param>
/// <param name="ChannelId">Identifiant de canal côté minilab.</param>
/// <param name="ShortSideMm">Côté qui doit correspondre à la largeur du rouleau.</param>
/// <param name="LengthMm">Longueur consommée sur le rouleau, ou longueur mini pour un format variable.</param>
/// <param name="IsVariable">Format à longueur libre (panoramique).</param>
public sealed record De100Format(
    string Name,
    string ChannelId,
    int ShortSideMm,
    int LengthMm,
    bool IsVariable = false);

/// <summary>
/// Estimation du nombre de tirages restants pour un format donné.
/// </summary>
/// <param name="Format">Le format concerné.</param>
/// <param name="RemainingPrints">Tirages encore possibles avec le papier restant.</param>
public sealed record De100FormatAvailability(De100Format Format, int RemainingPrints);

/// <summary>
/// Formats du minilab Fuji Frontier DE100, relevés dans le pilote de DiLand.
///
/// Un format n'est tirable que si la largeur du rouleau chargé correspond à l'un de ses
/// deux côtés. DiLand s'arrête là et annonce une quantité illimitée ; on va plus loin en
/// estimant le nombre de tirages possibles avec le papier qui reste.
/// </summary>
public static class De100Formats
{
    /// <summary>Catalogue complet, indépendant du papier chargé.</summary>
    public static IReadOnlyList<De100Format> All { get; } =
    [
        new("9xS", "9xS", 89, 50, IsVariable: true),
        new("9xL", "9xL", 89, 89, IsVariable: true),
        new("10xS", "10xS", 102, 50, IsVariable: true),
        new("10xL", "10xL", 102, 102, IsVariable: true),
        new("10x10", "10xL", 102, 102),
        new("10x13", "10x13", 102, 127),
        new("10x15", "10x15", 102, 152),
        new("10x20", "10x20", 102, 203),
        new("13xS", "13xS", 127, 50, IsVariable: true),
        new("13xL", "13xL", 127, 127, IsVariable: true),
        new("13x9", "13xS", 127, 89),
        new("13x13", "13xL", 127, 127),
        new("13x15", "13x15", 127, 152),
        new("13x17", "13xL", 127, 170),
        new("13x18", "13xL", 127, 180),
        new("13x19", "13xL", 127, 190),
        new("13x20", "13x20", 127, 203),
        new("13x26", "13xL", 127, 254),
        new("15xS", "15xS", 152, 50, IsVariable: true),
        new("15xL", "15xL", 152, 152, IsVariable: true),
        new("15x15", "15xL", 152, 152),
        new("15x20", "15x20", 152, 203),
        new("15x23", "15xL", 152, 228),
        new("15x30", "15xL", 152, 304),
        new("15x40", "15xL", 152, 400),
        new("20xS", "20xS", 203, 50, IsVariable: true),
        new("20xL", "20xL", 203, 203, IsVariable: true),
        new("20x20", "20xL", 203, 203),
        new("20x25", "20xL", 203, 256),
        new("20x27", "20xL", 203, 273),
        new("20x30", "20xL", 203, 307),
        new("20x40", "20xL", 203, 400),
    ];

    /// <summary>
    /// Formats tirables sur un rouleau de cette largeur. La règle est celle du pilote de
    /// DiLand : la largeur du rouleau doit être exactement l'un des deux côtés du format.
    /// </summary>
    public static IEnumerable<De100Format> ForPaperWidth(int paperWidthMm) =>
        All.Where(f => f.ShortSideMm == paperWidthMm || f.LengthMm == paperWidthMm);

    /// <summary>
    /// Tirages encore possibles pour chaque format, avec le papier restant.
    /// Les formats à longueur libre sont estimés sur leur longueur minimale.
    /// </summary>
    /// <param name="paperWidthMm">Largeur du rouleau chargé.</param>
    /// <param name="paperRemainingMm">Longueur de papier restante, en millimètres.</param>
    public static IReadOnlyList<De100FormatAvailability> Estimate(int paperWidthMm, double paperRemainingMm)
    {
        if (paperRemainingMm < 0) paperRemainingMm = 0;

        return ForPaperWidth(paperWidthMm)
            .Select(f => new De100FormatAvailability(f, EstimatePrints(f, paperRemainingMm, paperWidthMm)))
            .OrderBy(a => ConsumedLengthMm(a.Format, paperWidthMm))
            .ThenBy(a => a.Format.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Longueur de rouleau qu'un tirage consomme réellement.
    ///
    /// Un format se pose dans un sens ou dans l'autre : sur un rouleau de 152 mm, un
    /// 10×15 sort en travers et ne consomme que ses 102 mm. Compter sa grande dimension
    /// sous-estimerait d'un tiers le nombre de tirages restants.
    /// </summary>
    public static int ConsumedLengthMm(De100Format format, int paperWidthMm)
    {
        ArgumentNullException.ThrowIfNull(format);
        return paperWidthMm == format.ShortSideMm ? format.LengthMm : format.ShortSideMm;
    }

    /// <summary>Nombre de tirages entiers obtenables dans la longueur restante.</summary>
    public static int EstimatePrints(De100Format format, double paperRemainingMm, int paperWidthMm)
    {
        ArgumentNullException.ThrowIfNull(format);
        var consomme = ConsumedLengthMm(format, paperWidthMm);
        if (consomme <= 0 || paperRemainingMm <= 0) return 0;
        return (int)(paperRemainingMm / consomme);
    }
}
