using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

public class ImagePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioTests-" + Guid.NewGuid().ToString("N"));

    public ImagePipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string MakeJpeg(string name, uint width, uint height, MagickColor? color = null)
    {
        var path = Path.Combine(_root, name);
        using var image = new MagickImage(color ?? MagickColors.CornflowerBlue, width, height);
        image.Write(path, MagickFormat.Jpeg);
        return path;
    }

    private static RenderRequest Request(string source, int w, int h, FitMode fit = FitMode.Fill, int border = 0) =>
        new(source, w, h, CropSpec.Full, 0, fit, border, new ImageAdjustments());

    [Fact]
    public void Fill_ProducesExactTargetDimensions()
    {
        var source = MakeJpeg("wide.jpg", 6000, 4000);
        var output = Path.Combine(_root, "out.png");

        // 10×15 portrait à 300 dpi depuis une photo paysage 3:2
        ImagePipeline.RenderToFile(Request(source, 1205, 1795), output);

        using var result = new MagickImage(output);
        Assert.Equal(1205u, result.Width);
        Assert.Equal(1795u, result.Height);
    }

    [Fact]
    public void Fit_ProducesExactTargetDimensions_WithWhiteBorders()
    {
        var source = MakeJpeg("tall.jpg", 2000, 3000, MagickColors.Black);
        var output = Path.Combine(_root, "out.png");

        ImagePipeline.RenderToFile(Request(source, 1795, 1205, FitMode.Fit), output);

        using var result = new MagickImage(output);
        Assert.Equal(1795u, result.Width);
        Assert.Equal(1205u, result.Height);

        // les bandes latérales doivent être blanches (Q8 : canaux 0..255)
        using var corner = result.CloneArea(0, 0, 10, 10);
        Assert.Equal(255, corner.GetPixels().First().ToColor()!.R);
    }

    [Fact]
    public void UserRotation_SwapsDimensions()
    {
        var source = MakeJpeg("photo.jpg", 3000, 2000);
        var output = Path.Combine(_root, "out.png");

        ImagePipeline.RenderToFile(Request(source, 1205, 1795) with { RotationQuarterTurns = 1 }, output);

        using var result = new MagickImage(output);
        Assert.Equal(1205u, result.Width);
        Assert.Equal(1795u, result.Height);
    }

    [Fact]
    public void Grayscale_RemovesColor()
    {
        var source = MakeJpeg("red.jpg", 800, 600, MagickColors.Red);
        var output = Path.Combine(_root, "out.png");

        ImagePipeline.RenderToFile(
            Request(source, 400, 300) with { Adjustments = new ImageAdjustments { Grayscale = true } },
            output);

        using var result = new MagickImage(output);
        var pixel = result.GetPixels().First().ToColor()!;
        Assert.Equal(pixel.G, pixel.R); // R = G = B en niveaux de gris
        Assert.Equal(pixel.B, pixel.R);
    }

    [Fact]
    public void IdSheet_HasExactSize_CellsFilled_MarginsWhite()
    {
        var source = MakeJpeg("face.jpg", 2100, 2700, MagickColors.SaddleBrown);
        var output = Path.Combine(_root, "sheet.png");

        // planche 10×15 portrait à 300 dpi, 6 cellules 35×45
        var cellW = Studio.Imaging.Geometry.MmPx.ToPixels(35, 300);
        var cellH = Studio.Imaging.Geometry.MmPx.ToPixels(45, 300);
        ImagePipeline.RenderIdSheetToFile(
            Request(source, cellW, cellH), copies: 6, gapMm: 2, cutMarks: true,
            sheetWidthPx: 1205, sheetHeightPx: 1795, outputPath: output);

        using var result = new MagickImage(output);
        Assert.Equal(1205u, result.Width);
        Assert.Equal(1795u, result.Height);

        // grille 2×3 : la première cellule commence vers (177,77) — point pris en son cœur
        var inCell = result.CloneArea(380, 340, 4, 4).GetPixels().First().ToColor()!;
        Assert.True(inCell.R > 80 && inCell.B < 80, $"Cellule attendue, obtenu R={inCell.R} B={inCell.B}");

        // coin : marge blanche (hors traits de coupe)
        var corner = result.CloneArea(30, 60, 2, 2).GetPixels().First().ToColor()!;
        Assert.Equal(255, corner.R);
    }

    [Fact]
    public void Crop_IsAppliedBeforeResize()
    {
        // image moitié gauche noire, moitié droite blanche
        var path = Path.Combine(_root, "split.jpg");
        using (var image = new MagickImage(MagickColors.Black, 2000, 1000))
        {
            using var white = new MagickImage(MagickColors.White, 1000, 1000);
            image.Composite(white, 1000, 0);
            image.Write(path, MagickFormat.Jpeg);
        }

        var output = Path.Combine(_root, "out.png");
        // recadrage : uniquement la moitié droite (blanche)
        ImagePipeline.RenderToFile(
            Request(path, 500, 500) with { Crop = new CropSpec(0.5, 0, 0.5, 1) },
            output);

        using var result = new MagickImage(output);
        var pixel = result.GetPixels().First().ToColor()!;
        Assert.True(pixel.R > 230, $"Attendu blanc, obtenu R={pixel.R}");
    }
}
