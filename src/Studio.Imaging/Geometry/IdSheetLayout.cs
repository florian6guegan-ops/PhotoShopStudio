namespace Studio.Imaging.Geometry;

/// <summary>Trait de coupe : petit repère dessiné dans les marges de la planche.</summary>
public sealed record CutTick(int X1, int Y1, int X2, int Y2);

public sealed record SheetLayoutResult(
    IReadOnlyList<PixelRect> Cells,
    IReadOnlyList<CutTick> CutTicks,
    int Columns,
    int Rows);

/// <summary>
/// Disposition d'une planche de N photos identiques (identité) sur un tirage.
/// Fonctions pures, unit-testées au pixel.
/// </summary>
public static class IdSheetLayout
{
    /// <summary>
    /// Nombre maximal de cellules tenant sur la planche. Sert à borner le choix
    /// de l'opérateur avant d'appeler <see cref="Layout"/>, qui lèverait au-delà.
    /// Renvoie 0 si la cellule ne tient pas du tout.
    /// </summary>
    /// <param name="bottomReserve">
    /// Hauteur qu'il faut laisser libre en bas — celle de la bande qui porte la date.
    /// <b>Elle manquait ici</b>, et c'est ce qui privait de date les planches aux petites
    /// cases : le compte remplissait la planche jusqu'en bas, <see cref="Layout"/> n'avait
    /// plus où loger la bande, et elle disparaissait sans rien dire. Le format français
    /// (35 × 45) laissait par chance assez de marge ; un passeport étranger de 26 × 32, non.
    ///
    /// La compter ici revient à sortir une rangée de la planche quand il le faut : une photo
    /// de moins vaut mieux qu'une planche sans date, qui n'a plus de valeur pour prouver
    /// qu'elle est récente.
    /// </param>
    public static int MaxCopies(int sheetWidth, int sheetHeight, int cellWidth, int cellHeight,
        int gap, int bottomReserve = 0)
    {
        if (cellWidth <= 0 || cellHeight <= 0 || cellWidth > sheetWidth || cellHeight > sheetHeight)
            return 0;

        var utile = sheetHeight - Math.Max(0, bottomReserve);

        // Une planche trop courte pour porter à la fois une rangée et la bande garde la
        // rangée : la bande est un plus, la photo est le produit.
        if (utile < cellHeight) utile = sheetHeight;

        return (sheetWidth + gap) / (cellWidth + gap) * ((utile + gap) / (cellHeight + gap));
    }

    /// <summary>
    /// La meilleure des deux orientations du papier, et laquelle c'est.
    ///
    /// <b>Le papier n'a pas de sens imposé, les cases si.</b> Un carré de 50 mm ne tient
    /// qu'une rangée sur les 105 mm d'un 10×15 couché dès qu'on garde la place d'écrire la
    /// date — trois photos. Le même papier DEBOUT en tient deux rangées, donc quatre
    /// photos, et la bande y respire. Rien ne justifiait de tirer toujours dans le même
    /// sens : c'est de la géométrie, pas une habitude d'atelier.
    ///
    /// La planche est composée debout puis TOURNÉE avant l'envoi, pour que la machine
    /// reçoive exactement le format qu'elle attend — voir
    /// <c>ImagePipeline.RenderIdSheetToFile</c>. Les photos sortent alors couchées sur le
    /// papier ; une fois découpées, elles sont droites, et c'est tout ce qui compte.
    /// </summary>
    /// <returns>
    /// Le nombre de cases, et <c>Debout</c> à vrai quand il faut tourner le papier pour
    /// l'obtenir. À capacité égale, on garde le sens du papier : l'opérateur massicote
    /// toujours pareil.
    /// </returns>
    public static (int Copies, bool Debout) MeilleureCapacite(
        int sheetWidth, int sheetHeight, int cellWidth, int cellHeight,
        int gap, int bottomReserve = 0)
    {
        var couche = MaxCopies(sheetWidth, sheetHeight, cellWidth, cellHeight, gap, bottomReserve);
        var debout = MaxCopies(sheetHeight, sheetWidth, cellWidth, cellHeight, gap, bottomReserve);

        return debout > couche ? (debout, true) : (couche, false);
    }

