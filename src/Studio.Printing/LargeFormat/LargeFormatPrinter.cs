using System.Drawing;
using System.Drawing.Printing;

namespace Studio.Printing.LargeFormat;

/// <summary>
/// Impression d'un agrandissement posé à un endroit précis de la feuille, selon les
/// réglages de <see cref="LargeFormatPrintSettings"/>.
///
/// À la différence de <see cref="BitmapPrinter"/>, qui pose l'image 1:1 sur une page aux
/// dimensions exactes du produit, on travaille ici sur le média que l'opérateur a choisi
/// dans le pilote (A3+, rouleau…) et l'image y est mise à l'échelle et positionnée —
/// exactement ce que fait la boîte d'impression de Photoshop.
/// </summary>
public static class LargeFormatPrinter
{
    private const double MmPerInch = 25.4;

    /// <summary>Journal optionnel : trace le média retenu et l'emplacement obtenu.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Dimensions de la feuille telle que le pilote la retient, en millimètres. C'est sur
    /// cette taille que la boîte de dialogue calcule l'aperçu et le centrage.
    /// </summary>
    public static (double WidthMm, double HeightMm) GetPageSizeMm(string printerName, byte[]? devModeBytes,
        bool landscape = false)
    {
        var page = GetPageInfo(printerName, devModeBytes, landscape);
        return (page.WidthMm, page.HeightMm);
    }

    /// <summary>
    /// La feuille ET la définition du périphérique, en une seule interrogation du pilote.
    /// </summary>
    /// <param name="DeviceDpi">
    /// Définition d'impression annoncée par le pilote, en points par pouce. Vaut 0 quand le
    /// pilote répond par un simple niveau de qualité (« Brouillon », « Élevée ») au lieu
    /// d'un nombre : <c>PrinterResolution.X</c> est alors négatif.
    /// </param>
    public sealed record PageInfo(double WidthMm, double HeightMm, int DeviceDpi);

    /// <summary>Feuille et définition du périphérique, telles que le pilote les retient.</summary>
    public static PageInfo GetPageInfo(string printerName, byte[]? devModeBytes, bool landscape = false)
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Imprimante invalide ou hors ligne : « {printerName} »");

        if (devModeBytes is not null)
            DevMode.Apply(doc.PrinterSettings, devModeBytes);

        doc.DefaultPageSettings.Landscape = landscape;
        var bounds = doc.DefaultPageSettings.Bounds; // centièmes de pouce
        var definition = doc.DefaultPageSettings.PrinterResolution.X;

