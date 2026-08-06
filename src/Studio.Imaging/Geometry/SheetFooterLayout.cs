using Studio.Core;
using Studio.Core.Domain;

namespace Studio.Imaging.Geometry;

/// <summary>
/// Ce que porte la bande basse de la planche identité.
///
/// La date seule y figurait, centrée dans la marge résiduelle. Elle est là pour une raison
/// administrative — une photo d'identité doit être récente, et c'est la mention qui le
/// prouve — mais la planche que la boutique veut sortir en porte davantage : la mention de
/// conformité, un code QR, et la marque du studio. C'est la planche que les bornes du
/// commerce impriment, et le client la reconnaît.
/// </summary>
/// <param name="Moment">Date et heure du tirage.</param>
/// <param name="Mention">
/// Mention de conformité, sur une ou deux lignes séparées par un retour à la ligne.
/// Null ou vide = pas de mention.
/// </param>
/// <param name="LogoPath">
/// Image de la marque, posée à droite. Null, vide ou fichier absent = pas de logo : une
/// planche doit sortir même quand le fichier a été déplacé.
/// </param>
/// <param name="QrPng">
/// Code QR déjà encodé en PNG. Les octets, et non le texte à encoder : la génération vit
/// dans <c>Studio.Web</c>, dont l'imagerie n'a pas à dépendre.
/// </param>
public sealed record SheetFooter(
    DateTime Moment,
    string? Mention = null,
    string? LogoPath = null,
    byte[]? QrPng = null)
{
    /// <summary>Vrai s'il n'y a rien d'autre que la date : la bande se réduit alors.</summary>
    public bool DateSeule =>
        string.IsNullOrWhiteSpace(Mention) && string.IsNullOrWhiteSpace(LogoPath) && QrPng is null;

    /// <summary>
    /// La bande d'une planche tirée à <paramref name="moment"/>, selon la marque réglée.
    ///
    /// Le code QR est encodé ICI, une fois par planche : c'est un calcul, pas un fichier, et
    /// le laisser aux appelants voudrait dire le refaire à l'identique dans l'aperçu et dans
    /// l'impression — deux endroits où il pourrait diverger sans qu'on le voie.
    /// </summary>
    /// <param name="marque">Réglages de la boutique. Null = date seule, comme avant.</param>
    public static SheetFooter Pour(DateTime moment, MarqueSettings? marque)
    {
        if (marque is not { BandeActive: true }) return new SheetFooter(moment);

        byte[]? qr = null;
        if (!string.IsNullOrWhiteSpace(marque.QrTexte))
        {
            try
            {
                // nommé au complet : dans le corps de ce record, « QrPng » désigne le
                // paramètre et non le générateur du noyau
                qr = Studio.Core.QrPng.For(marque.QrTexte, pixelsPerModule: 8);
            }
            catch (Exception)
            {
                // texte qu'aucun code QR ne peut porter (trop long) : la planche sort sans.
                // C'est un ornement, la photo d'identité est le produit.
            }
        }

        return new SheetFooter(moment, marque.Mention, marque.LogoPath, qr);
    }
}

/// <summary>
/// Emplacements calculés dans la bande basse, en pixels de la planche.
/// Une zone nulle = l'élément ne tient pas et ne sera pas dessiné.
/// </summary>
/// <param name="Band">La bande entière, de bord à bord.</param>
/// <param name="Date">Zone de la date, alignée à gauche.</param>
/// <param name="Mention">Zone de la mention, centrée sur la planche.</param>
/// <param name="Qr">Carré du code QR.</param>
/// <param name="Logo">Zone du logo, alignée à droite.</param>
public sealed record FooterPlacement(
    PixelRect Band,
    PixelRect? Date,
    PixelRect? Mention,
    PixelRect? Qr,
    PixelRect? Logo);

