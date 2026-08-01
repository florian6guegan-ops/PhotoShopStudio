using Studio.Core.Domain;

namespace Studio.Imaging.Geometry;

/// <summary>
/// Le recadrage tel que DiLand le fait : <b>le cadre est fixe, la photo bouge derrière</b>.
///
/// C'est l'inverse de l'intuition, et c'est ce qui fait sa solidité. Le cadre EST le
/// format du tirage : il ne bouge jamais, ne change jamais de proportions. Le format est
/// donc respecté par construction — il n'y a aucun rapport à recalculer à chaque geste,
/// donc rien qui puisse dériver. C'est la photo qui coulisse et grandit derrière une
/// fenêtre immobile.
///
/// Relevé dans <c>FitEng.Base.Controls.FramedImageCropControl</c> (FitEng.Base.dll),
/// décompilé le 01/08/2026 : <c>fr</c> est le cadre, <c>img</c> la photo déplacée par
/// <c>Canvas.SetLeft/SetTop</c> et redimensionnée par <c>img.Width/Height</c>.
///
/// Tout se compte ici en « unités de cadre » : le cadre occupe (0,0)–(FrameWidth,
/// FrameHeight), et la photo est un rectangle placé dans ce même repère. La conversion
/// vers le <see cref="CropSpec"/> attendu par le rendu se fait tout à la fin.
/// </summary>
public sealed class FramedCrop
{
    private readonly int _imagePixelWidth;
    private readonly int _imagePixelHeight;

    /// <param name="imagePixelWidth">Largeur du fichier, une fois orienté.</param>
    /// <param name="imagePixelHeight">Hauteur du fichier, une fois orienté.</param>
    /// <param name="frameWidth">Largeur du cadre — le format du tirage.</param>
    /// <param name="frameHeight">Hauteur du cadre.</param>
    public FramedCrop(int imagePixelWidth, int imagePixelHeight, double frameWidth, double frameHeight)
    {
        if (imagePixelWidth <= 0 || imagePixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(imagePixelWidth), "Image sans dimensions.");
        if (frameWidth <= 0 || frameHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameWidth), "Cadre sans dimensions.");

        _imagePixelWidth = imagePixelWidth;
        _imagePixelHeight = imagePixelHeight;
        FrameWidth = frameWidth;
        FrameHeight = frameHeight;