        return new PageInfo(
            bounds.Width / 100.0 * MmPerInch,
            bounds.Height / 100.0 * MmPerInch,
            definition > 0 ? definition : 0);
    }

    /// <summary>
    /// Le format papier retenu pour un tirage, et les octets pilote qui le posent.
    /// </summary>
    /// <param name="DevMode">À mettre dans <c>LargeFormatPrintSettings.DevModeBytes</c>.</param>
    /// <param name="Nom">Nom du format tel que le pilote l'appelle — « 30 x 40 cm ».</param>
    /// <param name="WidthMm">Largeur de la feuille une fois la disposition appliquée.</param>
    /// <param name="HeightMm">Hauteur de la feuille une fois la disposition appliquée.</param>
    /// <param name="Paysage">Vrai si le format ne convient qu'en travers.</param>
    public sealed record FormatPapierChoisi(
        byte[] DevMode, string Nom, double WidthMm, double HeightMm, bool Paysage);

    /// <summary>
    /// Le plus petit format du pilote où le tirage tient ENTIER, prêt à être appliqué.
    ///
    /// <b>Pourquoi il faut le choisir soi-même.</b> La boîte d'agrandissement s'ouvrait sur
    /// le format par défaut de la file d'impression. Sur l'Epson du Kremlin-Bicêtre c'est
    /// « A4 210 × 297 mm » : un 30 × 40 demandé par le comptoir partait donc centré sur une
    /// A4, et <b>48 % du tirage tombait hors de la feuille</b> — relevé au journal le
    /// 21/08/2026, commande 21-005. Ce qui ressortait de la machine avait la taille d'une
    /// A4, c'est-à-dire à peu près un 20 × 30 : « tu choisis 30 × 40, il te met une 20 × 30 ».
    /// Le pilote proposait pourtant « 30 x 40 cm », exactement 300 × 400 mm.
    ///
    /// <b>Le choix se fait sur les DIMENSIONS, jamais sur le nom.</b> Un même papier
    /// s'appelle « 30 x 40 cm » ici, « 12 x 16 p. » ailleurs, et les index ne voyagent pas
    /// d'un poste à l'autre (voir <see cref="DevMode.RecalerLeFormatPapier"/>). Les
    /// millimètres, eux, sont les mêmes partout.
    ///
    /// On retient le plus PETIT format qui contient le tirage : sur cet Epson, un 30 × 40
    /// trouve son 30 × 40 plutôt que l'A2 qui le contient aussi et gâcherait le papier.
    ///
    /// ⚠ <b>Les formats « Personnalisée » sont écartés.</b> Le pilote y annonce la dernière
    /// taille saisie — ici 210 × 297, celle d'une A4 — qui ne dit rien de ce qui sortira. Un
    /// format dont la mesure ment ne peut pas servir à mesurer.
    ///
    /// ⚠ <b>Au moindre doute, on rend null</b> et l'appelant garde le format par défaut du
    /// pilote : imprimante muette, aucun format assez grand, ou pilote qui refuse de rendre
    /// ses octets. C'est le comportement d'avant, qui n'a jamais empêché un tirage de partir.
    ///
    /// ⚠ <b>Les octets rendus ne valent que sur CE poste, et ne doivent pas être
    /// enregistrés.</b> Le pilote Epson les rend avec un <c>dmFormName</c> VIDE — vérifié sur
    /// la machine du Kremlin-Bicêtre le 21/08/2026 — de sorte que
    /// <see cref="DevMode.RecalerLeFormatPapier"/> n'aurait aucun nom à relire pour les
    /// retraduire ailleurs, et laisserait passer un index étranger. Ici c'est sans
    /// conséquence : ils sont capturés sur la file même où l'on va imprimer, dans la seconde
    /// qui précède.
    /// </summary>
    /// <param name="printerName">File d'impression visée.</param>
    /// <param name="tirageWidthMm">Largeur du tirage voulu, en millimètres.</param>
    /// <param name="tirageHeightMm">Hauteur du tirage voulu.</param>
    /// <param name="toleranceMm">
    /// Ce qu'on accepte de manquer sur un bord. Un format papier est déclaré en centièmes de
    /// pouce : « 30 x 40 cm » se relit 300,0 × 400,0 mais « A3 297 × 420 » se relit
    /// 296,9 × 420,1. Sans cette tolérance, un tirage donné à la cote exacte de son papier
    /// serait jugé trop grand pour lui par un dixième de millimètre.
    /// </param>
    public static FormatPapierChoisi? ChoisirLeFormatPapier(
        string printerName, double tirageWidthMm, double tirageHeightMm, double toleranceMm = 0.6)
    {
        if (string.IsNullOrWhiteSpace(printerName)) return null;
        if (tirageWidthMm <= 0 || tirageHeightMm <= 0) return null;

        try
        {
            using var doc = new PrintDocument();
            doc.PrinterSettings.PrinterName = printerName;
            if (!doc.PrinterSettings.IsValid) return null;

            var offres = new List<PapierOffert>();
            foreach (PaperSize papier in doc.PrinterSettings.PaperSizes)
                offres.Add(new PapierOffert(
                    papier.PaperName,
                    papier.Width / 100.0 * MmPerInch,
                    papier.Height / 100.0 * MmPerInch,
                    papier.Kind == PaperKind.Custom));

            var choix = Retenir(offres, tirageWidthMm, tirageHeightMm, toleranceMm);
            if (choix is null) return null;

            var retenu = doc.PrinterSettings.PaperSizes
                .Cast<PaperSize>()
                .FirstOrDefault(p => p.PaperName == choix.Nom);
            if (retenu is null) return null;

            var page = (PageSettings)doc.DefaultPageSettings.Clone();
            page.PaperSize = retenu;
            page.Landscape = choix.Paysage;

            var octets = DevMode.Capture(doc.PrinterSettings, page);

            return new FormatPapierChoisi(
                octets, choix.Nom, choix.WidthMm, choix.HeightMm, choix.Paysage);
        }
        catch (Exception)
        {
            // pilote muet, imprimante debranchee : on ne sait pas choisir, on ne choisit pas
            return null;
        }
    }

    /// <summary>Un format tel que le pilote l'annonce, réduit à ce qui sert à choisir.</summary>
    /// <param name="Personnalise">
    /// Vrai pour l'entrée « Personnalisée » du pilote, dont les cotes annoncées sont celles
    /// de la dernière saisie et ne disent rien de ce qui sortira.
    /// </param>
    public sealed record PapierOffert(
        string Nom, double WidthMm, double HeightMm, bool Personnalise = false);

    /// <summary>Le format retenu, avant d'aller demander ses octets au pilote.</summary>
    public sealed record PapierRetenu(string Nom, double WidthMm, double HeightMm, bool Paysage);

    /// <summary>
    /// Le choix lui-même, séparé du pilote pour être vérifiable : le plus petit format où le
    /// tirage tient entier, couché seulement s'il le faut.
    ///
    /// Voir <see cref="ChoisirLeFormatPapier"/> pour le pourquoi et les garde-fous.
    /// </summary>
    public static PapierRetenu? Retenir(
        IEnumerable<PapierOffert> offres, double tirageWidthMm, double tirageHeightMm,
        double toleranceMm = 0.6)
    {
        ArgumentNullException.ThrowIfNull(offres);
        if (tirageWidthMm <= 0 || tirageHeightMm <= 0) return null;

        PapierRetenu? retenu = null;
        var retenuSurface = double.MaxValue;

        foreach (var offre in offres)
        {
            // un format dont la mesure ment ne peut pas servir à mesurer
            if (offre.Personnalise) continue;
            if (offre.WidthMm <= 0 || offre.HeightMm <= 0) continue;

            var debout =
                offre.WidthMm + toleranceMm >= tirageWidthMm
                && offre.HeightMm + toleranceMm >= tirageHeightMm;

            // en travers, le pilote permute les côtés de la feuille : c'est le drapeau
            // Landscape qui le fait, pas un autre format
            var couche =
                offre.HeightMm + toleranceMm >= tirageWidthMm
                && offre.WidthMm + toleranceMm >= tirageHeightMm;

            if (!debout && !couche) continue;

            var surface = offre.WidthMm * offre.HeightMm;
            if (surface > retenuSurface) continue;

            // à surface égale, on ne couche pas la feuille pour rien : une rotation inutile
            // est une occasion de charger le papier dans le mauvais sens
            if (surface == retenuSurface && !(retenu?.Paysage == true && debout)) continue;

            var paysage = !debout;
            retenu = new PapierRetenu(
                offre.Nom,
                paysage ? offre.HeightMm : offre.WidthMm,
                paysage ? offre.WidthMm : offre.HeightMm,
                paysage);
            retenuSurface = surface;
        }

        return retenu;
    }

    /// <summary>Formats déclarés par le pilote, pour information dans l'interface.</summary>
    public static IReadOnlyList<string> ListPaperSizes(string printerName)
    {
        using var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        if (!doc.PrinterSettings.IsValid) return [];

        var names = new List<string>();
        foreach (PaperSize paper in doc.PrinterSettings.PaperSizes)
            names.Add($"{paper.PaperName} — {paper.Width / 100.0 * MmPerInch:0} × {paper.Height / 100.0 * MmPerInch:0} mm");
        return names;
    }

    /// <summary>
    /// Imprime <paramref name="bitmap"/> selon <paramref name="settings"/>.
    /// </summary>
    /// <param name="bitmap">Image source, à sa résolution d'origine.</param>
    /// <param name="sourceDpi">Résolution d'origine de l'image (celle du fichier).</param>
    /// <param name="documentName">Nom affiché dans la file d'impression.</param>
    public static void Print(Bitmap bitmap, LargeFormatPrintSettings settings, double sourceDpi,
        string documentName = "Studio Photo — agrandissement")
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        ArgumentNullException.ThrowIfNull(settings);

        var problems = settings.Validate();
        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Les réglages d'impression sont incomplets :\n• " + string.Join("\n• ", problems));

        // La feuille et la définition du périphérique AVANT tout traitement : c'est d'elles
        // que dépend le nombre de pixels qu'il vaut la peine de convertir puis d'envoyer.
        var page = GetPageInfo(settings.PrinterName, settings.DevModeBytes, settings.Landscape);
        var placement = settings.ComputePlacement(bitmap.Width, bitmap.Height, sourceDpi,
            page.WidthMm, page.HeightMm);

        Bitmap? reduit = null;
        Bitmap? converti = null;

        try
        {
            // 1. Réduction à la résolution d'envoi. Elle vient EN PREMIER : tout ce qui suit
            //    — conversion ICC comprise — travaille alors sur moins de pixels.
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            reduit = MettreALEchelleDEnvoi(bitmap, placement, page.DeviceDpi, out var dpiEnvoi);
            var aTraiter = reduit ?? bitmap;
            var msReduction = chrono.ElapsedMilliseconds;

            if (reduit is not null)
                Log?.Invoke(
                    $"Mise à l'échelle d'envoi : {bitmap.Width}×{bitmap.Height} → " +
                    $"{reduit.Width}×{reduit.Height} px ({placement.EffectiveDpi:0} → {dpiEnvoi:0} ppp), " +
                    $"{msReduction} ms");

            // 2. La conversion ICC : ce qui part sur le papier n'est plus l'image d'origine
            //    mais sa version convertie pour ce papier-là. Sans elle, le profil, le mode de
            //    rendu et la compensation du point noir ne seraient que des cases à cocher
            //    sans effet — voir IccTransform.
            chrono.Restart();
            if (settings.ColorHandling == ColorHandling.ApplicationManagesColor
                && !string.IsNullOrWhiteSpace(settings.PrinterProfile))
            {
                converti = IccTransform.Apply(aTraiter, settings.DocumentProfileIcc, settings.PrinterProfile,
                    settings.RenderingIntent, settings.BlackPointCompensation);
                Log?.Invoke($"Conversion ICC : {chrono.ElapsedMilliseconds} ms");
            }

            // 3. L'envoi au spouleur.
            chrono.Restart();
            Imprimer(converti ?? aTraiter, settings, dpiEnvoi, documentName);
            Log?.Invoke($"Remise au spouleur : {chrono.ElapsedMilliseconds} ms");
        }
        finally
        {
            converti?.Dispose();
            reduit?.Dispose();
        }
    }

    /// <summary>
    /// Résolution d'envoi maximale, en points par pouce.
    ///
    /// C'est la règle de Photoshop, et elle n'a rien d'arbitraire : les Epson consomment les
    /// données à 360 ppp (720 pour la qualité la plus fine) et montent elles-mêmes à leur
    /// définition de tramage. Au-delà, on paie des pixels que le pilote jette.
    /// </summary>
    private const double PppEnvoiMaximal = 360;

    /// <summary>
    /// Ramène l'image au nombre de pixels que le pilote consommera réellement.
    ///
    /// <b>Pourquoi cette étape existe.</b> <c>Graphics.DrawImage</c> sur un contexte
    /// d'imprimante, avec une interpolation de qualité demandée, cesse de déléguer au pilote
    /// (<c>StretchDIBits</c>) et rééchantillonne LUI-MÊME à la définition du périphérique.
    /// Sur la SC-P800, qui annonce 1440 ppp, un 50×70 devient 28 346 × 39 685 px — plus d'un
    /// milliard de pixels fabriqués en mémoire puis spoulés. C'est ce seul point qui rendait
    /// « Imprimer » interminable une fois <see cref="IccTransform"/> corrigé.
    ///
    /// <b>On ne fait que RÉDUIRE.</b> Agrandir l'image pour atteindre 360 ppp fabriquerait
    /// des pixels que la source ne contient pas, en payant le même prix : la montée en
    /// définition est le travail du pilote, qui la fait mieux et gratuitement.
    /// </summary>
    /// <param name="deviceDpi">Définition annoncée par le pilote ; 0 = inconnue.</param>
    /// <param name="dpiEnvoi">
    /// Résolution à annoncer pour l'image rendue. Le placement en millimètres est ainsi
    /// rigoureusement conservé : réduire les pixels d'un facteur k revient à diviser la
    /// résolution par k.
    /// </param>
    /// <returns>La nouvelle image, ou null s'il n'y avait rien à réduire.</returns>
    private static Bitmap? MettreALEchelleDEnvoi(Bitmap bitmap, PrintPlacement placement,
        int deviceDpi, out double dpiEnvoi)
    {
        var (largeur, hauteur, dpi) = TailleDEnvoi(
            bitmap.Width, bitmap.Height, placement.EffectiveDpi, deviceDpi);

        dpiEnvoi = dpi;
        if (largeur == bitmap.Width && hauteur == bitmap.Height) return null;

        var reduit = new Bitmap(largeur, hauteur, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(reduit))
        {
            // ici, la qualité ne coûte presque rien : on travaille sur un bitmap mémoire, et
            // le résultat est plus petit que la source. C'est sur le contexte de
            // l'IMPRIMANTE qu'elle était ruineuse.
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.DrawImage(bitmap, new Rectangle(0, 0, largeur, hauteur));
        }

        return reduit;
    }

    /// <summary>
    /// La définition à envoyer au pilote, isolée pour être vérifiable : combien de pixels, et
    /// à quelle résolution les annoncer.
    ///
    /// Renvoie la taille d'origine quand il n'y a rien à réduire — c'est le cas courant, un
    /// rendu de l'atelier à 300 ppp tiré à 100 %.
    /// </summary>
    /// <param name="effectiveDpi">Résolution obtenue au format demandé (voir <see cref="PrintPlacement"/>).</param>
    /// <param name="deviceDpi">Définition annoncée par le pilote ; 0 = inconnue.</param>
    /// <returns>
    /// La taille retenue et la résolution à annoncer. Cette dernière suit EXACTEMENT le
    /// nombre de pixels, arrondi compris : sans quoi le tirage se décalerait de la fraction
    /// de pixel perdue à l'arrondi.
    /// </returns>
    internal static (int Width, int Height, double Dpi) TailleDEnvoi(
        int widthPx, int heightPx, double effectiveDpi, int deviceDpi)
    {
        // placement dégénéré (échelle nulle, feuille inconnue) : on ne touche à rien
        if (widthPx <= 0 || heightPx <= 0 || effectiveDpi <= 0)
            return (widthPx, heightPx, effectiveDpi);

        var plafond = deviceDpi > 0 ? Math.Min(PppEnvoiMaximal, deviceDpi) : PppEnvoiMaximal;

        // rien à gagner : l'image est déjà à la résolution d'envoi ou en dessous. Le pour
        // cent de jeu évite de rééchantillonner 48 Mpx pour trois pixels.
        if (effectiveDpi <= plafond * 1.01) return (widthPx, heightPx, effectiveDpi);

        var facteur = plafond / effectiveDpi;
        var largeur = Math.Max(1, (int)Math.Round(widthPx * facteur));
        var hauteur = Math.Max(1, (int)Math.Round(heightPx * facteur));

        if (largeur >= widthPx || hauteur >= heightPx)
            return (widthPx, heightPx, effectiveDpi);

        return (largeur, hauteur, effectiveDpi * largeur / widthPx);
    }

    private static void Imprimer(Bitmap bitmap, LargeFormatPrintSettings settings, double sourceDpi,
        string documentName)
    {
        using var doc = new PrintDocument();
        doc.DocumentName = documentName;
        doc.PrinterSettings.PrinterName = settings.PrinterName;
        if (!doc.PrinterSettings.IsValid)
            throw new InvalidOperationException($"Imprimante invalide ou hors ligne : « {settings.PrinterName} »");

        if (settings.DevModeBytes is not null)
            DevMode.Apply(doc.PrinterSettings, settings.DevModeBytes);

        doc.PrinterSettings.Copies = (short)Math.Max(1, settings.Copies);
        doc.DefaultPageSettings.Landscape = settings.Landscape;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.OriginAtMargins = false;
        doc.PrintController = new StandardPrintController();

        doc.PrintPage += (_, e) =>
        {
            var g = e.Graphics!;
            // (0,0) au coin physique de la feuille, comme dans BitmapPrinter
            g.TranslateTransform(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);

            var page = e.PageSettings.Bounds;
            var pageWidthMm = page.Width / 100.0 * MmPerInch;
            var pageHeightMm = page.Height / 100.0 * MmPerInch;

            var placement = settings.ComputePlacement(bitmap.Width, bitmap.Height, sourceDpi,
                pageWidthMm, pageHeightMm);

            var rect = new RectangleF(
                (float)(placement.LeftMm / MmPerInch * 100),
                (float)(placement.TopMm / MmPerInch * 100),
                (float)(placement.WidthMm / MmPerInch * 100),
                (float)(placement.HeightMm / MmPerInch * 100));

            var couleurs = settings.ColorHandling == ColorHandling.ApplicationManagesColor
                           && !string.IsNullOrWhiteSpace(settings.PrinterProfile)
                ? ", " + IccTransform.Describe(settings.PrinterProfile, settings.RenderingIntent,
                    settings.BlackPointCompensation)
                : ", couleurs gérées par l'imprimante";

            Log?.Invoke(
                $"Agrandissement « {documentName} » sur {settings.PrinterName} : " +
                $"feuille {pageWidthMm:0}×{pageHeightMm:0} mm, " +
                $"tirage {placement.WidthMm:0.0}×{placement.HeightMm:0.0} mm à {placement.ScalePercent:0.#} % " +
                $"({placement.EffectiveDpi:0} ppp), position {placement.LeftMm:0.0};{placement.TopMm:0.0} mm" +
                couleurs +
                Debordement(placement, settings.Scaling, pageWidthMm, pageHeightMm));

            // Interpolation par DÉFAUT, et surtout pas HighQualityBicubic.
            //
            // Demander la qualité ici fait sortir GDI+ de son chemin rapide : au lieu de
            // remettre le bitmap au pilote (StretchDIBits) et de le laisser monter à sa
            // définition de tramage, il rééchantillonne lui-même à 1440 ppp — plus d'un
            // milliard de pixels pour un 50×70, fabriqués en mémoire puis spoulés. Le
            // rééchantillonnage de qualité a déjà eu lieu, en amont et sur un bitmap
            // mémoire, dans MettreALEchelleDEnvoi.
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Default;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Default;

            // La photo est COUPÉE au bord de la feuille, jamais posée en travers.
            //
            // Sans ce rognage, un tirage plus grand que le papier partait quand même au
            // pilote dans sa taille demandée : ce qui dépassait était perdu sans qu'on ait
            // choisi OÙ, et l'aperçu montrait la photo à cheval sur le vide. C'est le sens
            // du mode « Remplir le support » (MediaScaling.FillMedia), qui déborde
            // VOLONTAIREMENT pour ne pas laisser de blanc ; le rognage vaut aussi pour un
            // débordement subi, où couper proprement reste mieux que de laisser faire.
            g.SetClip(new RectangleF(0, 0,
                (float)(pageWidthMm / MmPerInch * 100), (float)(pageHeightMm / MmPerInch * 100)));

            g.DrawImage(bitmap, rect);

            // le contour suit le bord du tirage, donc lui aussi disparaît là où la photo est
            // coupée : en remplissage il n'en reste rien, et c'est juste — il n'y a plus de
            // blanc à recouper
            if (settings.CutBorder) DessinerContourDeDecoupe(g, rect);

            g.ResetClip();

            e.HasMorePages = false;
        };

        doc.Print();
    }

    /// <summary>
    /// Ce que le journal dit du débordement.
    ///
    /// En remplissage il est VOULU : l'annoncer comme une anomalie ferait chercher une panne
    /// là où il n'y en a pas. On note alors ce qui est coupé, qui est l'information utile
    /// quand un client trouve son tirage trop serré.
    /// </summary>
    private static string Debordement(PrintPlacement placement, MediaScaling scaling,
        double pageWidthMm, double pageHeightMm)
    {
        if (!placement.OverflowsPaper(pageWidthMm, pageHeightMm)) return "";

        var coupe = placement.CroppedShare(pageWidthMm, pageHeightMm);
        return scaling == MediaScaling.FillMedia
            ? $", remplissage du support ({coupe:P0} coupé aux bords)"
            : $"  ⚠ le tirage déborde de la feuille, {coupe:P0} coupé";
    }

    /// <summary>Épaisseur du trait de découpe, en millimètres.</summary>
    private const double TraitDeDecoupeMm = 0.2;

    /// <summary>
    /// Le contour à suivre aux ciseaux, tracé À CHEVAL sur le bord du tirage.
    ///
    /// <c>DrawRectangle</c> centre le trait sur le tracé : la moitié tombe sur la photo, l'autre
    /// sur le blanc. À deux dixièmes de millimètre, le coup de ciseaux emporte les deux — c'est
    /// la règle des planches identité (<c>ImagePipeline.DrawCutBorders</c>), et elle vaut ici
    /// pour la même raison : un trait posé À L'INTÉRIEUR laisserait un liseré noir sur la photo
    /// coupée, un trait posé à l'extérieur laisserait du blanc.
    ///
    /// L'unité du contexte est le centième de pouce, comme <paramref name="rect"/>.
    /// </summary>
    private static void DessinerContourDeDecoupe(Graphics g, RectangleF rect)
    {
        var epaisseur = (float)(TraitDeDecoupeMm / MmPerInch * 100);

        using var plume = new Pen(Color.Black, epaisseur);
        g.DrawRectangle(plume, rect.X, rect.Y, rect.Width, rect.Height);
    }
}