/// <summary>
/// Découpe de la bande basse. Fonctions pures, vérifiables au pixel — comme
/// <see cref="IdSheetLayout"/>, et pour la même raison : ce qui se dessine sur un tirage
/// ne se corrige pas après coup.
/// </summary>
public static class SheetFooterLayout
{
    /// <summary>Hauteur visée de la bande, en millimètres, quand elle porte tout.</summary>
    public const double HauteurMm = 8;

    /// <summary>
    /// Hauteur minimale exploitable. En dessous, la mention devient illisible sur le papier
    /// et le code QR cesse d'être lu : mieux vaut alors ne garder que la date.
    /// </summary>
    public const double HauteurMinimaleMm = 6;

    /// <summary>Air laissé au-dessus et en dessous de ce que la bande porte.</summary>
    private const double MargeMm = 1.2;

    /// <summary>
    /// Air laissé à GAUCHE et à DROITE, plus large que l'autre — et il faut qu'il le soit.
    ///
    /// La planche est tirée à fond perdu : la machine réclame l'image avec 3 mm de débord
    /// qu'elle rogne elle-même (voir <c>PrintOrchestrator.RemplirLeDebord</c>), soit près
    /// d'un millimètre et demi mangé sur chaque bord. À 1,2 mm du bord, la date en perdait
    /// donc le premier chiffre — « légèrement coupée sur le côté gauche », constaté sur le
    /// papier le 06/08/2026. Quatre millimètres la mettent hors d'atteinte du rognage.
    /// </summary>
    private const double MargeBordMm = 4;

    /// <summary>
    /// Corps de la date. DiLand écrit la sienne en 5 mm — relevé dans son code :
    /// <c>new Font("Arial", 5 * unMillimetreEnPixels, GraphicsUnit.Pixel)</c>. En dessous,
    /// la date est illisible sur le tirage.
    /// </summary>
    public const double CorpsDateMm = 5;

    /// <summary>
    /// Corps de l'HEURE, en fraction de celui de la date.
    ///
    /// L'heure est une précision, pas la mention : c'est la DATE qui prouve qu'une photo
    /// d'identité est récente. Elle est donc écrite plus petite, à la suite — ce que
    /// demandait l'exploitant le 06/08/2026, et qui évite d'allonger la bande.
    /// </summary>
    public const double FractionHeure = 0.72;

    /// <summary>Ce qui sépare la date de l'heure, en fraction du corps de la date.</summary>
    public const double EcartHeureCadratins = 0.45;

    /// <summary>
    /// Largeur approchée d'un texte, sans contexte de dessin.
    ///
    /// Un caractère de sans-empattement tient dans un peu plus de la moitié de son corps.
    /// On MAJORE volontairement : une mention un peu plus courte vaut mieux qu'une date
    /// tronquée. Publique parce que le peintre doit tomber sur les mêmes largeurs que la
    /// découpe, faute de quoi l'heure sortirait du cadre réservé.
    /// </summary>
    public static double LargeurTexte(int caracteres, double corpsPx) =>
        caracteres * corpsPx * DemiCadratin;

    private const double DemiCadratin = 0.58;

    /// <summary>
    /// Hauteur à réserver en bas de la planche pour <paramref name="footer"/>, en pixels.
    ///
    /// C'est cette valeur qui part en <c>bottomReserve</c> à <see cref="IdSheetLayout.Layout"/> :
    /// le bloc de photos remonte d'autant, et la bande ne mord jamais sur une case — une
    /// photo d'identité rognée est une photo refusée au guichet.
    /// </summary>
    public static int ReservePx(SheetFooter? footer, int dpi) => footer switch
    {
        null => 0,
        { DateSeule: true } => MmPx.ToPixels(CorpsDateMm + 2, dpi),
        _ => MmPx.ToPixels(HauteurMm, dpi),
    };

