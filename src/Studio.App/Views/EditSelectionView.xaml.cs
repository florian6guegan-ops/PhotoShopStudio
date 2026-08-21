using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.IO;
using System.Threading.Tasks;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.App.Views;

/// <summary>
/// L'écran « Modifier » : on travaille la sélection sans jamais la perdre de vue.
///
/// Le menu déroulant de gauche montre toutes les photos retenues, avec leur cadre de
/// tirage dessiné dessus ; le grand aperçu de droite montre la photo courante. Recadrage
/// et corrections se voient EN DIRECT aux deux endroits. C'est ce qui remplace le défilé
/// photo par photo, où l'on ne savait jamais où on en était sur la planche.
///
/// Gestes repris de DiLand :
/// <list type="bullet">
///   <item><c>C</c> maintenue : recadrer à la souris directement sur une vignette,
///   celle que le curseur survole.</item>
///   <item>clic droit : pivoter le CADRE, pas la photo.</item>
///   <item><c>T</c> maintenue + molette : pivoter la photo.</item>
/// </list>
/// </summary>
internal partial class EditSelectionView : UserControl
{
    private readonly List<PhotoGridView.PhotoItem> _photos;
    private readonly Action _imprimer;
    private readonly Action? _personnalise;
    private readonly Action? _mettreEnAttente;
    private readonly Func<PhotoGridView.PhotoItem, PhotoGridView.PhotoItem>? _dupliquer;
    private PhotoGridView.PhotoItem _courante;

    private Point _dernierPoint;
    private bool _glisse;
    private PhotoGridView.PhotoItem? _glisseSur;

    /// <param name="photos">Les photos retenues, dans l'ordre de la planche.</param>
    /// <param name="imprimer">Lance l'impression de la sélection, telle que la prépare l'écran précédent.</param>
    /// <param name="personnalise">
    /// Bascule la commande vers une taille libre. Null quand elle y est déjà, ou quand
    /// l'écran appelant ne sait pas la faire.
    /// </param>
    /// <param name="mettreEnAttente">
    /// Met la commande de côté pour servir quelqu'un d'autre.
    ///
    /// C'est la GRILLE qui enregistre, pas cet écran : lui ne tient que les photos
    /// COCHÉES, et mettre de côté la moitié d'une commande serait pire que de ne rien
    /// mettre de côté du tout.
    /// </param>
    /// <param name="dupliquer">
    /// Reproduit une photo dans la commande, et rend le doublon.
    ///
    /// Fourni par la GRILLE, qui seule tient la liste que l'impression parcourt : un
    /// doublon ajouté à la seule liste de cet écran se serait affiché, se serait réglé, et
    /// ne serait jamais sorti. Null quand l'appelant ne sait pas dupliquer — le bouton
    /// disparaît alors.
    /// </param>
    public EditSelectionView(List<PhotoGridView.PhotoItem> photos, Action imprimer,
        Action? personnalise = null, Action? mettreEnAttente = null,
        Func<PhotoGridView.PhotoItem, PhotoGridView.PhotoItem>? dupliquer = null)
    {
        ArgumentNullException.ThrowIfNull(photos);

        _photos = photos;
        _imprimer = imprimer;
        _personnalise = personnalise;
        _mettreEnAttente = mettreEnAttente;
        _dupliquer = dupliquer;
        _courante = photos[0];

        // rien n'est visé en entrant : on travaille la photo affichée, et l'on vise
        // plusieurs photos au Ctrl+clic quand on veut régler d'un coup
        foreach (var photo in _photos) photo.Ciblee = false;

        InitializeComponent();

        // l'écran peut être ouvert par un appelant qui ne sait pas mettre de côté :
        // un bouton qui ne fait rien vaut moins que pas de bouton
        AttenteButton.Visibility = mettreEnAttente is null ? Visibility.Collapsed : Visibility.Visible;

        // même règle que ci-dessus : un bouton qui ne fait rien vaut moins que pas de bouton
        DupliquerButton.Visibility = dupliquer is null ? Visibility.Collapsed : Visibility.Visible;

        Strip.ItemsSource = _photos;
        Sliders.ItemsSource = ConstruireReglages();

        BrancherSurface();
        BrancherRaccourcis();
        ShowCrop();
        SetCurrent(_courante);

        Loaded += (_, _) => Focus(); // sans le focus, ni C ni T ne nous parviennent

        // en dernier : il travaille en tâche de fond sur la liste que tout ce qui précède
        // vient de mettre en place
        CadrerSurLesVisages();
    }

    /// <summary>
    /// L'opérateur a touché au cadrage. Le cadrage automatique s'arrête net dès que c'est
    /// vrai : il travaille en tâche de fond, photo après photo, et une photo qu'il
    /// atteindrait après coup écraserait un cadrage fait à la main.
    /// </summary>
    private bool _gesteOperateur;

    /// <summary>
    /// Pose le cadre sur le VISAGE, photo après photo, sans bloquer l'écran.
    ///
    /// Réglage du poste, faux par défaut (voir <c>PosteSettings.CadrageAutoVisage</c>). Il
    /// ne fait que DÉPLACER le cadre — voir <see cref="CadrageAutomatique"/> — et laisse
    /// tranquilles :
    ///
    /// <list type="bullet">
    ///   <item>les photos dont le cadrage vient d'une BORNE (<c>CadrageImpose</c>) : le
    ///   client a choisi sa zone, ce n'est pas à nous de la refaire ;</item>
    ///   <item>le mode « photo entière » et le Polaroid, où la photo tient tout entière
    ///   dans le cadre : il n'y a rien à déplacer ;</item>
    ///   <item>tout, dès que l'opérateur a posé un geste.</item>
    /// </list>
    ///
    /// La détection lit le FICHIER en pleine définition — une demi-seconde par photo — et
    /// tourne donc hors du fil de l'interface, une photo à la fois. Les vignettes se
    /// remettent à jour au fur et à mesure.
    /// </summary>
    private async void CadrerSurLesVisages()
    {
        if (!App.Services.Poste.CadrageAutoVisage) return;

        var faites = 0;
        var redressees = 0;

        foreach (var photo in _photos.ToList())
        {
            if (_gesteOperateur) break;
            if (!ACadrerSurLeVisage(photo)) continue;

            Studio.Imaging.Faces.DetectedFace? visage;
            try
            {
                var chemin = photo.Path;
                visage = await Task.Run(() => App.Services.Faces.DetectMain(chemin));
            }
            catch (Exception ex)
            {
                // pas de visage trouvable, modèle absent, fichier illisible : la photo
                // reste centrée, comme avant. Ce n'est pas une panne.
                FileLog.Write($"Cadrage automatique : détection impossible ({photo.Name})", ex);
                continue;
            }

            // l'écran a pu être quitté, ou l'opérateur toucher au cadre, pendant la détection
            if (_gesteOperateur || visage is null) continue;
            if (!ACadrerSurLeVisage(photo) || photo.Cadre is not { } cadre) continue;

            // LE REDRESSEMENT D'ABORD, le cadre ensuite : l'angle change le canevas sur
            // lequel les fractions du cadre se comptent (voir PhotoItem.FineRotationDegrees,
            // qui reporte l'angle sur FramedCrop). Poser le cadre avant reviendrait à le
            // poser sur une photo qui n'existe pas encore, et Contraindre le déplacerait
            // juste après.
            //
            // On ne touche PAS à un redressement que l'opérateur a déjà donné : c'est un
            // geste, et la règle de cet écran est de ne rien reprendre après un geste.
            if (Math.Abs(photo.FineRotationDegrees) < 0.01)
            {
                var (largeur, hauteur) = photo.SourcePixels;
                var redressement = CadrageAutomatique.AngleDeRedressement(
                    visage.Eyes, largeur, hauteur, photo.RotationQuarterTurns);

                if (Math.Abs(redressement) > 0.01)
                {
                    photo.FineRotationDegrees = Math.Round(redressement, 1);
                    redressees++;
                }
            }

            var point = CadrageAutomatique.TournerAvecLaPhoto(
                CadrageAutomatique.PointAViser(visage.Box, visage.Eyes),
                photo.RotationQuarterTurns);

            CadrageAutomatique.Poser(cadre, point);
            photo.MarquerCadrageAuto();

            Appliquer(photo, cadre);
            faites++;
        }

        if (faites > 0)
            FileLog.Write($"Cadrage automatique : cadre posé sur {faites} visage(s)" +
                          (redressees > 0 ? $", dont {redressees} redressé(s) sur la ligne des yeux" : ""));
    }

