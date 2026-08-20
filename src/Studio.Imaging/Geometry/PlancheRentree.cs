namespace Studio.Imaging.Geometry;

/// <summary>
/// Ce que porte une planche « rentrée » : les cases d'identité, et LA grande.
/// </summary>
/// <param name="Identites">Les cases à la norme du document, dans l'ordre de pose.</param>
/// <param name="Grande">La case du portrait, qui prend tout ce que les identités laissent.</param>
/// <param name="Colonnes">Colonnes du bloc d'identités.</param>
/// <param name="Rangees">Rangées du bloc d'identités.</param>
/// <param name="BandeBasse">
/// Ce qui reste libre SOUS LE BLOC D'IDENTITÉS, et sous lui seul : c'est là que s'écrivent
/// la date et la mention. Voir <see cref="PlancheRentree"/>.
/// </param>
public sealed record PlancheRentreeResult(
    IReadOnlyList<PixelRect> Identites,
    PixelRect Grande,
    IReadOnlyList<CutTick> CutTicks,
    int Colonnes,
    int Rangees,
    PixelRect BandeBasse)
{
    /// <summary>
    /// Toutes les cases de la planche, identités puis grande.
    ///
    /// C'est sous cette forme que le rendu les reprend pour tracer les contours de découpe
    /// et trouver le bas de la planche : le trait se pose autour de CHAQUE photo, et la
    /// grande n'y fait pas exception — c'est sur lui qu'on coupe.
    /// </summary>
    public IReadOnlyList<PixelRect> Toutes => [.. Identites, Grande];
}

/// <summary>
/// La planche de la RENTRÉE : quelques photos d'identité, et un portrait en grand sur ce
/// qu'elles laissent.
///
/// <b>Ce n'est pas une planche d'identité avec une case de plus.</b>
/// <see cref="IdSheetLayout"/> pose une grille UNIFORME — toutes les cases à la même
/// empreinte, c'est ce qui lui permet de compter les places. Ici deux formats cohabitent
/// sur le même papier : les identités gardent la cote exacte de la norme, qui ne se
/// négocie pas, et le portrait prend le reste, quel que soit ce reste.
///
/// <b>Le bloc d'identités va à GAUCHE, en colonnes pleines.</b> On empile d'abord en
/// hauteur — autant de rangées que le papier en porte — puis on ajoute une colonne. C'est
/// ce qui laisse au portrait le plus large morceau d'un seul tenant : quatre identités
/// 35 × 45 sur une planche 10×15 couchée tiennent en 2 × 2 et laissent 84 mm de largeur
/// sur toute la hauteur, soit un portrait de 84 × 97 mm. Les poser en une seule rangée
/// n'aurait laissé qu'une bande de 55 mm de haut.
///
/// Fonctions pures, comme <see cref="IdSheetLayout"/> : ce qui se dessine sur un tirage
/// doit pouvoir se vérifier sans imprimante.
/// </summary>
public static class PlancheRentree
{
    /// <summary>
    /// Largeur minimale du portrait, en millimètres.
    ///
    /// En dessous, ce n'est plus « une grande photo » mais une bande : autant dire à
    /// l'opérateur que ce document ne se prête pas à la planche de rentrée, plutôt que de
    /// lui sortir un marque-page. Cinquante millimètres, c'est déjà plus large qu'une case
    /// d'identité française.
    /// </summary>
    public const double LargeurMinimaleGrandeMm = 50;

    /// <summary>
    /// Air gardé AU-DESSUS du bloc d'identités, en millimètres, quand la planche porte une
    /// bande basse.
    ///
    /// Le tirage est à fond perdu : la machine réclame l'image avec du débord qu'elle rogne
    /// elle-même, près d'un millimètre et demi par bord. Une case d'identité rognée est une
    /// photo refusée au guichet — le bloc ne va donc jamais au bord.
    ///
    /// Deux millimètres, comme <c>SheetFooterLayout.MargeBasseMm</c> et pour la même
    /// raison : au-dessus du rognage mesuré, et pas plus. Chaque millimètre pris ici est un
    /// millimètre de moins pour écrire, et c'est ce qui décide si la mention reste lisible.
    /// </summary>
    public const double AirEnHautMm = 2;

