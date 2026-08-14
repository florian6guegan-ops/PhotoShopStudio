using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le sens de cadrage saisi par l'opérateur doit survivre au calcul de disposition.
///
/// Constaté en boutique le 03/08/2026 sur une commande de 41 photos en 7 × 10 : la planche
/// restait debout, les cellules étaient couchées pour en tenir deux, et le cadrage portrait
/// posé à l'écran ressortait en paysage — « les photos sont coupées dans le mauvais sens ».
/// Il suffisait de tourner la PLANCHE : deux cellules portrait tiennent alors côte à côte,
/// même rendement.
/// </summary>
public class CustomSheetOrientationTests
{
    private static PaperOption Dix15 => new("10x15", "10x15", 102, 152);

    /// <summary>
    /// LE CAS DE CRÉTEIL, commande 14-018 du 14/08/2026 : deux portraits repris couchés.
    ///
    /// L'opérateur voulait des photos de 6,5 × 8 cm et a saisi « 8 × 6,5 » — les deux mêmes
    /// nombres, dans l'autre ordre. Studio a composé deux cellules de 80 × 65 mm, mesurées
    /// sur la planche : le cadrage portrait qu'il avait posé s'est retrouvé coupé en haut et
    /// en bas.
    ///
    /// <b>Et il n'y gagnait RIEN</b> : les deux sens donnent deux photos par planche. C'est
    /// ce que cet essai fixe — la mauvaise saisie ne coûtait pas du papier, elle coûtait le
    /// cadrage, et l'écran n'en disait rien. D'où le choix « Debout / Couchée » ajouté à
    /// l'écran du format, et l'avertissement quand la cellule doit tourner.
    /// </summary>
    [Fact]
    public void HuitSurSixVirguleCinq_EtSonInverse_DonnentLeMemeRendement()
    {
        var couche = CustomSheetLayout.CapacityDetaillee(Dix15, 80, 65);
        var debout = CustomSheetLayout.CapacityDetaillee(Dix15, 65, 80);

        Assert.Equal(2, couche.PerSheet);
        Assert.Equal(2, debout.PerSheet);

        // dans les DEUX sens, le cadrage saisi est conservé : c'est la planche qui s'adapte
        Assert.False(couche.CellRotated, "8 × 6,5 doit rester couché");
        Assert.False(debout.CellRotated, "6,5 × 8 doit rester debout");
    }

    /// <summary>Le cas de la boutique : 7 × 10 sur 10 × 15.</summary>
    [Fact]
    public void SeptDix_SurDixQuinze_GardeLeCadragePortrait()
    {
        var (parPlanche, cellulePivotee, plancheTournee) =
            CustomSheetLayout.CapacityDetaillee(Dix15, 70, 100);

        Assert.Equal(2, parPlanche);
        Assert.False(cellulePivotee, "le cadrage demandé doit être conservé");
        Assert.True(plancheTournee, "c'est la planche qui tourne, pas la photo");
    }

    /// <summary>
    /// À rendement égal, on garde TOUJOURS le sens demandé. C'est la règle qui protège le
    /// cadrage de l'opérateur.
    /// </summary>
    [Theory]
    [InlineData(70, 100)]   // portrait
    [InlineData(100, 70)]   // paysage
    [InlineData(55, 80)]
    [InlineData(80, 55)]
    public void ARendementEgal_LeSensDemandeLEmporte(double largeurMm, double hauteurMm)
    {
        var (places, cellulePivotee, plancheTournee) =
            CustomSheetLayout.CapacityDetaillee(Dix15, largeurMm, hauteurMm);

        Assert.True(places >= 1);
        if (!cellulePivotee) return;   // le sens demandé a été gardé : rien à vérifier

        // la cellule n'a été couchée que si ça rapporte VRAIMENT des places
        var sansPivot = Math.Max(
            PlacesAvec(Dix15, largeurMm, hauteurMm, tournerLaPlanche: false),
            PlacesAvec(Dix15, largeurMm, hauteurMm, tournerLaPlanche: true));

        Assert.True(places > sansPivot,
            $"cellule couchée sans gain : {places} places contre {sansPivot} en gardant le sens " +
            $"(planche tournée = {plancheTournee})");
    }

    /// <summary>Places obtenues en gardant le sens de la cellule, planche au choix.</summary>
    private static int PlacesAvec(PaperOption papier, double largeurMm, double hauteurMm,
        bool tournerLaPlanche)
    {
        var sw = MmPx.ToPixels(tournerLaPlanche ? papier.HeightMm : papier.WidthMm, papier.Dpi);
        var sh = MmPx.ToPixels(tournerLaPlanche ? papier.WidthMm : papier.HeightMm, papier.Dpi);

        return IdSheetLayout.MaxCopies(sw, sh,
            MmPx.ToPixels(largeurMm, papier.Dpi),
            MmPx.ToPixels(hauteurMm, papier.Dpi),
            MmPx.ToPixels(CustomSheetLayout.DefaultGapMm, papier.Dpi));
    }

    /// <summary>
    /// Tourner la planche ne doit jamais faire PERDRE de places : le rendement passe avant
    /// le confort de cadrage, sinon on gâche du papier à chaque commande.
    /// </summary>
    [Theory]
    [InlineData(70, 100)]
    [InlineData(55, 80)]
    [InlineData(90, 130)]
    [InlineData(40, 60)]
    public void LeRendementResteLeCritereNumeroUn(double largeurMm, double hauteurMm)
    {
        var (places, _, _) = CustomSheetLayout.CapacityDetaillee(Dix15, largeurMm, hauteurMm);

        var meilleurPossible = new[]
        {
            PlacesAvec(Dix15, largeurMm, hauteurMm, false),
            PlacesAvec(Dix15, largeurMm, hauteurMm, true),
            PlacesAvec(Dix15, hauteurMm, largeurMm, false),
            PlacesAvec(Dix15, hauteurMm, largeurMm, true),
        }.Max();

        Assert.Equal(meilleurPossible, places);
    }

    /// <summary>La planche retenue porte les cotes du sens retenu, pas celles du papier.</summary>
    [Fact]
    public void LePlanDonneLesCotesDuSensRetenu()
    {
        var debout = new CustomSheetPlan(Dix15, 1, 1, CellRotated: false, SheetRotated: false);
        Assert.Equal(102, debout.SheetWidthMm);
        Assert.Equal(152, debout.SheetHeightMm);

        var couchee = new CustomSheetPlan(Dix15, 1, 1, CellRotated: false, SheetRotated: true);
        Assert.Equal(152, couchee.SheetWidthMm);
        Assert.Equal(102, couchee.SheetHeightMm);
    }
}
