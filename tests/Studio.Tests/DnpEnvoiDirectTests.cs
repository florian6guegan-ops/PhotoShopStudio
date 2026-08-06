using System.Drawing;
using System.Drawing.Imaging;
using Studio.Printing.Devices.Dnp;

namespace Studio.Tests;

/// <summary>
/// La trame envoyée à une DNP sans passer par le pilote Windows.
///
/// Les trois conventions vérifiées ici ont chacune coûté une feuille de papier le
/// 06/08/2026 : le miroir, le compactage des lignes, et l'ordre des octets.
/// </summary>
public class DnpEnvoiDirectTests
{
    /// <summary>Une image dont chaque pixel est reconnaissable à sa position.</summary>
    private static Bitmap Mire(int largeur, int hauteur)
    {
        var bmp = new Bitmap(largeur, hauteur, PixelFormat.Format24bppRgb);
        for (var y = 0; y < hauteur; y++)
        for (var x = 0; x < largeur; x++)
            bmp.SetPixel(x, y, Color.FromArgb(x * 10 % 256, y * 10 % 256, 128));
        return bmp;
    }

    [Fact]
    public void Les_lignes_sont_compactees_a_largeur_fois_trois()
    {
        // 5 pixels de large = 15 octets par ligne : LockBits en donnerait 16 (aligné sur 4).
        // C'est ce bourrage qui décalait chaque ligne d'un octet de plus que la précédente.
        using var image = Mire(5, 4);

        var (octets, largeur, hauteur) = DnpEnvoiDirect.Preparer(image);

        Assert.Equal(5, largeur);
        Assert.Equal(4, hauteur);
        Assert.Equal(5 * 3 * 4, octets.Length);
    }

    [Fact]
    public void L_image_part_en_miroir_gauche_droite()
    {
        using var image = Mire(4, 1);

        var (octets, largeur, _) = DnpEnvoiDirect.Preparer(image);

        // Le pixel de gauche de la trame envoyée doit être celui de DROITE de l'image.
        var droiteOrigine = image.GetPixel(largeur - 1, 0);

        // LockBits rend du BGR : bleu, vert, rouge dans cet ordre.
        Assert.Equal(droiteOrigine.B, octets[0]);
        Assert.Equal(droiteOrigine.G, octets[1]);
        Assert.Equal(droiteOrigine.R, octets[2]);
    }

    [Fact]
    public void Les_lignes_restent_dans_l_ordre_du_haut_vers_le_bas()
    {
        using var image = Mire(4, 3);

        var (octets, largeur, _) = DnpEnvoiDirect.Preparer(image);

        // Deuxième ligne de la trame = deuxième ligne de l'image (miroir horizontal
        // seulement : un retournement vertical sortirait la photo tête en bas).
        var attendu = image.GetPixel(largeur - 1, 1);
        var debut = largeur * 3;

        Assert.Equal(attendu.B, octets[debut]);
        Assert.Equal(attendu.G, octets[debut + 1]);
        Assert.Equal(attendu.R, octets[debut + 2]);
    }

    [Fact]
    public void L_image_d_origine_n_est_pas_modifiee()
    {
        // L'appelant garde la sienne pour les copies suivantes du même tirage : la retourner
        // sous ses pieds ferait sortir une copie sur deux à l'envers.
        using var image = Mire(4, 2);
        var avant = image.GetPixel(0, 0);

        DnpEnvoiDirect.Preparer(image);

        Assert.Equal(avant, image.GetPixel(0, 0));
    }

    [Fact]
    public void Une_image_en_32_bits_est_acceptee()
    {
        // Les rendus de Studio sont des PNG, souvent avec un canal alpha.
        using var image = new Bitmap(6, 2, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(image)) g.Clear(Color.Red);

        var (octets, largeur, hauteur) = DnpEnvoiDirect.Preparer(image);

        Assert.Equal(6 * 3 * 2, octets.Length);
        Assert.Equal(6, largeur);
        Assert.Equal(2, hauteur);
        Assert.Equal(0, octets[0]);      // bleu
        Assert.Equal(0, octets[1]);      // vert
        Assert.Equal(255, octets[2]);    // rouge
    }
}
