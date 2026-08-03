using Studio.Core.Domain;

namespace Studio.Imaging.Geometry;

/// <summary>Un papier sur lequel une planche personnalisée peut sortir.</summary>
/// <param name="Code">Code du produit correspondant : c'est lui qui portera la ligne de commande.</param>
/// <param name="Name">Nom affiché à l'opérateur (« 13x18 »).</param>
/// <param name="UnitPrice">Prix d'un tirage de ce format — c'est ce que coûte une planche.</param>
/// <param name="PriceTiers">Paliers dégressifs du format, comptés en PLANCHES.</param>
public sealed record PaperOption(
    string Code, string Name, double WidthMm, double HeightMm, int Dpi = 300,
    decimal UnitPrice = 0, IReadOnlyList<PriceTier>? PriceTiers = null)
{
    public double AreaMm2 => WidthMm * HeightMm;

    /// <summary>Ce que coûtent <paramref name="sheets"/> planches de ce papier.</summary>
    public decimal TotalPrice(int sheets) =>
        PriceTier.UnitPriceFor(PriceTiers ?? [], UnitPrice, Math.Max(1, sheets)) * sheets;
}

/// <summary>Ce que le logiciel a retenu pour tirer N photos à une taille choisie.</summary>
/// <param name="Paper">Papier retenu.</param>
/// <param name="Sheets">Nombre de planches à tirer.</param>
/// <param name="PerSheet">Nombre de photos par planche.</param>
/// <param name="CellRotated">
/// La cellule est posée en travers : une photo de 5,5×8 devient une case de 8×5,5 sur la
/// planche. C'est souvent ce qui fait tenir une rangée de plus.
/// </param>
/// <param name="SheetRotated">
/// La planche est tirée dans l'autre sens (un 10×15 sorti en 15×10). L'opérateur ne le voit
/// pas — le pilote oriente la page au tirage — mais ça évite souvent de coucher les
/// cellules, donc de trahir son cadrage.
/// </param>
public sealed record CustomSheetPlan(
    PaperOption Paper, int Sheets, int PerSheet, bool CellRotated, bool SheetRotated = false)
{
    /// <summary>Largeur de la planche, dans le sens retenu.</summary>
    public double SheetWidthMm => SheetRotated ? Paper.HeightMm : Paper.WidthMm;

    /// <summary>Hauteur de la planche, dans le sens retenu.</summary>
    public double SheetHeightMm => SheetRotated ? Paper.WidthMm : Paper.HeightMm;

    /// <summary>Places perdues sur la dernière planche.</summary>
    public int WastedCells(int totalCells) => Sheets * PerSheet - totalCells;
}

/// <summary>
/// Le format « personnalisé » : l'opérateur donne une taille (5,5 × 8 cm) et des quantités,
/// le logiciel cherche le plus petit papier du catalogue où tout tienne.
///
/// <b>Pourquoi ces calculs sont en PIXELS.</b> La disposition finale est faite par
/// <see cref="IdSheetLayout"/>, qui travaille en pixels entiers. Compter les places en
/// millimètres donnerait parfois une case de plus que ce que la planche accepte réellement,
/// et le rendu échouerait après que l'opérateur a annoncé son prix. Les deux comptent donc
/// sur les mêmes nombres.
///
/// Fonctions pures, unit-testées.
/// </summary>
public static class CustomSheetLayout
{
    /// <summary>Espace entre deux photos de la planche, en millimètres.</summary>
    public const double DefaultGapMm = SheetSpec.DefaultGapMm;

    /// <summary>
    /// Nombre de photos par planche, la cellule essayée DANS LES DEUX SENS.
    ///
    /// Une cellule de 5,5×8 sur un 10×15 donne 2 places debout et 4 couchée : ne pas
    /// essayer la rotation reviendrait à doubler la consommation de papier.
    /// </summary>
    /// <param name="rotated">Vrai si le meilleur résultat s'obtient cellule couchée.</param>
    public static int Capacity(int sheetWidth, int sheetHeight, int cellWidth, int cellHeight,
        int gap, out bool rotated) =>
        Capacity(sheetWidth, sheetHeight, cellWidth, cellHeight, gap, out rotated, out _);

