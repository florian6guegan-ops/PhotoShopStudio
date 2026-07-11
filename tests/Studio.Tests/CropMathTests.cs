using Studio.Imaging.Geometry;

namespace Studio.Tests;

public class CropMathTests
{
    [Fact]
    public void CenterCrop_WideImage_CropsSides()
    {
        // image 6000×4000 (3:2) vers 10×15 portrait (aspect 102/152 ≈ 0,671)
        var crop = CropMath.CenterCrop(6000, 4000, 102.0 / 152.0);

        Assert.Equal(1, crop.Height, 10);
        Assert.True(crop.Width < 1);
        Assert.Equal((1 - crop.Width) / 2, crop.X, 10);
        // l'aspect du recadrage en pixels doit être celui demandé
        var rect = CropMath.ToPixelRect(crop, 6000, 4000);
        Assert.Equal(102.0 / 152.0, (double)rect.Width / rect.Height, 2);
    }

    [Fact]
    public void CenterCrop_TallImage_CropsTopAndBottom()
    {
        var crop = CropMath.CenterCrop(4000, 6000, 152.0 / 102.0);

        Assert.Equal(1, crop.Width, 10);
        Assert.True(crop.Height < 1);
        var rect = CropMath.ToPixelRect(crop, 4000, 6000);
        Assert.Equal(152.0 / 102.0, (double)rect.Width / rect.Height, 2);
    }

    [Fact]
    public void CenterCrop_MatchingAspect_IsFull()
    {
        var crop = CropMath.CenterCrop(3000, 2000, 1.5);
        Assert.Equal(1, crop.Width, 10);
        Assert.Equal(1, crop.Height, 10);
    }

    [Fact]
    public void ToPixelRect_NeverExceedsImageBounds()
    {
        var rect = CropMath.ToPixelRect(new Studio.Core.Domain.CropSpec(0.5, 0.5, 0.5, 0.5), 3001, 2001);
        Assert.True(rect.Right <= 3001);
        Assert.True(rect.Bottom <= 2001);
    }

    [Fact]
    public void FitWithin_TallImageInPortraitCanvas_FullHeight()
    {
        // 10×15 à 300 dpi = 1205×1795 ; image 2:3 portrait
        var rect = CropMath.FitWithin(1205, 1795, 2.0 / 3.0);
        Assert.Equal(1795, rect.Height);
        Assert.Equal((int)Math.Round(1795 * 2.0 / 3.0), rect.Width);
        // centré
        Assert.Equal((1205 - rect.Width) / 2, rect.X);
    }

    [Fact]
    public void FitWithin_RespectsBorder()
    {
        var rect = CropMath.FitWithin(1000, 1000, 1.0, borderPx: 50);
        Assert.Equal(900, rect.Width);
        Assert.Equal(900, rect.Height);
        Assert.Equal(50, rect.X);
        Assert.Equal(50, rect.Y);
    }

    [Fact]
    public void ClampToBounds_PushedOutsideCrop_ComesBack()
    {
        var clamped = CropMath.ClampToBounds(new Studio.Core.Domain.CropSpec(0.8, -0.1, 0.5, 0.5));
        Assert.Equal(0.5, clamped.X, 10);
        Assert.Equal(0.0, clamped.Y, 10);
        Assert.True(clamped.IsValid);
    }
}