    /// <summary>Cette photo peut-elle recevoir le cadrage automatique ? Voir ci-dessus.</summary>
    private static bool ACadrerSurLeVisage(PhotoGridView.PhotoItem photo) =>
        !photo.CadrageAutoFait
        && !photo.CadrageImpose
        && photo.SourceSizeKnown
        && (photo.FitOverride ?? photo.Product?.DefaultFit ?? FitMode.Fill) == FitMode.Fill;

    /// <summary>
    /// Les raccourcis de la grille valent ici aussi.
    ///
    /// Ctrl+A manquait à cet écran : il n'existait que sur la grille, et son écoute est
    /// retirée dès qu'on la quitte. On se retrouvait donc sans aucun moyen de reprendre
    /// toute la sélection — or c'est lui qui commande ce que les corrections touchent.
    /// </summary>
    private void BrancherRaccourcis() =>
        new KeyMap()
            .OnCtrl(Key.A, ToutCocher)
            .OnCtrl(Key.W, () =>
            {
                GrayscaleToggle.IsChecked = GrayscaleToggle.IsChecked != true;
                OnGrayscaleChanged(GrayscaleToggle, new RoutedEventArgs());
            })
            .Attach(this);

    /// <summary>Vise toutes les photos, ou plus aucune si elles le sont déjà.</summary>
    private void ToutCocher()
    {
        var toutVise = _photos.Count > 0 && _photos.All(p => p.Ciblee);
        foreach (var photo in _photos) photo.Ciblee = !toutVise;

        // la plage repart de la photo affichée : l'ancre d'avant appartenait à une
        // sélection que l'on vient de refaire d'un bloc
        _ancreVisee = null;

        FileLog.Write(toutVise
            ? "Ctrl+A : plus aucune photo visée"
            : $"Ctrl+A : {_photos.Count} photo(s) visées");

        Refresh();
    }

    /// <summary>
    /// D'où part la prochaine plage de Maj+clic. Nulle tant que rien n'a été cliqué : on
    /// retombe alors sur la photo affichée, qui est bien celle que l'opérateur regarde.
    /// </summary>
    private PhotoGridView.PhotoItem? _ancreVisee;

    /// <summary>
    /// Vise toutes les vignettes entre deux photos, bornes comprises.
    ///
    /// <b>Elle VISE, elle ne bascule pas</b> : basculer relâcherait les photos déjà prises
    /// par une plage précédente, et l'opérateur perdrait son travail au lieu de l'étendre.
    /// C'est la même règle que sur la planche (voir <c>PhotoGridView.SelectionnerLaPlage</c>).
    /// </summary>
    private void ViserLaPlage(PhotoGridView.PhotoItem depuis, PhotoGridView.PhotoItem jusqua)
    {
        var debut = _photos.IndexOf(depuis);
        var fin = _photos.IndexOf(jusqua);
        if (debut < 0 || fin < 0) return;

        if (debut > fin) (debut, fin) = (fin, debut);

        for (var i = debut; i <= fin; i++) _photos[i].Ciblee = true;

        FileLog.Write($"Maj+clic : {fin - debut + 1} photo(s) visées");
        SetCurrent(jusqua);
    }

    /// <summary>
    /// La surface mène les gestes de recadrage ; l'écran ne fait qu'en tirer les
    /// conséquences. Elle ne connaît ni le panier ni les produits : elle bouge un cadre,
    /// et c'est ici qu'on le reporte sur la photo.
    /// </summary>
    private void BrancherSurface()
    {
        // Ces trois événements ne partent QUE d'un geste à la souris (voir CropSurface :
        // Bouge() n'est appelé que depuis les gestionnaires de souris et de pincement).
        // C'est donc ici qu'on sait que l'opérateur a repris la main, et que le cadrage
        // automatique doit cesser de travailler derrière lui.
        Surface.Changed += (_, _) =>
        {
            _gesteOperateur = true;
            if (Surface.Crop is { } cadre) Appliquer(_courante, cadre);
        };

        Surface.TiltRequested += (_, sens) =>
        {
            _gesteOperateur = true;
            Redresser(_courante, sens);
        };

        Surface.FrameRotationRequested += (_, _) =>
        {
            _gesteOperateur = true;
            PivoterCadre(_courante);
        };
    }

    /// <summary>Cadrage effectif de la photo courante : le sien, sinon celui du produit.</summary>
    private FitMode CadrageCourant =>
        _courante.FitOverride ?? _courante.Product?.DefaultFit ?? FitMode.Fill;

    /// <summary>Libellé du bouton de mode, lu par la liaison du panneau.</summary>
    public string FitLabel => CadrageCourant switch
    {
        FitMode.Fill => "Mode : remplir le format",
        FitMode.Polaroid => "Mode : Polaroid",
        _ => "Mode : photo entière",
    };

    // — affichage —

    private void Refresh()
    {
        MontrerSurface();

        var rang = _photos.IndexOf(_courante) + 1;
        PreviewCaption.Text =
            $"{rang}/{_photos.Count} · {_courante.Name} · {_courante.ProductLabel} · ×{_courante.Quantity}";

        var tirages = _photos.Sum(p => p.Quantity);
        var total = _photos.Sum(p => (p.Product?.Price ?? 0) * p.Quantity);
        SummaryText.Text = $"{_photos.Count} photo(s) · {tirages} tirage(s) · {total:0.00} €";

        // Ce que les boutons vont changer : la quantité des photos VISÉES. Quand elles n'ont
        // pas la même, on le dit — « 1–3 » — plutôt que d'en afficher une seule, qui ferait
        // croire que les autres suivent.
        var quantites = Visees().Select(p => p.Quantity).ToList();
        QuantiteText.Text = quantites.Count == 0 ? "—"
            : quantites.Min() == quantites.Max() ? $"×{quantites[0]}"
            : $"{quantites.Min()}–{quantites.Max()}";

        FormatButton.Content = _courante.Product is { } produit
            ? $"Format : {produit.Name}"
            : "Format : à choisir";
        FitButton.Content = FitLabel;

        // le Polaroid n'est pas un mode qu'on bascule : c'est la forme du produit
        FitButton.IsEnabled = CadrageCourant != FitMode.Polaroid;

        // <b>La case reste cliquable dans TOUS les modes.</b> Elle était grisée en
        // « remplir le format » — le mode par défaut de presque tous les produits — et
        // l'opérateur qui cliquait dessus ne la voyait jamais se cocher : elle passait pour
        // cassée (signalé le 06/08/2026). Le trait y a désormais un sens, et le rendu le
        // trace : c'est le bord du tirage, celui que les ciseaux suivent.
        CutBorderCheck.IsEnabled = true;

        // Ce que la case montre, c'est l'état des photos VISÉES — celles sur lesquelles le
        // clic va porter — et non de la seule photo affichée. Sur une sélection visée dont
        // la photo courante ne faisait pas partie, la case se remettait à zéro juste après
        // avoir été cochée.
        CutBorderCheck.IsChecked = Visees().All(p => p.CutBorder);
        CutBorderCheck.ToolTip = CadrageCourant switch
        {
            FitMode.Fit => "Trace un trait noir de 0,2 mm sur le bord de la photo, à suivre aux ciseaux.",
            FitMode.Polaroid => "Trace le bord du Polaroid, bande blanche du bas comprise : c'est là qu'on coupe.",
            _ => "Trace un trait noir de 0,2 mm tout au bord du tirage, à suivre aux ciseaux.",
        };

        GrayscaleToggle.IsChecked = _courante.Adjustments.Grayscale;
        RedEyeToggle.IsChecked = _courante.Adjustments.RedEye;
        AutoLevelsToggle.IsChecked = _courante.Adjustments.AutoLevels;
        AutoContrastToggle.IsChecked = _courante.Adjustments.AutoContrast;
        AutoColorToggle.IsChecked = _courante.Adjustments.AutoColor;

        MettreLeCompteAJour();

        foreach (var reglage in (IEnumerable<Reglage>)Sliders.ItemsSource) reglage.Relire(_courante.Adjustments);
    }

