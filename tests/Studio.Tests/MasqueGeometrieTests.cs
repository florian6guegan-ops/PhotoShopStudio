using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le masque du sujet suit LA MÊME géométrie que sa photo.
///
/// <b>Arcueil, commande 17-006 du 17/08/2026.</b> Une planche d'identité est sortie avec un
/// chevron clair derrière les épaules et une démarcation nette en travers du front, alors que
/// l'aperçu était parfait. Aucun fond n'était demandé : c'était la correction du sujet, +0,45
/// d'exposition, posée à côté du sujet. Huit fois sur la planche.
///
/// La cause : le masque était demandé en BAS du pipeline, l'image déjà recadrée en 35×45 — mais
/// la clé du cache est le FICHIER, il rendait donc celui de la photo entière, que
/// <c>MasqueALaTaille</c> étirait sur la case.
///
/// <c>RenderInto</c> prélève désormais le masque sur la photo entière, puis lui fait subir la
/// même géométrie qu'à l'image, par le même code.
///
/// <b>Le détourage n'est pas rejoué avec le réseau</b> — il demande un modèle de 109 Mo absent
/// des postes de développement. La photo d'essai a donc un fond parfaitement uni, sur lequel le
/// repli par couleur suffit : c'est le cas favorable, et il est ici voulu, car ce n'est pas la
/// qualité du détourage qu'on vérifie mais l'endroit où il tombe.
/// </summary>
[Collection(DetourageStatiqueCollection.Nom)]
public class MasqueGeometrieTests : IDisposable
{
    private const int Dpi = 300;

    /// <summary>Largeur et hauteur de la photo d'essai.</summary>
    private const int PhotoW = 400;
    private const int PhotoH = 600;

    /// <summary>
    /// Le « sujet » : un rectangle franc, entouré de fond des QUATRE côtés — le pourtour doit
    /// rester uni, c'est sur lui que le repli par couleur se repère. Il couvre un quart de
    /// l'image, donc bien au-dessus du dixième sous lequel le détourage refuse de conclure.
    /// </summary>
    private const int SujetX = 100, SujetY = 150, SujetW = 200, SujetH = 300;

    /// <summary>
    /// ⚠ La photo d'essai est COLORÉE, et ce n'est pas un détail.
    ///
    /// Une image parfaitement neutre, ImageMagick l'écrit en NIVEAUX DE GRIS — un seul canal —
    /// et le masque, lui, est en sRGB à trois canaux : <c>Fondre</c> lève alors
    /// « Too many values specified » au lieu de corriger quoi que ce soit. Ce n'est pas le
    /// sujet de ces essais, mais c'est le premier mur qu'ils ont rencontré, et un fond de
    /// studio comme une peau ont de toute façon une couleur.
    /// </summary>
    private static readonly MagickColor Fond = MagickColor.FromRgb(250, 250, 252);
    private static readonly MagickColor Sujet = MagickColor.FromRgb(90, 62, 44);

    private readonly string _photo;
    private readonly List<string> _rendus = [];

    public MasqueGeometrieTests()
    {
        _photo = Path.Combine(Path.GetTempPath(), $"studio-masque-{Guid.NewGuid():N}.png");

        using var image = new MagickImage(Fond, PhotoW, PhotoH);
        using var sujet = new MagickImage(Sujet, SujetW, SujetH);
        image.Composite(sujet, SujetX, SujetY, CompositeOperator.Over);
        image.Write(_photo);

        // Le cache est statique et porte la clé du FICHIER : sans ce ménage, un essai
        // hériterait du masque d'un autre.
        MasqueSujet.Oublier();
    }

    public void Dispose()
    {
        MasqueSujet.Oublier();

        foreach (var fichier in _rendus.Append(_photo))
        {
            try { File.Delete(fichier); } catch (IOException) { /* le ménage n'est pas le sujet */ }
        }

        GC.SuppressFinalize(this);
    }

    private static int Clarte(IMagickImage<byte> image, int x, int y)
    {
        using var pixels = image.GetPixels();
        return pixels.GetPixel(x, y).GetChannel(0);
    }

