namespace Studio.Imaging.Geometry;

/// <summary>
/// Pose le cadre d'un tirage sur le VISAGE plutôt qu'au centre de la photo.
///
/// Le cadre part toujours centré (voir <see cref="FramedCrop.Reset"/>), ce qui est le bon
/// choix quand on ne sait rien de la photo. Mais sur un portrait pris un peu de travers, ou
/// sur une photo cadrée large, le centre géométrique tombe rarement sur le sujet : le
/// tirage coupe une épaule et laisse la moitié du ciel. L'opérateur rattrapait chaque photo
/// à la main.
///
/// Ce qui est calculé ici ne fait que DÉPLACER la photo derrière le cadre — jamais
/// l'agrandir, jamais changer le format. Tout ce que <see cref="FramedCrop"/> garantit
/// tient donc encore : le cadre reste au rapport du produit, et <c>Contraindre</c> empêche
/// la photo de découvrir un bord.
/// </summary>
public static class CadrageAutomatique
{
    /// <summary>
    /// Hauteur à laquelle on pose le regard, en fraction du cadre.
    ///
    /// Pas 0,5. Un portrait dont les yeux tombent au milieu paraît tassé vers le bas et
    /// laisse un vide au-dessus de la tête — c'est la règle des tiers, que tous les
    /// portraitistes appliquent sans y penser. 0,40 place le regard entre le tiers haut et
    /// le milieu : assez haut pour respirer, assez bas pour ne pas rogner le menton sur un
    /// buste serré.
    /// </summary>
    public const double HauteurDuRegard = 0.40;

    /// <summary>
    /// Le point du visage à viser : le milieu des yeux si le détecteur les a rendus, sinon
    /// le centre de la boîte.
    ///
    /// Les yeux valent mieux que le centre de la boîte : celle-ci englobe le menton et le
    /// front, et son centre descend donc avec une bouche ouverte ou une frange. Le regard,
    /// lui, ne bouge pas — et c'est lui qu'on regarde.
    /// </summary>
    public static NormPoint PointAViser(NormRect boite, IReadOnlyList<NormPoint>? yeux)
    {
        ArgumentNullException.ThrowIfNull(boite);

        if (yeux is { Count: 2 })
            return new NormPoint((yeux[0].X + yeux[1].X) / 2, (yeux[0].Y + yeux[1].Y) / 2);

        return new NormPoint(boite.X + boite.Width / 2, boite.Y + boite.Height / 2);
    }

    /// <summary>
    /// Suit les quarts de tour que l'opérateur a donnés à la photo.
    ///
    /// La détection travaille sur le FICHIER, une fois son EXIF appliqué ; le cadre, lui,
    /// raisonne sur la photo telle qu'on la voit. Entre les deux il peut y avoir un ou
    /// plusieurs quarts de tour, et sans cette conversion le cadre irait chercher le visage
    /// à l'autre bout de l'image.
    ///
    /// Le sens est celui du rendu (<c>ImagePipeline</c> : <c>Rotate(90 × quarts)</c>, donc
    /// horaire) : un point (x, y) se retrouve en (1 − y, x).
    /// </summary>
    public static NormPoint TournerAvecLaPhoto(NormPoint point, int quartsDeTour)
    {
        ArgumentNullException.ThrowIfNull(point);

        var quarts = ((quartsDeTour % 4) + 4) % 4;

        return quarts switch
        {
            1 => new NormPoint(1 - point.Y, point.X),
            2 => new NormPoint(1 - point.X, 1 - point.Y),
            3 => new NormPoint(point.Y, 1 - point.X),
            _ => point,
        };
    }

    /// <summary>
    /// Fait glisser la photo pour amener <paramref name="pointAViser"/> à sa place dans le
    /// cadre : au milieu en largeur, à <see cref="HauteurDuRegard"/> en hauteur.
    ///
    /// <b>Un cadre qui ne peut pas bouger ne bouge pas</b>, et c'est voulu : quand la photo
    /// couvre tout juste le cadre — le cas d'une photo au même rapport que le tirage — il
    /// n'y a aucun jeu, <c>Contraindre</c> la ramène où elle était, et le cadrage
    /// automatique n'a simplement rien à donner.
    /// </summary>
    public static void Poser(FramedCrop cadre, NormPoint pointAViser)
    {
        ArgumentNullException.ThrowIfNull(cadre);
        ArgumentNullException.ThrowIfNull(pointAViser);

        var viseX = cadre.FrameWidth / 2;
        var viseY = cadre.FrameHeight * HauteurDuRegard;

        var actuelX = cadre.X + pointAViser.X * cadre.Width;
        var actuelY = cadre.Y + pointAViser.Y * cadre.Height;

        cadre.Move(viseX - actuelX, viseY - actuelY);
    }
}
