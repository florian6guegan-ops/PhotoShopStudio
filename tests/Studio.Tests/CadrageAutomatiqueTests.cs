using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le cadrage posé sur le VISAGE plutôt qu'au centre de la photo.
///
/// Ce sont des mesures sur la géométrie, sans détecteur ni image : on vérifie que le cadre
/// va bien là où le visage est, qu'il suit les quarts de tour, et surtout qu'il ne casse
/// rien de ce que <see cref="FramedCrop"/> garantit — le format du tirage et l'absence de
/// bord blanc.
/// </summary>
public class CadrageAutomatiqueTests
{
    /// <summary>Une photo 3:2 couchée, un tirage 10×15 debout : il y a du jeu à gauche et à droite.</summary>
    private static FramedCrop Cadre() => new(3000, 2000, 102, 152);

    /// <summary>
    /// <b>Le jeu n'existe que sur UN axe</b>, et c'est la clé de ces deux essais : une photo
    /// posée pour couvrir le cadre le touche exactement sur un côté, et déborde de l'autre.
    /// Une photo couchée dans un tirage debout ne coulisse donc qu'en largeur ; c'est là, et
    /// seulement là, qu'on peut poser le visage.
    /// </summary>
    [Fact]
    public void Une_photo_couchee_amene_le_visage_au_milieu_en_largeur()
    {
        var cadre = Cadre();

        // un visage au tiers gauche de la photo
        CadrageAutomatique.Poser(cadre, new NormPoint(0.33, 0.50));

        Assert.Equal(cadre.FrameWidth / 2, cadre.X + 0.33 * cadre.Width, 3);
    }

    /// <summary>Une photo debout dans un tirage couché coulisse, elle, en HAUTEUR.</summary>
    [Fact]
    public void Une_photo_debout_pose_le_regard_aux_deux_cinquiemes()
    {
        var cadre = new FramedCrop(2000, 3000, 152, 102);

        CadrageAutomatique.Poser(cadre, new NormPoint(0.50, 0.30));

        var vise = cadre.FrameHeight * CadrageAutomatique.HauteurDuRegard;
        Assert.Equal(vise, cadre.Y + 0.30 * cadre.Height, 3);
    }