    /// <summary>
    /// Rend la MOITIÉ BASSE de la photo, à sa taille, avec la correction demandée.
    ///
    /// Ce cadrage est ce qui fait tout : il décale le sujet verticalement. Dans la case, le
    /// rectangle n'occupe plus que le haut — de 0 à 150 sur 300 — alors qu'un masque de la
    /// photo entière simplement étiré le placerait de 75 à 225. Les deux se distinguent donc
    /// au pixel, et c'est exactement le défaut du 17/08.
    /// </summary>
    private MagickImage RendreLaMoitieBasse(CorrectionsSujet? sujet = null, bool fondGris = false)
    {
        var reglages = new ImageAdjustments
        {
            GrayBackground = fondGris,
            Sujet = sujet ?? new CorrectionsSujet(),
            CleDeLaPhoto = _photo,
        };

        var demande = new RenderRequest(
            _photo, PhotoW, PhotoH / 2, new CropSpec(0, 0.5, 1, 0.5),
            0, 0, FitMode.Fill, 0, reglages);

        var sortie = Path.Combine(Path.GetTempPath(), $"studio-rendu-{Guid.NewGuid():N}.png");
        _rendus.Add(sortie);

        ImagePipeline.RenderToFile(demande, sortie, Dpi);
        return new MagickImage(sortie);
    }

    /// <summary>
    /// <b>Le cas du 17/08.</b> La correction tombe sur le sujet — le haut de la case, où est
    /// vraiment le rectangle — et le fond ne bouge pas.
    ///
    /// Avant la correction du pipeline, le masque étiré plaçait le sujet au MILIEU de la case :
    /// le point vérifié ici restait noir, et celui du milieu s'éclaircissait à sa place.
    /// </summary>
    [Fact]
    public void La_correction_du_sujet_tombe_sur_le_sujet_et_pas_a_cote()
    {
        using var rendu = RendreLaMoitieBasse(
            new CorrectionsSujet { Actif = true, Exposure = 1.0 });

        // dans la case : y 0..150 est le rectangle, y 150..300 est le fond sous lui
        Assert.True(Clarte(rendu, 200, 40) > 120,
            "le sujet devait s'éclaircir : il est en HAUT de la case après ce cadrage");

        Assert.True(Clarte(rendu, 200, 250) > 240,
            "sous le sujet, le fond devait rester intact");

        Assert.True(Clarte(rendu, 30, 40) > 240,
            "à gauche du sujet, le fond devait rester intact");
    }

    /// <summary>
    /// Le fond posé suit le même masque, donc le même chemin : il couvre le fond et épargne le
    /// sujet. C'est le second usage du masque, et il aurait été faux de la même façon dès que
    /// l'aperçu avait rangé un masque sous la clé du fichier.
    /// </summary>
    [Fact]
    public void Le_fond_pose_epargne_le_sujet()
    {
        using var rendu = RendreLaMoitieBasse(fondGris: true);

        var surLeSujet = Clarte(rendu, 200, 40);
        var surLeFond = Clarte(rendu, 200, 250);

        Assert.True(surLeSujet < 130, $"le sujet devait rester lui-même, lu {surLeSujet}");

        // le gris identité vaut 210 — franchement plus sombre que le fond d'origine à 250
        Assert.True(surLeFond is > 195 and < 225,
            $"le fond devait passer au gris identité (210), lu {surLeFond}");
    }

    /// <summary>
    /// <b>Non-régression.</b> Sans correction du sujet ni fond, le rendu ne demande aucun
    /// masque et le cadrage sort tel quel — c'est le chemin de tous les tirages ordinaires,
    /// celui qu'il ne fallait pas abîmer en réorganisant le pipeline.
    /// </summary>
    [Fact]
    public void Sans_masque_demande_le_cadrage_sort_tel_quel()
    {
        using var rendu = RendreLaMoitieBasse();

        Assert.Equal(PhotoW, (int)rendu.Width);
        Assert.Equal(PhotoH / 2, (int)rendu.Height);

        Assert.True(Clarte(rendu, 200, 40) < 120, "le sujet devait sortir inchangé");
        Assert.True(Clarte(rendu, 200, 250) > 240, "le fond devait sortir inchangé");
    }
}
