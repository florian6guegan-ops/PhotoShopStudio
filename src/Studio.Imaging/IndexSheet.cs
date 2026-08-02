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
    /// <param name="Aspects">
    /// Rapports largeur/hauteur des photos, ORIENTÉS, dans le même ordre que
    /// <paramref name="Photos"/> — quand l'appelant les connaît déjà. La planche-contact les a
    /// lus en affichant ses vignettes : les redemander revenait à rouvrir tous les fichiers
    /// pour une information déjà à l'écran. Null, ou de longueur différente : on les lit.
    /// Une valeur nulle ou négative dans la liste vaut « inconnu » et sera lue.
    /// </param>
    public sealed record Request(
        IReadOnlyList<string> Photos,
        int SheetWidthPx,
        int SheetHeightPx,
        int Dpi,
        string Title,
        DateTime Date,
        IReadOnlyList<double>? Aspects = null);

    /// <summary>Ce qu'une planche a coûté et contient, pour le dire à l'opérateur.</summary>
    /// <param name="Files">Les planches rendues, dans l'ordre.</param>
    /// <param name="PerSheet">Vignettes par planche.</param>
    /// <param name="Columns">Colonnes de la grille retenue.</param>
    /// <param name="Rows">Lignes de la grille retenue.</param>
    /// <param name="Thumbnails">
    /// Vignette JPEG de chaque planche, dans l'ordre de <paramref name="Files"/>, tirée de
    /// l'image encore en mémoire. L'appelant qui veut montrer la planche n'a donc pas à
    /// relire — et redécoder — le fichier qu'on vient d'écrire.
    /// </param>
    public sealed record Result(
        IReadOnlyList<string> Files, int PerSheet, int Columns, int Rows,
        IReadOnlyList<byte[]> Thumbnails);

    /// <summary>Côté de la vignette rendue avec chaque planche, pour l'affichage en grille.</summary>
    private const int VignettePlancheePx = 360;

    /// <summary>
    /// Finesse maximale d'une vignette de planche, quelle que soit la taille de la cellule.
    ///
    /// <b>Pourquoi un plafond.</b> Sur un 30×40 à 300 ppp, une cellule fait 751 px : sans
    /// plafond on demandait du 1024, on manquait le cache de la planche-contact, et les
    /// trente-six fichiers de 39 Mpx repassaient au décodeur — 5 109 ms sur les 6,3 s du rendu.
    ///
    /// <b>Pourquoi 512 suffit.</b> Sur la plus grande cellule qu'on rencontre (63 mm de large),
    /// cela fait encore ~206 ppp imprimés. Une planche d'index sert à DÉSIGNER une photo : le
    /// client y coche un numéro. Les 413 ppp que donnait le palier 1024 ne servaient personne,
    /// et se payaient à chaque planche. C'est aussi la taille du cache de la grille
    /// (<see cref="ThumbnailService.Defaut"/>) : les deux se rejoignent toujours.
    /// </summary>
    private const int VignetteMaximalePx = ThumbnailService.Defaut;

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
            RapportDominant(request.Photos, request.Aspects));

        // Les vignettes sont demandées à la taille où elles seront posées, pas plus — et
        // jamais au-delà du plafond, voir VignetteMaximalePx.
        var boite = Math.Clamp(
            plan.Cells.Max(c => Math.Max(c.Width, c.Height)), 64, VignetteMaximalePx);

        // toutes les cellules ont la même taille, d'une planche à l'autre : les vignettes
        // peuvent donc être préparées une fois pour toutes, avant la moindre composition
        var enCache = PrechargerLesVignettes(
            request.Photos, plan.Cells[0], numero, boite, thumbnails);

        try
        {
            var fichiers = new List<string>(plan.Pages);
            var apercus = new List<byte[]>(plan.Pages);

            for (var page = 0; page < plan.Pages; page++)
            {
                var chemin = Path.Combine(
                    outputDirectory,
                    plan.Pages == 1 ? $"{baseName}.jpg" : $"{baseName}-{page + 1:00}.jpg");

                apercus.Add(RendrePlanche(request, plan, page, numero, enCache, chemin));
                fichiers.Add(chemin);
            }

            return new Result(fichiers, plan.PerPage, plan.Columns, plan.Rows, apercus);
        }
        finally
        {
            foreach (var vignette in enCache.Values) vignette.Dispose();
        }
    }

    /// <summary>
    /// Lit, décode ET redimensionne toutes les vignettes AVANT de composer — en parallèle.
    ///
    /// C'est là que passait le temps : vingt-neuf fichiers ouverts, décodés et mis à l'échelle
    /// à la file, sur un seul cœur, pendant que le client attend au comptoir. Ces trois
    /// opérations sont indépendantes d'une photo à l'autre ; seule la composition ne l'est
    /// pas, puisqu'il n'y a qu'une image cible — elle reste donc séquentielle, et ne coûte
    /// plus qu'une recopie.
    ///
    /// Une photo illisible n'entre simplement pas dans le dictionnaire : sa case restera
    /// blanche, avec son numéro, comme avant.
    /// </summary>
    private static Dictionary<string, IMagickImage<byte>> PrechargerLesVignettes(
        IReadOnlyList<string> photos, PixelRect cellule, int hauteurNumero, int boite,
        ThumbnailService thumbnails)
    {
        var pretes = new Dictionary<string, IMagickImage<byte>>(
            photos.Count, StringComparer.OrdinalIgnoreCase);
        var verrou = new object();

        Parallel.ForEach(photos.Distinct(StringComparer.OrdinalIgnoreCase), chemin =>
        {
            MagickImage? vignette = null;
            try
            {
                vignette = new MagickImage(thumbnails.GetJpeg(chemin, boite));

                var rapport = vignette.Height == 0 ? 1 : vignette.Width / (double)vignette.Height;
                var place = IndexSheetLayout.PlaceVignette(cellule, hauteurNumero, rapport);

                vignette.Resize(new MagickGeometry((uint)place.Width, (uint)place.Height)
                {
                    IgnoreAspectRatio = true,
                });
            }
            catch (Exception)
            {
                vignette?.Dispose(); // une photo écartée ne doit pas laisser d'image derrière
                return;              // case blanche, la planche sort quand même
            }

            lock (verrou)
            {
                // un même fichier deux fois dans la sélection : une seule vignette suffit
                if (!pretes.TryAdd(chemin, vignette)) vignette.Dispose();
            }
        });

        return pretes;
    }

    /// <summary>
    /// Les proportions à donner aux cellules : la MÉDIANE des photos de la planche.
    ///
    /// Une cellule taillée pour du couché laisse un bandeau vide au-dessus et au-dessous de
    /// chaque photo debout, et l'inverse. Sur vingt-sept vignettes, ce vide se paie
    /// directement en taille de vignette. La médiane suit l'orientation dominante sans se
    /// laisser emporter par deux photos tournées.
    ///
    /// Les rapports que l'appelant connaît déjà sont repris tels quels ; les autres seulement
    /// sont « pingés » — on lit alors l'en-tête, jamais les pixels — et en parallèle.
    /// </summary>
    private static double RapportDominant(
        IReadOnlyList<string> photos, IReadOnlyList<double>? connus)
    {
        var fournis = connus is not null && connus.Count == photos.Count ? connus : null;

        var rapports = new List<double>(photos.Count);
        var aLire = new List<string>();

        for (var i = 0; i < photos.Count; i++)
        {
            if (fournis is not null && fournis[i] > 0) rapports.Add(fournis[i]);
            else aLire.Add(photos[i]);
        }

        if (aLire.Count > 0)
        {
            var lus = new double[aLire.Count];

            Parallel.For(0, aLire.Count, i =>
            {
                try
                {
                    using var image = new MagickImage();
                    image.Ping(aLire[i]);

                    var largeur = (double)image.Width;
                    var hauteur = (double)image.Height;

                    // l'orientation EXIF compte : un portrait pris à l'horizontale est stocké
                    // couché, et sa vignette sera pourtant debout
                    if (image.Orientation is OrientationType.LeftTop or OrientationType.RightTop
                        or OrientationType.RightBottom or OrientationType.LeftBottom)
                        (largeur, hauteur) = (hauteur, largeur);

                    if (largeur > 0 && hauteur > 0) lus[i] = largeur / hauteur;
                }
                catch (Exception)
                {
                    // photo illisible : elle ne pèsera pas sur la forme de la grille
                }
            });

            rapports.AddRange(lus.Where(r => r > 0));
        }

        if (rapports.Count == 0) return IndexSheetLayout.RapportParDefaut;

        rapports.Sort();
        return rapports[rapports.Count / 2];
    }

    /// <summary>Rend une planche, l'écrit, et renvoie sa vignette d'affichage.</summary>
    private static byte[] RendrePlanche(
        Request request, IndexSheetLayout.Plan plan, int page, int hauteurNumero,
        IReadOnlyDictionary<string, IMagickImage<byte>> vignettes, string chemin)
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

            var pose = PoserLaVignette(request.Photos[i], cellule, hauteurNumero,
                                       vignettes, planche);

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

        // la vignette est tirée de l'image ENCORE EN MÉMOIRE. Elle était auparavant obtenue
        // en relisant le fichier qu'on venait d'écrire — un aller-retour disque et un
        // décodage complets, pour une image qu'on tenait déjà.
        //
        // Réduction SUR PLACE, et non sur une copie : la planche est écrite, elle ne sert
        // plus à rien d'autre, et cloner deux millions de pixels pour les jeter aussitôt
        // coûtait à soi seul le sixième du temps de rendu.
        planche.Thumbnail(VignettePlancheePx, VignettePlancheePx);
        planche.Quality = 82;
        return planche.ToByteArray(MagickFormat.Jpeg);
    }

    /// <summary>
    /// Pose une photo dans sa cellule et renvoie le rectangle occupé, ou null si la photo
    /// est illisible.
    ///
    /// Une photo manquante ne doit pas emporter la planche : le client attend au comptoir,
    /// et vingt-neuf vignettes valent mieux qu'une erreur.
    /// </summary>
    private static PixelRect? PoserLaVignette(
        string photo, PixelRect cellule, int hauteurNumero,
        IReadOnlyDictionary<string, IMagickImage<byte>> vignettes, MagickImage planche)
    {
        // absente du dictionnaire = illisible : la case reste blanche, le numéro est tout de
        // même écrit pour que la numérotation ne se décale pas d'une planche à l'autre
        if (!vignettes.TryGetValue(photo, out var vignette)) return null;

        try
        {
            // la vignette est déjà à la taille de sa place (voir PrechargerLesVignettes) :
            // il ne reste qu'à la centrer dans SA cellule, qui change à chaque case
            var place = Centrer(cellule, hauteurNumero, vignette.Width, vignette.Height);

            planche.Composite(vignette, place.X, place.Y, CompositeOperator.Over);
            return place;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Centre une vignette déjà dimensionnée dans sa cellule, au-dessus de la place du numéro.
    /// Même règle que <see cref="IndexSheetLayout.PlaceVignette"/>, dont la taille est ici
    /// déjà connue.
    /// </summary>
    private static PixelRect Centrer(PixelRect cellule, int hauteurNumero, uint largeur, uint hauteur)
    {
        var disponible = Math.Max(1, cellule.Height - hauteurNumero);

        return new PixelRect(
            cellule.X + (cellule.Width - (int)largeur) / 2,
            cellule.Y + (disponible - (int)hauteur) / 2,
            (int)largeur, (int)hauteur);
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