    /// <summary>
    /// La règle qui compte : déplacer le cadre ne doit JAMAIS faire apparaître de blanc.
    /// C'est <c>Contraindre</c> qui l'assure, et c'est pour cela qu'on passe par
    /// <c>Move</c> plutôt que d'écrire X et Y.
    /// </summary>
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.02, 0.97)]
    public void Un_visage_au_bord_ne_fait_jamais_sortir_la_photo(double x, double y)
    {
        var cadre = Cadre();

        CadrageAutomatique.Poser(cadre, new NormPoint(x, y));

        // la photo couvre encore le cadre entier, des quatre côtés
        Assert.True(cadre.X <= 1e-9, $"bord gauche découvert : X = {cadre.X}");
        Assert.True(cadre.Y <= 1e-9, $"bord haut découvert : Y = {cadre.Y}");
        Assert.True(cadre.X + cadre.Width >= cadre.FrameWidth - 1e-9, "bord droit découvert");
        Assert.True(cadre.Y + cadre.Height >= cadre.FrameHeight - 1e-9, "bord bas découvert");
    }

    /// <summary>Le cadre ne change ni de taille ni de forme : il ne fait que glisser.</summary>
    [Fact]
    public void Le_format_du_tirage_ne_bouge_pas()
    {
        var cadre = Cadre();
        var (largeur, hauteur) = (cadre.Width, cadre.Height);

        CadrageAutomatique.Poser(cadre, new NormPoint(0.2, 0.8));

        Assert.Equal(largeur, cadre.Width, 9);
        Assert.Equal(hauteur, cadre.Height, 9);
        Assert.Equal(102, cadre.FrameWidth, 9);
        Assert.Equal(152, cadre.FrameHeight, 9);
    }

    /// <summary>Les yeux l'emportent sur le centre de la boîte : voir <c>PointAViser</c>.</summary>
    [Fact]
    public void Le_point_vise_est_le_milieu_des_yeux_quand_on_les_a()
    {
        var boite = new NormRect(0.30, 0.20, 0.40, 0.40);
        var yeux = new[] { new NormPoint(0.42, 0.30), new NormPoint(0.58, 0.34) };

        var point = CadrageAutomatique.PointAViser(boite, yeux);

        Assert.Equal(0.50, point.X, 9);
        Assert.Equal(0.32, point.Y, 9);
    }

    [Fact]
    public void Sans_yeux_on_retombe_sur_le_centre_de_la_boite()
    {
        var boite = new NormRect(0.30, 0.20, 0.40, 0.40);

        var point = CadrageAutomatique.PointAViser(boite, null);

        Assert.Equal(0.50, point.X, 9);
        Assert.Equal(0.40, point.Y, 9);
    }

    /// <summary>
    /// Un seul œil rendu ne fait pas un milieu : on retombe sur la boîte plutôt que de
    /// viser un point qui n'est pas au centre du visage.
    /// </summary>
    [Fact]
    public void Un_seul_oeil_ne_suffit_pas()
    {
        var boite = new NormRect(0.30, 0.20, 0.40, 0.40);

        var point = CadrageAutomatique.PointAViser(boite, [new NormPoint(0.42, 0.30)]);

        Assert.Equal(0.50, point.X, 9);
        Assert.Equal(0.40, point.Y, 9);
    }

    /// <summary>
    /// Un quart de tour horaire envoie (x, y) en (1 − y, x) — le sens du rendu. Sans cela,
    /// le cadre irait chercher le visage à l'autre bout de la photo.
    /// </summary>
    [Theory]
    [InlineData(0, 0.25, 0.10)]
    [InlineData(1, 0.90, 0.25)]
    [InlineData(2, 0.75, 0.90)]
    [InlineData(3, 0.10, 0.75)]
    [InlineData(4, 0.25, 0.10)]   // quatre quarts : on revient au point de départ
    [InlineData(-1, 0.10, 0.75)]  // les quarts négatifs comptent aussi
    public void Le_point_suit_les_quarts_de_tour(int quarts, double x, double y)
    {
        var tourne = CadrageAutomatique.TournerAvecLaPhoto(new NormPoint(0.25, 0.10), quarts);

        Assert.Equal(x, tourne.X, 9);
        Assert.Equal(y, tourne.Y, 9);
    }

    // — le redressement sur la ligne des yeux —

    private static IReadOnlyList<NormPoint> Yeux(double xg, double yg, double xd, double yd) =>
        [new NormPoint(xg, yg), new NormPoint(xd, yd)];

    /// <summary>
    /// Une tête penchée dans le sens horaire — l'œil droit plus bas que le gauche — se
    /// redresse dans l'AUTRE sens. Se tromper de signe doublerait l'inclinaison au lieu de
    /// l'annuler, et cela ne se verrait que sur le tirage.
    /// </summary>
    [Fact]
    public void Une_tete_penchee_se_redresse_dans_lautre_sens()
    {
        // carré, pour que les fractions soient directement des pixels : 100 à droite,
        // 10 vers le bas → environ 5,7° dans le sens horaire
        var angle = CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.50, 0.50, 0.51), 1000, 1000, 0);

        Assert.True(angle < 0, $"attendu négatif (anti-horaire), obtenu {angle}");
        Assert.Equal(-5.71, angle, 1);
    }

    /// <summary>
    /// <b>Le rapport de l'image change l'angle</b>, et c'est le piège de ce calcul : les
    /// points du détecteur sont normalisés. Les MÊMES fractions sur une photo 2:1 couchée
    /// donnent un angle deux fois plus petit que sur un carré — la même différence
    /// verticale couvre deux fois moins de pixels par rapport à l'horizontale.
    ///
    /// Prendre l'angle sur les fractions brutes se tromperait donc systématiquement, et
    /// toujours dans le même sens : les photos les plus allongées seraient les plus
    /// abîmées.
    /// </summary>
    [Fact]
    public void Le_rapport_de_limage_entre_dans_le_calcul()
    {
        // 0,10 en largeur et 0,01 en hauteur : sous la limite de redressement des deux côtés
        var carre = CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.50, 0.50, 0.51), 1000, 1000, 0);

        var couchee = CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.50, 0.50, 0.51), 2000, 1000, 0);

        Assert.Equal(-5.71, carre, 1);    // atan(10 / 100)
        Assert.Equal(-2.86, couchee, 1);  // atan(10 / 200)
    }

    /// <summary>Sans deux yeux, il n'y a pas de ligne : on ne redresse rien.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Sans_deux_yeux_on_ne_redresse_pas(int combien)
    {
        IReadOnlyList<NormPoint> yeux = combien == 0
            ? []
            : [new NormPoint(0.4, 0.5)];

        Assert.Equal(0, CadrageAutomatique.AngleDeRedressement(yeux, 1000, 1000, 0));
    }

    /// <summary>
    /// Un angle trop grand n'est plus une tête penchée : c'est une pose, ou une détection
    /// qui s'est trompée. On ne touche à rien plutôt que de faire grossir la photo.
    /// </summary>
    [Fact]
    public void Un_angle_trop_grand_est_ignore()
    {
        // 45°, très au-delà de la limite
        Assert.Equal(0, CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.40, 0.50, 0.50), 1000, 1000, 0));
    }

    /// <summary>Un angle négligeable ne vaut pas la peine de bouger la photo.</summary>
    [Fact]
    public void Un_angle_negligeable_est_ignore()
    {
        // 1 px de dénivelé sur 100 : 0,57°, sous le seuil
        Assert.Equal(0, CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.500, 0.50, 0.501), 1000, 1000, 0));
    }

    /// <summary>
    /// L'ordre dans lequel le détecteur rend les deux yeux ne doit rien changer : c'est
    /// celui de gauche qui sert d'origine, pas le premier de la liste.
    /// </summary>
    [Fact]
    public void Lordre_des_yeux_ne_change_rien()
    {
        var normal = CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.50, 0.50, 0.55), 1000, 1000, 0);

        var inverse = CadrageAutomatique.AngleDeRedressement(
            Yeux(0.50, 0.55, 0.40, 0.50), 1000, 1000, 0);

        Assert.Equal(normal, inverse, 6);
    }

    /// <summary>
    /// Un quart de tour ne doit pas retourner le redressement : la photo tourne, la tête
    /// avec, et l'angle à corriger garde sa valeur.
    /// </summary>
    [Fact]
    public void Le_redressement_suit_les_quarts_de_tour()
    {
        var droit = CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.50, 0.50, 0.55), 1000, 1000, 0);

        var tourne = CadrageAutomatique.AngleDeRedressement(
            Yeux(0.40, 0.50, 0.50, 0.55), 1000, 1000, 1);

        Assert.Equal(Math.Abs(droit), Math.Abs(tourne), 6);
    }

    /// <summary>
    /// Une photo au rapport exact du tirage n'a aucun jeu : le cadrage automatique n'a
    /// rien à donner, et il ne doit surtout pas forcer.
    /// </summary>
    [Fact]
    public void Sans_jeu_le_cadre_ne_bouge_pas()
    {
        var cadre = new FramedCrop(1020, 1520, 102, 152);
        var (x, y) = (cadre.X, cadre.Y);

        CadrageAutomatique.Poser(cadre, new NormPoint(0.1, 0.9));

        Assert.Equal(x, cadre.X, 6);
        Assert.Equal(y, cadre.Y, 6);
    }
}
