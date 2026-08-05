namespace Studio.Imaging.Geometry;

/// <summary>
/// La part d'une grille qu'un écran montre, en rangs de tuiles.
/// </summary>
/// <param name="Premier">Rang de la première tuile à fabriquer ; -1 si la grille est vide.</param>
/// <param name="Dernier">Rang de la dernière, comprise ; -1 si la grille est vide.</param>
/// <param name="Colonnes">Tuiles par rangée.</param>
public readonly record struct TrancheVisible(int Premier, int Dernier, int Colonnes)
{
    /// <summary>Nombre de tuiles dans la tranche.</summary>
    public int Compte => Premier < 0 || Dernier < Premier ? 0 : Dernier - Premier + 1;

    /// <summary>La tranche d'une grille sans rien à montrer.</summary>
    public static TrancheVisible Vide(int colonnes) => new(-1, -1, Math.Max(1, colonnes));
}

/// <summary>
/// La géométrie d'une planche de vignettes toutes identiques, qui s'enroulent d'une rangée
/// à l'autre — et surtout, celle des SEULES tuiles qu'un écran donné laisse voir.
///
/// <b>Pourquoi ce calcul existe.</b> La planche de l'écran « Sélectionnez les photos »
/// affichait ses tuiles TOUTES À LA FOIS, un <c>WrapPanel</c> dans un <c>ScrollViewer</c>
/// ne sachant rien virtualiser. Or un dossier peut en compter jusqu'à
/// <c>PhotoScanner.MaxAffichable</c>, soit 1200 : autant de sous-arbres visuels d'une
/// quinzaine d'éléments chacun — dix-huit mille objets — et autant de vignettes décodées
/// tenues en mémoire vive, pour une trentaine de tuiles réellement sous les yeux. Ouvrir une
/// carte pleine figeait l'écran plusieurs secondes et laissait l'application près du gigaoctet.
///
/// Le calcul est ici, séparé de WPF, parce que c'est lui qui décide de tout : le nombre de
/// colonnes, la hauteur à faire défiler, et la tranche à fabriquer. Il se vérifie donc sans
/// interface — voir <c>GrilleVirtuelleTests</c> — et <c>PlancheVirtualisee</c> ne fait plus
/// que poser les tuiles là où il le dit.
/// </summary>
public static class GrilleVirtuelle
{
    /// <summary>
    /// Tuiles par rangée. Jamais zéro : une fenêtre plus étroite qu'une tuile en montre
    /// quand même une, tronquée, plutôt que de faire disparaître la planche.
    /// </summary>
    public static int Colonnes(double largeurVisible, double largeurTuile)
    {
        if (largeurTuile <= 0 || double.IsNaN(largeurVisible) || largeurVisible <= 0) return 1;

        return Math.Max(1, (int)(largeurVisible / largeurTuile));
    }

    /// <summary>Rangées occupées par <paramref name="tuiles"/> tuiles.</summary>
    public static int Rangees(int tuiles, int colonnes)
    {
        if (tuiles <= 0) return 0;

        var parRangee = Math.Max(1, colonnes);
        return (tuiles + parRangee - 1) / parRangee;
    }

    /// <summary>Hauteur totale à faire défiler.</summary>
    public static double Hauteur(int tuiles, int colonnes, double hauteurTuile) =>
        Rangees(tuiles, colonnes) * Math.Max(0, hauteurTuile);

    /// <summary>Coin haut-gauche de la tuile de rang <paramref name="rang"/>.</summary>
    public static (double X, double Y) Position(
        int rang, int colonnes, double largeurTuile, double hauteurTuile)
    {
        var parRangee = Math.Max(1, colonnes);
        return (rang % parRangee * largeurTuile, rang / parRangee * hauteurTuile);
    }

    /// <summary>
    /// Les tuiles à fabriquer pour l'écran tel qu'il est.
    ///
    /// <paramref name="rangeesDeMarge"/> en fabrique quelques-unes au-delà, de part et
    /// d'autre : le défilement à la molette avance d'un cran entier, et sans cette marge la
    /// rangée qui entre serait fabriquée pendant qu'on la regarde arriver — elle apparaîtrait
    /// vide puis se remplirait. Une rangée d'avance suffit à ce que le défilement reste lisse
    /// sans annuler l'économie : trente tuiles vues en coûtent alors une cinquantaine, pas
    /// douze cents.
    /// </summary>
    /// <param name="tuiles">Nombre total de tuiles de la planche.</param>
    /// <param name="largeurVisible">Largeur de la zone d'affichage.</param>
    /// <param name="hauteurVisible">Hauteur de la zone d'affichage.</param>
    /// <param name="largeurTuile">Largeur d'une tuile, marges comprises.</param>
    /// <param name="hauteurTuile">Hauteur d'une tuile, marges comprises.</param>
    /// <param name="decalage">De combien la planche est descendue sous le haut de la zone.</param>
    /// <param name="rangeesDeMarge">Rangées fabriquées en plus, au-dessus et en dessous.</param>
    public static TrancheVisible Tranche(
        int tuiles, double largeurVisible, double hauteurVisible,
        double largeurTuile, double hauteurTuile, double decalage, int rangeesDeMarge = 1)
    {
        var colonnes = Colonnes(largeurVisible, largeurTuile);
        if (tuiles <= 0 || hauteurTuile <= 0) return TrancheVisible.Vide(colonnes);

        var rangees = Rangees(tuiles, colonnes);
        var marge = Math.Max(0, rangeesDeMarge);

        // le décalage peut dépasser ce qui reste à montrer — la fenêtre s'agrandit, ou la
        // planche raccourcit — et un rang négatif ferait fabriquer des tuiles inexistantes
        var haut = Math.Max(0, decalage);

        var premiereRangee = Math.Clamp((int)(haut / hauteurTuile) - marge, 0, rangees - 1);

        var visible = double.IsNaN(hauteurVisible) || hauteurVisible <= 0 ? 0 : hauteurVisible;
        var derniereRangee = Math.Clamp(
            (int)Math.Ceiling((haut + visible) / hauteurTuile) - 1 + marge, premiereRangee, rangees - 1);

        var premier = premiereRangee * colonnes;
        var dernier = Math.Min(tuiles - 1, (derniereRangee + 1) * colonnes - 1);

        return new TrancheVisible(premier, dernier, colonnes);
    }
}
