using System.Windows;
using System.Windows.Media;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.App.Infrastructure;

/// <summary>
/// Le petit SCHÉMA d'une planche, pour la tuile qui la vend.
///
/// « Rentrée — 4 photos d'identité + 1 grande, sur la même feuille » ne tient pas sur une
/// tuile de 330 × 170 : le texte s'y enroule sur trois lignes et se coupe au milieu d'un
/// mot. Un dessin dit la même chose d'un coup d'œil, et mieux — quatre cases à gauche, une
/// grande à droite, c'est exactement ce que l'opérateur va tendre au client.
///
/// <b>Dessiné depuis la VRAIE géométrie</b>, celle qui découpe le papier
/// (<see cref="PlancheRentree"/>, <see cref="IdSheetLayout"/>), et non redessiné à la main.
/// Une icône dessinée à part serait juste le jour où on la trace et fausse au premier
/// changement de disposition : elle montrerait quatre cases là où la planche en pose six,
/// et l'opérateur croirait vendre autre chose. Ici, changer la disposition change le
/// dessin.
///
/// Rendu en unités du dessin, sans dimension : le <c>Viewbox</c> de la tuile l'échelonne.
/// </summary>
public static class VignetteDePlanche
{
    /// <summary>Le papier sur lequel on schématise : le 10×15 couché de la boutique.</summary>
    private const double FeuilleLargeurMm = 156.1;
    private const double FeuilleHauteurMm = 105;

    /// <summary>Points par millimètre du schéma. Assez fin pour que les cases restent nettes.</summary>
    private const int Ppmm = 4;

    private static readonly Brush Trait = Geler(new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Case = Geler(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush Grande = Geler(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));

    /// <summary>
    /// Le schéma d'un format, ou null quand il n'y a rien à montrer — une planche ordinaire
    /// se passe de dessin, son intitulé suffit.
    /// </summary>
    /// <param name="genre">Ce que la tuile fabrique.</param>
    /// <param name="document">La norme visée : c'est elle qui fixe la taille des cases.</param>
    /// <param name="photos">Cases imposées par la tuile, ou null pour le défaut du format.</param>
    public static ImageSource? Pour(GenreDePlanche genre, IdDocumentSpec? document, int? photos)
    {
        var norme = document ?? IdDocumentSpec.France;

        try
        {
            return genre switch
            {
                GenreDePlanche.Rentree =>
                    Rentree(norme, photos ?? PlancheDeRentree.IdentitesParDefaut),
                GenreDePlanche.PlancheEtTirage => PlancheEtTirage(norme, photos),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            // Une norme trop grande pour la feuille du schéma fait lever la disposition. La
            // tuile garde alors son texte : c'est un ornement, la vente est ailleurs — et
            // c'est la règle déjà suivie par le picker, qui omet plutôt que d'échouer.
            FileLog.Write($"Schéma de planche non dessiné — {norme.Label}", ex);
            return null;
        }
    }

    /// <summary>
    /// UNE feuille : les cases d'identité à gauche, le portrait à droite.
    /// </summary>
    private static ImageSource? Rentree(IdDocumentSpec document, int identites)
    {
        var (feuilleL, feuilleH) = Feuille();

        var mise = PlancheRentree.Layout(
            feuilleL, feuilleH,
            Px(document.WidthMm), Px(document.HeightMm),
            Px(1), identites,
            largeurMinimaleGrandePx: Px(PlancheRentree.LargeurMinimaleGrandeMm),
            // ⚠ LES MÊMES ARGUMENTS QUE LE RENDU. La tuile dessine la vraie géométrie —
            // c'est tout son intérêt — et une tuile calculée sans l'air du bord montrerait
            // un bloc collé à gauche que le tirage, lui, ne fait plus.
            airAuBord: Px(PlancheRentree.AirAuBordMm));

        if (mise is null) return null;

        var dessin = new DrawingGroup();
        Poser(dessin, new Rect(0, 0, feuilleL, feuilleH), null, Trait, 1);

        foreach (var c in mise.Identites)
            Poser(dessin, Vers(c), Case, Trait, 0.8);

        Poser(dessin, Vers(mise.Grande), Grande, Trait, 0.8);

        return Figer(dessin);
    }

    /// <summary>
    /// DEUX feuilles côte à côte : la planche, et le tirage qui l'accompagne. L'écart entre
    /// les deux est ce qui distingue ce format de la rentrée — là-bas tout tient sur une
    /// feuille, ici le client repart avec deux.
    /// </summary>
    private static ImageSource? PlancheEtTirage(IdDocumentSpec document, int? photos)
    {
        var (feuilleL, feuilleH) = Feuille();

        var caseL = Px(document.WidthMm);
        var caseH = Px(document.HeightMm);
        var ecartCases = Px(1);

        // « Planche pleine » n'a pas de nombre : on compte les places du schéma. C'est un
        // DESSIN, pas une vente — la capacité réelle se calcule sur le papier retenu, avec
        // sa bande basse, et c'est l'écran de cadrage qui la dit.
        var colonnes = (feuilleL + ecartCases) / (caseL + ecartCases);
        var rangees = (feuilleH + ecartCases) / (caseH + ecartCases);
        var places = Math.Max(1, colonnes * rangees);

        var copies = Math.Clamp(photos ?? places, 1, places);

        var mise = IdSheetLayout.Layout(
            feuilleL, feuilleH, caseL, caseH, ecartCases, copies);

        if (mise.Cells.Count == 0) return null;

        var dessin = new DrawingGroup();
        Poser(dessin, new Rect(0, 0, feuilleL, feuilleH), null, Trait, 1);

        foreach (var c in mise.Cells)
            Poser(dessin, Vers(c), Case, Trait, 0.8);

        // le 10×15 DEBOUT à côté, à l'échelle : un portrait se tire en hauteur
        var ecart = Px(8);
        var tirageL = Px(102);
        var tirageH = Px(152);
        var y = (feuilleH - tirageH) / 2.0;

        Poser(dessin, new Rect(feuilleL + ecart, y, tirageL, tirageH), Grande, Trait, 1);

        return Figer(dessin);
    }

    private static (int Largeur, int Hauteur) Feuille() =>
        (Px(FeuilleLargeurMm), Px(FeuilleHauteurMm));

    private static int Px(double mm) => (int)Math.Round(mm * Ppmm);

    private static Rect Vers(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static void Poser(
        DrawingGroup dessin, Rect ou, Brush? fond, Brush trait, double epaisseur)
    {
        var stylo = new Pen(trait, epaisseur);
        stylo.Freeze();
        dessin.Children.Add(new GeometryDrawing(fond, stylo, new RectangleGeometry(ou)));
    }

    private static ImageSource Figer(DrawingGroup dessin)
    {
        dessin.Freeze();
        var image = new DrawingImage(dessin);
        image.Freeze();
        return image;
    }

    private static Brush Geler(Brush brosse)
    {
        brosse.Freeze();
        return brosse;
    }
}
