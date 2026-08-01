using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Studio.App.Infrastructure;
using Studio.Imaging.Geometry;

namespace Studio.App.Controls;

/// <summary>
/// La surface de recadrage, à la façon de DiLand : <b>le cadre est fixe, la photo bouge
/// derrière</b>.
///
/// Relevé dans <c>FitEng.Base.Controls.FramedImageCropControl</c> : un canevas où le
/// cadre <c>fr</c> ne bouge jamais, et où la photo <c>img</c> est placée par
/// <c>Canvas.SetLeft/SetTop</c> et dimensionnée par <c>img.Width/Height</c>. Le format du
/// tirage est donc respecté par construction — il n'y a aucun rapport à recalculer, donc
/// rien qui puisse dériver d'un geste à l'autre.
///
/// Ce qu'on ajoute à DiLand : la photo reste visible AUTOUR du cadre, assombrie. Chez lui
/// on ne voit que ce qui rentre ; ici on voit aussi ce qu'on coupe, ce qui est la moitié
/// de la décision quand on recadre au comptoir.
///
/// La surface ne touche à rien d'autre qu'au <see cref="FramedCrop"/> qu'on lui confie :
/// ni au fichier, ni à la photo du panier. C'est l'appelant qui, sur
/// <see cref="Changed"/>, en tire le <c>CropSpec</c> et redessine ce qu'il veut.
/// </summary>
public partial class CropSurface : UserControl
{
    /// <summary>
    /// Part de la surface que le cadre occupe. Le reste laisse voir la photo qui déborde :
    /// sans cette marge, recadrer redeviendrait un geste à l'aveugle.
    /// </summary>
    private const double PartDuCadre = 0.80;

    /// <summary>Quatre coins, puis quatre milieux de côté.</summary>
    private const int NombreDePoignees = 8;

    private BitmapSource? _photo;
    private FramedCrop? _cadre;
    private double _redressement;

    /// <summary>Pixels d'écran par unité de cadre, posée au dernier tracé.</summary>
    private double _echelle = 1;

    /// <summary>Où tombe le point (0,0) du cadre à l'écran.</summary>
    private Point _origine;

    /// <summary>
    /// Les huit points de saisie de la photo à l'écran : d'abord les quatre coins, dans le
    /// sens des aiguilles depuis le haut-gauche, puis les quatre milieux de côté, dans le
    /// même sens depuis le haut.
    /// </summary>
    private Point[] _prises = [];

    /// <summary>Poignée en cours de glissement, ou −1 si l'on déplace la photo.</summary>
    private int _poigneeTiree = -1;

    /// <summary>
    /// Largeur de la photo au moment où la poignée a été saisie.
    ///
    /// Le geste doit se compter depuis son DÉBUT, jamais depuis l'image précédente : la
    /// poignée est sur le cadre, qui ne bouge pas, donc un calcul relatif redemanderait le
    /// même agrandissement à chaque pixel parcouru et la photo fondrait en deux
    /// centimètres de souris.
    /// </summary>
    private double _largeurAuDepart;

    private Point _dernierPoint;
    private bool _glisse;

    public CropSurface() => InitializeComponent();

    /// <summary>Le cadrage a bougé : à l'appelant d'en tirer le <c>CropSpec</c>.</summary>
    public event EventHandler? Changed;

    /// <summary>T + molette : redresser de ±1, l'angle restant à l'appelant.</summary>
    public event EventHandler<int>? TiltRequested;

    /// <summary>Clic droit : pivoter le CADRE d'un quart de tour, la photo ne bougeant pas.</summary>
    public event EventHandler? FrameRotationRequested;

    /// <summary>Le cadre en cours, ou null tant qu'on n'a rien montré.</summary>
    public FramedCrop? Crop => _cadre;

    /// <summary>
    /// Montre une photo et son cadre.
    /// </summary>
    /// <param name="photo">La photo DROITE : quarts de tour et corrections déjà faits,
    /// mais pas le redressement — la surface le rend elle-même, pour qu'un degré de plus
    /// ne coûte pas un rendu d'image entier.</param>
    /// <param name="cadre">Le cadre à manipuler ; null efface la surface.</param>
    /// <param name="redressementDegres">Le « Tilt » de DiLand, en degrés.</param>
    public void Show(BitmapSource? photo, FramedCrop? cadre, double redressementDegres)
    {
        _photo = photo;
        _cadre = cadre;
        _redressement = redressementDegres;

        Photo.Source = photo;
        Redessiner();
    }

