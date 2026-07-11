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

    [Fact]
    public void Pan_KeepsSizeAndStaysInBounds()
    {
        var crop = new Studio.Core.Domain.CropSpec(0.2, 0.2, 0.4, 0.4);

        var moved = CropMath.Pan(crop, 0.1, -0.05);
        Assert.Equal(0.3, moved.X, 10);
        Assert.Equal(0.15, moved.Y, 10);
        Assert.Equal(crop.Width, moved.Width, 10);

        var blocked = CropMath.Pan(crop, 5, 5); // butée bas-droite
        Assert.Equal(1 - crop.Width, blocked.X, 10);
        Assert.Equal(1 - crop.Height, blocked.Y, 10);
    }

    [Fact]
    public void Zoom_PreservesPixelAspect()
    {
        const double aspect = 102.0 / 152.0;
        var crop = CropMath.CenterCrop(6000, 4000, aspect);

        var zoomed = CropMath.Zoom(crop, 0.5, 6000, 4000, aspect);
        var rect = CropMath.ToPixelRect(zoomed, 6000, 4000);
        Assert.Equal(aspect, (double)rect.Width / rect.Height, 2);
        Assert.Equal(crop.Width * 0.5, zoomed.Width, 6);
    }

    [Fact]
    public void Zoom_NeverExceedsMaxCrop_NorMinShare()
    {
        const double aspect = 1.0;
        var max = CropMath.CenterCrop(4000, 3000, aspect);
        var small = CropMath.Zoom(max, 0.01, 4000, 3000, aspect); // demande ×100 : bornée à ×5
        Assert.Equal(max.Width * CropMath.MinZoomShare, small.Width, 6);

        var big = CropMath.Zoom(small, 100, 4000, 3000, aspect); // dézoom au-delà du max : borné
        Assert.Equal(max.Width, big.Width, 6);
        Assert.Equal(max.Height, big.Height, 6);
    }

    [Fact]
    public void OrientCanvas_LandscapePhotoOnPortraitProduct_Swaps()
    {
        // 10×15 (1205×1795 px) avec une photo 3:2 paysage → canevas 15×10
        var (w, h) = CropMath.OrientCanvas(1205, 1795, 6000, 4000, Studio.Core.Domain.CropSpec.Full);
        Assert.Equal((1795, 1205), (w, h));
    }

    [Fact]
    public void OrientCanvas_PortraitPhoto_Unchanged()
    {
        var (w, h) = CropMath.OrientCanvas(1205, 1795, 4000, 6000, Studio.Core.Domain.CropSpec.Full);
        Assert.Equal((1205, 1795), (w, h));
    }

    [Fact]
    public void OrientCanvas_FollowsCropAspect_NotImageAspect()
    {
        // photo paysage mais recadrage portrait serré → le canevas reste portrait
        var crop = new Studio.Core.Domain.CropSpec(0.3, 0, 0.3, 1); // 1800×4000 px sur 6000×4000
        var (w, h) = CropMath.OrientCanvas(1205, 1795, 6000, 4000, crop);
        Assert.Equal((1205, 1795), (w, h));
    }
}
