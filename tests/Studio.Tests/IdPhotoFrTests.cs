using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

public class IdPhotoFrTests
{
    // tête plein cadre idéale : 4000×6000, tête centrée
    private static readonly NormRect Head = new(0.35, 0.20, 0.30, 0.40);

    [Fact]
    public void ComputeCrop_PutsHeadAt34mm_Crown4mm_Centered()
    {
        var crop = IdPhotoFr.ComputeCrop(Head, 4000, 6000);
        var compliance = IdPhotoFr.Check(crop, Head);

        Assert.Equal(IdPhotoFr.TargetHeadMm, compliance.HeadHeightMm, 1);
        Assert.Equal(IdPhotoFr.TargetCrownMarginMm, compliance.CrownMarginMm, 1);
        Assert.Equal(0, compliance.CenterOffsetMm, 1);
        Assert.True(compliance.Compliant);
    }

    [Fact]
    public void ComputeCrop_HasPhotoAspectInPixels()
    {
        var crop = IdPhotoFr.ComputeCrop(Head, 4000, 6000);
        var rect = CropMath.ToPixelRect(crop, 4000, 6000);
        Assert.Equal(35.0 / 45.0, (double)rect.Width / rect.Height, 2);
    }

    [Fact]
    public void Check_HeadTooSmall_NotCompliant()
    {
        // cadre deux fois trop grand : la tête ne fait plus que ~17 mm
        var ideal = IdPhotoFr.ComputeCrop(Head, 4000, 6000);
        var tooWide = CropMath.Zoom(ideal, 2.0, 4000, 6000, 35.0 / 45.0 * 6000 / 4000);

        var compliance = IdPhotoFr.Check(tooWide, Head);
        Assert.False(compliance.HeadHeightOk);
        Assert.False(compliance.Compliant);
    }

    [Fact]
    public void Check_OffCenterHead_NotCompliant()
    {
        var ideal = IdPhotoFr.ComputeCrop(Head, 4000, 6000);
        var shifted = CropMath.Pan(ideal, 0.12, 0); // décale le cadre → tête excentrée

        var compliance = IdPhotoFr.Check(shifted, Head);
        Assert.False(compliance.CenteredOk);
    }

    [Fact]
    public void EstimateHead_ExtendsFaceBoxUpwards()
    {
        var face = new NormRect(0.4, 0.3, 0.2, 0.25);
        var head = IdPhotoFr.EstimateHead(face);

        Assert.True(head.Y < face.Y);                     // le crâne déborde au-dessus
        Assert.Equal(face.Bottom, head.Bottom, 10);       // même menton
        Assert.Equal(face.X, head.X, 10);
        Assert.True(head.Height > face.Height);
    }

    [Fact]
    public void ComputeCrop_TightImage_IsClampedButMeasurable()
    {
        // tête énorme sur image serrée : le cadre idéal sort de l'image → borné
        var bigHead = new NormRect(0.05, 0.02, 0.9, 0.9);
        var crop = IdPhotoFr.ComputeCrop(bigHead, 1000, 1200);

        Assert.True(crop.IsValid);
        var compliance = IdPhotoFr.Check(crop, bigHead);
        Assert.True(compliance.HeadHeightMm > IdPhotoFr.HeadMaxMm); // et l'UI le signalera
    }
}
