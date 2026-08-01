using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;
using Studio.Imaging.Geometry;

namespace Studio.Imaging;

/// <summary>
/// La planche d'index : les vignettes de toutes les photos, numérotées, sur un tirage.
///
/// <b>À quoi ça sert au comptoir.</b> Le client arrive avec une pellicule ou une clé, on
/// lui tire une planche à zéro euro, il coche ce qu'il veut, et l'on tire ensuite. Tout
/// repose donc sur UN point : qu'il puisse désigner une photo. D'où les numéros.
///
/// <b>Ce qu'on corrige par rapport à DiLand</b> (planches réelles examinées le
/// 01/08/2026) :
///
/// — <b>les étiquettes</b> : il écrit le nom du fichier, coupé à la largeur de la cellule.
///   Sur les commandes de la boutique, les vingt-sept vignettes portaient toutes
///   « kodakREAPHOT », le numéro — la seule chose qui distingue — étant tombé. La planche
///   ne servait alors plus à rien. Ici c'est le RANG qui est écrit, court par nature.
///
/// — <b>le remplissage</b> : sa grille est fixe, et vingt-sept photos sortaient sur deux
///   planches 10×15, la seconde à trois vignettes. Ici la grille suit le nombre de photos
///   (voir <see cref="IndexSheetLayout"/>) : les vingt-sept tiennent sur une seule.
///
/// Les corrections d'image ne sont PAS appliquées : la planche sert à choisir, pas à
/// juger du rendu final, et la photo doit s'y reconnaître telle qu'elle est arrivée.
/// </summary>
public static class IndexSheet
{
    /// <summary>Marge autour de la planche, en millimètres.</summary>
    private const double MargeMm = 4;

    /// <summary>Espace entre deux vignettes.</summary>
    private const double EcartMm = 2;

    /// <summary>Bandeau du titre, en haut.</summary>
    private const double TitreMm = 8;

    /// <summary>Bandeau de la date et de la pagination, en bas.</summary>
    private const double PiedMm = 5;

    /// <summary>Place du numéro, sous chaque vignette.</summary>
    private const double NumeroMm = 3.5;

    /// <summary>
    /// Largeur minimale d'une vignette. En dessous, on ne reconnaît plus la photo : mieux
    /// vaut une seconde planche qu'une planche inutilisable.
    /// </summary>
    private const double VignetteMinimaleMm = 15;

    /// <param name="Photos">Les photos à indexer, dans l'ordre où elles seront numérotées.</param>
    /// <param name="SheetWidthPx">Largeur du tirage, en pixels.</param>
    /// <param name="SheetHeightPx">Hauteur du tirage.</param>
    /// <param name="Dpi">Résolution du tirage.</param>
    /// <param name="Title">Titre porté en haut de chaque planche.</param>
    /// <param name="Date">Date portée en bas ; celle de la commande, pas celle du rendu.</param>
    public sealed record Request(
        IReadOnlyList<string> Photos,
        int SheetWidthPx,
        int SheetHeightPx,
        int Dpi,
        string Title,
        DateTime Date);

    /// <summary>Ce qu'une planche a coûté et contient, pour le dire à l'opérateur.</summary>
    /// <param name="Files">Les planches rendues, dans l'ordre.</param>
    /// <param name="PerSheet">Vignettes par planche.</param>
    /// <param name="Columns">Colonnes de la grille retenue.</param>
    /// <param name="Rows">Lignes de la grille retenue.</param>
    public sealed record Result(
        IReadOnlyList<string> Files, int PerSheet, int Columns, int Rows);

    /// <summary>
    /// Rend les planches et renvoie les fichiers écrits.
    /// </summary>
    /// <param name="request">Les photos et le format du tirage.</param>
    /// <param name="thumbnails">Le service de vignettes : il décode en taille réduite et
    /// garde en cache, sans quoi une planche de trente photos relirait trente fichiers de
    /// vingt-quatre mégapixels.</param>
    /// <param name="outputDirectory">Dossier où écrire ; créé au besoin.</param>
    /// <param name="baseName">Début du nom des fichiers.</param>
    public static Result Render(
        Request request, ThumbnailService thumbnails, string outputDirectory, string baseName = "index")
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(thumbnails);