    /// <summary>
    /// Dit noir sur blanc ce que le panneau de correction va toucher.
    ///
    /// Sans cela, rien à l'écran ne distingue « je corrige cette photo » de « je corrige
    /// les trente-deux » — et c'est précisément ce qui a fait croire que les boutons ne
    /// se défaisaient pas.
    /// </summary>
    private void MettreLeCompteAJour()
    {
        var visees = _photos.Count(p => p.Ciblee);

        CorrectScopeText.Text = visees > 0
            ? $"Les corrections portent sur les {visees} photo(s) visées. " +
              "Ctrl+A n'en vise plus aucune."
            : "Les corrections portent sur la photo affichée. " +
              "Cliquez les vignettes pour en viser plusieurs, Ctrl+A pour toutes.";
    }

    /// <summary>
    /// Sources en haute définition du grand aperçu, chargées à la demande.
    ///
    /// La vignette plafonne à 360 px : agrandie sur la moitié de l'écran, elle était
    /// floue et ne permettait pas de juger une netteté ni un cadrage au pixel près. On
    /// garde donc deux définitions — la petite pour la bande, la grande pour l'aperçu —
    /// et une seule et même composition pour les deux.
    /// </summary>
    private readonly CacheImages _hautesDefinitions = new();

    private const int PreviewBoxPx = 1600;

    /// <summary>
    /// Quelques images seulement, les dernières servies.
    ///
    /// Une commande de trente-deux photos gardait trente-deux aperçus de 1600 px, soit
    /// près de deux cents méga-octets — doublés par les images préparées pour la surface.
    /// L'application ramait et frôlait le plantage (signalé le 01/08/2026). Quatre
    /// suffisent : on ne regarde qu'une photo à la fois, et revenir à la précédente doit
    /// rester immédiat.
    /// </summary>
    private sealed class CacheImages
    {
        private const int Maximum = 4;

        private readonly Dictionary<string, BitmapSource> _images = new();
        private readonly List<string> _ordre = new(); // du plus ancien au plus récemment servi

        public bool TryGet(string cle, out BitmapSource image)
        {
            if (!_images.TryGetValue(cle, out var trouvee))
            {
                image = null!;
                return false;
            }

            _ordre.Remove(cle);
            _ordre.Add(cle);
            image = trouvee;
            return true;
        }

        public void Set(string cle, BitmapSource image)
        {
            _images[cle] = image;
            _ordre.Remove(cle);
            _ordre.Add(cle);

            while (_ordre.Count > Maximum)
            {
                _images.Remove(_ordre[0]);
                _ordre.RemoveAt(0);
            }
        }

        public void Remove(string cle)
        {
            _images.Remove(cle);
            _ordre.Remove(cle);
        }
    }

    /// <summary>Redessine la photo touchée : vignette ET surface, pour voir en direct.</summary>
    private void Redessiner(PhotoGridView.PhotoItem photo)
    {
        photo.RefreshThumbnail();
        if (ReferenceEquals(photo, _courante)) MontrerSurface();
    }

    /// <summary>
    /// Les photos préparées pour la surface : quarts de tour et corrections appliqués,
    /// sans redressement ni cadre.
    ///
    /// Elles sont gardées, car le recadrage se refait à chaque pixel de glissement : les
    /// recomposer à chaque fois ferait passer la photo par ImageMagick des dizaines de
    /// fois par seconde, et le geste collerait. Ce sont les corrections et les quarts de
    /// tour qui les périment — voir <see cref="Perimer"/>.
    /// </summary>
    private readonly CacheImages _photosPretes = new();

    /// <summary>
    /// Change dès que ce que la surface doit montrer change : correction, quart de tour,
    /// ou passage à une autre photo. C'est ce compteur qui dit à la préparation en tâche
    /// de fond si son résultat vaut encore quelque chose à son retour.
    /// </summary>
    private int _versionSurface;

    /// <summary>À appeler dès que les PIXELS de la photo changent, pas son cadrage.</summary>
    private void Perimer(PhotoGridView.PhotoItem photo)
    {
        _photosPretes.Remove(photo.Cle);
        _versionSurface++;
    }

    /// <summary>
    /// Montre la photo courante et son cadre sur la surface.
    ///
    /// Le cadrage se refait à chaque pixel de glissement : la photo préparée est donc
    /// gardée, et ce chemin-là ne coûte rien. Si elle manque, on montre la vignette en
    /// attendant et on prépare à côté — jamais sur le fil de l'interface.
    /// </summary>
    private void MontrerSurface()
    {
        var cadre = Cadre(_courante);
        var angle = _courante.FineRotationDegrees;

        // le trait de découpe se voit sur la surface, comme il sortira sur le papier
        Surface.ContourDeDecoupe = _courante.CutBorder;

        // et la marge blanche aussi : sur un « bord blanc », la photo remplit la fenêtre et
        // rien ne disait que cinq millimètres de blanc allaient l'entourer
        Surface.LisereMm = LisereDe(_courante);

        // rangée par CLÉ et non par chemin : un doublon partage le fichier de son original
        // sans partager ses corrections (voir PhotoItem.Cle)
        if (_photosPretes.TryGet(_courante.Cle, out var prete))
        {
            Surface.Show(prete, cadre, angle);
            return;
        }

        Surface.Show(_courante.SourceThumbnail, cadre, angle);
        PreparerEnFond();
    }

    private bool _preparationEnCours;

