namespace Studio.Imaging.Geometry;

/// <summary>
/// Disposition d'une planche d'index : les vignettes de N photos DIFFÉRENTES sur un
/// tirage, chacune sous son numéro.
///
/// C'est l'inverse de <see cref="IdSheetLayout"/>, qui répète la même photo dans une
/// cellule de taille imposée. Ici la taille de cellule n'est pas donnée : elle se DÉDUIT
/// du nombre de photos, et c'est tout l'enjeu — on veut la plus grande vignette possible,
/// et le moins de planches possible.
///
/// <b>Pourquoi ce calcul.</b> La planche d'index de DiLand travaille sur une grille fixe
/// (relevé le 01/08/2026 : 24 vignettes par planche, quel que soit le nombre de photos).
/// Vingt-sept photos y sortaient donc sur DEUX tirages 10×15, le second portant trois
/// vignettes et 90 % de blanc. Ici la grille suit le nombre de photos : vingt-sept tiennent
/// sur une seule planche, et le papier ne part pas à la poubelle.
///
/// Fonctions pures, vérifiables au pixel sans rien rendre.
/// </summary>
public static class IndexSheetLayout
{
    /// <summary>
    /// Proportions retenues à défaut : le 3:2 des appareils photo, couché.
    ///
    /// L'appelant a intérêt à passer les proportions RÉELLES de la planche — la médiane des
    /// photos suffit. Une cellule taillée pour du couché laisse un bandeau vide sous chaque
    /// photo si elles sont debout, et réciproquement : sur vingt-sept vignettes, cela se
    /// paie en taille.
    /// </summary>
    public const double RapportParDefaut = 3.0 / 2.0;

    /// <param name="Cells">Cellules d'UNE planche, dans l'ordre de lecture. Chaque cellule
    /// contient la vignette PUIS son numéro, celui-ci occupant le bas.</param>
    /// <param name="PerPage">Photos par planche ; la dernière peut en porter moins.</param>
    /// <param name="Pages">Nombre de planches.</param>
    public sealed record Plan(
        IReadOnlyList<PixelRect> Cells,
        int Columns,
        int Rows,
        int PerPage,
        int Pages);

    /// <summary>
    /// Calcule la disposition.
    /// </summary>
    /// <param name="sheetWidth">Largeur du tirage, en pixels.</param>
    /// <param name="sheetHeight">Hauteur du tirage, en pixels.</param>
    /// <param name="photoCount">Nombre de photos à indexer.</param>
    /// <param name="margin">Marge autour de la planche.</param>
    /// <param name="gap">Espace entre deux cellules.</param>
    /// <param name="headerHeight">Bande réservée au titre, en haut.</param>
    /// <param name="footerHeight">Bande réservée à la date et à la pagination, en bas.</param>
    /// <param name="labelHeight">Hauteur du numéro, sous chaque vignette.</param>
    /// <param name="minThumbWidth">
    /// Largeur minimale d'une vignette. En dessous, on ne reconnaît plus la photo et la
    /// planche ne sert plus à rien : on préfère alors une seconde planche. C'est ce seuil,
    /// et lui seul, qui décide de passer à deux.
    /// </param>
    /// <param name="referenceAspect">
    /// Proportions typiques des photos (largeur/hauteur). La grille est taillée pour
    /// elles : c'est ce qui évite de laisser un bandeau vide dans chaque cellule.
    /// </param>
    public static Plan Compute(
        int sheetWidth, int sheetHeight, int photoCount,
        int margin, int gap, int headerHeight, int footerHeight, int labelHeight,
        int minThumbWidth, double referenceAspect = RapportParDefaut)
    {
        if (referenceAspect <= 0) throw new ArgumentOutOfRangeException(nameof(referenceAspect));
        if (photoCount < 1) throw new ArgumentOutOfRangeException(nameof(photoCount));
        if (margin < 0 || gap < 0) throw new ArgumentOutOfRangeException(nameof(margin));

        var largeurUtile = sheetWidth - 2 * margin;
        var hauteurUtile = sheetHeight - 2 * margin - headerHeight - footerHeight;

        if (largeurUtile <= 0 || hauteurUtile <= 0)
            throw new ArgumentOutOfRangeException(nameof(sheetWidth),
                $"Rien ne tient sur {sheetWidth}×{sheetHeight}px une fois marges et bandeaux retirés.");

        var parPlanche = Capacite(
            largeurUtile, hauteurUtile, gap, labelHeight, minThumbWidth, photoCount, referenceAspect);

        var planches = (photoCount + parPlanche - 1) / parPlanche;

        // Rééquilibrage : on répartit également plutôt que de remplir puis laisser un fond
        // de planche. Vingt-huit photos à vingt-quatre par planche font deux planches de
        // quatorze, et non une pleine suivie d'une planche à quatre vignettes.
        parPlanche = (photoCount + planches - 1) / planches;

        var (colonnes, lignes, cellW, cellH, _) = MeilleureGrille(
            largeurUtile, hauteurUtile, gap, labelHeight, parPlanche, minThumbWidth, referenceAspect);

        var blocLargeur = colonnes * cellW + (colonnes - 1) * gap;
        var blocHauteur = lignes * cellH + (lignes - 1) * gap;

        var gauche = margin + (largeurUtile - blocLargeur) / 2;
        var haut = margin + headerHeight + (hauteurUtile - blocHauteur) / 2;

        var cellules = new List<PixelRect>(parPlanche);
        for (var i = 0; i < parPlanche; i++)
        {
            var colonne = i % colonnes;
            var ligne = i / colonnes;
            cellules.Add(new PixelRect(
                gauche + colonne * (cellW + gap),
                haut + ligne * (cellH + gap),
                cellW, cellH));
        }

        return new Plan(cellules, colonnes, lignes, parPlanche, planches);
    }