    /// <summary>
    /// Calcule la grille : autant de colonnes que possible, lignes nécessaires pour
    /// atteindre <paramref name="copies"/>, bloc centré sur la planche.
    /// Lève une exception si les copies ne tiennent pas.
    /// </summary>
    /// <param name="bottomReserve">
    /// Hauteur laissée libre en bas de la planche, par exemple pour y porter la date. Le
    /// bloc de photos est alors centré sur ce qui reste, donc remonte.
    /// </param>
    public static SheetLayoutResult Layout(
        int sheetWidth, int sheetHeight,
        int cellWidth, int cellHeight,
        int gap, int copies, int tickLength = 0, int bottomReserve = 0)
    {
        if (copies < 1) throw new ArgumentOutOfRangeException(nameof(copies));
        if (cellWidth <= 0 || cellHeight <= 0 || cellWidth > sheetWidth || cellHeight > sheetHeight)
            throw new ArgumentOutOfRangeException(nameof(cellWidth), "Cellule invalide pour la planche");
        if (bottomReserve < 0) throw new ArgumentOutOfRangeException(nameof(bottomReserve));

        var maxCols = (sheetWidth + gap) / (cellWidth + gap);
        var maxRows = (sheetHeight + gap) / (cellHeight + gap);
        if (maxCols * maxRows < copies)
            throw new InvalidOperationException(
                $"{copies} copies de {cellWidth}×{cellHeight}px ne tiennent pas sur {sheetWidth}×{sheetHeight}px " +
                $"(maximum {maxCols * maxRows})");

        var cols = Math.Min(maxCols, copies);
        var rows = (int)Math.Ceiling((double)copies / cols);
        // rééquilibre : préfère une grille compacte (ex 6 copies → 2×3 plutôt que 3×2 selon la place)
        while (rows > maxRows)
        {
            cols++;
            rows = (int)Math.Ceiling((double)copies / cols);
        }

        var blockW = cols * cellWidth + (cols - 1) * gap;
        var blockH = rows * cellHeight + (rows - 1) * gap;
        var originX = (sheetWidth - blockW) / 2;

        // la réserve est prise en bas : le bloc se centre sur la hauteur restante
        var utile = Math.Max(blockH, sheetHeight - bottomReserve);

        // ⚠ LE BLOC SE CENTRE. IL N'EST PLUS POUSSÉ POUR DÉGAGER LES REPÈRES.
        //
        // Il l'était — <c>Math.Max(tickLength, centre)</c> — pour qu'aucune case ne
        // recouvre les traits de coupe du haut. Or ce décalage ne sert à rien quand la
        // planche est LARGE (le centre est déjà plus bas que les repères) et il fait des
        // dégâts quand elle est JUSTE : le bloc descendait sous le bord, le garde-fou
        // ci-dessous le rattrapait en le collant AU BORD BAS, et le massicot de la machine
        // — qui rogne près d'un millimètre et demi par bord sur un tirage à fond perdu —
        // mangeait le bas de la photo.
        //
        // Mesuré le 20/08/2026, commande 20-033 : un 7 × 10 sur un 10×15. Centre à 11 px,
        // repères à 35, bloc posé à 22 px avec ZÉRO en bas — 22 px de marge en haut, rien
        // en bas, alors que la planche en avait 11 à donner de chaque côté. « Les 7x10 sont
        // rognés en partie. »
        //
        // Ce sont donc les REPÈRES qui cèdent : ils se raccourcissent à la marge disponible
        // (voir <see cref="BuildCutTicks"/>). Un repère écourté se pardonne ; un tirage
        // rogné se refait.
        var originY = Math.Max(0, (utile - blockH) / 2);
        if (originY + blockH > sheetHeight) originY = Math.Max(0, sheetHeight - blockH);

        var cells = new List<PixelRect>(copies);
        for (var i = 0; i < copies; i++)
        {
            var col = i % cols;
            var row = i / cols;
            cells.Add(new PixelRect(
                originX + col * (cellWidth + gap),
                originY + row * (cellHeight + gap),
                cellWidth, cellHeight));
        }

        IReadOnlyList<CutTick> ticks = tickLength > 0
            ? BuildCutTicks(sheetWidth, sheetHeight, cells, tickLength)
            : Array.Empty<CutTick>();

        return new SheetLayoutResult(cells, ticks, cols, rows);
    }

    /// <summary>
    /// Repères de coupe dans les marges : pour chaque bord vertical de cellule, deux
    /// ticks en haut et en bas de la planche ; idem horizontalement.
    ///
    /// ⚠ <b>Ils tiennent dans la MARGE, et ne mordent jamais sur une photo.</b> Leur
    /// longueur est ramenée à ce que le bloc laisse libre de son côté — verticalement pour
    /// les traits du haut et du bas, horizontalement pour ceux des flancs. Une planche
    /// juste n'en porte donc plus, et c'est le bon arbitrage : c'est le bloc qui était
    /// poussé jusqu'au bord pour leur faire place, et le massicot de la machine y prenait
    /// un bout de la photo (voir <see cref="Layout"/>).
    /// </summary>
    private static List<CutTick> BuildCutTicks(
        int sheetWidth, int sheetHeight, IReadOnlyList<PixelRect> cells, int tickLength)
    {
        var xs = new SortedSet<int>();
        var ys = new SortedSet<int>();
        foreach (var cell in cells)
        {
            xs.Add(cell.X);
            xs.Add(cell.Right);
            ys.Add(cell.Y);
            ys.Add(cell.Bottom);
        }

        // la marge réellement libre de chaque côté du bloc
        var haut = Math.Min(tickLength, Math.Max(0, Math.Min(ys.Min, sheetHeight - ys.Max)));
        var flanc = Math.Min(tickLength, Math.Max(0, Math.Min(xs.Min, sheetWidth - xs.Max)));

        var ticks = new List<CutTick>();

        if (haut > 0)
            foreach (var x in xs)
            {
                ticks.Add(new CutTick(x, 0, x, haut));
                ticks.Add(new CutTick(x, sheetHeight - haut, x, sheetHeight));
            }

        if (flanc > 0)
            foreach (var y in ys)
            {
                ticks.Add(new CutTick(0, y, flanc, y));
                ticks.Add(new CutTick(sheetWidth - flanc, y, sheetWidth, y));
            }

        return ticks;
    }
}
