namespace Studio.Imaging.Geometry;

/// <summary>
/// Ce que donnera le montage de tirages d'un format sur une feuille.
/// </summary>
/// <param name="Feuille">La feuille retenue — un vrai produit du catalogue, grand format.</param>
/// <param name="ParFeuille">Places sur une feuille. Toujours ≥ 2 : voir <see cref="MontageFeuille"/>.</param>
/// <param name="CelluleTournee">
/// La case est posée en travers sur la feuille : un 24×30 y occupe une empreinte de 30×24.
///
/// ⚠ <b>Cela ne veut PAS dire que la photo est recadrée en paysage.</b> Elle est rendue à
/// son orientation, puis l'image est tournée d'un quart de tour pour être posée. Le tirage
/// découpé retrouve son sens dans la main de l'opérateur.
/// </param>
/// <param name="FeuilleTournee">
/// La feuille est composée dans l'autre sens. L'opérateur ne le voit pas — il reçoit un
/// fichier aux bonnes dimensions — mais c'est souvent ce qui fait tenir une rangée de plus.
/// </param>
public sealed record PlanMontage(
    PaperOption Feuille, int ParFeuille, bool CelluleTournee, bool FeuilleTournee)
{
    /// <summary>Largeur de la feuille dans le sens retenu.</summary>
    public double LargeurMm => FeuilleTournee ? Feuille.HeightMm : Feuille.WidthMm;

    /// <summary>Hauteur de la feuille dans le sens retenu.</summary>
    public double HauteurMm => FeuilleTournee ? Feuille.WidthMm : Feuille.HeightMm;

    /// <summary>Feuilles nécessaires pour <paramref name="tirages"/> tirages.</summary>
    public int Feuilles(int tirages) =>
        tirages < 1 ? 0 : (int)Math.Ceiling((double)tirages / ParFeuille);

    /// <summary>Places qui partiront à la chute sur la dernière feuille.</summary>
    public int PlacesPerdues(int tirages) => Math.Max(0, Feuilles(tirages) * ParFeuille - tirages);
}

/// <summary>
/// Le montage des agrandissements : plusieurs tirages du même format composés sur une seule
/// feuille, que l'opérateur massicote ensuite.
///
/// <b>Pourquoi ça existe.</b> Un agrandissement rendait un fichier par tirage. Deux 24×30
/// donnaient deux feuilles de 40×60, dont la moitié partait à la chute à chaque fois — alors
/// que les deux tiennent exactement sur une seule. Demandé par l'exploitant le 12/08/2026.
///
/// <b>Ce que cette classe ne fait PAS.</b> Elle ne choisit pas la feuille : c'est
/// l'opérateur qui sait quel rouleau est chargé. Elle ne touche pas au prix : monter deux
/// tirages ensemble ne change rien à ce que le client paie, l'économie est celle de la
/// boutique. Voir <c>OrderLine.MontageSheetCode</c>.
///
/// Elle se distingue de <see cref="CustomSheetLayout.Choose"/>, qui, lui, DÉCIDE d'un papier
/// au meilleur prix parce que là c'est le papier qui est facturé. Les deux partagent le
/// comptage des places, rien d'autre.
///
/// Fonctions pures, unit-testées.
/// </summary>
public static class MontageFeuille
{
    /// <summary>
    /// En dessous de deux places, il n'y a pas de montage.
    ///
    /// ⚠ <b>C'est la garde principale de toute la fonctionnalité</b>, et elle est ici plutôt
    /// que répétée chez chaque appelant. Une feuille qui ne porte qu'un tirage ne fait rien
    /// gagner : le rendu composé n'y ajouterait que des traits de coupe, et changerait donc
    /// le tirage de tous les postes qui n'ont rien demandé.
    /// </summary>
    public const int MinimumUtile = 2;

    /// <summary>
    /// Le plan de montage d'un format sur une feuille donnée, ou <c>null</c> s'il n'y a rien
    /// à gagner — feuille trop petite, ou une seule place.
    /// </summary>
    /// <param name="celluleLargeurMm">Largeur du tirage, dans le sens déclaré au catalogue.</param>
    /// <param name="celluleHauteurMm">Hauteur du tirage, dans le sens déclaré au catalogue.</param>
    public static PlanMontage? Pour(PaperOption feuille,
        double celluleLargeurMm, double celluleHauteurMm,
        double gapMm = CustomSheetLayout.DefaultGapMm)
    {
        ArgumentNullException.ThrowIfNull(feuille);
        if (celluleLargeurMm <= 0 || celluleHauteurMm <= 0) return null;

        var (parFeuille, celluleTournee, feuilleTournee) =
            CustomSheetLayout.CapacityDetaillee(feuille, celluleLargeurMm, celluleHauteurMm, gapMm);

        return parFeuille < MinimumUtile
            ? null
            : new PlanMontage(feuille, parFeuille, celluleTournee, feuilleTournee);
    }

    /// <summary>
    /// Les feuilles où le format tient au moins deux fois, <b>la plus petite d'abord</b> :
    /// à nombre de places égal, c'est elle qui gâche le moins de papier.
    /// </summary>
    public static IReadOnlyList<PlanMontage> Candidats(
        IEnumerable<PaperOption> feuilles, double celluleLargeurMm, double celluleHauteurMm,
        double gapMm = CustomSheetLayout.DefaultGapMm)
    {
        ArgumentNullException.ThrowIfNull(feuilles);

        return feuilles
            .Select(f => Pour(f, celluleLargeurMm, celluleHauteurMm, gapMm))
            .OfType<PlanMontage>()
            .OrderBy(p => p.Feuille.AreaMm2)
            .ThenBy(p => p.Feuille.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// L'empreinte d'une case sur la feuille, en pixels de la feuille : la taille du tirage,
    /// couchée si le plan l'a retenu.
    ///
    /// C'est CETTE taille que la grille occupe. La photo, elle, est rendue à son propre sens
    /// et tournée pour s'y poser — voir <see cref="PlanMontage.CelluleTournee"/>.
    /// </summary>
    public static (int Largeur, int Hauteur) EmpreintePixels(PlanMontage plan,
        double celluleLargeurMm, double celluleHauteurMm, int dpi)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var largeur = MmPx.ToPixels(plan.CelluleTournee ? celluleHauteurMm : celluleLargeurMm, dpi);
        var hauteur = MmPx.ToPixels(plan.CelluleTournee ? celluleLargeurMm : celluleHauteurMm, dpi);
        return (largeur, hauteur);
    }
}