    /// <summary>
    /// Découpe la bande sous le bloc de photos.
    ///
    /// <paramref name="photosBottom"/> est le bas de la dernière rangée : la bande occupe
    /// tout ce qui reste en dessous. Elle peut être plus HAUTE que la réserve demandée —
    /// une planche à six cases sur un papier prévu pour huit laisse de la place — et c'est
    /// tant mieux, la mention y respire.
    /// </summary>
    /// <returns>Null si l'espace restant ne permet même pas d'écrire la date.</returns>
    public static FooterPlacement? Place(
        SheetFooter footer, int sheetWidth, int sheetHeight, int photosBottom, int dpi)
    {
        ArgumentNullException.ThrowIfNull(footer);

        var hauteur = sheetHeight - photosBottom;
        var corpsDate = MmPx.ToPixels(CorpsDateMm, dpi);

        // pas même de quoi écrire la date : mordre sur les photos serait pire que se taire
        if (hauteur < corpsDate + MmPx.ToPixels(1, dpi)) return null;

        var band = new PixelRect(0, photosBottom, sheetWidth, hauteur);
        var marge = MmPx.ToPixels(MargeMm, dpi);
        var margeBord = MmPx.ToPixels(MargeBordMm, dpi);
        var utile = hauteur - 2 * marge;

        // Bande courte, ou rien d'autre à porter : la date reprend sa place d'avant, seule
        // et centrée. Une mention de 2 mm de haut ne se lit pas, et un QR de 2 mm ne se
        // scanne pas — les poser quand même reviendrait à salir le tirage pour rien.
        if (footer.DateSeule || hauteur < MmPx.ToPixels(HauteurMinimaleMm, dpi) || utile <= 0)
            return new FooterPlacement(
                band,
                Date: new PixelRect(0, photosBottom, sheetWidth, hauteur),
                Mention: null, Qr: null, Logo: null);

        var y = photosBottom + marge;

        // De droite à gauche : le logo puis le QR, tous deux carrés sur la hauteur utile.
        // Ils sont dimensionnés AVANT la mention parce qu'ils ne se compriment pas — un
        // code QR trop petit cesse d'être lu, là où un texte se resserre.
        var droite = sheetWidth - margeBord;

        PixelRect? logo = null;
        if (!string.IsNullOrWhiteSpace(footer.LogoPath))
        {
            // le logo est plus large que haut ; on lui réserve deux fois le côté du carré,
            // et le dessin s'y inscrira selon ses propres proportions
            var largeur = Math.Min(utile * 2, sheetWidth / 4);
            logo = new PixelRect(droite - largeur, y, largeur, utile);
            droite = logo.X - marge;
        }

        PixelRect? qr = null;
        if (footer.QrPng is { Length: > 0 })
        {
            qr = new PixelRect(droite - utile, y, utile, utile);
            droite = qr.X - marge;
        }

        // La date garde le corps de DiLand, et la bande est taillée pour l'accueillir.
        var largeurDate = LargeurDeLaDate(corpsDate);
        var date = new PixelRect(margeBord, y, largeurDate, utile);

        // La mention prend ce qui sépare la date de ce qui est à droite. Elle est centrée
        // sur CET espace et non sur la planche : centrée sur la planche, elle chevaucherait
        // le QR dès que la date est longue.
        var gauche = date.Right + marge;
        var largeurMention = droite - gauche;

        PixelRect? mention = null;
        if (!string.IsNullOrWhiteSpace(footer.Mention) && largeurMention > corpsDate)
            mention = new PixelRect(gauche, y, largeurMention, utile);

        return new FooterPlacement(band, date, mention, qr, logo);
    }

    /// <summary>
    /// Largeur à réserver pour « JJ/MM/AAAA » suivi de « HH:MM » en plus petit.
    ///
    /// On ne MESURE pas le texte — cela demanderait un contexte de dessin, donc une image,
    /// dans ce qui doit rester un calcul pur : voir <see cref="LargeurTexte"/>.
    /// </summary>
    private static int LargeurDeLaDate(int corpsPx) => (int)Math.Ceiling(
        LargeurTexte("00/00/0000".Length, corpsPx)
        + corpsPx * EcartHeureCadratins
        + LargeurTexte("00:00".Length, corpsPx * FractionHeure));
}