    /// <summary>
    /// Combien de photos tiennent sur une planche sans que la vignette devienne trop
    /// petite. Jamais moins d'une : une planche qui ne porterait rien n'aurait pas de sens.
    /// </summary>
    private static int Capacite(
        int largeurUtile, int hauteurUtile, int gap, int labelHeight,
        int minThumbWidth, int maximum, double rapport)
    {
        var tenable = 1;

        for (var n = 1; n <= maximum; n++)
        {
            var grille = MeilleureGrille(
                largeurUtile, hauteurUtile, gap, labelHeight, n, minThumbWidth, rapport);
            if (!grille.Lisible) break;
            tenable = n;
        }

        return tenable;
    }

    /// <summary>
    /// La grille qui donne les plus grandes vignettes pour ce nombre de photos, parmi
    /// celles qui restent lisibles.
    ///
    /// On essaie toutes les largeurs de grille : c'est au plus quelques dizaines de cas, et
    /// cela évite d'avoir à deviner une formule qui se tromperait sur les planches presque
    /// carrées. La contrainte de lisibilité est examinée grille par grille, et non sur la
    /// seule meilleure : sur un tirage couché, une grille large peut rester lisible là où
    /// la plus « grande » ne l'est plus.
    /// </summary>
    private static (int Colonnes, int Lignes, int CellW, int CellH, bool Lisible) MeilleureGrille(
        int largeurUtile, int hauteurUtile, int gap, int labelHeight, int nombre, int minCote,
        double rapport)
    {
        (int Colonnes, int Lignes, int CellW, int CellH) meilleure = (nombre, 1, 1, 1);
        var meilleurScore = -1.0;
        var lisible = false;

        // à défaut de grille lisible, on retient tout de même la plus grande : mieux vaut
        // une planche serrée qu'une exception au comptoir
        (int Colonnes, int Lignes, int CellW, int CellH) repli = (nombre, 1, 1, 1);
        var scoreRepli = -1.0;

        for (var colonnes = 1; colonnes <= nombre; colonnes++)
        {
            var lignes = (nombre + colonnes - 1) / colonnes;

            var cellW = (largeurUtile - (colonnes - 1) * gap) / colonnes;
            var cellH = (hauteurUtile - (lignes - 1) * gap) / lignes;
            var vignetteH = cellH - labelHeight;

            if (cellW < 1 || vignetteH < 1) continue;

            var score = AireVignette(cellW, vignetteH, rapport);

            if (score > scoreRepli)
            {
                scoreRepli = score;
                repli = (colonnes, lignes, cellW, cellH);
            }

            // Lisibilité mesurée sur le CÔTÉ de la place offerte, et non sur la largeur de
            // la vignette : un portrait est étroit par nature, et le juger à sa largeur
            // ferait ouvrir une seconde planche pour rien. Ce qui compte est que la photo,
            // quel que soit son sens, tienne dans un carré d'au moins cette taille.
            if (Math.Min(cellW, vignetteH) < minCote) continue;

            if (score <= meilleurScore) continue;

            meilleurScore = score;
            meilleure = (colonnes, lignes, cellW, cellH);
            lisible = true;
        }

        return lisible
            ? (meilleure.Colonnes, meilleure.Lignes, meilleure.CellW, meilleure.CellH, true)
            : (repli.Colonnes, repli.Lignes, repli.CellW, repli.CellH, false);
    }

    /// <summary>Aire qu'occuperait une photo de référence posée dans cette cellule.</summary>
    private static double AireVignette(int cellW, int vignetteH, double rapport)
    {
        var (largeur, hauteur) = Ajustee(cellW, vignetteH, rapport);
        return largeur * hauteur;
    }

    /// <summary>Une photo de ce rapport, posée au plus grand dans la cellule.</summary>
    private static (double L, double H) Ajustee(int cellW, int vignetteH, double rapport)
    {
        var largeur = Math.Min(cellW, vignetteH * rapport);
        return (largeur, largeur / rapport);
    }

    /// <summary>
    /// Où poser une photo de ces proportions dans sa cellule : centrée, entière, et
    /// au-dessus de la place réservée à son numéro.
    /// </summary>
    public static PixelRect PlaceVignette(PixelRect cellule, int labelHeight, double rapport)
    {
        if (rapport <= 0) throw new ArgumentOutOfRangeException(nameof(rapport));

        var hauteurDisponible = Math.Max(1, cellule.Height - labelHeight);

        var largeur = (int)Math.Round(Math.Min(cellule.Width, hauteurDisponible * rapport));
        var hauteur = (int)Math.Round(largeur / rapport);

        largeur = Math.Clamp(largeur, 1, cellule.Width);
        hauteur = Math.Clamp(hauteur, 1, hauteurDisponible);

        return new PixelRect(
            cellule.X + (cellule.Width - largeur) / 2,
            cellule.Y + (hauteurDisponible - hauteur) / 2,
            largeur, hauteur);
    }
}
