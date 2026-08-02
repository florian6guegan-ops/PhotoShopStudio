using Studio.Imaging.Geometry;
using Xunit;

namespace Studio.Tests;

/// <summary>
/// Le cadre du produit « POLA ».
///
/// Ce qu'il remplace : une marge blanche uniforme de 2 mm, qui ne ressemblait à rien d'un
/// Polaroid. Deux choses font la forme, et ce sont elles qu'on vérifie ici — la fenêtre
/// image est presque CARRÉE, et la bande du bas fait près du quart de la hauteur.
///
/// Cotes de référence : film Polaroid 600 / i-Type, chiffres publiés par Polaroid —
/// tirage 3,483 × 4,233 po (88,47 × 107,52 mm), fenêtre 3,108 × 3,024 po (78,94 × 76,80 mm).
/// </summary>
public class PolaroidFrameTests
{
    [Fact]
    public void La_fenetre_est_presque_carree()
    {
        // 1,028 : c'est ce rapport-là que l'écran de recadrage doit montrer, et non le
        // 0,67 d'un 10×15 — sinon l'opérateur cadre sur des bords qui seront coupés
        Assert.InRange(PolaroidFrame.WindowAspect, 1.02, 1.04);
    }

    [Fact]
    public void La_bande_du_bas_fait_pres_du_quart_de_la_hauteur()
    {
        var part = PolaroidFrame.BottomBandMm / PolaroidFrame.PrintHeightMm;
        Assert.InRange(part, 0.23, 0.25);

        // et elle est cinq fois plus haute que les trois autres marges : c'est ce
        // déséquilibre qu'on reconnaît d'un coup d'œil
        Assert.InRange(PolaroidFrame.BottomBandMm / PolaroidFrame.MarginMm, 5.0, 5.9);
    }

    [Fact]
    public void Sur_un_10x15_le_cadre_prend_toute_la_largeur()
    {
        // 10×15 à 300 ppp : 1205 × 1795 px. Le Polaroid (0,823) est plus large que le
        // 10×15 (0,671) : c'est donc la LARGEUR qui limite, et du blanc reste en haut
        // et en bas — le contour de découpe dit où couper.
        var pose = PolaroidFrame.Place(1205, 1795);

        Assert.Equal(1205, pose.Frame.Width);
        Assert.InRange(pose.Frame.Height, 1460, 1470);   // 1205 / 0,823
        Assert.Equal(0, pose.Frame.X);
        Assert.True(pose.Frame.Y > 100, "le cadre doit être centré, donc descendu");
    }

    [Fact]
    public void La_fenetre_tient_dans_le_cadre_et_y_est_centree_horizontalement()
    {
        var pose = PolaroidFrame.Place(1205, 1795);
        var cadre = pose.Frame;
        var fenetre = pose.Window;

        Assert.True(fenetre.X >= cadre.X, "la fenêtre déborde à gauche");
        Assert.True(fenetre.Right <= cadre.Right, "la fenêtre déborde à droite");
        Assert.True(fenetre.Y >= cadre.Y, "la fenêtre déborde en haut");
        Assert.True(fenetre.Bottom <= cadre.Bottom, "la fenêtre déborde en bas");

        // marges gauche et droite égales à un pixel près (arrondi)
        var gauche = fenetre.X - cadre.X;
        var droite = cadre.Right - fenetre.Right;
        Assert.InRange(Math.Abs(gauche - droite), 0, 1);
    }

    [Fact]
    public void La_bande_du_bas_est_bien_plus_haute_que_la_marge_du_haut()
    {
        var pose = PolaroidFrame.Place(1205, 1795);

        var haut = pose.Window.Y - pose.Frame.Y;
        var bas = pose.Frame.Bottom - pose.Window.Bottom;

        Assert.True(bas > haut * 4,
            $"la bande basse ({bas} px) doit écraser la marge haute ({haut} px) : c'est le Polaroid");
    }

    [Fact]
    public void Un_tirage_paysage_recoit_le_meme_cadre_debout()
    {
        // sur un 15×10, c'est la HAUTEUR qui limite. Le cadre reste debout : le Polaroid
        // n'a pas de version couchée, et sa fenêtre presque carrée s'accommode des deux.
        var pose = PolaroidFrame.Place(1795, 1205);

        Assert.Equal(1205, pose.Frame.Height);
        Assert.True(pose.Frame.Width < pose.Frame.Height, "le cadre doit rester debout");
        Assert.True(pose.Frame.X > 300, "le cadre doit être centré dans la largeur");
    }

    [Fact]
    public void Un_tirage_sans_dimension_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PolaroidFrame.Place(0, 1795));
    }
}