    /// <summary>
    /// Nombre de photos par planche, LA CELLULE ET LA PLANCHE essayées chacune dans les
    /// deux sens.
    ///
    /// Essayer aussi de tourner la PLANCHE change tout pour l'opérateur. Du 7 × 10 sur un
    /// 10 × 15 : planche debout, il faut coucher les cellules pour en mettre deux — et le
    /// cadrage portrait posé à l'écran est alors repris en paysage, donc coupé dans le
    /// mauvais sens. Planche couchée, deux cellules portrait tiennent côte à côte : même
    /// rendement, cadrage respecté. Constaté en boutique le 03/08/2026 sur une commande de
    /// 41 photos.
    ///
    /// D'où l'ordre des départages : le rendement d'abord, puis LE SENS DEMANDÉ par
    /// l'opérateur, et seulement ensuite celui de la planche — qu'il ne voit pas, puisque
    /// c'est le pilote qui l'oriente au tirage.
    /// </summary>
    /// <param name="cellRotated">Vrai si la cellule est posée en travers.</param>
    /// <param name="sheetRotated">Vrai si la planche est tirée dans l'autre sens.</param>
    public static int Capacity(int sheetWidth, int sheetHeight, int cellWidth, int cellHeight,
        int gap, out bool cellRotated, out bool sheetRotated)
    {
        (bool Cellule, bool Planche, int Places)[] combinaisons =
        [
            (false, false, IdSheetLayout.MaxCopies(sheetWidth, sheetHeight, cellWidth, cellHeight, gap)),
            (false, true, IdSheetLayout.MaxCopies(sheetHeight, sheetWidth, cellWidth, cellHeight, gap)),
            (true, false, IdSheetLayout.MaxCopies(sheetWidth, sheetHeight, cellHeight, cellWidth, gap)),
            (true, true, IdSheetLayout.MaxCopies(sheetHeight, sheetWidth, cellHeight, cellWidth, gap)),
        ];

        var meilleure = combinaisons
            .OrderByDescending(c => c.Places)
            .ThenBy(c => c.Cellule)   // faux avant vrai : le sens saisi l'emporte
            .ThenBy(c => c.Planche)
            .First();

        cellRotated = meilleure.Cellule;
        sheetRotated = meilleure.Planche;
        return meilleure.Places;
    }

    /// <summary>
    /// Le papier à retenir pour <paramref name="totalCells"/> photos à la taille demandée.
    ///
    /// <b>La règle est le PRIX, pas la surface de papier.</b> C'est ce que l'exploitant a
    /// demandé le 02/08/2026, et ses trois exemples le disent mieux qu'une définition, pour
    /// des photos de 5,5 × 8 cm :
    ///
    /// <list type="bullet">
    /// <item>1 photo → un 8×10 (0,60 €), et non un 10×15 qui coûte pareil mais gâche plus ;</item>
    /// <item>2 photos → un seul 10×15 (0,60 €), et non deux 8×10 (1,20 €) ;</item>
    /// <item>4 photos → DEUX 10×15 (1,20 €), et non un 13×18 (1,50 €) — bien que le 13×18
    /// tienne en une seule planche et consomme moins de papier.</item>
    /// </list>
    ///
    /// Le troisième cas est celui qui condamne l'ancienne règle : elle privilégiait la
    /// planche unique, donc le 13×18, donc 30 centimes de trop à chaque commande. Le magasin
    /// ne paie pas la surface, il paie le tirage — et deux petits tirages coûtent souvent
    /// moins qu'un grand.
    ///
    /// Départages, dans l'ordre : prix le plus bas, puis le moins de planches à couper, puis
    /// le plus petit papier.
    /// </summary>
    /// <param name="forcedPaperCode">
    /// Papier imposé par l'opérateur. Il l'emporte sur tout le reste : c'est lui qui sait
    /// quel rouleau est chargé et ce qu'il veut vendre. Null = choix automatique.
    /// </param>
    /// <returns>Le plan retenu, ou null si la photo ne tient sur AUCUN papier proposé.</returns>
    public static CustomSheetPlan? Choose(int totalCells, double cellWidthMm, double cellHeightMm,
        IReadOnlyList<PaperOption> papers, double gapMm = DefaultGapMm, string? forcedPaperCode = null)
    {
        ArgumentNullException.ThrowIfNull(papers);
        if (totalCells < 1)
            throw new ArgumentOutOfRangeException(nameof(totalCells), "Il faut au moins une photo.");
        if (cellWidthMm <= 0 || cellHeightMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(cellWidthMm), "La taille demandée doit être positive.");

        var candidats = string.IsNullOrWhiteSpace(forcedPaperCode)
            ? papers
            : papers.Where(p => p.Code.Equals(forcedPaperCode, StringComparison.OrdinalIgnoreCase)).ToList();

        var possibles = new List<CustomSheetPlan>();

        foreach (var papier in candidats)
        {
            var (parPlanche, cellule, planche) =
                CapacityDetaillee(papier, cellWidthMm, cellHeightMm, gapMm);
            if (parPlanche < 1) continue; // la photo ne tient pas sur ce papier

            var planches = (int)Math.Ceiling((double)totalCells / parPlanche);
            possibles.Add(new CustomSheetPlan(papier, planches, parPlanche, cellule, planche));
        }

        if (possibles.Count == 0) return null;

        // un papier à 0,00 € l'emporterait toujours : sans prix connu, on retombe sur la
        // surface consommée, faute de mieux
        if (possibles.All(p => p.Paper.UnitPrice <= 0))
            return possibles
                .OrderBy(p => p.Sheets * p.Paper.AreaMm2)
                .ThenBy(p => p.Sheets)
                .ThenBy(p => p.Paper.AreaMm2)
                .First();

        return possibles
            .Where(p => p.Paper.UnitPrice > 0)
            .OrderBy(p => p.Paper.TotalPrice(p.Sheets))
            .ThenBy(p => p.Sheets)
            .ThenBy(p => p.Paper.AreaMm2)
            .First();
    }

