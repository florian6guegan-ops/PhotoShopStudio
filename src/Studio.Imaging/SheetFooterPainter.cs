using System.Globalization;
using ImageMagick;
using ImageMagick.Drawing;
using Studio.Imaging.Geometry;

namespace Studio.Imaging;

/// <summary>
/// Dessine la bande basse d'une planche identité : date, mention de conformité, code QR et
/// marque du studio.
///
/// La découpe en zones est faite ailleurs et sans pixel — voir
/// <see cref="SheetFooterLayout"/> — pour qu'elle soit vérifiable sans fabriquer d'image.
/// Ici on ne fait que poser de l'encre dans les zones rendues.
/// </summary>
public static class SheetFooterPainter
{
    /// <summary>
    /// Pose la bande sous le bloc de photos. Ne dessine rien si la place manque : mordre
    /// sur une case rendrait la photo non conforme, ce qui est pire que l'absence de bande.
    /// </summary>
    /// <param name="photosBottom">Bas de la dernière rangée de photos, en pixels.</param>
    public static void Draw(MagickImage sheet, SheetFooter footer, int photosBottom, int dpi)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        ArgumentNullException.ThrowIfNull(footer);

        var pose = SheetFooterLayout.Place(
            footer, (int)sheet.Width, (int)sheet.Height, photosBottom, dpi);
        if (pose is null) return;

        // les images d'abord, le texte ensuite : un logo à fond blanc effacerait sinon la
        // mention qu'il jouxte
        if (pose.Qr is { } qr && footer.QrPng is { Length: > 0 } octets)
            PoserImage(sheet, octets, qr);

        if (pose.Logo is { } logo && LireLeLogo(footer.LogoPath) is { } fichier)
            PoserImage(sheet, fichier, logo);

        if (pose.Date is { } date)
            EcrireLaDate(sheet, footer, date, pose.Mention is null, dpi);

        if (pose.Mention is { } mention && !string.IsNullOrWhiteSpace(footer.Mention))
            EcrireLaMention(sheet, footer.Mention, mention);
    }

    /// <summary>
    /// La date, en corps 5 mm comme celle de DiLand.
    ///
    /// <paramref name="seule"/> la recentre sur toute la bande : c'est la planche d'avant,
    /// celle où la bande ne porte rien d'autre, et il n'y a pas de raison de la coller à
    /// gauche d'un espace vide.
    /// </summary>
    private static void EcrireLaDate(MagickImage sheet, SheetFooter footer, PixelRect zone,
        bool seule, int dpi)
    {
        var corps = MmPx.ToPixels(SheetFooterLayout.CorpsDateMm, dpi);
        var texte = footer.Moment.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

        // Le corps est donné en PIXELS : ImageMagick dessine à 72 points par pouce quelle
        // que soit la densité de l'image, donc un point vaut ici un pixel. Le convertir
        // comme un vrai corps typographique le divisait par quatre — mention illisible.
        var dessin = new Drawables()
            .Font(Fonts.SansEmpattement())
            .FontPointSize(corps)
            .FillColor(MagickColors.Black)
            .StrokeColor(MagickColors.Transparent);

        // la ligne de base tombe aux trois quarts du corps sous le haut du texte : c'est ce
        // qui centre optiquement une capitale et un chiffre dans leur zone
        var ligneDeBase = zone.Y + (zone.Height + corps * 0.72) / 2;

        if (seule)
            dessin.TextAlignment(TextAlignment.Center)
                .Text(sheet.Width / 2.0, ligneDeBase, texte);
        else
            dessin.TextAlignment(TextAlignment.Left)
                .Text(zone.X, ligneDeBase, texte);

        sheet.Draw(dessin);
    }

    /// <summary>
    /// La mention de conformité, centrée dans sa zone, sur une ou deux lignes.
    ///
    /// Le corps se DÉDUIT de la zone au lieu d'être fixé : la bande fait la même hauteur en
    /// millimètres quel que soit le papier, mais la largeur disponible dépend de ce que la
    /// date et le code QR laissent. Un corps figé sortirait de la zone sur un petit papier.
    /// </summary>
    private static void EcrireLaMention(MagickImage sheet, string mention, PixelRect zone)
    {
        var lignes = mention.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lignes.Length == 0) return;

        // Deux lignes se partagent la hauteur, l'interligne compris ; une seule la prend
        // toute. Le facteur 0,62 laisse l'air qui sépare les lignes.
        var corps = lignes.Length > 1 ? zone.Height * 0.40 : zone.Height * 0.62;

        // ...mais jamais au point de dépasser en largeur. Le même demi-cadratin que la date
        // (voir SheetFooterLayout) majore la largeur d'un texte sans le mesurer.
        var laPlusLongue = lignes.Max(l => l.Trim().Length);
        corps = Math.Min(corps, zone.Width / (laPlusLongue * 0.58));
        if (corps < 2) return;

        var centreX = zone.X + zone.Width / 2.0;

        // le bloc de lignes est centré sur la zone : on part du haut du bloc, pas du haut
        // de la zone, sinon deux lignes pendent vers le bas
        var interligne = corps * 1.25;
        var hauteurBloc = interligne * (lignes.Length - 1) + corps;
        var ligneDeBase = zone.Y + (zone.Height - hauteurBloc) / 2 + corps * 0.86;

        for (var i = 0; i < lignes.Length; i++)
        {
            // la première ligne porte l'annonce, les suivantes la précisent : le gras et le
            // corps les distinguent, comme sur les planches du commerce
            var dessin = new Drawables()
                .Font(Fonts.SansEmpattement(), FontStyleType.Normal,
                    i == 0 ? FontWeight.Bold : FontWeight.Normal, FontStretch.Normal)
                .FontPointSize(i == 0 ? corps : corps * 0.78)
                .FillColor(i == 0 ? MagickColors.Black : new MagickColor("#3C3C3C"))
                .StrokeColor(MagickColors.Transparent)
                .TextAlignment(TextAlignment.Center)
                .Text(centreX, ligneDeBase + i * interligne, lignes[i].Trim());

            sheet.Draw(dessin);
        }
    }

    /// <summary>
    /// Pose une image dans sa zone, à ses propres proportions et centrée dedans.
    ///
    /// Un code QR déformé cesse d'être lu et un logo étiré fait amateur : on tient dans la
    /// zone, on ne la remplit pas de force.
    /// </summary>
    private static void PoserImage(MagickImage sheet, byte[] octets, PixelRect zone)
    {
        if (zone.Width <= 0 || zone.Height <= 0) return;

        try
        {
            using var image = new MagickImage(octets);
            image.Resize(new MagickGeometry((uint)zone.Width, (uint)zone.Height)); // proportions gardées

            sheet.Composite(image,
                zone.X + (zone.Width - (int)image.Width) / 2,
                zone.Y + (zone.Height - (int)image.Height) / 2,
                CompositeOperator.Over);
        }
        catch (MagickException)
        {
            // fichier illisible ou format inconnu : la planche sort sans, plutôt que pas du
            // tout. C'est un ornement, la photo d'identité est le produit.
        }
    }

    /// <summary>Les octets du logo, ou null si le chemin est vide ou le fichier absent.</summary>
    private static byte[]? LireLeLogo(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin) || !File.Exists(chemin)) return null;

        try
        {
            return File.ReadAllBytes(chemin);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