    /// <summary>
    /// Dispose les identités et le portrait sur la planche.
    ///
    /// <paramref name="bottomReserve"/> joue le même rôle qu'à
    /// <see cref="IdSheetLayout.Layout"/> : la hauteur laissée libre en bas pour la bande
    /// qui porte la date. <b>Elle n'est prise QUE SUR LE BLOC D'IDENTITÉS</b>, et le
    /// portrait descend jusqu'au bord de la feuille.
    ///
    /// ⚠ <b>C'est un changement du 20/08/2026, et il répare la mention manquante.</b> La
    /// réserve courait avant sous toute la planche, portrait compris. Le portrait étant
    /// taillé pour occuper exactement ce qui restait, la bande tombait toujours sur son
    /// MINIMUM — 4,5 mm de hauteur utile, sous le plancher de 6 mm en dessous duquel
    /// <see cref="SheetFooterLayout"/> ne garde que la date. La planche de rentrée ne
    /// pouvait donc PAS porter « PHOTOS CONFORMES » ni le nom de la boutique : pas par
    /// oubli, mais par géométrie.
    ///
    /// Sous le seul bloc d'identités, la bande trouve une dizaine de millimètres — deux
    /// rangées de 45 mm sur un 10×15 couché n'en occupent que 91 — et le portrait gagne au
    /// passage les 6,5 mm qu'il cédait sur sa hauteur.
    /// </summary>
    /// <param name="identites">Nombre de cases à la norme du document.</param>
    /// <param name="largeurMinimaleGrandePx">
    /// Largeur en deçà de laquelle on renonce. Voir <see cref="LargeurMinimaleGrandeMm"/>.
    /// </param>
    /// <returns>
    /// Null quand la planche ne peut pas porter cet assortiment : cases trop hautes pour le
    /// papier, ou portrait réduit à une lisière. <b>Null plutôt qu'une exception</b>, parce
    /// que l'appelant s'en sert aussi pour SAVOIR — l'écran de cadrage teste chaque papier
    /// du catalogue pour n'offrir que ceux qui conviennent.
    /// </returns>
    public static PlancheRentreeResult? Layout(
        int sheetWidth, int sheetHeight,
        int cellWidth, int cellHeight,
        int gap, int identites,
        int tickLength = 0, int bottomReserve = 0,
        int largeurMinimaleGrandePx = 0, int airEnHaut = 0)
    {
        if (identites < 1) return null;
        if (cellWidth <= 0 || cellHeight <= 0) return null;
        if (gap < 0 || bottomReserve < 0) return null;
        if (cellWidth > sheetWidth || cellHeight > sheetHeight) return null;

        // La bande basse est prise sur la hauteur AVANT tout calcul : une planche qui
        // remplit le papier jusqu'en bas n'a plus où écrire sa date, et une planche
        // d'identité sans date ne prouve plus qu'elle est récente.
        //
        // ⚠ Elle ne vaut que pour le BLOC D'IDENTITÉS. Le portrait, lui, prend toute la
        // feuille — voir la documentation de la méthode.
        var utile = sheetHeight - bottomReserve;
        if (utile < cellHeight) return null;

        // On empile en HAUTEUR d'abord : c'est ce qui garde le portrait large. Voir la
        // documentation de la classe.
        var maxRangees = (utile + gap) / (cellHeight + gap);
        if (maxRangees < 1) return null;

        var rangees = Math.Min(maxRangees, identites);
        var colonnes = (int)Math.Ceiling((double)identites / rangees);

        var blocW = colonnes * cellWidth + (colonnes - 1) * gap;
        var blocH = rangees * cellHeight + (rangees - 1) * gap;
        if (blocW >= sheetWidth) return null;

        var largeurGrande = sheetWidth - blocW - gap;
        if (largeurGrande < Math.Max(1, largeurMinimaleGrandePx)) return null;

        // ⚠ LE BLOC MONTE, POUR QUE LA BANDE RESPIRE.
        //
        // Il était centré sur la hauteur utile, ce qui se défendait quand la bande courait
        // sous toute la planche : le jeu se partageait alors entre le haut et le bas. Sous
        // le seul bloc, ce partage coûte la moitié de la place à écrire — et le texte s'y
        // retrouvait à 13 px de corps contre 26 sur une planche ordinaire, la date écrite
        // PLUS GROS que la mention qu'elle accompagne (signalé le 20/08/2026).
        //
        // Il ne va pas pour autant au bord : le tirage est à fond perdu, la machine rogne
        // près d'un millimètre et demi, et une case d'identité rognée est une photo refusée
        // au guichet. On lui laisse donc <see cref="AirEnHautMm"/>, et tout le reste va à
        // la bande.
        var originY = bottomReserve > 0
            ? Math.Max(tickLength, airEnHaut)
            : Math.Max(tickLength, (utile - blocH) / 2);

        if (originY + blocH > sheetHeight) originY = Math.Max(0, sheetHeight - blocH);

        var cases = new List<PixelRect>(identites);
        for (var i = 0; i < identites; i++)
        {
            // colonne par colonne, de haut en bas : la colonne incomplète est donc la
            // DERNIÈRE, et elle se remplit par le haut — c'est le sens de lecture, et
            // celui dans lequel l'opérateur vérifie sa planche
            var colonne = i / rangees;
            var rangee = i % rangees;
            cases.Add(new PixelRect(
                colonne * (cellWidth + gap),
                originY + rangee * (cellHeight + gap),
                cellWidth, cellHeight));
        }

        // Le portrait descend jusqu'au bord : la bande ne court plus sous lui.
        var grande = new PixelRect(blocW + gap, 0, largeurGrande, sheetHeight);

        // Tout ce qui reste sous le bloc d'identités, sur SA largeur : la réserve demandée,
        // plus le jeu que les rangées n'ont pas comblé.
        var basDuBloc = originY + blocH;
        var bande = new PixelRect(0, basDuBloc, blocW, sheetHeight - basDuBloc);

        IReadOnlyList<CutTick> ticks = tickLength > 0
            ? Reperes(sheetWidth, sheetHeight, [.. cases, grande], tickLength)
            : [];

        return new PlancheRentreeResult(cases, grande, ticks, colonnes, rangees, bande);
    }