    /// <summary>Capacité d'un papier donné, en pixels de ce papier.</summary>
    public static (int PerSheet, bool Rotated) CapacityOf(PaperOption paper,
        double cellWidthMm, double cellHeightMm, double gapMm = DefaultGapMm)
    {
        var (places, cellule, _) = CapacityDetaillee(paper, cellWidthMm, cellHeightMm, gapMm);
        return (places, cellule);
    }

    /// <summary>
    /// Capacité d'un papier, en disant AUSSI dans quel sens la planche est tirée.
    /// Voir <see cref="Capacity(int,int,int,int,int,out bool,out bool)"/>.
    /// </summary>
    public static (int PerSheet, bool CellRotated, bool SheetRotated) CapacityDetaillee(
        PaperOption paper, double cellWidthMm, double cellHeightMm, double gapMm = DefaultGapMm)
    {
        ArgumentNullException.ThrowIfNull(paper);

        var parPlanche = Capacity(
            MmPx.ToPixels(paper.WidthMm, paper.Dpi),
            MmPx.ToPixels(paper.HeightMm, paper.Dpi),
            MmPx.ToPixels(cellWidthMm, paper.Dpi),
            MmPx.ToPixels(cellHeightMm, paper.Dpi),
            MmPx.ToPixels(gapMm, paper.Dpi),
            out var cellule, out var planche);

        return (parPlanche, cellule, planche);
    }

    /// <summary>
    /// La cellule en pixels, dans le sens retenu par le plan. À utiliser telle quelle pour
    /// bâtir les rendus : c'est elle qui a servi à compter les places.
    /// </summary>
    public static (int Width, int Height) CellPixels(CustomSheetPlan plan,
        double cellWidthMm, double cellHeightMm)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var largeur = MmPx.ToPixels(plan.CellRotated ? cellHeightMm : cellWidthMm, plan.Paper.Dpi);
        var hauteur = MmPx.ToPixels(plan.CellRotated ? cellWidthMm : cellHeightMm, plan.Paper.Dpi);
        return (largeur, hauteur);
    }

    /// <summary>
    /// Répartition des photos sur les planches : combien de cases pour chacune.
    /// Les exemplaires d'une même photo restent groupés, et le débordement passe à la
    /// planche suivante.
    /// </summary>
    /// <param name="quantities">Quantité demandée pour chaque photo, dans l'ordre d'affichage.</param>
    /// <returns>Une entrée par planche ; chaque entrée donne, par photo, le nombre de cases.</returns>
    public static IReadOnlyList<IReadOnlyList<(int PhotoIndex, int Copies)>> Distribute(
        IReadOnlyList<int> quantities, int perSheet)
    {
        ArgumentNullException.ThrowIfNull(quantities);
        if (perSheet < 1) throw new ArgumentOutOfRangeException(nameof(perSheet));

        var planches = new List<IReadOnlyList<(int, int)>>();
        var courante = new List<(int, int)>();
        var place = perSheet;

        for (var i = 0; i < quantities.Count; i++)
        {
            var reste = quantities[i];
            while (reste > 0)
            {
                var pose = Math.Min(reste, place);
                if (pose > 0)
                {
                    courante.Add((i, pose));
                    reste -= pose;
                    place -= pose;
                }

                if (place != 0) continue;

                planches.Add(courante);
                courante = new List<(int, int)>();
                place = perSheet;
            }
        }

        if (courante.Count > 0) planches.Add(courante);
        return planches;
    }
}
