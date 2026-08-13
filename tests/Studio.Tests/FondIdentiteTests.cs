using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le fond posé derrière le sujet d'une photo d'identité.
///
/// <b>Pourquoi le gris existe à côté du blanc.</b> Les textes demandent un fond « uni, de
/// couleur claire », et le blanc franc y est mal vu : une chemise blanche, des cheveux
/// blancs ou une peau très claire s'y fondent, et la silhouette cesse de se détacher.
/// </summary>
/// <remarks>
/// Dans la collection depuis le 13/08/2026 : poser un fond passe désormais par la mémoire
/// des masques de <c>MasqueSujet</c>, qui n'en garde que quatre. Menés de front avec
/// <see cref="MasqueReutiliseTests"/>, ces essais évinçaient les masques qu'il venait
/// justement de ranger — et il tombait une fois sur deux, sans rien avoir de faux.
/// </remarks>
[Collection(DetourageStatiqueCollection.Nom)]
public class FondIdentiteTests
{
    /// <summary>Une image dont le pourtour est un fond de studio et le centre un « sujet ».</summary>
    private static MagickImage Portrait()
    {
        var image = new MagickImage(MagickColor.FromRgb(203, 210, 207), 240, 320);
        using var sujet = new MagickImage(MagickColor.FromRgb(60, 40, 30), 120, 200);
        image.Composite(sujet, 60, 90, CompositeOperator.Over);
        return image;
    }

    /// <summary>Couleur relevée au coin, là où il n'y a jamais personne.</summary>
    private static IMagickColor<byte> CouleurDuFond(IMagickImage<byte> image)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(2, 2).ToColor()!;
    }

    // ————— la couleur posée —————

    /// <summary>
    /// 210 sur les trois canaux : la valeur des labos. Si elle change un jour, que ce soit
    /// une décision, pas un effet de bord.
    /// </summary>
    [Fact]
    public void Le_gris_d_identite_vaut_210_sur_les_trois_canaux()
    {
        Assert.Equal(210, BackgroundRemoval.GrisIdentite.R);
        Assert.Equal(210, BackgroundRemoval.GrisIdentite.G);
        Assert.Equal(210, BackgroundRemoval.GrisIdentite.B);
    }

    /// <summary>Il doit rester franchement clair : un fond sombre fait refuser la photo.</summary>
    [Fact]
    public void Le_gris_d_identite_reste_clair()
    {
        Assert.InRange(BackgroundRemoval.GrisIdentite.R, (byte)180, (byte)240);
    }

    [Fact]
    public void Le_fond_gris_pose_bien_du_gris()
    {
        using var image = Portrait();

        Assert.True(BackgroundRemoval.PoserUnFond(image, BackgroundRemoval.GrisIdentite));

        var coin = CouleurDuFond(image);
        Assert.InRange(coin.R, (byte)200, (byte)220);
        Assert.InRange(coin.G, (byte)200, (byte)220);
        Assert.InRange(coin.B, (byte)200, (byte)220);
    }

    /// <summary>Le blanc existait avant et ne doit pas avoir bougé.</summary>
    [Fact]
    public void Le_fond_blanc_pose_toujours_du_blanc()
    {
        using var image = Portrait();

        Assert.True(BackgroundRemoval.PoserUnFondBlanc(image));

        var coin = CouleurDuFond(image);
        Assert.InRange(coin.R, (byte)245, (byte)255);
    }

    // ————— ce que la commande retient —————

    /// <summary>Une photo sans réglage ne doit pas déclencher de détourage.</summary>
    [Fact]
    public void Sans_fond_demande_la_commande_reste_neutre()
    {
        Assert.True(new ImageAdjustments().IsNeutral);
    }

    /// <summary>
    /// <b>Sans cela, le fond gris ne serait jamais rendu.</b> <c>ImageAdjuster</c> s'arrête
    /// net sur une commande dite neutre : le gris oublié dans ce test-là sortirait à
    /// l'écran mais pas sur le tirage.
    /// </summary>
    [Fact]
    public void Un_fond_gris_demande_rend_la_commande_non_neutre()
    {
        Assert.False(new ImageAdjustments { GrayBackground = true }.IsNeutral);
    }

    [Fact]
    public void Un_fond_blanc_demande_rend_la_commande_non_neutre()
    {
        Assert.False(new ImageAdjustments { WhiteBackground = true }.IsNeutral);
    }

    // ————— quand les deux arrivent vrais —————

    /// <summary>
    /// L'écran rend les deux cases exclusives, mais une commande relue d'un journal ancien
    /// peut porter les deux. Le gris l'emporte : il est accepté partout où le blanc l'est,
    /// l'inverse n'est pas vrai.
    /// </summary>
    [Fact]
    public void Si_les_deux_sont_demandes_le_gris_l_emporte()
    {
        using var image = Portrait();

        ImageAdjuster.Apply(image, new ImageAdjustments
        {
            WhiteBackground = true,
            GrayBackground = true,
        });

        var coin = CouleurDuFond(image);
        Assert.InRange(coin.R, (byte)200, (byte)220);
    }
}