        if (request.Photos.Count == 0)
            throw new ArgumentException("Aucune photo à indexer.", nameof(request));

        MagickInit.Configure();
        Directory.CreateDirectory(outputDirectory);

        var dpi = request.Dpi;
        var marge = MmPx.ToPixels(MargeMm, dpi);
        var ecart = MmPx.ToPixels(EcartMm, dpi);
        var titre = MmPx.ToPixels(TitreMm, dpi);
        var pied = MmPx.ToPixels(PiedMm, dpi);
        var numero = MmPx.ToPixels(NumeroMm, dpi);

        var plan = IndexSheetLayout.Compute(
            request.SheetWidthPx, request.SheetHeightPx, request.Photos.Count,
            marge, ecart, titre, pied, numero,
            MmPx.ToPixels(VignetteMinimaleMm, dpi),
            RapportDominant(request.Photos));

        // Les vignettes sont demandées à la taille où elles seront posées, pas plus : au
        // pixel près c'est ce qui distingue une planche rendue en deux secondes d'une
        // planche rendue en trente.
        var boite = Math.Max(64, plan.Cells.Max(c => Math.Max(c.Width, c.Height)));

        var fichiers = new List<string>(plan.Pages);

        for (var page = 0; page < plan.Pages; page++)
        {
            var chemin = Path.Combine(
                outputDirectory,
                plan.Pages == 1 ? $"{baseName}.jpg" : $"{baseName}-{page + 1:00}.jpg");

            RendrePlanche(request, plan, page, numero, boite, thumbnails, chemin);
            fichiers.Add(chemin);
        }

