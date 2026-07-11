using ImageMagick;

namespace Studio.Tests;

/// <summary>
/// Spikes M0 : vérifie que Magick.NET et ses bibliothèques natives (libheif pour
/// le HEIC des iPhone, lcms2 pour l'ICC) sont opérationnels sur cette machine.
/// </summary>
public class MagickSpikeTests
{
    [Fact]
    public void Heic_ReadSupported()
    {
        var info = MagickFormatInfo.Create(MagickFormat.Heic);
        Assert.NotNull(info);
        Assert.True(info!.SupportsReading, "HEIC illisible : libheif absent du paquet Magick.NET");
    }

    [Fact]
    public void Jpeg_EncodeDecodeRoundTrip()
    {
        using var image = new MagickImage(MagickColors.Tomato, 320, 200);
        var jpeg = image.ToByteArray(MagickFormat.Jpeg);
        Assert.True(jpeg.Length > 0);

        using var back = new MagickImage(jpeg);
        Assert.Equal(320u, back.Width);
        Assert.Equal(200u, back.Height);
    }

    [Fact]
    public void Jpeg_SizeHintDecode_ProducesSmallThumbnail()
    {
        // le décodage avec indication de taille est la clé de la frugalité mémoire :
        // on ne décode jamais un 24 Mpx entier pour afficher une vignette
        using var big = new MagickImage(MagickColors.SteelBlue, 6000, 4000);
        var jpeg = big.ToByteArray(MagickFormat.Jpeg);

        // « jpeg:size » : libjpeg décode directement à l'échelle DCT la plus proche
        // (ici 6000 → 750 px), sans jamais matérialiser l'image pleine résolution
        var settings = new MagickReadSettings();
        settings.SetDefine(MagickFormat.Jpeg, "size", "400x400");
        using var thumb = new MagickImage(jpeg, settings);
        Assert.True(thumb.Width < 6000, "L'indication jpeg:size a été ignorée");
        Assert.True(thumb.Width <= 800, $"Vignette trop grande : {thumb.Width}px");
    }

    [Fact]
    public void SrgbColorProfile_IsAvailable()
    {
        Assert.NotNull(ColorProfiles.SRGB);
        Assert.True(ColorProfiles.SRGB.ToByteArray().Length > 0);
    }
}