    /// <summary>Change la photo affichée sans toucher au cadrage (correction retouchée).</summary>
    public void UpdatePhoto(BitmapSource? photo)
    {
        _photo = photo;
        Photo.Source = photo;
        Redessiner();
    }

    /// <summary>Change le redressement ; le cadre s'y contraint de lui-même.</summary>
    public void SetTilt(double degres)
    {
        _redressement = degres;
        if (_cadre is not null) _cadre.RotationDegrees = degres;
        Redessiner();
    }

    // — tracé —

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redessiner();

    /// <summary>
    /// Place le cadre puis la photo derrière lui.
    ///
    /// Tout part du cadre : on lui donne la plus grande place possible dans la surface, à
    /// SES proportions, et le reste s'en déduit. La photo n'est jamais qu'un rectangle
    /// posé dans le repère du cadre, à l'échelle près.
    /// </summary>
    private void Redessiner()
    {
        var largeur = Root.ActualWidth;
        var hauteur = Root.ActualHeight;

        var visible = _photo is not null && _cadre is not null && largeur > 0 && hauteur > 0;
        Scene.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Consigne.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;

        var cadre = _cadre!;

        _echelle = Math.Min(largeur * PartDuCadre / cadre.FrameWidth,
                            hauteur * PartDuCadre / cadre.FrameHeight);

        var cadreLargeur = cadre.FrameWidth * _echelle;
        var cadreHauteur = cadre.FrameHeight * _echelle;
        var cadreX = (largeur - cadreLargeur) / 2;
        var cadreY = (hauteur - cadreHauteur) / 2;

        // repère retenu pour convertir dans les deux sens pendant les gestes
        _origine = new Point(cadreX, cadreY);

        var photoX = cadreX + cadre.X * _echelle;
        var photoY = cadreY + cadre.Y * _echelle;

        Canvas.SetLeft(Photo, photoX);
        Canvas.SetTop(Photo, photoY);
        Photo.Width = cadre.Width * _echelle;
        Photo.Height = cadre.Height * _echelle;

        // Le redressement est rendu ici, à la volée, et PAS en refabriquant une image
        // tournée : WPF refuse tout angle qui ne soit pas un quart de tour à
        // TransformedBitmap, et refaire un RenderTargetBitmap à chaque degré rendrait le
        // geste poussif. La photo tourne autour du CENTRE DU CADRE, comme dans le modèle
        // — c'est ce qui rend sa contrainte exacte, et donc le tirage sans coin blanc.
        Photo.RenderTransform = Math.Abs(_redressement) < 0.01
            ? Transform.Identity
            : new RotateTransform(_redressement,
                cadreX + cadreLargeur / 2 - photoX,
                cadreY + cadreHauteur / 2 - photoY);

        // le papier sous la photo : ce qu'elle ne couvre pas sortira blanc
        Canvas.SetLeft(Papier, cadreX);
        Canvas.SetTop(Papier, cadreY);
        Papier.Width = cadreLargeur;
        Papier.Height = cadreHauteur;

        // En mode « photo entière » il n'y a rien à recadrer : la photo tient tout entière
        // dans le format, et le tirage la centrera de toute façon. Montrer des poignées
        // laisserait croire qu'on peut y toucher, et ce qu'on aurait déplacé serait perdu
        // à l'impression.
        MontrerLesPoignees(!cadre.AllowsWhiteMargins, cadre);

        var cadreRect = new Rect(cadreX, cadreY, cadreLargeur, cadreHauteur);

        Voile.Data = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(new Rect(0, 0, largeur, hauteur)),
            new RectangleGeometry(cadreRect));