        return new Result(fichiers, plan.PerPage, plan.Columns, plan.Rows);
    }

    /// <summary>
    /// Les proportions à donner aux cellules : la MÉDIANE des photos de la planche.
    ///
    /// Une cellule taillée pour du couché laisse un bandeau vide au-dessus et au-dessous de
    /// chaque photo debout, et l'inverse. Sur vingt-sept vignettes, ce vide se paie
    /// directement en taille de vignette. La médiane suit l'orientation dominante sans se
    /// laisser emporter par deux photos tournées.
    ///
    /// Les fichiers ne sont que « pingés » : on lit l'en-tête, jamais les pixels.
    /// </summary>
    private static double RapportDominant(IReadOnlyList<string> photos)
    {
        var rapports = new List<double>(photos.Count);

        foreach (var chemin in photos)
        {
            try
            {
                using var image = new MagickImage();
                image.Ping(chemin);

                var largeur = (double)image.Width;
                var hauteur = (double)image.Height;

                // l'orientation EXIF compte : un portrait pris à l'horizontale est stocké
                // couché, et sa vignette sera pourtant debout
                if (image.Orientation is OrientationType.LeftTop or OrientationType.RightTop
                    or OrientationType.RightBottom or OrientationType.LeftBottom)
                    (largeur, hauteur) = (hauteur, largeur);

                if (largeur > 0 && hauteur > 0) rapports.Add(largeur / hauteur);
            }
            catch (Exception)
            {
                // photo illisible : elle ne pèsera pas sur la forme de la grille
            }
        }

        if (rapports.Count == 0) return IndexSheetLayout.RapportParDefaut;

        rapports.Sort();
        return rapports[rapports.Count / 2];
    }

    private static void RendrePlanche(
        Request request, IndexSheetLayout.Plan plan, int page,
        int hauteurNumero, int boiteVignette, ThumbnailService thumbnails, string chemin)
    {
        using var planche = new MagickImage(
            MagickColors.White, (uint)request.SheetWidthPx, (uint)request.SheetHeightPx);

        // la densité AVANT de dessiner : ImageMagick convertit les tailles de police
        // d'après la résolution de l'image. Posée après, tout le texte sortirait calculé
        // à 72 dpi, donc quatre fois trop petit.
        planche.Density = new Density(request.Dpi, request.Dpi, DensityUnit.PixelsPerInch);

        var premier = page * plan.PerPage;
        var dernier = Math.Min(request.Photos.Count, premier + plan.PerPage);

        var numeros = new Drawables()
            .Font(Fonts.SansEmpattement())
            .FontPointSize(hauteurNumero)
            .FillColor(MagickColors.Black)
            .StrokeColor(MagickColors.Transparent)
            .TextAlignment(TextAlignment.Center);

        var quelqueChoseAEcrire = false;

        for (var i = premier; i < dernier; i++)
        {
            var cellule = plan.Cells[i - premier];

            var pose = PoserLaVignette(request.Photos[i], cellule, hauteurNumero, boiteVignette,
                                       thumbnails, planche);

            // le numéro juste sous la vignette, et non en bas de la cellule : sur une
            // planche qui mêle portraits et paysages, un numéro aligné sur la cellule
            // flotterait loin des photos couchées
            var basVignette = pose?.Bottom ?? (cellule.Bottom - hauteurNumero);

            numeros.Text(
                cellule.X + cellule.Width / 2.0,
                basVignette + hauteurNumero,
                (i + 1).ToString(CultureInfo.InvariantCulture));

            quelqueChoseAEcrire = true;
        }

        if (quelqueChoseAEcrire) planche.Draw(numeros);

        EcrireLesBandeaux(planche, request, page, plan.Pages);

        // une image créée à partir d'une couleur porte le pseudo-format « XC », que rien
        // ne sait écrire : il faut le poser explicitement
        planche.Format = MagickFormat.Jpeg;
        planche.Quality = 92;
        planche.Write(chemin);
    }

    /// <summary>
    /// Pose une photo dans sa cellule et renvoie le rectangle occupé, ou null si la photo
    /// est illisible.
    ///
    /// Une photo manquante ne doit pas emporter la planche : le client attend au comptoir,
    /// et vingt-neuf vignettes valent mieux qu'une erreur.
    /// </summary>
    private static PixelRect? PoserLaVignette(
        string photo, PixelRect cellule, int hauteurNumero, int boite,
        ThumbnailService thumbnails, MagickImage planche)
    {
        try
        {
            using var vignette = new MagickImage(thumbnails.GetJpeg(photo, boite));

            var rapport = vignette.Height == 0 ? 1 : vignette.Width / (double)vignette.Height;
            var place = IndexSheetLayout.PlaceVignette(cellule, hauteurNumero, rapport);

            vignette.Resize(new MagickGeometry((uint)place.Width, (uint)place.Height)
            {
                IgnoreAspectRatio = true,
            });

            planche.Composite(vignette, place.X, place.Y, CompositeOperator.Over);
            return place;
        }
        catch (Exception)
        {
            // photo illisible : la case reste blanche, le numéro est tout de même écrit
            // pour que la numérotation ne se décale pas d'une planche à l'autre
            return null;
        }
    }

    /// <summary>
    /// Le titre en haut, la date et la pagination en bas.
    ///
    /// La pagination n'est pas décorative : elle dit au client qu'il lui manque une
    /// planche, et à l'opérateur qu'un tirage n'est pas sorti.
    /// </summary>
    private static void EcrireLesBandeaux(
        MagickImage planche, Request request, int page, int planches)
    {
        var dpi = request.Dpi;
        var marge = MmPx.ToPixels(MargeMm, dpi);
        var corpsTitre = MmPx.ToPixels(TitreMm - 2, dpi);
        var corpsPied = MmPx.ToPixels(PiedMm - 1.5, dpi);

        var titre = new Drawables()
            .Font(Fonts.SansEmpattement())
            .FontPointSize(corpsTitre)
            .FillColor(MagickColors.Black)
            .StrokeColor(MagickColors.Transparent)
            .TextAlignment(TextAlignment.Left)
            .Text(marge, marge + corpsTitre, request.Title);

        planche.Draw(titre);

        var mention = $"{request.Date:dd/MM/yyyy} · {request.Photos.Count} photos";
        if (planches > 1) mention += $" · planche {page + 1}/{planches}";

        var pied = new Drawables()
            .Font(Fonts.SansEmpattement())
            .FontPointSize(corpsPied)
            .FillColor(new MagickColor("#424242"))
            .StrokeColor(MagickColors.Transparent)
            .TextAlignment(TextAlignment.Right)
            .Text(planche.Width - marge, planche.Height - marge, mention);

        planche.Draw(pied);
    }
}
