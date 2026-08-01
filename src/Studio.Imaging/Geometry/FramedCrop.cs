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
    /// Autorise la photo à laisser du vide dans le cadre. Faux par défaut, comme chez
    /// DiLand : un tirage avec une bande blanche imprévue est un tirage perdu.
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
    /// incliné. C'est exact, et non une approximation.
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
    /// Replace la photo : la plus petite taille qui couvre entièrement le cadre, centrée.
    /// C'est le cadrage de départ, et celui sur lequel « Réinitialiser » revient.
    /// </summary>
    public void Reset()
    {
        Width = LargeurMinimale();
        Height = Width / ImageAspect;

        Centrer();
    }

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
    /// Pas de zoom de DiLand : un vingtième du plus grand côté de l'image, rapporté au
    /// cadre. Le pas est donc constant, et non proportionnel — le geste garde la même
    /// sensibilité qu'on soit très zoomé ou pas.
    /// </summary>
    private double PasZoom => Math.Max(FrameWidth, FrameHeight) / 20.0;

    public void ZoomIn() => Zoom(PasZoom);
    public void ZoomOut() => Zoom(-PasZoom);

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
        var x = (0 - X) / Width;
        var y = (0 - Y) / Height;
        var largeur = FrameWidth / Width;
        var hauteur = FrameHeight / Height;

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

        Width = FrameWidth / crop.Width;
        Height = FrameHeight / crop.Height;
        X = -crop.X * Width;
        Y = -crop.Y * Height;

        Contraindre();
    }
}
