using Studio.Imaging.Geometry;

namespace Studio.Tests;

public class IdSheetLayoutTests
{
    // Planche identité française : 6 photos 35×45 mm sur un 10×15 (102×152 mm) à 300 dpi
    private const int Dpi = 300;
    private static readonly int SheetW = MmPx.ToPixels(102, Dpi);  // 1205
    private static readonly int SheetH = MmPx.ToPixels(152, Dpi);  // 1795
    private static readonly int CellW = MmPx.ToPixels(35, Dpi);    // 413
    private static readonly int CellH = MmPx.ToPixels(45, Dpi);    // 531
    private static readonly int Gap = MmPx.ToPixels(2, Dpi);       // 24

    [Fact]
    public void MmPx_ReferenceValues()
    {
        Assert.Equal(1205, MmPx.ToPixels(102, 300));
        Assert.Equal(1795, MmPx.ToPixels(152, 300));
        Assert.Equal(413, MmPx.ToPixels(35, 300));
        Assert.Equal(531, MmPx.ToPixels(45, 300));
    }

    [Fact]
    public void MaxCopies_On10x15_Is6()
    {
        // 2 colonnes × 3 lignes de 35×45 : c'est la borne du sélecteur « Photos » de l'écran identité
        Assert.Equal(6, IdSheetLayout.MaxCopies(SheetW, SheetH, CellW, CellH, Gap));
    }

    [Fact]
    public void MaxCopies_IsZero_WhenCellTooBigForSheet()
    {
        Assert.Equal(0, IdSheetLayout.MaxCopies(SheetW, SheetH, SheetW + 1, CellH, Gap));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(6)]
    public void AnyCountUpToMax_LaysOut(int copies)
    {
        var layout = IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH, Gap, copies);

        Assert.Equal(copies, layout.Cells.Count);
        Assert.All(layout.Cells, cell =>
        {
            Assert.InRange(cell.X, 0, SheetW - CellW);
            Assert.InRange(cell.Y, 0, SheetH - CellH);
        });
    }

    [Fact]
    public void SixIdPhotos_FitOn10x15_As2x3()
    {
        var layout = IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH, Gap, copies: 6);

        Assert.Equal(6, layout.Cells.Count);
        Assert.Equal(2, layout.Columns);
        Assert.Equal(3, layout.Rows);

        // toutes les cellules sont dans la planche
        Assert.All(layout.Cells, c =>
        {
            Assert.True(c.X >= 0 && c.Y >= 0);
            Assert.True(c.Right <= SheetW && c.Bottom <= SheetH);
        });

        // toutes les cellules font exactement la taille demandée
        Assert.All(layout.Cells, c =>
        {
            Assert.Equal(CellW, c.Width);
            Assert.Equal(CellH, c.Height);
        });

        // le bloc est centré (à 1 px près, division entière)
        var minX = layout.Cells.Min(c => c.X);
        var maxRight = layout.Cells.Max(c => c.Right);
        Assert.True(Math.Abs(minX - (SheetW - maxRight)) <= 1);
    }

    [Fact]
    public void Cells_DoNotOverlap()
    {
        var layout = IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH, Gap, copies: 6);
        var cells = layout.Cells;
        for (var i = 0; i < cells.Count; i++)
            for (var j = i + 1; j < cells.Count; j++)
            {
                var a = cells[i];
                var b = cells[j];
                var overlap = a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
                Assert.False(overlap, $"Les cellules {i} et {j} se chevauchent");
            }
    }

    [Fact]
    public void FourPhotos_LayoutIs2x2()
    {
        var layout = IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH, Gap, copies: 4);
        Assert.Equal(2, layout.Columns);
        Assert.Equal(2, layout.Rows);
    }

    [Fact]
    public void TooManyCopies_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH, Gap, copies: 100));
    }

    [Fact]
    public void CutTicks_StayInMargins_AndAlignWithCellEdges()
    {
        var tick = MmPx.ToPixels(3, Dpi);
        var layout = IdSheetLayout.Layout(SheetW, SheetH, CellW, CellH, Gap, copies: 6, tickLength: tick);

        Assert.NotEmpty(layout.CutTicks);
        var xs = layout.Cells.SelectMany(c => new[] { c.X, c.Right }).ToHashSet();
        var ys = layout.Cells.SelectMany(c => new[] { c.Y, c.Bottom }).ToHashSet();

        Assert.All(layout.CutTicks, t =>
        {
            var vertical = t.X1 == t.X2;
            if (vertical) Assert.Contains(t.X1, xs);
            else Assert.Contains(t.Y1, ys);
        });
    }
}