        Reset();
    }

    /// <summary>Le cadre, immobile. C'est le format du tirage.</summary>
    public double FrameWidth { get; }
    public double FrameHeight { get; }

    /// <summary>La photo derrière le cadre, dans le repère du cadre.</summary>
    public double X { get; private set; }
    public double Y { get; private set; }
    public double Width { get; private set; }
    public double Height { get; private set; }

    /// <summary>
    /// Autorise la photo à laisser du vide dans le cadre — c'est le mode « photo entière »,
    /// où le tirage sort avec des marges blanches plutôt que recoupé. Faux par défaut,
    /// comme chez DiLand : une bande blanche IMPRÉVUE est un tirage perdu.
    ///
    /// À poser avant <see cref="Reset"/>, qui n'en tire pas la même taille de départ. Le
    /// constructeur appelle <c>Reset</c> avant que ce drapeau ne soit posé : un cadre créé
    /// pour ce mode doit donc être réinitialisé une fois de plus.
    /// </summary>
    public bool AllowsWhiteMargins { get; set; }

    private double ImageAspect => _imagePixelWidth / (double)_imagePixelHeight;

    private double _rotationDegrees;

    /// <summary>
    /// Redressement de la photo, en degrés — le « Tilt » de DiLand.
    ///
    /// Le cadre doit en tenir compte : une photo inclinée n'offre plus le même rectangle
    /// utile, ses coins deviennent vides. Sans cette contrainte, le cadre peut se
    /// retrouver à cheval sur du blanc, et le tirage sort avec des angles blancs sans que
    /// rien à l'écran ne l'ait annoncé.
    /// </summary>
    public double RotationDegrees
    {
        get => _rotationDegrees;
        set
        {
            _rotationDegrees = value;
            Contraindre(); // la photo doit peut-être grandir pour couvrir le cadre incliné
        }
    }

    /// <summary>
    /// L'encombrement que le cadre réclame à la photo, une fois le redressement pris en
    /// compte.
    ///
    /// Le raisonnement tient en une bascule : « la photo tournée de θ couvre le cadre »
    /// équivaut à « le cadre tourné de −θ tient dans la photo droite ». Or la photo est
    /// un rectangle droit : il suffit donc qu'elle couvre la boîte englobante du cadre
    /// incliné.
    ///
    /// La photo tourne autour du CENTRE DU CADRE — c'est la convention de ce modèle, et
    /// c'est elle qui rend la bascule exacte : le cadre tourné autour de son propre
    /// centre y reste centré, sa boîte englobante aussi. Le rendu doit tourner la photo
    /// au même endroit, ce dont <see cref="Canevas"/> se charge.
    /// </summary>
    private (double Width, double Height) EncombrementRequis()
    {
        var radians = _rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        return (FrameWidth * cos + FrameHeight * sin,
                FrameWidth * sin + FrameHeight * cos);
    }

    /// <summary>
    /// Le canevas que le redressement produit : la photo tournée, coins vides compris.
    ///
    /// C'est LUI que le rendu recadre. <c>ImagePipeline</c> tourne l'image, puis applique
    /// les fractions du <see cref="CropSpec"/> sur le résultat ; DiLand fait pareil, en
    /// passant à son <c>FramedImageCropControl</c> une image déjà redressée. Les
    /// fractions doivent donc se compter ici, sur le canevas, et non sur la photo droite
    /// — sinon le tirage sort décalé de tout ce que la rotation a ajouté, et va chercher
    /// des coins vides que l'écran ne montrait pas.
    /// </summary>
    private (double Left, double Top, double Width, double Height) Canevas()
    {
        var radians = _rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        var largeur = Width * cos + Height * sin;
        var hauteur = Width * sin + Height * cos;

        // la photo tourne autour du centre du cadre : le centre du canevas est donc le
        // centre de la photo, tourné du même angle autour du centre du cadre
        var (cx, cy) = AutourDuCadre(X + Width / 2, Y + Height / 2, radians);

        return (cx - largeur / 2, cy - hauteur / 2, largeur, hauteur);
    }

    /// <summary>Tourne un point autour du centre du cadre, dans le sens des aiguilles.</summary>
    private (double X, double Y) AutourDuCadre(double x, double y, double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        var dx = x - FrameWidth / 2;
        var dy = y - FrameHeight / 2;

        return (FrameWidth / 2 + dx * cos - dy * sin,
                FrameHeight / 2 + dx * sin + dy * cos);
    }

    /// <summary>
    /// Replace la photo, centrée dans le cadre. C'est le cadrage de départ, et celui sur
    /// lequel « Réinitialiser » revient.
    ///
    /// Deux tailles possibles, selon le mode du produit :
    /// « remplir le format », la plus petite qui COUVRE le cadre — rien de blanc, mais on
    /// coupe ; « photo entière », la plus grande qui TIENNE dedans — rien de coupé, mais
    /// des marges blanches. Le mode se déclare par <see cref="AllowsWhiteMargins"/> avant
    /// d'appeler ceci.
    /// </summary>
    public void Reset()
    {
        Width = AllowsWhiteMargins ? LargeurContenue() : LargeurMinimale();
        Height = Width / ImageAspect;

        Centrer();
    }

    /// <summary>
    /// La plus grande photo qui tienne entière dans le cadre — le mode « photo entière ».
    ///
    /// Le redressement n'entre pas en compte : quand le blanc est permis, une photo
    /// inclinée n'a plus de coin vide à éviter, c'est justement ce que les marges
    /// absorbent.
    /// </summary>
    private double LargeurContenue() => Math.Min(FrameWidth, FrameHeight * ImageAspect);

    /// <summary>
    /// La plus petite photo qui couvre encore le cadre, redressement compris. En dessous,
    /// le tirage sortirait avec du blanc.
    /// </summary>
    private double LargeurMinimale()
    {
        var (largeur, hauteur) = EncombrementRequis();
        return Math.Max(largeur, hauteur * ImageAspect);
    }

    private void Centrer()
    {
        X = (FrameWidth - Width) / 2;
        Y = (FrameHeight - Height) / 2;
    }

    /// <summary>
    /// Un cran de molette : 10 % de la taille actuelle de la photo.
    ///
    /// Le pas a fait deux allers-retours, et les deux extrêmes ont été essayés en
    /// boutique. Il valait d'abord 4 % du cadre — jugé trop fin le 01/08/2026. Il est
    /// alors passé à un vingtième du plus grand côté de l'image, à la façon de DiLand :
    /// une photo de 6000 px avançait de la MOITIÉ de sa largeur d'un seul cran, ce qui a
    /// été jugé trop brutal le même jour.
    ///
    /// D'où ce pas-ci : multiplicatif, donc identique quelle que soit la définition — une
    /// photo de 6000 px et une de 1200 px avancent du même dixième, et l'opérateur n'a
    /// pas à réapprendre le geste d'une photo à l'autre.
    ///
    /// <b>Il ne sert plus à la grande surface de recadrage</b>, où la molette avance
    /// désormais d'un pixel d'écran par cran (voir <see cref="WidenBy"/>) : c'est
    /// justement le côté proportionnel de ce pas-ci qui faisait avancer le cadrage par
    /// marches. Il reste celui des vignettes de la bande, où l'on cadre gros et vite.
    /// </summary>
    public const double PasZoomMolette = 1.10;

    public void ZoomIn() => ScaleBy(PasZoomMolette);
    public void ZoomOut() => ScaleBy(1 / PasZoomMolette);

    /// <summary>
    /// Élargit la photo d'une quantité <b>fixe</b>, exprimée en unités de cadre.
    ///
    /// C'est ce qu'il faut à la molette de la surface de recadrage : là, un cran vaut un
    /// pixel d'écran, ni plus ni moins. Un pas multiplicatif — même petit — reste
    /// proportionnel à la taille en cours, donc gros quand la photo est grosse : c'est
    /// exactement ce qui faisait avancer le cadrage par marches (signalé le 01/08/2026).
    /// Un pas fixe, lui, ne peut pas faire de marche puisqu'il vaut déjà le plus petit
    /// écart visible.
    /// </summary>
    public void WidenBy(double deltaLargeur) => Zoom(deltaLargeur);

    /// <summary>
    /// Agrandit la photo d'un facteur — le geste du pincement, qui est multiplicatif là où
    /// la molette de DiLand avance par pas fixes. Même bornage : la photo ne peut pas
    /// descendre sous la taille qui couvre le cadre.
    /// </summary>
    public void ScaleBy(double facteur)
    {
        if (facteur <= 0) throw new ArgumentOutOfRangeException(nameof(facteur));
        Zoom(Width * facteur - Width);
    }

    private void Zoom(double delta)
    {
        var ancienneLargeur = Width;
        var ancienneHauteur = Height;

        var largeur = Width + delta;

        // on ne descend jamais sous la taille qui couvre le cadre : sinon la photo
        // laisserait un vide, et le tirage sortirait avec une bande blanche
        if (!AllowsWhiteMargins)
            largeur = Math.Max(largeur, LargeurMinimale());
        else if (largeur <= 0)
        {
            return;
        }

        Width = largeur;
        Height = Width / ImageAspect;

        // zoom autour du centre du cadre, comme lui
        X += (ancienneLargeur - Width) / 2;
        Y += (ancienneHauteur - Height) / 2;

        Contraindre();
    }

    /// <summary>
    /// Redimensionne la photo en gardant un point fixe — la poignée de coin.
    ///
    /// Tirer un coin de la photo doit laisser le coin opposé où il est : un seul côté
    /// s'agrandit, et l'on cadre par là où l'on tire. Le zoom ordinaire, lui, grandit des
    /// deux côtés à la fois, ce qui oblige à replacer la photo après chaque cran.
    /// </summary>
    /// <param name="nouvelleLargeur">Largeur voulue pour la photo, en unités de cadre.</param>
    /// <param name="ancreX">Point qui ne doit pas bouger — en pratique le coin opposé.</param>
    /// <param name="ancreY">Idem, en ordonnée.</param>
    public void ResizeAnchored(double nouvelleLargeur, double ancreX, double ancreY)
    {
        if (!AllowsWhiteMargins)
            nouvelleLargeur = Math.Max(nouvelleLargeur, LargeurMinimale());

        if (nouvelleLargeur <= 0) return;

        var facteur = nouvelleLargeur / Width;

        // le point d'ancrage garde sa position : tout le reste s'éloigne ou se rapproche
        // de lui proportionnellement
        X = ancreX - (ancreX - X) * facteur;
        Y = ancreY - (ancreY - Y) * facteur;

        Width = nouvelleLargeur;
        Height = Width / ImageAspect;

        Contraindre();
    }

    /// <summary>Fait glisser la photo derrière le cadre.</summary>
    public void Move(double dx, double dy)
    {
        X += dx;
        Y += dy;
        Contraindre();
    }

    /// <summary>
    /// Empêche la photo de découvrir le cadre — les quatre tests de débordement de
    /// <c>WhenThumbDragDelta</c>. Sans cela, un glissement trop franc ferait sortir la
    /// photo et laisserait du blanc dans le tirage.
    /// </summary>
    private void Contraindre()
    {
        if (AllowsWhiteMargins) return;

        // la photo doit d'abord être assez grande : un redressement qu'on accentue peut
        // la rendre trop petite pour le cadre incliné, il faut alors la faire grandir
        var minimum = LargeurMinimale();
        if (Width < minimum)
        {
            var ancienneLargeur = Width;
            var ancienneHauteur = Height;

            Width = minimum;
            Height = Width / ImageAspect;

            X += (ancienneLargeur - Width) / 2;
            Y += (ancienneHauteur - Height) / 2;
        }

        // puis elle doit être bien placée : on borne sur l'encombrement du cadre incliné,
        // centré comme lui, et non sur le cadre droit
        var (requiseW, requiseH) = EncombrementRequis();
        var gaucheRequise = (FrameWidth - requiseW) / 2;
        var hautRequis = (FrameHeight - requiseH) / 2;

        if (X + Width < gaucheRequise + requiseW) X = gaucheRequise + requiseW - Width;
        if (X > gaucheRequise) X = gaucheRequise;
        if (Y + Height < hautRequis + requiseH) Y = hautRequis + requiseH - Height;
        if (Y > hautRequis) Y = hautRequis;
    }

    /// <summary>
    /// Ce que le cadre retient de la photo, en fractions de l'image — la seule forme que
    /// le rendu comprenne. La conversion n'a lieu qu'ici : tant qu'on manipule, on reste
    /// dans le repère du cadre, où rien ne peut déformer le format.
    /// </summary>
    public CropSpec ToCropSpec()
    {
        var (gauche, haut, largeurCanevas, hauteurCanevas) = Canevas();

        var x = (0 - gauche) / largeurCanevas;
        var y = (0 - haut) / hauteurCanevas;
        var largeur = FrameWidth / largeurCanevas;
        var hauteur = FrameHeight / hauteurCanevas;

        // une photo autorisée à laisser du blanc peut déborder du [0,1] : on borne, le
        // rendu complétera en blanc de son côté
        x = Math.Clamp(x, 0, 1);
        y = Math.Clamp(y, 0, 1);
        largeur = Math.Clamp(largeur, 0, 1 - x);
        hauteur = Math.Clamp(hauteur, 0, 1 - y);

        return new CropSpec(x, y, largeur, hauteur);
    }

    /// <summary>
    /// Replace la photo pour qu'elle corresponde à un recadrage déjà enregistré : c'est
    /// l'opération inverse de <see cref="ToCropSpec"/>, pour rouvrir une photo là où on
    /// l'avait laissée.
    /// </summary>
    public void SetFromCropSpec(CropSpec crop)
    {
        if (!crop.IsValid || crop.Width <= 0 || crop.Height <= 0)
        {
            Reset();
            return;
        }

        // le recadrage enregistré porte sur le canevas redressé : on remonte d'abord au
        // canevas, puis à la photo droite qui, tournée, le produit
        var largeurCanevas = FrameWidth / crop.Width;
        var hauteurCanevas = FrameHeight / crop.Height;

        var radians = _rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));

        // largeurCanevas = Width·cos + Height·sin, avec Height = Width / rapport
        Width = largeurCanevas / (cos + sin / ImageAspect);
        Height = Width / ImageAspect;

        // le centre du canevas, ramené autour du centre du cadre à celui de la photo
        var (cx, cy) = AutourDuCadre(
            -crop.X * largeurCanevas + largeurCanevas / 2,
            -crop.Y * hauteurCanevas + hauteurCanevas / 2,
            -radians);

        X = cx - Width / 2;
        Y = cy - Height / 2;

        Contraindre();
    }
}
