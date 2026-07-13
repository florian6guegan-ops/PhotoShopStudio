namespace Studio.Imaging.Geometry;

/// <summary>Trait de coupe : petit repère dessiné dans les marges de la planche.</summary>
public sealed record CutTick(int X1, int Y1, int X2, int Y2);

public sealed record SheetLayoutResult(
    IReadOnlyList<PixelRect> Cells,
    IReadOnlyList<CutTick> CutTicks,
    int Columns,
    int Rows);

/// <summary>
/// Disposition d'une planche de N photos identiques (identité) sur un tirage.
/// Fonctions pures, unit-testées au pixel.
/// </summary>
public static class IdSheetLayout
{
    /// <summary>
    /// Nombre maximal de cellules tenant sur la planche. Sert à borner le choix
    /// de l'opérateur avant d'appeler <see cref="Layout"/>, qui lèverait au-delà.
    /// Renvoie 0 si la cellule ne tient pas du tout.
    /// </summary>
    public static int MaxCopies(int sheetWidth, int sheetHeight, int cellWidth, int cellHeight, int gap)
    {
        if (cellWidth <= 0 || cellHeight <= 0 || cellWidth > sheetWidth || cellHeight > sheetHeight)
            return 0;
        return (sheetWidth + gap) / (cellWidth + gap) * ((sheetHeight + gap) / (cellHeight + gap));
    }

    /// <summary>
    /// Calcule la grille : autant de colonnes que possible, lignes nécessaires pour
    /// atteindre <paramref name="copies"/>, bloc centré sur la planche.
    /// Lève une exception si les copies ne tiennent pas.
    /// </summary>
    public static SheetLayoutResult Layout(
        int sheetWidth, int sheetHeight,
        int cellWidth, int cellHeight,
        int gap, int copies, int tickLength = 0)
    {
        if (copies < 1) throw new ArgumentOutOfRangeException(nameof(copies));
        if (cellWidth <= 0 || cellHeight <= 0 || cellWidth > sheetWidth || cellHeight > sheetHeight)
            throw new ArgumentOutOfRangeException(nameof(cellWidth), "Cellule invalide pour la planche");

        var maxCols = (sheetWidth + gap) / (cellWidth + gap);
        var maxRows = (sheetHeight + gap) / (cellHeight + gap);
        if (maxCols * maxRows < copies)
            throw new InvalidOperationException(
                $"{copies} copies de {cellWidth}×{cellHeight}px ne tiennent pas sur {sheetWidth}×{sheetHeight}px " +
                $"(maximum {maxCols * maxRows})");

        var cols = Math.Min(maxCols, copies);
        var rows = (int)Math.Ceiling((double)copies / cols);
        // rééquilibre : préfère une grille compacte (ex 6 copies → 2×3 plutôt que 3×2 selon la place)
        while (rows > maxRows)
        {
            cols++;
            rows = (int)Math.Ceiling((double)copies / cols);
        }

        var blockW = cols * cellWidth + (cols - 1) * gap;
        var blockH = rows * cellHeight + (rows - 1) * gap;
        var originX = (sheetWidth - blockW) / 2;
        var originY = (sheetHeight - blockH) / 2;

        var cells = new List<PixelRect>(copies);
        for (var i = 0; i < copies; i++)
        {
            var col = i % cols;
            var row = i / cols;
            cells.Add(new PixelRect(
                originX + col * (cellWidth + gap),
                originY + row * (cellHeight + gap),
                cellWidth, cellHeight));
        }

        IReadOnlyList<CutTick> ticks = tickLength > 0
            ? BuildCutTicks(sheetWidth, sheetHeight, cells, tickLength)
            : Array.Empty<CutTick>();

        return new SheetLayoutResult(cells, ticks, cols, rows);
    }

    /// <summary>
    /// Repères de coupe dans les marges : pour chaque bord vertical de cellule, deux
    /// ticks en haut et en bas de la planche ; idem horizontalement.
    /// </summary>
    private static List<CutTick> BuildCutTicks(
        int sheetWidth, int sheetHeight, IReadOnlyList<PixelRect> cells, int tickLength)
    {
        var xs = new SortedSet<int>();
        var ys = new SortedSet<int>();
        foreach (var cell in cells)
        {
            xs.Add(cell.X);
            xs.Add(cell.Right);
            ys.Add(cell.Y);
            ys.Add(cell.Bottom);
        }

        var ticks = new List<CutTick>();
        foreach (var x in xs)
        {
            ticks.Add(new CutTick(x, 0, x, tickLength));
            ticks.Add(new CutTick(x, sheetHeight - tickLength, x, sheetHeight));
        }
        foreach (var y in ys)
        {
            ticks.Add(new CutTick(0, y, tickLength, y));
            ticks.Add(new CutTick(sheetWidth - tickLength, y, sheetWidth, y));
        }
        return ticks;
    }
}