    /// <summary>
    /// Prépare l'image de la surface à côté, une seule à la fois.
    ///
    /// Si les réglages ont encore bougé pendant le calcul, on recommence au lieu de
    /// garder un résultat périmé : l'affichage saute les états intermédiaires plutôt que
    /// de prendre du retard sur la main de l'opérateur. C'est ce qui remplace l'ancien
    /// calcul en ligne, qui figeait l'écran à chaque cran de curseur.
    ///
    /// <b>PENDANT LE GESTE, ON COMPOSE SUR LA VIGNETTE.</b> L'aperçu fait jusqu'à
    /// 1600 px de grand côté — 1356 × 2048 relevé au journal le 13/08/2026 — et le
    /// recomposer dans ImageMagick à chaque cran de curseur coûtait des centaines de
    /// millisecondes : la photo avançait par à-coups, un cran sur trois. C'est la
    /// saccade signalée depuis la boutique sur les tirages DE100.
    ///
    /// <b>Le geste se reconnaît tout seul</b>, sans écouter les curseurs : si les réglages
    /// ont bougé PENDANT le calcul, c'est que la main est encore dessus. La boucle le
    /// savait déjà pour recommencer — elle s'en sert maintenant pour choisir sa source.
    /// Dès que ça se calme, un dernier tour repasse en pleine définition, et c'est LUI seul
    /// qui est mis en mémoire : la vignette composée ne doit jamais se retrouver dans
    /// <see cref="_photosPretes"/>, sinon l'aperçu resterait flou une fois le geste fini.
    ///
    /// L'écran d'identité résout le même problème autrement, avec un tampon réduit gardé
    /// d'avance (<c>_departBgra</c>). Ici la vignette est déjà chargée pour la bande du
    /// bas : elle ne coûte rien, et c'est déjà elle que <see cref="MontrerSurface"/> montre
    /// en attendant.
    /// </summary>
    private async void PreparerEnFond()
    {
        if (_preparationEnCours) return;
        _preparationEnCours = true;

        try
        {
            // premier tour en pleine définition : un réglage isolé — une case cochée, un
            // changement de photo — ne doit pas passer par une étape floue
            var enPleinGeste = false;

            while (true)
            {
                var version = _versionSurface;
                var photo = _courante;

                var source = enPleinGeste || !_hautesDefinitions.TryGet(photo.Path, out var haute)
                    ? photo.SourceThumbnail
                    : haute;

                if (source is null) return;

                // instantané des réglages : l'opérateur continue de bouger les curseurs
                // pendant le calcul, et les lire depuis l'autre fil serait une course
                var reglages = photo.Adjustments.Clone();
                var quarts = photo.RotationQuarterTurns;

                var preparee = await Task.Run(
                    () => PhotoGridView.PhotoItem.ComposerPhoto(source, quarts, reglages));

                // ça a encore bougé : la main est sur le curseur, on recommence en réduit
                if (version != _versionSurface)
                {
                    enPleinGeste = true;
                    continue;
                }

                if (enPleinGeste)
                {
                    // le geste s'arrête. On montre TOUT DE SUITE ce qu'on vient de calculer
                    // — l'opérateur voit le résultat de son dernier cran sans attendre — et
                    // l'on refait un tour en pleine définition, qui le remplacera.
                    if (ReferenceEquals(photo, _courante))
                        Surface.Show(preparee, Cadre(photo), photo.FineRotationDegrees);

                    enPleinGeste = false;
                    continue;
                }

                _photosPretes.Set(photo.Cle, preparee);
                if (ReferenceEquals(photo, _courante))
                    Surface.Show(preparee, Cadre(photo), photo.FineRotationDegrees);

                return;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write("Aperçu : préparation impossible", ex);
        }
        finally
        {
            _preparationEnCours = false;
        }
    }

    private async void ChargerHauteDefinition(PhotoGridView.PhotoItem photo)
    {
        if (_hautesDefinitions.TryGet(photo.Path, out _)) return;

        try
        {
            var octets = await Task.Run(
                () => App.Services.Thumbnails.GetJpeg(photo.Path, PreviewBoxPx));

            using var flux = new MemoryStream(octets);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = flux;
            bitmap.EndInit();
            bitmap.Freeze();

            _hautesDefinitions.Set(photo.Path, bitmap);
            FileLog.Write($"Aperçu : {photo.Name} chargé en {bitmap.PixelWidth}×{bitmap.PixelHeight}");

            // la surface montrait la vignette en attendant : elle est à refaire
            Perimer(photo);
            if (ReferenceEquals(photo, _courante)) MontrerSurface();
        }
        catch (Exception ex)
        {
            // illisible en grand : la vignette fait l'affaire, on ne bloque pas l'écran.
            // Mais on le DIT dans le journal : c'est la seule façon de savoir pourquoi
            // l'aperçu reste flou au lieu de le supposer.
            FileLog.Write($"Aperçu : chargement haute définition impossible ({photo.Name})", ex);
        }
    }

    private void SetCurrent(PhotoGridView.PhotoItem photo)
    {
        _courante = photo;
        _versionSurface++; // ce que la surface doit montrer a changé

        // une comparaison en cours appartenait à la photo précédente : la laisser courir
        // ferait revenir son original par-dessus la nouvelle au relâchement
        _comparaisonEnCours = false;

        Cadre(photo); // crée le cadre au format du produit, ou le reprend
        ChargerHauteDefinition(photo);
        Refresh();
    }

    /// <summary>
    /// Met le cadre au rapport du produit, centré et aussi grand que possible.
    ///
    /// Sans cela le cadre partait de l'image entière : un 10×15 demandé sur une photo
    /// 2:3 sortait au rapport de la photo, pas à celui du tirage. C'est le défaut le plus
    /// grave signalé le 01/08/2026 — il ne se voit qu'une fois le papier sorti.
    ///
    /// Le rapport se calcule en PIXELS : un cadre est une fraction de l'image, donc un
    /// même rapport en millimètres ne donne pas la même fraction selon la définition.
    /// </summary>


    /// <summary>Le plus grand cadre de ce rapport qui tient dans l'image, centré.</summary>

    /// <summary>
    /// Les photos que les réglages touchent : celles qu'on a visées au Ctrl+clic.
    ///
    /// Aucune n'est visée en entrant : on règle la photo affichée, et l'on ne vise
    /// plusieurs photos qu'en le demandant. Si rien n'est visé, on retombe donc sur la
    /// photo courante — un bouton qui ne fait rien laisse croire à une panne.
    ///
    /// Ce n'est PAS <c>Selected</c>, qui dit ce qui part à l'impression : viser une photo
    /// pour la corriger ne doit rien changer à la commande.
    /// </summary>
    private List<PhotoGridView.PhotoItem> Visees()
    {
        var visees = _photos.Where(p => p.Ciblee).ToList();
        return visees.Count > 0 ? visees : [_courante];
    }

    // — recadrage à la souris —

    /// <summary>
    /// Le cadre d'une photo — porté par la photo elle-même (<c>PhotoItem.Cadre</c>), et
    /// non plus par cet écran.
    ///
    /// C'est lui qui porte la vérité : le <see cref="CropSpec"/> n'en est que la
    /// traduction pour le rendu. Le tenir ici revenait à ne le calculer que pour les
    /// photos qu'on ouvrait — les autres partaient à l'impression sans cadrage.
    /// </summary>
    private static FramedCrop? Cadre(PhotoGridView.PhotoItem photo) => photo.Cadre;

    /// <summary>
    /// La marge blanche qui entourera ce tirage, en millimètres — zéro pour tout le reste.
    ///
    /// <b>Deux sources, et il faut les deux.</b> Un produit du catalogue porte sa marge dans
    /// <c>BorderMm</c> ; une taille libre à bord blanc la porte sur la TAILLE, parce que son
    /// produit n'est qu'un fantôme fabriqué pour l'écran et que c'est la
    /// <c>CustomSheetSpec</c> qui la transporte jusqu'au rendu. N'en lire qu'une laisserait
    /// la moitié des bords blancs invisibles à l'écran — et l'autre moitié, c'est celle que
    /// l'opérateur vient de saisir à la main.
    /// </summary>
    private static double LisereDe(PhotoGridView.PhotoItem photo) =>
        photo.TaillePerso?.BorderMm is > 0 and var perso
            ? perso
            : photo.Product is { ABordBlanc: true } produit ? produit.BorderMm : 0;

    /// <summary>Reporte le cadre sur la photo et redessine — le seul point de conversion.</summary>
    private void Appliquer(PhotoGridView.PhotoItem photo, FramedCrop cadre)
    {
        photo.Crop = cadre.ToCropSpec();

        VignetteBientot(photo);
        if (ReferenceEquals(photo, _courante)) MontrerSurface();
    }

    private readonly HashSet<PhotoGridView.PhotoItem> _vignettesEnRetard = new();
    private bool _vignettesPlanifiees;

    /// <summary>
    /// Refait les vignettes au premier moment de calme.
    ///
    /// Un glissement lève un événement par pixel parcouru, et un curseur de correction
    /// touche d'un coup les trente-deux photos cochées. Les refaire à chaque fois ferait
    /// coller le geste. En priorité <c>Background</c>, elles attendent que la file des
    /// mouvements de souris soit vide : la bande suit à l'œil, et la surface, elle, est
    /// déjà à jour de son côté.
    ///
    /// Les photos s'accumulent dans un ensemble : la version précédente n'en retenait
    /// qu'une et laissait les autres avec leur ancienne vignette.
    /// </summary>
    private void VignetteBientot(PhotoGridView.PhotoItem photo)
    {
        _vignettesEnRetard.Add(photo);

        if (_vignettesPlanifiees) return;
        _vignettesPlanifiees = true;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _vignettesPlanifiees = false;

            var aRefaire = _vignettesEnRetard.ToList();
            _vignettesEnRetard.Clear();
            foreach (var attardee in aRefaire) attardee.RefreshThumbnail();
        }));
    }

    /// <summary>Fait glisser la photo derrière le cadre.</summary>
    private void Deplacer(PhotoGridView.PhotoItem photo, double dx, double dy)
    {
        if (Cadre(photo) is not { } cadre) return;

        // les deltas arrivent en fractions ; le cadre travaille dans ses propres unités
        cadre.Move(dx * cadre.FrameWidth, dy * cadre.FrameHeight);
        Appliquer(photo, cadre);
    }

    /// <summary>
    /// Resserre ou élargit le cadre autour de son centre. Le rapport est conservé : un
    /// cadre qui ne serait plus à la forme du produit donnerait un tirage déformé.
    /// </summary>
    private void Zoomer(PhotoGridView.PhotoItem photo, bool avant)
    {
        if (Cadre(photo) is not { } cadre) return;

        if (avant) cadre.ZoomIn();
        else cadre.ZoomOut();

        Appliquer(photo, cadre);
    }

    /// <summary>
    /// Pivote le CADRE d'un quart de tour : ses deux côtés s'échangent, la photo ne bouge
    /// pas. Faire tenir une photo verticale dans un tirage horizontal est un besoin
    /// quotidien, et pivoter la photo ne le résout pas.
    /// </summary>
    private void PivoterCadre(PhotoGridView.PhotoItem photo)
    {
        if (Cadre(photo) is not { } ancien) return;

        // pivoter le cadre, c'est échanger ses deux côtés. Dans ce modèle l'opération est
        // triviale et surtout SANS EFFET DE BORD : la photo se replace toute seule pour
        // couvrir le nouveau cadre. L'ancienne version échangeait les côtés du recadrage,
        // ce qui ne faisait rien du tout quand celui-ci valait l'image entière.
        var pixels = photo.PixelsVus;
        var pivote = new FramedCrop(pixels.Width, pixels.Height, ancien.FrameHeight, ancien.FrameWidth)
        {
            RotationDegrees = ancien.RotationDegrees,
        };

        photo.RemplacerCadre(pivote);
        Appliquer(photo, pivote);
    }

    /// <summary>
    /// Pivote la PHOTO d'un quart de tour. Ses deux côtés s'échangent : le cadre est donc
    /// refait, et le cadrage repart du centre — ses repères viennent de changer de sens,
    /// le garder donnerait un cadrage pris ailleurs que là où on l'avait posé.
    /// </summary>
    private void PivoterPhoto(PhotoGridView.PhotoItem photo, int sens)
    {
        // le cadre et le cadrage repartent du centre d'eux-mêmes (voir PhotoItem)
        photo.RotationQuarterTurns = (photo.RotationQuarterTurns + sens + 4) % 4;

        Perimer(photo); // les pixels tournent : la photo prête pour la surface est à refaire
        PerimerLOriginal(photo); // …et l'original de la comparaison avec elle
        Redessiner(photo);
        Refresh();
    }

    /// <summary>Un degré par cran de molette : DiLand stocke un angle ENTIER.</summary>
    private const double PasRedressement = 1;

    /// <summary>
    /// Le « Tilt » de DiLand (touche T) : un redressement de quelques degrés, pour
    /// remettre un horizon d'aplomb. Rien à voir avec les quarts de tour de
    /// Ctrl+←/Ctrl+→ — c'était l'erreur signalée le 01/08/2026.
    /// </summary>
    private void Redresser(PhotoGridView.PhotoItem photo, int sens)
    {
        // bornes de DiLand, relevées dans son PhotoItem : -90 < angle < 90, en degrés
        // entiers. Au-delà, c'est un quart de tour qu'il faut, pas un redressement.
        photo.FineRotationDegrees = Math.Clamp(
            Math.Round(photo.FineRotationDegrees + sens * PasRedressement), -89, 89);

        // le cadre doit suivre : une photo inclinée offre moins de surface utile, donc
        // la photo grandit et se replace pour ne pas laisser de coin vide dans le tirage
        if (Cadre(photo) is { } cadre)
        {
            cadre.RotationDegrees = photo.FineRotationDegrees;
            Appliquer(photo, cadre); // redessine vignette et surface
        }
        else
        {
            Redessiner(photo);
        }

        Refresh();
    }

    // — gestes sur les vignettes (C maintenue) —

    private static bool CTenue => Keyboard.IsKeyDown(Key.C);

    /// <summary>
    /// Le redressement prend-il la molette ?
    ///
    /// Deux façons de le dire, et les deux valent : le mode armé par T sur la surface de
    /// recadrage, ou la touche tenue à l'ancienne. La bande de vignettes suit la surface —
    /// une fois le mode armé, la molette redresse ici AUSSI, sinon l'opérateur devrait se
    /// souvenir de quelle moitié de l'écran obéit à quelle règle.
    /// </summary>
    private bool TTenue => Surface.RedressementArme || Keyboard.IsKeyDown(Key.T);

    /// <summary>
    /// Note dans le journal ce qu'un geste a réellement reçu.
    ///
    /// Les gestes souris ne se vérifient pas depuis la ligne de commande : aucun test ne
    /// presse C ni ne clique. Sans cette trace, on en est réduit à supposer pourquoi un
    /// raccourci « ne marche pas » — et on s'est trompé plusieurs fois le 01/08/2026.
    /// </summary>
    private void Tracer(string geste, PhotoGridView.PhotoItem? photo = null) =>
        FileLog.Write($"Geste « {geste} » · C={CTenue} T={TTenue} " +
                      $"(armé={Surface.RedressementArme}) " +
                      $"Ctrl={Keyboard.Modifiers.HasFlag(ModifierKeys.Control)}" +
                      (photo is null ? "" : $" · {photo.Name}"));

    private static PhotoGridView.PhotoItem? Cible(object sender) =>
        (sender as FrameworkElement)?.Tag as PhotoGridView.PhotoItem;

    private void OnStripDown(object sender, MouseButtonEventArgs e)
    {
        if (Cible(sender) is not { } photo) return;
        Tracer(nameof(OnStripDown), photo);

        // Maj+clic : toute la PLAGE depuis la dernière vignette touchée. C'est le geste de
        // l'explorateur Windows, et il manquait à cette bande : viser vingt photos qui se
        // suivent demandait vingt Ctrl+clic.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            ViserLaPlage(_ancreVisee ?? _courante, photo);

            // la case à cocher de la vignette ne doit pas basculer par-dessus : le geste
            // est celui de la plage, et lui seul
            e.Handled = true;
            return;
        }

        // Ctrl maintenue : on vise. Rien n'est visé au départ ; Ctrl+clic ajoute une photo
        // à ce que les réglages toucheront, sans rien changer à ce qui sera imprimé.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            photo.Ciblee = !photo.Ciblee;
            _ancreVisee = photo;
            SetCurrent(photo);
            return;
        }

        // UN CLIC SIMPLE NE GARDE QU'ELLE — la case à cocher n'existe plus.
        //
        // La case avait été posée parce que le Ctrl+clic ne s'annonçait nulle part et que
        // personne ne l'avait trouvé. Mais viser une case de quinze pixels dans le coin
        // d'une vignette est un geste de précision, au comptoir, avec un client devant
        // soi : c'est la CIBLE qui gênait, pas l'idée. Retirée le 21/08/2026 à la demande
        // de l'exploitant, le clic prend sa place.
        //
        // ⚠ IL REMPLACE LA SÉLECTION, IL NE S'Y AJOUTE PAS. C'est le geste de
        // l'explorateur Windows, et c'est ce que l'exploitant a demandé le 21/08/2026 :
        // « quand j'en ai plusieurs et que je clique sur une seule, elle doit être la seule
        // sélectionnée ». Une première version basculait la photo cliquée sans toucher aux
        // autres — on ne pouvait donc plus RÉDUIRE une sélection d'un geste, seulement
        // l'agrandir ou la défaire photo par photo.
        //
        // Ctrl+clic garde l'ancien geste — ajouter ou retirer sans toucher au reste — et
        // Maj+clic garde la plage. Ceux qui les ont appris n'ont rien à réapprendre.
        //
        // ⚠ Sauf avec C : ce geste-là recadre à la souris sur la vignette, et faire
        // basculer la sélection sous un recadrage serait une surprise à chaque prise en
        // main.
        //
        // Posé AVANT SetCurrent, qui rafraîchit l'écran : l'inverse aurait montré l'ancienne
        // sélection pendant un instant, puis la nouvelle.
        if (!CTenue)
            foreach (var autre in _photos) autre.Ciblee = ReferenceEquals(autre, photo);

        // l'ancre suit le dernier clic SIMPLE : c'est de là que partira la prochaine plage
        _ancreVisee = photo;
        SetCurrent(photo);

        if (!CTenue) return;

        // C maintenue : on s'apprête à recadrer à la souris sur la vignette
        _gesteOperateur = true;

        _glisse = true;
        _glisseSur = photo;
        _dernierPoint = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void OnStripMove(object sender, MouseEventArgs e)
    {
        if (!_glisse || _glisseSur is null) return;

        var point = e.GetPosition(this);
        var dx = (point.X - _dernierPoint.X) / 200.0;
        var dy = (point.Y - _dernierPoint.Y) / 200.0;
        _dernierPoint = point;

        // on tire la photo sous le cadre : le cadre part donc en sens inverse
        Deplacer(_glisseSur, -dx, -dy);
    }

    private void OnStripUp(object sender, MouseButtonEventArgs e)
    {
        _glisse = false;
        _glisseSur = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnStripWheel(object sender, MouseWheelEventArgs e)
    {
        if (Cible(sender) is not { } photo) return;
        Tracer(nameof(OnStripWheel), photo);
        if (!CTenue && !TTenue) return;

        if (TTenue) Redresser(photo, e.Delta > 0 ? 1 : -1);
        else Zoomer(photo, e.Delta > 0);

        e.Handled = true;
    }

    /// <summary>
    /// Pivoter le cadrage, mais seulement C maintenue : c'est le geste documenté par
    /// DiLand lui-même (« C + Right click » → <c>S_Buttons_RotateCrop</c>). Sans C, le
    /// clic droit ne doit rien faire, sous peine de pivoter un cadre par mégarde.
    /// </summary>
    private void OnStripRightClick(object sender, MouseButtonEventArgs e)
    {
        if (Cible(sender) is not { } photo) return;
        Tracer(nameof(OnStripRightClick), photo);
        if (!CTenue) return;

        SetCurrent(photo);
        PivoterCadre(photo);
        e.Handled = true;
    }

    // Les gestes du grand aperçu vivent désormais dans la surface elle-même
    // (CropSurface), branchée par BrancherSurface : elle n'y demande aucune touche à
    // maintenir, puisqu'elle ne sert qu'à ça.

    // — panneaux —

    private void OnShowCrop(object sender, RoutedEventArgs e) => ShowCrop();
    private void OnShowCorrect(object sender, RoutedEventArgs e) => ShowCorrect();

    private void ShowCrop()
    {
        CropPanel.Visibility = Visibility.Visible;
        CorrectPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowCorrect()
    {
        CropPanel.Visibility = Visibility.Collapsed;
        CorrectPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Change le format du tirage sans repasser par la grille.
    ///
    /// Le cadre se refait tout seul au nouveau format (voir <c>PhotoItem.Product</c>), et
    /// l'aperçu le montre aussitôt : c'est le seul moyen de juger un changement de format
    /// sur une photo, puisqu'un 10×15 et un 13×18 ne coupent pas au même endroit.
    /// </summary>
    private void OnPickFormat(object sender, RoutedEventArgs e)
    {
        if (sender is not Button bouton) return;

        ProductMenu.Ouvrir(bouton, _courante.Product, _courante.Finish,
            (produit, finition) => PoserLeFormat(produit, finition),
            // la taille libre s'applique à TOUTE la commande, pas aux seules photos visées :
            // une planche ne mélange pas deux tailles. L'écran appelant s'en charge et nous
            // reprend la main.
            personnalise: _personnalise,
            // un agrandissement, lui, se pose photo par photo comme n'importe quel format :
            // c'est un vrai produit du catalogue, pas une planche à composer
            agrandissement: () => Navigator.Go(
                new CustomEnlargementView(produit => PoserLeFormat(produit, null)),
                "Agrandissement personnalisé"));
    }

    /// <summary>
    /// Applique un format aux photos visées.
    ///
    /// Le format suit les photos visées, comme les corrections : c'est ainsi qu'on tire trois
    /// photos d'une planche en 13×18 et le reste en 10×15, sans repasser par la grille.
    /// </summary>
    private void PoserLeFormat(Product produit, string? finition)
    {
        var visees = Visees();
        foreach (var photo in visees)
        {
            photo.Product = produit;
            photo.Finish = finition;
            VignetteBientot(photo);
        }

        FileLog.Write($"Format « {produit.Name} » sur {visees.Count} photo(s)");

        // les pixels ne changent pas, mais le cadre oui : la surface doit le relire
        _versionSurface++;
        MontrerSurface();
        Refresh();
    }

    /// <summary>
    /// Les boutons du panneau de recadrage portent sur les photos VISÉES, comme les
    /// corrections et le contour de découpe.
    ///
    /// Ils ne le faisaient pas : ils écrivaient sur <c>_courante</c> et sur elle seule.
    /// Ctrl+A puis « Remplir » ne changeait donc qu'UNE photo sur trente — signalé par
    /// l'exploitant le 04/08/2026. Le même défaut touchait les deux rotations et la
    /// réinitialisation ; on les corrige ensemble, parce que le geste est le même et que
    /// l'opérateur ne distingue pas ces quatre boutons du reste du panneau.
    ///
    /// La ligne de journal n'est pas une politesse : c'est le seul moyen de vérifier après
    /// coup qu'un geste a bien porté sur la planche entière — aucun essai ne clique.
    /// </summary>
    private void SurLesVisees(string geste, Action<PhotoGridView.PhotoItem> action)
    {
        // un bouton du panneau est un geste : le cadrage automatique s'arrête là
        _gesteOperateur = true;

        var visees = Visees();
        foreach (var photo in visees) action(photo);

        FileLog.Write($"{geste} sur {visees.Count} photo(s)");
        Refresh();
    }

    private void OnRotateFrame(object sender, RoutedEventArgs e) =>
        SurLesVisees("Cadre pivoté", PivoterCadre);

    private void OnRotatePhoto(object sender, RoutedEventArgs e) =>
        SurLesVisees("Photo pivotée d'un quart de tour", photo => PivoterPhoto(photo, 1));

    // — avant / après —

    /// <summary>
    /// Les originaux déjà composés, par clé de photo — l'image telle qu'elle est arrivée,
    /// remise d'aplomb mais sans une seule correction.
    ///
    /// Le quart de tour EST appliqué : sans lui, l'original apparaîtrait couché sous un
    /// cadre resté debout, et la comparaison ne dirait plus rien de la retouche.
    /// </summary>
    private readonly CacheImages _originaux = new();

    /// <summary>Vrai tant que le bouton est enfoncé : la surface montre alors l'original.</summary>
    private bool _comparaisonEnCours;

    /// <summary>
    /// Montre la photo telle qu'elle est arrivée, tant que le bouton reste enfoncé.
    ///
    /// <b>Maintenu et non basculé.</b> Un interrupteur laisserait l'écran sur l'original —
    /// devant un client, on croirait la retouche perdue, et rien à l'écran ne dirait que
    /// c'est une vue de comparaison. Relâcher revient toujours au tirage, y compris si la
    /// souris quitte le bouton en cours de route (MouseLeave, LostMouseCapture).
    ///
    /// <b>Le CADRE reste affiché.</b> C'est lui qui montre le cadrage : on voit donc du même
    /// coup ce que la photo était et ce que le tirage en gardera. Le retirer ferait sauter
    /// l'image sous les yeux sans rien apprendre de plus.
    /// </summary>
    private async void OnComparerAppuye(object sender, MouseButtonEventArgs e)
    {
        if (_comparaisonEnCours) return;
        _comparaisonEnCours = true;

        var photo = _courante;

        try
        {
            if (!_originaux.TryGet(photo.Cle, out var original))
            {
                // la haute définition si elle est déjà là, la vignette sinon : c'est la
                // même source que la surface montre, donc la même finesse
                var source = _hautesDefinitions.TryGet(photo.Path, out var haute)
                    ? haute
                    : photo.SourceThumbnail;
                if (source is null) return;

                var quarts = photo.RotationQuarterTurns;

                // réglages NEUFS : c'est tout l'objet de la comparaison
                original = await Task.Run(
                    () => PhotoGridView.PhotoItem.ComposerPhoto(source, quarts, new ImageAdjustments()));

                _originaux.Set(photo.Cle, original);
            }

            // relâché pendant le calcul, ou photo changée : on ne montre plus rien
            if (!_comparaisonEnCours || !ReferenceEquals(photo, _courante)) return;

            Surface.UpdatePhoto(original);
            ComparerButton.Content = "👁  Original — relâchez";
        }
        catch (Exception ex)
        {
            FileLog.Write("Avant/après : original impossible à préparer", ex);
            _comparaisonEnCours = false;
        }
    }

    private void OnComparerRelache(object sender, RoutedEventArgs e)
    {
        if (!_comparaisonEnCours) return;
        _comparaisonEnCours = false;

        ComparerButton.Content = "👁  Voir l'original   (maintenir)";
        MontrerSurface();
    }

    /// <summary>
    /// Un original ne vaut plus quand la photo a tourné d'un quart de tour : c'est le seul
    /// réglage qui entre dans sa composition. Les curseurs, eux, ne le concernent pas —
    /// c'est justement ce qu'on veut comparer.
    /// </summary>
    private void PerimerLOriginal(PhotoGridView.PhotoItem photo) => _originaux.Remove(photo.Cle);

    // — la quantité —

    /// <summary>
    /// Une de moins, une de plus, sur les photos VISÉES.
    ///
    /// <b>Elle ne se réglait qu'à l'écran de sélection</b>, alors qu'elle est écrite sur
    /// chaque vignette de celui-ci : on la lisait sans pouvoir la changer, et « j'en veux
    /// finalement trois » obligeait à ressortir, retrouver la vignette et rouvrir
    /// « Modifier ». Demandé le 18/08/2026.
    ///
    /// <b>Le pas part de la photo AFFICHÉE</b>, et non d'un compteur propre à l'écran :
    /// les photos visées peuvent porter des quantités différentes, et un compteur commun
    /// les aurait toutes alignées sur sa valeur au premier clic — en écrasant sans le dire
    /// ce que l'opérateur avait posé photo par photo.
    /// </summary>
    private void OnQuantiteMoins(object sender, RoutedEventArgs e) => ChangerLaQuantite(-1);

    private void OnQuantitePlus(object sender, RoutedEventArgs e) => ChangerLaQuantite(+1);

    /// <summary>
    /// Décale la quantité des photos visées. Bornes <b>0</b>..99.
    ///
    /// <b>Zéro est une réponse, et c'est celle qu'on attendait ici.</b> Le plancher était à
    /// un : descendre une photo déjà à un exemplaire ne faisait rien, et retirer une photo
    /// d'une commande obligeait à ressortir vers l'écran de sélection pour la décocher.
    /// Demandé par l'exploitant le 21/08/2026 : « moins sur une photo déjà à 1, elle ne doit
    /// pas sortir ».
    ///
    /// La vignette le dit alors d'une croix — voir <c>PhotoItem.NeSortPas</c> : une quantité
    /// à zéro qui ne se verrait pas serait exactement le genre de silence qui fait rendre la
    /// monnaie pour un tirage qui n'est jamais sorti.
    /// </summary>
    private void ChangerLaQuantite(int pas)
    {
        var visees = Visees();

        foreach (var photo in visees)
            photo.Quantity = Math.Clamp(photo.Quantity + pas, 0, 99);

        FileLog.Write(
            $"Quantité {(pas > 0 ? "montée" : "descendue")} d'un cran sur {visees.Count} photo(s) " +
            "depuis l'écran de modification");

        Refresh();
    }

    /// <summary>
    /// Le contour de découpe, posé sur toutes les photos VISÉES — comme les corrections.
    ///
    /// Une commande de vingt tirages à marges blanches se recoupe entière : cocher photo par
    /// photo n'aurait servi personne.
    /// </summary>
    private void OnCutBorderChanged(object sender, RoutedEventArgs e)
    {
        var actif = CutBorderCheck.IsChecked == true;
        var visees = Visees();

        foreach (var photo in visees) photo.CutBorder = actif;

        // Il manquait TOUT retour : la case posait bien le contour — il sortait au
        // tirage — mais rien ne bougeait à l'écran, ni ici ni sur la vignette. Elle
        // passait donc pour morte. Signalé le 04/08/2026.
        Surface.ContourDeDecoupe = _courante.CutBorder;
        foreach (var photo in visees) Redessiner(photo);

        FileLog.Write($"Contour de découpe {(actif ? "activé" : "retiré")} sur {visees.Count} photo(s)");

        // et l'on relit l'état : sans cela, la case affichait encore celui d'avant dès que
        // la photo courante n'était pas du lot visé
        Refresh();
    }

    /// <summary>
    /// Bascule « remplir le format » / « photo entière » sur toutes les photos visées.
    ///
    /// Le mode voulu est déduit de la photo COURANTE, puis IMPOSÉ aux autres — il n'est pas
    /// basculé photo par photo. Sur une planche à moitié en « remplir » et à moitié en
    /// « entier », basculer chacune de son côté les inverserait sans jamais les aligner,
    /// alors que le geste demandé est « mets-moi tout en remplir ».
    /// </summary>
    private void OnToggleFit(object sender, RoutedEventArgs e)
    {
        var produit = _courante.Product;
        if (produit is null) return;

        var actuel = _courante.FitOverride ?? produit.DefaultFit;

        // le Polaroid ne se bascule pas : sortir de ce mode donnerait un tirage sans cadre
        // que rien ne signalerait. Le bouton est grisé, ceci n'est qu'une ceinture de plus.
        if (actuel == FitMode.Polaroid) return;

        var voulu = actuel == FitMode.Fill ? FitMode.Fit : FitMode.Fill;

        SurLesVisees($"Mode « {(voulu == FitMode.Fill ? "remplir le format" : "photo entière")} » posé", photo =>
        {
            // le produit se lit sur CHAQUE photo : une planche peut mélanger deux formats,
            // et le mode ne s'exprime que par rapport au défaut de son propre produit
            if (photo.Product is not { } sien || (photo.FitOverride ?? sien.DefaultFit) == FitMode.Polaroid)
                return;

            photo.FitOverride = voulu == sien.DefaultFit ? null : voulu;
            Redessiner(photo);
        });
    }

    /// <summary>
    /// Repart de zéro : plus de quart de tour, plus de redressement, et le cadre au
    /// milieu de la photo.
    ///
    /// Le cadre doit être remis lui aussi, et pas seulement le <c>CropSpec</c> : c'est lui
    /// qui porte la vérité, et le laisser en place ferait revenir l'ancien cadrage au
    /// premier geste suivant.
    /// </summary>
    private void OnResetCrop(object sender, RoutedEventArgs e) =>
        SurLesVisees("Recadrage réinitialisé", photo =>
        {
            photo.RotationQuarterTurns = 0;
            photo.FineRotationDegrees = 0;
            photo.OublierCadre();

            Perimer(photo);
            Redessiner(photo);
        });

    /// <summary>Le cadrage de la photo courante, repris sur toute la planche d'un geste.</summary>
    private void OnCropToAll(object sender, RoutedEventArgs e)
    {
        foreach (var photo in Visees())
        {
            photo.Crop = _courante.Crop;
            photo.RotationQuarterTurns = _courante.RotationQuarterTurns;
            photo.FitOverride = _courante.FitOverride;
            Redessiner(photo);
        }
    }

    // — corrections —

    /// <summary>
    /// Noir et blanc — le <c>Grayscale</c> du modèle, déjà appliqué par le rendu et par
    /// la grille (Ctrl+W). Il manquait seulement ici, sur l'écran où l'on corrige.
    /// </summary>
    private void OnGrayscaleChanged(object sender, RoutedEventArgs e)
    {
        var actif = GrayscaleToggle.IsChecked == true;
        Regler($"noir et blanc {(actif ? "activé" : "annulé")}", a => a.Grayscale = actif);
    }

    private void OnRedEyeChanged(object sender, RoutedEventArgs e)
    {
        var actif = RedEyeToggle.IsChecked == true;
        Regler($"yeux rouges {(actif ? "enlevés" : "conservés")}", a => a.RedEye = actif);
    }

    private void OnAutoChanged(object sender, RoutedEventArgs e)
    {
        var (niveaux, contraste, couleur) =
            (AutoLevelsToggle.IsChecked == true,
             AutoContrastToggle.IsChecked == true,
             AutoColorToggle.IsChecked == true);

        Regler($"auto niveaux={niveaux} contraste={contraste} couleur={couleur}", a =>
        {
            a.AutoLevels = niveaux;
            a.AutoContrast = contraste;
            a.AutoColor = couleur;
        });
    }

    private void OnResetAdjustments(object sender, RoutedEventArgs e)
    {
        // remise à neuf complète : on repart d'un objet vierge plutôt que de remettre
        // chaque champ à zéro, pour qu'un réglage ajouté plus tard ne soit pas oublié ici
        foreach (var photo in Visees()) photo.Adjustments = new ImageAdjustments();

        Regler("corrections annulées", _ => { });
        Refresh(); // les curseurs et les bascules doivent revenir à zéro eux aussi
    }

    /// <summary>
    /// Applique un réglage à TOUTES les photos cochées — c'est le modèle de DiLand, et
    /// celui que l'opérateur a en tête.
    ///
    /// Chaque bouton ne touchait que la photo courante : ré-appuyer dessus ne défaisait
    /// donc la correction que sur elle, les autres la gardaient, et « Annuler les
    /// corrections » n'en libérait qu'une sur trente-deux. Signalé le 01/08/2026.
    ///
    /// Pour ne corriger qu'une photo, Ctrl+A décoche tout : le réglage retombe alors sur
    /// la seule photo courante (voir <see cref="Visees"/>).
    /// </summary>
    private void Regler(string quoi, Action<ImageAdjustments> reglage)
    {
        var visees = Visees();
        foreach (var photo in visees)
        {
            reglage(photo.Adjustments);
            Perimer(photo);
            VignetteBientot(photo);
        }

        // aucun test ne clique : sans cette trace, on ne saurait pas ce qu'un bouton a
        // réellement touché ni sur combien de photos
        FileLog.Write($"Correction « {quoi} » sur {visees.Count} photo(s)");

        MontrerSurface();
        MettreLeCompteAJour();
    }

    /// <summary>Une correction change les pixels : la photo de la surface est à refaire.</summary>
    private void Corrigee(PhotoGridView.PhotoItem photo)
    {
        Perimer(photo);
        Redessiner(photo);
    }

    /// <summary>Un curseur du panneau, façon Lightroom.</summary>
    private sealed class Reglage : ObservableObject
    {
        private readonly Func<ImageAdjustments, double> _lire;
        private readonly Action<double> _appliquer;
        private double _valeur;

        /// <param name="lire">Relit la valeur sur la photo courante, pour placer le curseur.</param>
        /// <param name="appliquer">Porte la valeur sur toutes les photos visées.</param>
        public Reglage(string nom, double min, double max,
            Func<ImageAdjustments, double> lire, Action<double> appliquer)
        {
            Nom = nom;
            Min = min;
            Max = max;
            _lire = lire;
            _appliquer = appliquer;
        }

        public string Nom { get; }
        public double Min { get; }
        public double Max { get; }

        public string Affichage => _valeur.ToString(Max <= 5 ? "+0.00;-0.00;0" : "+0;-0;0");

        public double Valeur
        {
            get => _valeur;
            set
            {
                if (!Set(ref _valeur, value)) return;
                OnPropertyChanged(nameof(Affichage));

                if (_relecture) return; // on replace le curseur, ce n'est pas un geste
                _appliquer(value);
            }
        }

        private bool _relecture;

        /// <summary>Reprend la valeur de la photo courante, sans rien appliquer.</summary>
        public void Relire(ImageAdjustments cible)
        {
            _relecture = true;
            try
            {
                _valeur = _lire(cible);
                OnPropertyChanged(nameof(Valeur));
                OnPropertyChanged(nameof(Affichage));
            }
            finally
            {
                _relecture = false;
            }
        }
    }

    private List<Reglage> ConstruireReglages()
    {
        // chaque curseur porte sur les photos cochées, comme les bascules au-dessus
        Reglage Curseur(string nom, double min, double max,
            Func<ImageAdjustments, double> lire, Action<ImageAdjustments, double> ecrire) =>
            new(nom, min, max, lire,
                valeur => Regler($"{nom} → {valeur:0.##}", a => ecrire(a, valeur)));

        return
        [
            Curseur("Exposition (IL)", -2, 2, a => a.Exposure, (a, v) => a.Exposure = v),
            Curseur("Contraste", -100, 100, a => a.Contrast, (a, v) => a.Contrast = v),
            Curseur("Hautes lumières", -100, 100, a => a.Highlights, (a, v) => a.Highlights = v),
            Curseur("Ombres", -100, 100, a => a.Shadows, (a, v) => a.Shadows = v),
            Curseur("Blancs", -100, 100, a => a.Whites, (a, v) => a.Whites = v),
            Curseur("Noirs", -100, 100, a => a.Blacks, (a, v) => a.Blacks = v),
            Curseur("Température", -100, 100, a => a.Temperature, (a, v) => a.Temperature = v),
            Curseur("Teinte", -100, 100, a => a.Tint, (a, v) => a.Tint = v),
            Curseur("Vibrance", -100, 100, a => a.Vibrance, (a, v) => a.Vibrance = v),
            Curseur("Saturation", -100, 100, a => a.Saturation, (a, v) => a.Saturation = v),
            Curseur("Clarté", -100, 100, a => a.Clarity, (a, v) => a.Clarity = v),
            Curseur("Netteté", 0, 100, a => a.Sharpness, (a, v) => a.Sharpness = v),
        ];
    }

    // — sortie —

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnPrint(object sender, RoutedEventArgs e) => _imprimer();

    private void OnMettreEnAttente(object sender, RoutedEventArgs e) => _mettreEnAttente?.Invoke();

    /// <summary>
    /// Reproduit les photos visées, pour les tirer dans un second format.
    ///
    /// <b>Ce sont les DOUBLONS qui restent visés</b>, et les originaux qui sont relâchés :
    /// le geste suivant est toujours « et maintenant, en 15×20 ». Viser les deux ferait
    /// changer le format des originaux du même coup, et l'on n'aurait rien gagné.
    ///
    /// La duplication passe par la GRILLE (voir <c>PhotoGridView.DupliquerPhoto</c>) : elle
    /// seule tient la liste que l'impression parcourt.
    /// </summary>
    private void OnDupliquer(object sender, RoutedEventArgs e)
    {
        if (_dupliquer is null) return;

        var visees = Visees();
        if (visees.Count == 0) return;

        var doublons = new List<PhotoGridView.PhotoItem>(visees.Count);

        foreach (var photo in visees)
        {
            var copie = _dupliquer(photo);

            // le doublon se range juste après son original, ici aussi : la bande doit
            // montrer le même ordre que la planche
            var rang = _photos.IndexOf(photo);
            if (rang < 0) _photos.Add(copie);
            else _photos.Insert(rang + 1, copie);

            photo.Ciblee = false;
            copie.Ciblee = true;
            doublons.Add(copie);
        }

        // _photos est une List : sans cette remise en place, les doublons n'apparaîtraient
        // pas dans la bande
        Strip.ItemsSource = null;
        Strip.ItemsSource = _photos;

        SetCurrent(doublons[0]);
        Refresh();
    }
}