    /// <summary>
    /// Repères de coupe dans les marges, comme sur une planche ordinaire : pour chaque bord
    /// de case, un trait en haut et en bas, un à gauche et à droite.
    ///
    /// Recopié d'<see cref="IdSheetLayout"/> plutôt que partagé : le sien est privé, et les
    /// deux planches n'ont en commun que l'idée. Douze lignes valent mieux qu'une abstraction
    /// à deux clients.
    /// </summary>
    private static List<CutTick> Reperes(
        int sheetWidth, int sheetHeight, IReadOnlyList<PixelRect> cases, int tickLength)
    {
        var xs = new SortedSet<int>();
        var ys = new SortedSet<int>();
        foreach (var c in cases)
        {
            xs.Add(c.X);
            xs.Add(c.Right);
            ys.Add(c.Y);
            ys.Add(c.Bottom);
        }

        var ticks = new List<CutTick>();
        foreach (var x in xs)
        {
            ticks.Add(new CutTick(x, 0, x, tickLength));
            ticks.Add(new CutTick(x, sheetHeight - tickLength, x, sheetHeight));
        }
        foreach (var y in ys)
        {
            ticks.Add(new CutTick(0, y, tickLength, y));
            ticks.Add(new CutTick(sheetWidth - tickLength, y, sheetWidth, y));
        }
        return ticks;
    }
}