        Canvas.SetLeft(Cadre, cadreX);
        Canvas.SetTop(Cadre, cadreY);
        Cadre.Width = cadreLargeur;
        Cadre.Height = cadreHauteur;
    }

    private static readonly string ConsigneRecadrage =
        "Faites glisser la photo · tirez une poignée du cadre vers l'extérieur pour en voir plus " +
        "· molette : zoom au pixel · clic droit : pivoter le cadre · T + molette : redresser";

    private static readonly string ConsigneEntiere =
        "Photo entière : le blanc autour, c'est la marge du papier. " +
        "Passez en « remplir le format » pour recadrer.";

    /// <summary>Affiche ou retire les poignées, selon qu'il y a quelque chose à recadrer.</summary>
    private void MontrerLesPoignees(bool visibles, FramedCrop cadre)
    {
        Consigne.Text = visibles ? ConsigneRecadrage : ConsigneEntiere;

        var etat = visibles ? Visibility.Visible : Visibility.Collapsed;
        foreach (var forme in Poignees()) forme.Visibility = etat;

        if (visibles) PlacerLesPoignees(cadre);
        else _prises = [];   // plus rien à saisir : le clic reprend le glissement simple
    }

    private Shape[] Poignees() =>
    [
        Poignee0, Poignee1, Poignee2, Poignee3,
        PoigneeHaut, PoigneeDroite, PoigneeBas, PoigneeGauche,
    ];

    /// <summary>
    /// Les huit prises, posées sur le CADRE : les quatre coins d'abord, les quatre milieux
    /// de côté ensuite. L'ordre est celui que <see cref="TirerPoignee"/> attend.
    ///
    /// Elles étaient sur la photo, et le geste s'en trouvait inversé : tirer une poignée
    /// vers l'extérieur agrandissait la photo, donc <b>réduisait</b> ce qu'on garde. Or
    /// l'opérateur ne pense pas « j'agrandis la photo », il pense « j'élargis ce que je
    /// tire » — et c'est le geste de tous les outils de recadrage. Sur le cadre, tirer
    /// vers l'extérieur montre PLUS de photo (signalé le 01/08/2026).
    ///
    /// Elles ne suivent pas le redressement, contrairement à la photo : le cadre, lui, ne
    /// penche jamais — c'est le format du papier.
    /// </summary>
    private void PlacerLesPoignees(FramedCrop cadre)
    {
        var gauche = _origine.X;
        var haut = _origine.Y;
        var droite = gauche + cadre.FrameWidth * _echelle;
        var bas = haut + cadre.FrameHeight * _echelle;
        var milieuX = (gauche + droite) / 2;
        var milieuY = (haut + bas) / 2;

        _prises =
        [
            new Point(gauche, haut),
            new Point(droite, haut),
            new Point(droite, bas),
            new Point(gauche, bas),

            new Point(milieuX, haut),
            new Point(droite, milieuY),
            new Point(milieuX, bas),
            new Point(gauche, milieuY),
        ];

        var formes = Poignees();

        for (var i = 0; i < NombreDePoignees; i++)
        {
            Canvas.SetLeft(formes[i], _prises[i].X - formes[i].Width / 2);
            Canvas.SetTop(formes[i], _prises[i].Y - formes[i].Height / 2);
        }
    }

    // — gestes —

    /// <summary>
    /// Note dans le journal ce que la surface a reçu.
    ///
    /// Aucun test ne clique ni ne tourne une molette : sans cette trace, on en serait
    /// réduit à supposer pourquoi un geste « ne marche pas ». Les glissements ne sont
    /// notés qu'à leur début et à leur fin — un par pixel noierait le journal.
    /// </summary>
    private void Tracer(string geste)
    {
        if (_cadre is not { } cadre) return;

        FileLog.Write($"Surface « {geste} » · cadre {cadre.FrameWidth:0}×{cadre.FrameHeight:0} " +
                      $"· photo {cadre.Width:0}×{cadre.Height:0} en ({cadre.X:0},{cadre.Y:0}) " +
                      $"· redressement {_redressement:0.#}°");
    }

    /// <summary>Le geste a modifié le cadrage : on redessine et on prévient l'appelant.</summary>
    private void Bouge()
    {
        Redessiner();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_cadre is null) return;

        _glisse = true;
        _dernierPoint = e.GetPosition(Root);
        _poigneeTiree = PoigneeSous(_dernierPoint);
        _largeurAuDepart = _cadre.Width;
        Root.CaptureMouse();

        Tracer(_poigneeTiree >= 0 ? $"poignée {_poigneeTiree} saisie" : "début de glissement");
    }

    /// <summary>Rayon de saisie d'une poignée : large, pour que ça se prenne au doigt.</summary>
    private const double RayonPoignee = 26;

    /// <summary>
    /// Faux en mode « photo entière » : la photo tient tout entière dans le format, il n'y
    /// a rien à cadrer, et ce qu'on déplacerait ne serait pas imprimé — le tirage la
    /// centre dans le papier quoi qu'il arrive.
    /// </summary>
    private bool Recadrable => _cadre is { AllowsWhiteMargins: false };

    /// <summary>
    /// La poignée sous le curseur, ou −1 s'il n'y en a pas.
    ///
    /// Les coins sont interrogés d'abord : sur une photo étroite, une encoche de côté peut
    /// venir à portée d'un coin, et c'est le coin qui doit gagner — il commande les deux
    /// bords à la fois, donc le geste le plus large.
    /// </summary>
    private int PoigneeSous(Point point)
    {
        for (var i = 0; i < _prises.Length; i++)
        {
            var dx = point.X - _prises[i].X;
            var dy = point.Y - _prises[i].Y;
            if (dx * dx + dy * dy <= RayonPoignee * RayonPoignee) return i;
        }
        return -1;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_glisse) Tracer(_poigneeTiree >= 0 ? "fin de poignée" : "fin de glissement");

        _glisse = false;
        _poigneeTiree = -1;
        Root.ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_cadre is null) return;

        var point = e.GetPosition(Root);

        // le curseur annonce ce qui va se passer, même sans bouton enfoncé
        if (!_glisse)
        {
            Root.Cursor = Recadrable ? Curseur(PoigneeSous(point)) : Cursors.Arrow;
            return;
        }

        if (!Recadrable) return;

        if (_poigneeTiree >= 0) TirerPoignee(point);
        else
            // La photo suit le curseur : on la pousse comme on pousserait un tirage sur
            // une table lumineuse. Elle partait dans l'autre sens — le geste déplaçait le
            // cadre par-dessus la photo — et l'opérateur voyait le cadrage lui échapper
            // (signalé deux fois, la seconde le 01/08/2026).
            _cadre.Move((point.X - _dernierPoint.X) / _echelle,
                        (point.Y - _dernierPoint.Y) / _echelle);

        _dernierPoint = point;
        Bouge();
    }

    /// <summary>La forme du curseur dit quel bord la poignée sous lui commande.</summary>
    private static Cursor Curseur(int poignee) => poignee switch
    {
        0 or 2 => Cursors.SizeNWSE,
        1 or 3 => Cursors.SizeNESW,
        4 or 6 => Cursors.SizeNS,
        5 or 7 => Cursors.SizeWE,
        _ => Cursors.SizeAll,
    };

    /// <summary>
    /// Élargit ou resserre le cadrage en gardant fixe le côté d'en face.
    ///
    /// <b>Le raisonnement.</b> Le cadre ne peut pas bouger : c'est le format du papier, il
    /// est posé au milieu de l'écran une fois pour toutes. Tirer sa poignée revient donc à
    /// dire « je veux garder CE rectangle-là de la photo » : on mesure le rectangle que le
    /// curseur demande, et l'on met la photo à l'échelle qui le fait retomber pile dans le
    /// cadre. Tirer vers l'extérieur demande un rectangle plus grand, donc une photo plus
    /// petite derrière — et l'on voit PLUS. C'est l'inverse de ce que faisait la version
    /// précédente, où les poignées étaient sur la photo.
    ///
    /// Le point d'ancrage est celui d'en face, sur le cadre : par un COIN, le coin opposé
    /// ne bouge pas, donc on n'élargit que du côté qu'on tire. Par une ENCOCHE de côté,
    /// c'est le milieu du côté opposé : le bord d'en face reste en place et l'axe reste
    /// centré, donc on redécouvre le haut sans déranger le bas.
    /// </summary>
    private void TirerPoignee(Point point)
    {
        var cadre = _cadre!;

        var droite = cadre.FrameWidth;
        var bas = cadre.FrameHeight;
        var milieuX = droite / 2;
        var milieuY = bas / 2;

        // le point du CADRE qui ne bougera pas : en face de celui qu'on tire
        var (ancreX, ancreY) = _poigneeTiree switch
        {
            0 => (droite, bas),     // coin haut-gauche tiré
            1 => (0.0, bas),        // haut-droit
            2 => (0.0, 0.0),        // bas-droit
            3 => (droite, 0.0),     // bas-gauche
            4 => (milieuX, bas),    // encoche du haut
            5 => (0.0, milieuY),    // encoche de droite
            6 => (milieuX, 0.0),    // encoche du bas
            _ => (droite, milieuY), // encoche de gauche
        };

        // le curseur dans le repère du cadre. Sans défaire le redressement, contrairement
        // aux gestes portant sur la photo : les poignées sont sur le cadre, qui ne penche
        // pas.
        var vise = new Point((point.X - _origine.X) / _echelle,
                             (point.Y - _origine.Y) / _echelle);

        var rapport = cadre.FrameWidth / cadre.FrameHeight;

        // Le rectangle demandé garde le format du tirage : chaque poignée ne lit du
        // curseur que ce qu'elle commande. Un coin lit les deux axes et retient le plus
        // exigeant, pour que l'angle tiré ne se décroche pas du curseur ; une encoche de
        // côté ne lit que son axe, sinon un tremblement latéral ferait sauter la hauteur.
        var voulue = _poigneeTiree switch
        {
            4 or 6 => Math.Abs(vise.Y - ancreY) * rapport,
            5 or 7 => Math.Abs(vise.X - ancreX),
            _ => Math.Max(Math.Abs(vise.X - ancreX), Math.Abs(vise.Y - ancreY) * rapport),
        };

        // sous un demi-pixel de cadre, le rapport partirait à l'infini : on laisse le
        // geste sans effet plutôt que de faire disparaître la photo
        var part = voulue / cadre.FrameWidth;
        if (part < 0.01) return;

        cadre.ResizeAnchored(_largeurAuDepart / part, ancreX, ancreY);
    }

    /// <summary>
    /// Un cran de molette agrandit la photo d'<b>un pixel d'écran</b>, molette vers l'avant
    /// pour serrer le cadrage.
    ///
    /// Les deux points ont été demandés, et deux fois plutôt qu'une. Le sens d'abord :
    /// vers l'avant on se rapproche, comme partout ailleurs. Le pas ensuite : il valait
    /// 10 % de la taille en cours, étalés sur une seconde par un lisseur, et cela se
    /// voyait avancer par marches — un pas proportionnel est d'autant plus gros que la
    /// photo l'est déjà, et aucun lissage ne rattrape un saut de cinquante pixels.
    ///
    /// Un pixel par cran ne peut pas faire de marche : c'est le plus petit écart que
    /// l'écran sache montrer. Le lisseur devient inutile et disparaît — il ne servait qu'à
    /// masquer la brutalité du pas. Pour cadrer large, on tire un coin ou une encoche :
    /// c'est là le geste rapide, et la molette reste le réglage fin.
    ///
    /// T maintenue, c'est le redressement qui prend la molette — même partage de touches
    /// que sur les vignettes, pour qu'un geste appris à un endroit vaille partout.
    /// </summary>
    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_cadre is null) return;

        // 120 est le cran de molette de Windows ; une souris à défilement fin en envoie
        // des fractions, qu'on garde telles quelles plutôt que de les arrondir à un cran
        var crans = e.Delta / 120.0;
        if (crans == 0) return;

        if (Keyboard.IsKeyDown(Key.T))
        {
            var sens = crans > 0 ? 1 : -1;
            Tracer($"redressement {sens:+0;-0}");
            TiltRequested?.Invoke(this, sens);
        }
        else if (Recadrable && _echelle > 0)
        {
            _cadre.WidenBy(crans / _echelle);
            Bouge();
        }

        e.Handled = true;
    }

    /// <summary>
    /// Clic droit : pivoter le cadre. Sur les vignettes DiLand demande C maintenue, parce
    /// qu'un clic droit y sert aussi à autre chose ; ici la surface ne fait QUE recadrer,
    /// donc pas de touche à tenir.
    /// </summary>
    private void OnRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_cadre is null) return;

        Tracer("pivoter le cadre");
        FrameRotationRequested?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnManipulationStarting(object sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = Root;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
    }

    /// <summary>Pincement tactile : déplacement et échelle, sans lissage — ça doit coller aux doigts.</summary>
    private void OnManipulationDelta(object sender, ManipulationDeltaEventArgs e)
    {
        if (!Recadrable || _cadre is not { } cadre) return;

        // même sens qu'à la souris : la photo suit le doigt
        var translation = e.DeltaManipulation.Translation;
        if (translation.X != 0 || translation.Y != 0)
            cadre.Move(translation.X / _echelle, translation.Y / _echelle);

        var echelle = e.DeltaManipulation.Scale.X;
        if (Math.Abs(echelle - 1) > 0.001) cadre.ScaleBy(echelle);

        Bouge();
        e.Handled = true;
    }
}
