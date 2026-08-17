using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;
using Studio.Printing;
using Studio.Sources;
using Studio.Store;

namespace Studio.App.Views;

/// <summary>
/// Photos d'identité : détection du visage → pré-cadrage 35×45 conforme,
/// gabarit surimprimé (vert quand conforme), ajustement manuel, impression
/// de la planche (produit à SheetSpec).
/// </summary>
public partial class IdPhotoView : UserControl, ITravailReprenable
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
    private static readonly Brush NeutralBrush = Brushes.White;

    private readonly string _rootPath;
    private readonly bool _avecSousDossiers;
    private readonly List<StripItem> _photos = new();
    private CancellationTokenSource? _loadCts;
    private int _quantity = 1;
    private int _copies = 6;   // photos sur la planche ; recalé sur le produit à la sélection

    /// <summary>
    /// Photos par planche VOULUES par le raccourci qui a ouvert cet écran — « France —
    /// planche de 6 » —, ou null quand personne n'en a demandé.
    ///
    /// <b>Ce n'est pas le nombre courant, c'est le DÉFAUT.</b> Partout où l'écran repartait
    /// de la planche pleine — au changement de papier, à l'ouverture d'une photo jamais
    /// réglée — il repart désormais de ce nombre-ci quand il existe. L'opérateur reste libre
    /// de le monter ou de le descendre au compteur : le raccourci pose le point de départ,
    /// il ne verrouille rien.
    /// </summary>
    private readonly int? _copiesVoulues;

    private StripItem? _current;
    private BitmapSource? _displayBitmap;

    /// <summary>Aperçu détouré, quand « fond blanc » est coché. Null sinon.</summary>
    private BitmapSource? _detoure;

    /// <summary>
    /// Corrections d'image posées par le module « Corriger », hors noir et blanc et fond
    /// blanc — ces deux-là restent des cases de cet écran.
    ///
    /// Elles manquaient : une photo d'identité prise au comptoir est sous-exposée ou tire
    /// au jaune comme n'importe quelle autre, et il n'y avait ici aucun moyen de la
    /// reprendre. Demandé par l'exploitant le 04/08/2026.
    ///
    /// Elles valent pour la photo COURANTE : changer de photo dans la bande les remet à
    /// neutre. Une planche d'identité ne porte qu'un visage — appliquer les réglages de
    /// l'un à l'autre n'aurait pas de sens, et se remarquerait au tirage.
    /// </summary>
    private ImageAdjustments _corrections = new();

    /// <summary>Aperçu corrigé, gardé pour ne pas repasser ImageMagick à chaque redessin.</summary>
    private BitmapSource? _corrige;

    /// <summary>
    /// La photo de départ de l'aperçu, désossée en pixels BGRA une bonne fois.
    ///
    /// <b>L'aperçu passait par un PNG à chaque mouvement de curseur</b> : l'écran encodait
    /// la vignette, ImageMagick la décodait, corrigeait, réencodait, et l'écran redécodait.
    /// Quatre compressions pour bouger un curseur — 300 des 950 ms que coûtait un réglage
    /// (mesuré le 11/08/2026). Les mêmes pixels bruts se lisent en 6 ms et se rendent en 7.
    ///
    /// Le tableau ne change jamais après sa lecture : il se partage donc sans risque avec
    /// le fil qui calcule, là où une image vivante demanderait un verrou.
    /// </summary>
    private byte[]? _departBgra;
    private int _departLargeur;
    private int _departHauteur;

    /// <summary>L'image dont <see cref="_departBgra"/> a été tiré, pour savoir quand le refaire.</summary>
    private BitmapSource? _departSource;

    /// <summary>
    /// Compteur des pixels de départ : il avance à chaque fois qu'ils sont relus.
    ///
    /// Il sert à nommer l'image auprès du détourage. Le chemin de la photo seul ne
    /// suffirait pas — poser un fond blanc change les pixels sans changer le fichier, et
    /// c'est l'ancien masque qui ressortirait.
    /// </summary>
    private int _departNumero;

    /// <summary>
    /// La minuterie qui fait avancer la barre du détourage, et le chronomètre qu'elle lit.
    ///
    /// Le détourage ne sait pas dire où il en est — c'est un passage de réseau de neurones,
    /// opaque du début à la fin. La barre avance donc sur le TEMPS, rapporté à ce que ce
    /// poste a mis la dernière fois. C'est une promesse, pas une mesure : elle s'arrête
    /// juste avant le bout tant que le travail n'est pas fini, plutôt que d'annoncer une
    /// fin qui n'est pas venue.
    /// </summary>
    private DispatcherTimer? _attenteTimer;
    private readonly Stopwatch _attenteChrono = new();
    private TimeSpan _attenteEstimee;
    private string _attenteQuoi = "";

    /// <summary>
    /// Le recalcul d'aperçu en cours, à abandonner dès qu'un curseur rebouge.
    ///
    /// Jamais libéré explicitement : il ne porte pas de minuterie, et le libérer pendant
    /// qu'un autre fil l'attend le ferait éclater. Le ramasse-miettes s'en charge.
    /// </summary>
    private CancellationTokenSource? _apercuCts;

    /// <summary>
    /// Un seul aperçu calculé à la fois.
    ///
    /// L'abandon ne suffit pas : un calcul déjà parti continue jusqu'au bout dans son fil.
    /// Sans cette file, un glissement de curseur laissait derrière lui une dizaine de
    /// calculs vivants — et, quand la correction du sujet est allumée, autant de détourages
    /// menés de front sur la carte graphique.
    /// </summary>
    private readonly SemaphoreSlim _apercuFile = new(1, 1);

    /// <summary>
    /// Le temps qu'on laisse au curseur de se poser avant de recalculer, en millisecondes.
    ///
    /// Un glissement de souris produit des dizaines d'événements. Sans cette pause, chacun
    /// lançait son calcul complet ; avec elle, seul le dernier — celui que l'opérateur
    /// voulait voir — arrive au bout.
    ///
    /// Il valait 200 ms quand un aperçu coûtait presque une seconde. Le calcul étant
    /// retombé sous les 100 ms, la pause peut suivre : c'est désormais elle qui se
    /// remarquerait, pas le calcul.
    /// </summary>
    private const int DelaiDuCurseurMs = 60;

    /// <summary>
    /// Redressement, en degrés — le « Tilt » de DiLand.
    ///
    /// Une photo d'identité prise à main levée penche presque toujours d'un demi-degré ou
    /// deux, et le guichet le voit. Le geste est celui des autres écrans qui recadrent
    /// (T maintenue + molette), pour ne pas faire réapprendre l'outil.
    /// </summary>
    private double _redressement;

    /// <summary>Pas de redressement, en degrés, par cran de molette.</summary>
    private const double PasDeRedressement = 0.25;

    /// <summary>Redressement maximal admis, en degrés, de part et d'autre.</summary>
    private const double RedressementMax = 15;
    private CropSpec _crop = CropSpec.Full;
    private NormRect? _head;

    private Point _dragLast;
    private bool _dragging;

    /// <summary>
    /// Document visé. Toute la géométrie en découle : format du tirage, bornes du visage,
    /// taille des ovales du gabarit. Un passeport espagnol fait 26 × 32 mm là où le
    /// français fait 35 × 45 — afficher le gabarit français sur l'un ou l'autre
    /// donnerait une planche refusée au guichet.
    /// </summary>
    private IdDocumentSpec _document = IdDocumentSpec.France;

    /// <summary>
    /// Repères du sommet du crâne et du bas du menton, posés par la détection du visage.
    ///
    /// Ils ne se voient plus — les anneaux qui les portaient ont été retirés le 11/08/2026,
    /// ils gênaient la lecture du visage — mais ils font toujours tout le travail : c'est
    /// d'eux que sortent la hauteur de tête, le cadre et le verdict de conformité.
    /// </summary>
    private NormPoint? _crown;
    private NormPoint? _chin;

    /// <summary>
    /// Axe vertical du visage, en fraction de la largeur de l'image.
    ///
    /// Les deux repères ne mesurent qu'une HAUTEUR — du sommet du crâne au bas du menton —
    /// et c'est elle seule qui fixe le cadre. Ils partagent donc cet axe, et c'est le
    /// cadre qu'on déplace pour recentrer le visage.
    ///
    /// Il est enregistré avec le travail : une reprise doit retrouver le cadrage tel qu'il
    /// a été laissé, axe compris.
    /// </summary>
    private double _axeVisage = 0.5;

    /// <summary>
    /// Les photos imposées par l'écran de sélection, dans SON ordre. Null = on scanne le
    /// dossier, comme avant.
    ///
    /// Les deux entrées coexistent : le parcours normal passe par la sélection, mais
    /// « Modifier » sur une commande du jour rouvre un dossier de commande entier, où tout
    /// est à traiter.
    /// </summary>
    private readonly IReadOnlyList<string>? _cheminsImposes;

    /// <param name="chemins">Photos retenues à l'écran précédent, dans l'ordre du choix.</param>
    /// <param name="document">Norme visée. Null = norme française.</param>
    /// <param name="photosParPlanche">Photos par planche imposées, ou null pour la planche pleine.</param>
    public IdPhotoView(IReadOnlyList<string> chemins, IdDocumentSpec? document = null,
        int? photosParPlanche = null)
        : this("", document, false, chemins, photosParPlanche)
    {
    }

    /// <param name="rootPath">Dossier des photos.</param>
    /// <param name="document">
    /// Norme visée. Null = norme française, le cas courant de la boutique.
    /// </param>
    /// <param name="avecSousDossiers">Descendre ou non sous <paramref name="rootPath"/>.</param>
    /// <param name="photosParPlanche">Photos par planche imposées, ou null pour la planche pleine.</param>
    public IdPhotoView(string rootPath, IdDocumentSpec? document = null,
        bool avecSousDossiers = true, int? photosParPlanche = null)
        : this(rootPath, document, avecSousDossiers, null, photosParPlanche)
    {
    }

    /// <summary>
    /// Reprend une planche mise de côté, telle qu'elle a été laissée : même norme, mêmes
    /// photos, mêmes repères, même photo affichée.
    /// </summary>
    /// <param name="travail">Le travail mis de côté ; son <c>Identite</c> ne doit pas être null.</param>
    public IdPhotoView(TravailEnAttente travail)
        : this(travail?.PhotosDirectory ?? "",
               DocumentDe(travail?.Identite ?? throw new ArgumentNullException(nameof(travail))),
               travail.AvecSousDossiers,
               travail.Identite.Chemins.Count > 0 ? travail.Identite.Chemins : null)
    {
        _enAttente = travail;
        _attenteId = travail.Id;
    }

    private IdPhotoView(string rootPath, IdDocumentSpec? document, bool avecSousDossiers,
        IReadOnlyList<string>? chemins, int? photosParPlanche = null)
    {
        _rootPath = rootPath;
        _avecSousDossiers = avecSousDossiers;
        _cheminsImposes = chemins;
        _document = document ?? IdDocumentSpec.France;
        _copiesVoulues = photosParPlanche;
        InitializeComponent();

        TitleText.Text = _document.Country == "France"
            ? $"Photo d'identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
            : $"{_document.Country} — {_document.Document} ({_document.WidthMm:0.#}×{_document.HeightMm:0.#} mm)";

        // La carte du panneau, qui dit la norme visée et s'ouvre pour en changer.
        DocumentText.Text = _document.Country == "France"
            ? $"France · {_document.WidthMm:0.#} × {_document.HeightMm:0.#} mm"
            : $"{_document.Country} · {_document.Document} · {_document.WidthMm:0.#} × {_document.HeightMm:0.#} mm";

        // La capacité est celle du DOCUMENT visé, pas celle inscrite au produit : un
        // passeport espagnol (26 × 32) tient à douze sur le papier où le français (35 × 45)
        // tient à huit. Les papiers qui ne peuvent pas porter une seule case sont écartés
        // de la liste plutôt que proposés puis refusés à l'impression.
        var sheetProducts = App.Services.Catalog.Enabled
            .Where(p => p.Sheet is not null)
            .Select(p => new ProductChoice(p, CapaciteDe(p, _document)))
            .Where(c => c.Capacite > 0)
            .ToList();
        ProductCombo.ItemsSource = sheetProducts;
        ProductCombo.SelectedIndex = 0;

        // sans produit « planche » actif, l'écran était muet : combo vide, bouton grisé,
        // aucune explication. On le dit à l'opérateur, qui peut activer le produit au Catalogue.
        if (sheetProducts.Count == 0)
            Loaded += (_, _) => MessageBox.Show(
                $"Aucun papier du catalogue ne peut porter une photo de " +
                $"{_document.WidthMm:0.#} × {_document.HeightMm:0.#} mm.\n\n" +
                "Ouvrez Catalogue et activez (ou créez) un produit de type planche assez grand " +
                "pour ce document.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);

        Loaded += async (_, _) => await LoadStripAsync();
        Unloaded += (_, _) => _loadCts?.Cancel();

        // Le mode redressement se capte sur la FENÊTRE : voir _redressementArme. Sur cet
        // écran-ci, le focus part sur la liste des papiers dès que l'opérateur choisit son
        // tirage — la touche ne serait jamais revenue jusqu'ici.
        //
        // L'abonnement passe par ToucheFenetre : posé à la main, un second Loaded le
        // doublait et T — qui est une bascule — jouait deux fois, donc le mode ne
        // s'armait jamais. Voir ToucheFenetre.
        _ = new ToucheFenetre(this, OnFenetreKeyDown, auDepart: () => RedressementArme = false);
    }

    /// <param name="Capacite">Cases du document visé qui tiennent sur ce papier.</param>
    private sealed record ProductChoice(Product Product, int Capacite)
    {
        public string Label => $"{Product.Name} — {Capacite} par planche — {Product.Price:0.00} €";
    }

    /// <summary>
    /// Nombre de photos du document visé qui tiennent sur ce papier, 0 si pas même une.
    ///
    /// Le calcul se fait en PIXELS et par <see cref="IdSheetLayout.MaxCopies"/> — celui-là
    /// même qui posera la grille au rendu. Compter en millimètres donnerait parfois une case
    /// de plus que la planche n'accepte, et l'impression échouerait après l'annonce du prix.
    ///
    /// <b>La bande basse est comptée avec.</b> Sans elle dans le calcul, les documents aux
    /// petites cases — un passeport étranger de 26 × 32 contre 35 × 45 en France —
    /// remplissaient la planche jusqu'en bas, et la date n'avait plus où être écrite : elle
    /// disparaissait du tirage sans que rien ne le signale. Une photo de moins vaut mieux
    /// qu'une planche sans date, qui ne prouve plus qu'elle est récente.
    /// </summary>
    private static int CapaciteDe(Product product, IdDocumentSpec document)
    {
        if (product.Sheet is not { } sheet) return 0;

        // Ce que la bande exige VRAIMENT : la place d'écrire une date, pas sa hauteur
        // nominale. Compter les 8 mm de la bande complète coûtait une rangée entière sur
        // les formats carrés — six photos ramenées à trois sur un passeport américain.
        var bande = sheet.DateStamp
            ? SheetFooterLayout.ReserveMinimalePx(
                SheetFooter.Pour(DateTime.Now, App.Services.Marque), product.Dpi)
            : 0;

        // La MEILLEURE des deux orientations : le rendu tournera le papier si le document
        // y tient davantage — un carré de 50 mm passe de trois photos à quatre.
        return IdSheetLayout.MeilleureCapacite(
            MmPx.ToPixels(product.WidthMm, product.Dpi),
            MmPx.ToPixels(product.HeightMm, product.Dpi),
            MmPx.ToPixels(document.WidthMm, product.Dpi),
            MmPx.ToPixels(document.HeightMm, product.Dpi),
            // l'écart RÉEL, celui que le rendu appliquera : à fond perdu il se réduit au
            // trait de découpe, et compter avec 2 mm annoncerait moins de photos que la
            // planche n'en porte (voir SheetSpec.LayoutGapMm)
            MmPx.ToPixels(sheet.LayoutGapMm, product.Dpi),
            bande).Copies;
    }

    private async Task LoadStripAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        if (_photos.Count == 0)
        {
            // Photos imposées par l'écran de sélection : SON ordre, et rien d'autre. Le
            // rescanner ramènerait les quatre-vingts photos de la carte, dont l'opérateur
            // vient justement de désigner trois.
            //
            // Sinon on scanne, la plus récente en premier comme sur la grille des tirages :
            // la photo d'identité qu'on vient de prendre est en bout de carte.
            //
            // Les PDF sont écartés : on ne fait pas une photo d'identité depuis un
            // document, et la détection de visage n'aurait rien à y chercher.
            var files = _cheminsImposes is not null
                ? _cheminsImposes.Where(f => !PhotoScanner.IsPdf(f)).ToList()
                : await Task.Run(
                    () => PhotoScanner.TrierParDateDecroissante(
                            PhotoScanner.Scan(_rootPath, _avecSousDossiers, PhotoScanner.MaxAffichable, ct)
                                .Where(f => !PhotoScanner.IsPdf(f)))
                        .ToList(),
                    ct);

            // ⚠ QUI ENTRE DANS LE LOT, ET QUI N'EST QUE MONTRÉ.
            //
            // Les photos venues de l'écran de sélection ONT ÉTÉ CHOISIES : l'opérateur les a
            // désignées une à une, elles partent donc à une planche chacune.
            //
            // Celles d'une carte mémoire, NON. La bande les montre toutes — c'est son rôle,
            // et l'opérateur y cherche celle du client — mais rien n'a encore été demandé.
            // Elles entrent dans le lot en s'ouvrant (voir ReprendreDeLaPhoto).
            //
            // Sans cette distinction, ouvrir Studio Photo Identité sur une carte de
            // quatre-vingts photos et toucher « Imprimer » sortait QUATRE-VINGTS PLANCHES.
            // Signalé depuis Arcueil le 17/08/2026.
            var choisiesDavance = _cheminsImposes is not null;

            var rang = 0;
            foreach (var file in files)
                _photos.Add(new StripItem(file)
                {
                    Rang = ++rang,
                    Quantite = LotIdentite.QuantiteDeDepart(choisiesDavance),
                });
            PhotoStrip.ItemsSource = _photos;

            // le travail mis de côté est reposé AVANT toute ouverture : ouvrir d'abord
            // relancerait la détection de visage sur une photo dont les repères sont
            // pourtant déjà connus
            if (_enAttente?.Identite is { } garde) AppliquerLAttente(garde);

            // sans photo, la bande reste vide : on le dit là où l'aperçu s'afficherait
            if (_photos.Count == 0)
                EmptyText.Text = "Aucune photo dans ce dossier — revenez en arrière " +
                                 "pour en choisir un autre.";

            AnnoncerLeLot();

            // La première photo s'ouvre TOUTE SEULE quand elles ont été choisies à l'écran
            // précédent : l'opérateur vient de les désigner, lui redemander de cliquer sur
            // la première serait un clic pour rien. Sur un dossier scanné, en revanche, il
            // n'a encore rien choisi — l'écran attend.
            // Une planche reprise rouvre la photo qu'on regardait, et non la première :
            // l'opérateur repart exactement là où il s'était arrêté.
            var aOuvrir = _enAttente?.Identite?.PhotoCourante is { Length: > 0 } nom
                ? _photos.FirstOrDefault(p => string.Equals(p.Name, nom, StringComparison.OrdinalIgnoreCase))
                : _cheminsImposes is not null ? _photos.FirstOrDefault() : null;

            if (aOuvrir is not null) await OuvrirLaPhotoAsync(aOuvrir);
        }

        var thumbnails = App.Services.Thumbnails;
        foreach (var photo in _photos)
        {
            if (ct.IsCancellationRequested) return;
            if (photo.Thumbnail is not null) continue;
            try
            {
                var bytes = await Task.Run(() => thumbnails.GetJpeg(photo.Path, 220), ct);
                photo.Thumbnail = ToBitmap(bytes);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { }
        }
    }

    private static BitmapImage ToBitmap(byte[] jpegBytes)
    {
        using var stream = new MemoryStream(jpegBytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ----- choix de la photo -----

    private async void OnStripPhotoClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not StripItem item) return;
        await OuvrirLaPhotoAsync(item);
    }

    /// <summary>
    /// Met la photo À L'ABRI du retrait de la carte.
    ///
    /// <b>Ce que ça règle.</b> La photo était lue sur son support jusqu'au bout — carte du
    /// client, téléphone, clé. L'opérateur cadre, corrige, détoure, et si la carte quitte le
    /// lecteur avant l'impression, tout est perdu : plus de pixels à relire. Au comptoir, un
    /// client qui reprend sa carte pendant qu'on travaille, c'est un geste banal. Signalé
    /// depuis Arcueil le 14/08/2026.
    ///
    /// La copie se fait à l'OUVERTURE, pas au chargement de la bande : recopier une carte
    /// de quatre cents photos pour en tirer une seule serait absurde. Une photo ouverte,
    /// une copie — c'est-à-dire une poignée par client.
    ///
    /// Le nom du fichier du client est conservé (avec une empreinte courte contre les
    /// collisions) : c'est lui qu'on lit dans la commande et dans les messages.
    ///
    /// <b>Un échec de copie n'arrête rien</b> : on continue à lire depuis le support, comme
    /// avant. Perdre la mise à l'abri est ennuyeux ; perdre la photo à l'écran le serait
    /// bien davantage.
    /// </summary>
    private static async Task MettreALAbriAsync(StripItem item)
    {
        var source = item.Path;

        // déjà chez nous : rien à copier (une reprise, ou une photo déjà mise à l'abri)
        if (source.StartsWith(App.Services.DataRoot, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            var dossier = Path.Combine(App.Services.CacheDir, "travail",
                DateTime.Now.ToString("yyyyMMdd"));
            Directory.CreateDirectory(dossier);

            var empreinte = Math.Abs(source.GetHashCode()).ToString("x8");
            var copie = Path.Combine(dossier,
                $"{Path.GetFileNameWithoutExtension(source)}-{empreinte}{Path.GetExtension(source)}");

            if (!File.Exists(copie))
                await Task.Run(() => File.Copy(source, copie, overwrite: true));

            item.SuivreLaCopie(copie);
        }
        catch (Exception ex)
        {
            FileLog.Write(
                $"Photo « {item.Name} » non mise à l'abri : elle reste lue depuis son support, " +
                "et sera perdue si celui-ci est retiré avant l'impression", ex);
        }
    }

    /// <summary>
    /// Ouvre une photo dans la scène de cadrage, en DÉPOSANT d'abord le travail fait sur
    /// la précédente.
    ///
    /// L'ordre compte : sans le dépôt préalable, changer de photo perdrait tout ce qui vient
    /// d'être réglé sur celle qu'on quitte — c'était le comportement d'avant, et il
    /// obligeait à imprimer une photo avant d'en toucher une autre.
    /// </summary>
    private async Task OuvrirLaPhotoAsync(StripItem item)
    {
        if (ReferenceEquals(_current, item)) return;

        SauverDansLaPhoto();

        foreach (var p in _photos) p.Selected = p == item;
        _current = item;

        // Les aperçus calculés appartiennent à la photo précédente : les garder
        // afficherait le fond blanc et l'exposition d'un autre client. Ils se refont ; le
        // travail, lui, est repris de l'objet.
        _detoure = null;
        _corrige = null;

        // les pixels de départ appartenaient à la photo précédente
        _departBgra = null;
        _departSource = null;

        EmptyText.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = CurseurStudio.Attente;

        try
        {
            await MettreALAbriAsync(item);

            var path = item.Path;
            var bytes = await Task.Run(() => App.Services.Thumbnails.GetJpeg(path, 1600));
            _displayBitmap = ToBitmap(bytes);

            ReprendreDeLaPhoto(item);

            // Première ouverture seulement : la détection écraserait le placement manuel
            // que l'opérateur vient de corriger à la main.
            if (!item.Prete)
            {
                // ⚠ UN CADRAGE DÉJÀ POSÉ NE SE REFAIT PAS.
                //
                // Une planche rouverte depuis « Commandes du jour » arrive avec le cadrage
                // de la commande mais SANS repères — la commande n'en garde pas. Il faut
                // donc laisser la détection les retrouver, et surtout PAS recadrer ensuite :
                // le seul motif de rouvrir une planche est que le guichet a refusé le
                // cadrage, et repartir du cadrage automatique effacerait justement celui
                // qu'on vient chercher.
                var cadrageRepris = !_crop.IsFull;

                // La détection tourne MÊME quand le cadrage automatique est éteint : elle
                // pose les repères, donc le contrôle de conformité du bandeau. Ce qu'on
                // coupe alors, c'est le PLACEMENT du cadre, pas la mesure de la tête.
                var face = await Task.Run(() => App.Services.Faces.DetectMain(path));
                var detecte = face is null ? null : IdPhotoFr.EstimateHead(face.Box);
                PoserReperes(detecte);

                if (!cadrageRepris) AutoCrop(App.Services.Identite.CadrageAutomatique);
                item.Prete = true;
            }

            // le fond se recalcule sur la nouvelle image, il ne se reporte pas
            if (item.FondBlanc || item.FondGris) await RefaireLeFondBlancAsync();
            await RecalculerLApercuCorrigeAsync();
        }
        catch (Exception ex)
        {
            // ÉCRIT AU JOURNAL, et pas seulement montré à l'opérateur.
            //
            // Cette boîte-là a annoncé « Can't read ONNX file » pendant toute une soirée à
            // Créteil sans laisser la moindre trace : le journal ne portait rien, et le
            // défaut n'était diagnosticable qu'en photographiant l'écran. Une erreur qu'on
            // montre est exactement celle qu'on voudra relire à distance.
            FileLog.Write("Photo d'identité : image illisible", ex);

            MessageBox.Show($"Photo illisible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
            _current = null;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        PrintButton.IsEnabled = _current is not null;
        MailButton.IsEnabled = _current is not null;
        Redraw();
        AnnoncerLeLot();
    }

    /// <summary>Dépose sur la photo courante tout ce que l'écran porte.</summary>
    private void SauverDansLaPhoto()
    {
        if (_current is not { } photo) return;

        photo.Crop = _crop;
        photo.Crown = _crown;
        photo.Chin = _chin;
        photo.Head = _head;
        photo.AxeVisage = _axeVisage;
        photo.Redressement = _redressement;
        photo.Corrections = _corrections.Clone();
        photo.NoirEtBlanc = GrayscaleCheck.IsChecked == true;
        photo.FondBlanc = WhiteBackgroundCheck.IsChecked == true;
        photo.FondGris = GrayBackgroundCheck.IsChecked == true;
        photo.Copies = _copies;
        photo.Quantite = _quantity;
    }

    /// <summary>
    /// Reprend sur l'écran ce que la photo porte.
    ///
    /// Les deux cases sont posées SANS déclencher leur gestionnaire : cocher « fond
    /// blanc » ici relancerait un détourage de quatre secondes alors qu'on vient de
    /// l'ordonner nous-même juste après.
    /// </summary>
    private void ReprendreDeLaPhoto(StripItem photo)
    {
        _crop = photo.Crop;
        _crown = photo.Crown;
        _chin = photo.Chin;
        _head = photo.Head;
        _axeVisage = photo.AxeVisage;
        _corrections = photo.Corrections.Clone();

        SansGestionnaires(() =>
        {
            GrayscaleCheck.IsChecked = photo.NoirEtBlanc;
            WhiteBackgroundCheck.IsChecked = photo.FondBlanc;
            GrayBackgroundCheck.IsChecked = photo.FondGris;
        });

        Redresser(photo.Redressement);

        // OUVRIR UNE PHOTO, C'EST LA CHOISIR : elle entre dans le lot à une planche.
        //
        // Aux ouvertures SUIVANTES (`Prete`), on respecte ce que l'opérateur a réglé — zéro
        // compris. C'est ainsi qu'il retire du lot une photo ouverte par erreur en
        // parcourant la carte, et sans cela le zéro qu'il vient de poser reviendrait à 1
        // dès qu'il regarde une autre photo puis revient.
        SetQuantity(LotIdentite.QuantiteALOuverture(photo.Quantite, photo.Prete));

        // 0 = jamais réglée : on part de ce que le raccourci a demandé, ou de la planche
        // pleine à défaut — comme au choix du papier
        SetCopies(photo.Copies > 0 ? photo.Copies : CopiesParDefaut());

        MontrerLesCorrections();
    }

    /// <summary>
    /// Vrai pendant qu'on repose l'état d'une photo : les gestionnaires des deux cases
    /// doivent alors se taire.
    /// </summary>
    private bool _enReprise;

    private void SansGestionnaires(Action action)
    {
        _enReprise = true;
        try
        {
            action();
        }
        finally
        {
            _enReprise = false;
        }
    }

    /// <summary>
    /// Ce que le lot représente, en bas d'écran : le compte des planches à sortir.
    ///
    /// Il ne DÉPOSE rien : appelé depuis <c>SetQuantity</c>, qui est lui-même appelé
    /// pendant la reprise d'une photo, un dépôt écrirait sur la nouvelle photo les
    /// réglages de l'ancienne, à moitié repris.
    /// </summary>
    private void AnnoncerLeLot()
    {
        // On compte ce qui va SORTIR, pas ce que la bande montre. Le `Math.Max(1, …)` d'avant
        // comptait une planche pour chaque photo de la carte : la phrase annonçait
        // « 80 photos · 80 planches » alors que l'opérateur n'en avait choisi qu'une — et
        // l'impression, elle, les sortait vraiment.
        var planches = _photos.Sum(p => p.Quantite);
        var retenues = _photos.Count(p => LotIdentite.EstRetenue(p.Quantite));

        LotText.Text = _photos.Count <= 1
            ? ""
            // Les deux nombres, parce qu'ils ne disent pas la même chose : ce que la carte
            // porte, et ce qu'on a retenu dessus.
            : $"{retenues} photo{(retenues > 1 ? "s" : "")} retenue{(retenues > 1 ? "s" : "")} " +
              $"sur {_photos.Count} · {planches} planche{(planches > 1 ? "s" : "")}";
    }

    /// <summary>
    /// Envoie au client la photo qu'il a sous les yeux — cadrage, redressement et
    /// corrections compris.
    ///
    /// C'est une prestation à part, facturée à la photo : elle n'imprime rien, et
    /// imprimer n'envoie rien. Un client peut vouloir les deux, ou l'un des deux, et le
    /// prix n'est pas le même.
    /// </summary>
    private void OnSendByMail(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show("Choisissez d'abord une photo dans la bande du bas.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // revenirEnArriere : l'envoi ne doit pas emporter la photo en cours — l'opérateur
        // enchaîne souvent sur la planche pour le même client. Voir MailSendView.
        Navigator.Go(
            new MailSendView([new MailSendView.PhotoAEnvoyer(
                    _current.Path, _crop, 0, _redressement, ReglagesRetenus())],
                revenirEnArriere: true),
            "Envoyer les photos par courriel");
    }

    /// <summary>
    /// Tout ce qui sera appliqué aux pixels : les corrections du module, plus les deux
    /// cases de cet écran.
    ///
    /// Un seul endroit, parce qu'il y a trois sorties — la planche, le courriel et
    /// l'aperçu — et qu'une quatrième oubliée tirerait autre chose que ce qu'on montre.
    /// </summary>
    private ImageAdjustments ReglagesRetenus()
    {
        var reglages = _corrections.Clone();
        reglages.Grayscale = GrayscaleCheck.IsChecked == true;
        reglages.WhiteBackground = WhiteBackgroundCheck.IsChecked == true;

        // ⚠ LE FOND GRIS MANQUAIT ICI, et lui seul. Sa jumelle ReglagesDe() le posait
        // bien, donc la PLANCHE sortait détourée pendant que le COURRIEL partait avec le
        // fond du studio — sans rien d'anormal à l'écran ni au journal. Le fond gris est
        // arrivé après le blanc (commit d438034) et n'a été branché que sur le chemin du
        // récapitulatif. Signalé depuis la boutique le 13/08/2026.
        //
        // Les deux méthodes doivent porter les MÊMES trois lignes : l'une lit l'écran,
        // l'autre une photo du lot, mais elles décrivent le même résultat. Toute case
        // ajoutée à cet écran est à poser des deux côtés — voir ReglagesDe().
        reglages.GrayBackground = GrayBackgroundCheck.IsChecked == true;

        // Nomme la photo pour que le détourage se retrouve d'un écran à l'autre. Sans
        // elle, le récapitulatif, l'impression et le courriel repayaient chacun un passage
        // complet du réseau sur la même photo — voir CleDuFichier.
        reglages.CleDeLaPhoto = CleDuFichier(_current?.Path);

        return reglages;
    }

    /// <summary>
    /// Un nom stable pour les pixels d'un fichier, qui sert de clé au cache des masques de
    /// détourage (voir <see cref="MasqueSujet.Nu"/>).
    ///
    /// <b>Le chemin ne suffit pas.</b> Une photo reprise à la borne, ou un fichier réécrit
    /// sous le même nom, rendrait un masque qui n'est plus le sien — et le sujet sortirait
    /// découpé sur la silhouette de quelqu'un d'autre. La taille et la date de dernière
    /// écriture referment ce trou pour le prix d'un appel système.
    ///
    /// Volontairement DIFFÉRENTE de la clé de l'aperçu (<c>{chemin}#{_departNumero}</c>),
    /// qui nomme les pixels réduits d'un aperçu précis. Celle-ci nomme le FICHIER, et c'est
    /// ce qui permet au récapitulatif, à l'impression et au courriel de se partager un seul
    /// détourage.
    ///
    /// Null si le fichier est illisible : on retombe alors sur l'empreinte des pixels, plus
    /// lente mais toujours juste.
    /// </summary>
    private static string? CleDuFichier(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return null;

        try
        {
            var fichier = new FileInfo(chemin);
            if (!fichier.Exists) return null;

            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{fichier.FullName}|{fichier.Length}|{fichier.LastWriteTimeUtc.Ticks}");
        }
        catch (Exception)
        {
            // chemin trop long, disque retiré, droits : l'empreinte des pixels prendra le relais
            return null;
        }
    }

    /// <summary>
    /// Place les deux anneaux : sur la tête détectée si elle l'a été, sinon à une position
    /// plausible que l'opérateur ajustera. Ils sont toujours proposés — la détection se
    /// trompe sur les cheveux volumineux, les couvre-chefs et les bébés, et c'est
    /// précisément là que le placement manuel sauve la photo.
    /// </summary>
    private void PoserReperes(NormRect? detecte)
    {
        if (detecte is { } tete)
        {
            _axeVisage = Math.Clamp(tete.CenterX, 0, 1);
            _crown = new NormPoint(_axeVisage, tete.Y);
            _chin = new NormPoint(_axeVisage, tete.Bottom);
        }
        else
        {
            _axeVisage = 0.5;
            _crown = new NormPoint(_axeVisage, 0.22);
            _chin = new NormPoint(_axeVisage, 0.62);
        }
    }

    /// <summary>
    /// Recalcule la tête et le cadre à partir des deux repères.
    /// </summary>
    /// <param name="suivreLesReperes">
    /// Faux pour poser un cadre CENTRÉ au rapport du document, sans tenir compte du visage —
    /// c'est le poste réglé sur « pas de cadrage automatique »
    /// (<see cref="ReglagesIdentite.CadrageAutomatique"/>). On emprunte alors exactement le
    /// repli qui servait déjà quand aucun visage n'était détecté : il n'y a pas deux façons
    /// de poser un cadre neutre.
    ///
    /// Le bouton « Cadrage automatique » de l'écran, lui, appelle toujours avec vrai : il
    /// EST la demande explicite, et le réglage du poste n'a pas à l'empêcher.
    /// </param>
    private void AutoCrop(bool suivreLesReperes = true)
    {
        if (_displayBitmap is null) return;

        if (suivreLesReperes && _crown is not null && _chin is not null)
        {
            try
            {
                _head = IdPhotoFr.HeadFromMarkers(_crown, _chin);
                _crop = IdPhotoFr.ComputeCrop(_head, _displayBitmap.PixelWidth, _displayBitmap.PixelHeight,
                    _document);
                return;
            }
            catch (ArgumentException)
            {
                // repères confondus : on garde le cadre précédent plutôt que de tout perdre
                return;
            }
        }

        _head = null;
        _crop = CropMath.CenterCrop(_displayBitmap.PixelWidth, _displayBitmap.PixelHeight, TargetAspect);
    }

    private void OnRedetect(object sender, RoutedEventArgs e)
    {
        if (_current is not null)
        {
            var face = App.Services.Faces.DetectMain(_current.Path);
            PoserReperes(face is null ? null : IdPhotoFr.EstimateHead(face.Box));
        }
        AutoCrop();
        Redraw();
    }

    // ----- poignées de cadrage -----

    /// <summary>
    /// Poignée saisie : les coins « 0 » haut-gauche, « 1 » haut-droit, « 2 » bas-gauche,
    /// « 3 » bas-droit ; les côtés « H », « B », « G », « D ».
    /// </summary>
    private string? _poigneeDrag;

    private void OnPoigneeDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement poignee) return;

        _poigneeDrag = poignee.Tag as string;
        Stage.CaptureMouse();   // la souris sort du carré dès qu'on tire
        e.Handled = true;       // sans quoi le cadre se déplacerait en même temps
    }

    /// <summary>
    /// Redimensionne le cadre pendant qu'on tire un coin ; vrai si le geste a été consommé.
    ///
    /// <b>Le point opposé reste immobile</b>, comme dans n'importe quel outil de recadrage :
    /// c'est ce qui permet de caler un bord puis d'ajuster l'autre. La dimension que la
    /// souris commande suit le curseur, l'autre suit le format du document — on ne déforme
    /// jamais une photo d'identité, même en tirant un côté.
    /// </summary>
    private bool TirerLaPoignee(Point positionStage)
    {
        if (_poigneeDrag is null || _displayBitmap is null) return false;

        var display = DisplayRect();
        if (display.IsEmpty || display.Width <= 0 || display.Height <= 0) return true;

        // l'ancre est le point OPPOSÉ à celui qu'on tient : le coin d'en face pour un coin,
        // le milieu du bord d'en face pour un côté
        var (ancreX, ancreY) = _poigneeDrag switch
        {
            "0" => (1.0, 1.0),
            "1" => (0.0, 1.0),
            "2" => (1.0, 0.0),
            "3" => (0.0, 0.0),
            "H" => (0.5, 1.0),
            "B" => (0.5, 0.0),
            "G" => (1.0, 0.5),
            _ => (0.0, 0.5),
        };

        // Le haut et le bas se mesurent en HAUTEUR, tout le reste en largeur : c'est la
        // dimension que la souris commande vraiment. L'autre s'en déduit par le format.
        var surLaHauteur = _poigneeDrag is "H" or "B";

        var (voulue, actuelle) = surLaHauteur
            ? (Math.Abs(positionStage.Y - (display.Y + (_crop.Y + ancreY * _crop.Height) * display.Height)),
               _crop.Height * display.Height)
            : (Math.Abs(positionStage.X - (display.X + (_crop.X + ancreX * _crop.Width) * display.Width)),
               _crop.Width * display.Width);

        // un cadre réduit à rien n'a pas de facteur : on laisse le geste sans effet plutôt
        // que de diviser par zéro
        if (actuelle <= 1 || voulue <= 1) return true;

        _crop = CropMath.ZoomDepuisUnCoin(
            _crop, voulue / actuelle, ancreX, ancreY,
            _displayBitmap.PixelWidth, _displayBitmap.PixelHeight, TargetAspect);

        Redraw();
        return true;
    }

    /// <summary>Pose les carrés sur les coins du cadre et les encoches au milieu des côtés.</summary>
    private void PlacerLesPoignees(Rect cropRect)
    {
        var visible = _displayBitmap is not null && !cropRect.IsEmpty;
        var etat = visible ? Visibility.Visible : Visibility.Collapsed;

        Poignee0.Visibility = Poignee1.Visibility =
            Poignee2.Visibility = Poignee3.Visibility = etat;

        PoigneeHaut.Visibility = PoigneeBas.Visibility =
            PoigneeGauche.Visibility = PoigneeDroite.Visibility = etat;

        if (!visible) return;

        Poser(Poignee0, cropRect.Left, cropRect.Top);
        Poser(Poignee1, cropRect.Right, cropRect.Top);
        Poser(Poignee2, cropRect.Left, cropRect.Bottom);
        Poser(Poignee3, cropRect.Right, cropRect.Bottom);

        var milieuX = cropRect.Left + cropRect.Width / 2;
        var milieuY = cropRect.Top + cropRect.Height / 2;

        Poser(PoigneeHaut, milieuX, cropRect.Top);
        Poser(PoigneeBas, milieuX, cropRect.Bottom);
        Poser(PoigneeGauche, cropRect.Left, milieuY);
        Poser(PoigneeDroite, cropRect.Right, milieuY);

        static void Poser(FrameworkElement carre, double x, double y)
        {
            Canvas.SetLeft(carre, x - carre.Width / 2);
            Canvas.SetTop(carre, y - carre.Height / 2);
        }
    }

    private void OnGrayscaleChanged(object sender, RoutedEventArgs e)
    {
        if (_enReprise) return;
        if (_current is not null) _current.NoirEtBlanc = GrayscaleCheck.IsChecked == true;
        ApplyGrayscalePreview();
    }

    /// <summary>
    /// Aperçu du fond blanc.
    ///
    /// Le calcul se fait sur l'aperçu (1600 px) et non sur la photo d'origine : il prend
    /// quatre secondes sur un 24 Mpx, ce qui figerait l'écran à chaque clic. Le tirage,
    /// lui, refait la découpe en pleine résolution — c'est le pipeline de rendu qui s'en
    /// charge, par <see cref="ImageAdjustments.WhiteBackground"/>.
    /// </summary>
    private async void OnWhiteBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_enReprise || _bascuMeFond) return;
        if (_current is null || _displayBitmap is null) return;

        // les deux fonds s'excluent : demander l'un retire l'autre
        if (WhiteBackgroundCheck.IsChecked == true) DecocherSansRelancer(GrayBackgroundCheck);

        _current.FondBlanc = WhiteBackgroundCheck.IsChecked == true;
        _current.FondGris = GrayBackgroundCheck.IsChecked == true;

        await AppliquerLeFondAsync();
    }

    /// <summary>Aperçu du fond gris. Même chemin que le blanc, seule la couleur diffère.</summary>
    private async void OnGrayBackgroundChanged(object sender, RoutedEventArgs e)
    {
        if (_enReprise || _bascuMeFond) return;
        if (_current is null || _displayBitmap is null) return;

        if (GrayBackgroundCheck.IsChecked == true) DecocherSansRelancer(WhiteBackgroundCheck);

        _current.FondGris = GrayBackgroundCheck.IsChecked == true;
        _current.FondBlanc = WhiteBackgroundCheck.IsChecked == true;

        await AppliquerLeFondAsync();
    }

    /// <summary>
    /// Vrai pendant qu'on décoche l'autre case : sans cela, chaque bascule rappellerait le
    /// gestionnaire d'en face et l'on recalculerait le détourage deux fois — quatre
    /// secondes pour rien, à chaque clic.
    /// </summary>
    private bool _bascuMeFond;

    private void DecocherSansRelancer(System.Windows.Controls.CheckBox case_)
    {
        if (case_.IsChecked != true) return;
        _bascuMeFond = true;
        try { case_.IsChecked = false; }
        finally { _bascuMeFond = false; }
    }

    /// <summary>Pose le fond demandé, ou revient à l'original si aucun n'est coché.</summary>
    private async Task AppliquerLeFondAsync()
    {
        if (WhiteBackgroundCheck.IsChecked != true && GrayBackgroundCheck.IsChecked != true)
        {
            _detoure = null;
            await RecalculerLApercuCorrigeAsync();  // les corrections repartent de l'original
            return;
        }

        await RefaireLeFondBlancAsync();
    }

    /// <summary>
    /// Calcule (ou recalcule) le détourage de la photo courante.
    ///
    /// Isolé du gestionnaire de la case parce qu'il sert aussi à la REPRISE : une photo
    /// déjà réglée en fond blanc doit retrouver son détourage en revenant dessus, sans que
    /// reposer la case ne déclenche autre chose.
    /// </summary>
    private async Task RefaireLeFondBlancAsync()
    {
        if (_current is null || _displayBitmap is null) return;

        Mouse.OverrideCursor = CurseurStudio.Attente;

        // Le fond passe par le MÊME détourage que la correction du sujet, et il fait donc
        // attendre pareil. Rien ne le disait : la photo restait immobile sous un curseur
        // d'attente, sans que rien n'annonce combien de temps.
        CommencerLAttente(GrayBackgroundCheck.IsChecked == true
            ? "Pose du fond gris"
            : "Pose du fond blanc");

        try
        {
            var chemin = _current.Path;
            var gris = GrayBackgroundCheck.IsChecked == true;
            var octets = await Task.Run(() =>
            {
                var jpeg = App.Services.Thumbnails.GetJpeg(chemin, 1600);
                using var image = new ImageMagick.MagickImage(jpeg);
                var fond = gris
                    ? BackgroundRemoval.GrisIdentite
                    : ImageMagick.MagickColors.White;
                return BackgroundRemoval.PoserUnFond(image, fond)
                    ? image.ToByteArray(ImageMagick.MagickFormat.Png)
                    : null;
            });

            if (octets is null)
            {
                DecocherSansRelancer(WhiteBackgroundCheck);
                DecocherSansRelancer(GrayBackgroundCheck);
                if (_current is not null) { _current.FondBlanc = false; _current.FondGris = false; }
                MessageBox.Show(
                    "Le fond de cette photo n'est pas assez uni pour être remplacé sans risque " +
                    "d'entamer le sujet.\n\nLa photo est laissée telle quelle.",
                    "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _detoure = ToBitmap(octets);
        }
        catch (Exception ex)
        {
            DecocherSansRelancer(WhiteBackgroundCheck);
            DecocherSansRelancer(GrayBackgroundCheck);
            if (_current is not null) { _current.FondBlanc = false; _current.FondGris = false; }
            _detoure = null;
            FileLog.Write("Fond de photo d'identité impossible", ex);
            MessageBox.Show($"Fond impossible à poser : {ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            FinirLAttente();

            // le détourage vient de changer la base : l'aperçu corrigé est à refaire
            // par-dessus, sans quoi il montrerait encore l'ancien fond
            await RecalculerLApercuCorrigeAsync();
        }
    }

    /// <summary>
    /// L'aperçu, dans l'ordre du RENDU : fond blanc, puis corrections, puis noir et blanc.
    ///
    /// C'est celui d'<see cref="ImageAdjuster.Apply"/>, et il ne doit pas en différer : le
    /// fond blanc raisonne sur les couleurs d'origine (après une correction, le fond ne
    /// ressemblerait plus à ce que le pourtour a mesuré), et le noir et blanc vient en
    /// dernier, sans quoi les réglages de couleur n'auraient plus de prise.
    /// </summary>
    private void ApplyGrayscalePreview()
    {
        var source = _corrige ?? _detoure ?? _displayBitmap;
        if (source is null) return;

        if (GrayscaleCheck.IsChecked == true)
        {
            var gray = new FormatConvertedBitmap(source, PixelFormats.Gray8, null, 0);
            gray.Freeze();
            Photo.Source = gray;
        }
        else
        {
            Photo.Source = source;
        }
    }

    // ----- corrections -----

    /// <summary>
    /// Ouvre le module de corrections sur la photo courante — le même écran que sur les
    /// tirages, avec les mêmes réglages.
    ///
    /// Le noir et blanc et le fond blanc n'y sont PAS passés : ce sont des cases de cet
    /// écran-ci, et les laisser aussi dans le module donnerait deux commandes pour un même
    /// réglage, dont l'une mentirait dès qu'on toucherait l'autre.
    /// </summary>
    /// <summary>
    /// Déplie ou replie le panneau des corrections. <b>Rien ne se charge.</b>
    ///
    /// Le bouton ouvrait un ÉCRAN : la photo disparaissait, un aperçu se recalculait depuis
    /// le fichier, on réglait sans voir le cadrage qu'on venait de poser, puis on revenait
    /// — deux attentes pour bouger un curseur, devant le client. L'aperçu corrigé part
    /// désormais de la vignette DÉJÀ en mémoire, celle que la scène affiche : ouvrir et
    /// fermer ne coûtent qu'un changement de visibilité.
    /// </summary>
    private void OnCorrect(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            MessageBox.Show("Choisissez d'abord une photo dans la bande du bas.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MontrerLePanneauDeCorrection(CorrectionsPanel.Visibility != Visibility.Visible);
    }

    private void OnFermerLesCorrections(object sender, RoutedEventArgs e) =>
        MontrerLePanneauDeCorrection(false);

    private void MontrerLePanneauDeCorrection(bool ouvert)
    {
        CorrectionsPanel.Visibility = ouvert ? Visibility.Visible : Visibility.Collapsed;

        // le sujet partage la colonne : rouvrir « Corriger » le referme, et refermer
        // « Corriger » rend la place à la photo quel que soit celui des deux qui était là
        if (ouvert) SujetPanel.Visibility = Visibility.Collapsed;

        var occupee = ouvert || SujetPanel.Visibility == Visibility.Visible;
        CorrectionsColonne.Width = occupee ? new GridLength(300) : new GridLength(0);

        if (ouvert) RelireLesCorrections();
    }

    /// <summary>
    /// Vrai pendant qu'on repose les contrôles depuis <see cref="_corrections"/> : sans ce
    /// drapeau, chaque affectation déclencherait son propre <c>ValueChanged</c> et
    /// recalculerait l'aperçu sept fois pour un changement de photo.
    /// </summary>
    private bool _relectureDesCorrections;

    /// <summary>Repose les contrôles du panneau d'après les réglages de la photo courante.</summary>
    private void RelireLesCorrections()
    {
        _relectureDesCorrections = true;
        try
        {
            IdRedEyeToggle.IsChecked = _corrections.RedEye;
            IdAutoLevelsToggle.IsChecked = _corrections.AutoLevels;
            IdAutoContrastToggle.IsChecked = _corrections.AutoContrast;
            IdAutoColorToggle.IsChecked = _corrections.AutoColor;

            IdExposureSlider.Value = _corrections.Exposure;
            IdContrastSlider.Value = _corrections.Contrast;
            IdHighlightsSlider.Value = _corrections.Highlights;
            IdShadowsSlider.Value = _corrections.Shadows;
            IdTemperatureSlider.Value = _corrections.Temperature;
            IdSaturationSlider.Value = _corrections.Saturation;
            IdSharpnessSlider.Value = _corrections.Sharpness;
        }
        finally
        {
            _relectureDesCorrections = false;
        }

        MettreLesEtiquettesDeCorrection();
    }

    private void MettreLesEtiquettesDeCorrection()
    {
        IdExposureLabel.Text = $"Exposition   {_corrections.Exposure:+0.00;-0.00;0} IL";
        IdContrastLabel.Text = $"Contraste   {_corrections.Contrast:+0;-0;0}";
        IdHighlightsLabel.Text = $"Hautes lumières   {_corrections.Highlights:+0;-0;0}";
        IdShadowsLabel.Text = $"Ombres   {_corrections.Shadows:+0;-0;0}";
        IdTemperatureLabel.Text = $"Température   {_corrections.Temperature:+0;-0;0}";
        IdSaturationLabel.Text = $"Saturation   {_corrections.Saturation:+0;-0;0}";
        IdSharpnessLabel.Text = $"Netteté   {_corrections.Sharpness:0}";
    }

    /// <summary>
    /// Un réglage a bougé : on le pose, on met à jour la photo, et rien de plus.
    ///
    /// Le noir et blanc et le fond blanc n'y figurent PAS : ce sont les deux cases de la
    /// barre du bas, et elles restent maîtresses de leur réglage.
    /// </summary>
    private void OnIdCorrectionChanged(object sender, RoutedEventArgs e)
    {
        if (_relectureDesCorrections || !IsLoaded) return;

        _corrections.RedEye = IdRedEyeToggle.IsChecked == true;
        _corrections.AutoLevels = IdAutoLevelsToggle.IsChecked == true;
        _corrections.AutoContrast = IdAutoContrastToggle.IsChecked == true;
        _corrections.AutoColor = IdAutoColorToggle.IsChecked == true;

        _corrections.Exposure = IdExposureSlider.Value;
        _corrections.Contrast = IdContrastSlider.Value;
        _corrections.Highlights = IdHighlightsSlider.Value;
        _corrections.Shadows = IdShadowsSlider.Value;
        _corrections.Temperature = IdTemperatureSlider.Value;
        _corrections.Saturation = IdSaturationSlider.Value;
        _corrections.Sharpness = IdSharpnessSlider.Value;

        MettreLesEtiquettesDeCorrection();

        // les corrections appartiennent à la photo : sans ce report, revenir dessus après
        // en avoir vu une autre les retrouverait à neutre
        if (_current is not null) _current.Corrections = _corrections.Clone();

        _ = RecalculerLApercuCorrigeAsync(DelaiDuCurseurMs);
    }

    private void OnIdCorrectionChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        OnIdCorrectionChanged(sender, (RoutedEventArgs)e);

    // ----- le sujet seul, sans le fond -----

    /// <summary>
    /// Ouvre le panneau du sujet à la place de celui des corrections.
    ///
    /// <b>Il remplace, il ne s'ajoute pas.</b> Les deux panneaux occupent la même colonne :
    /// en ouvrir un second à côté rétrécirait la photo, qui est justement ce qu'on regarde
    /// pendant qu'on règle. On revient aux corrections par ✕ ou par Échap.
    /// </summary>
    private void OnOuvrirLaSelectionDuSujet(object sender, RoutedEventArgs e)
    {
        CorrectionsPanel.Visibility = Visibility.Collapsed;
        SujetPanel.Visibility = Visibility.Visible;
        CorrectionsColonne.Width = new GridLength(300);

        // LA DÉTECTION PART À L'APPUI SUR LE BOUTON, pas à la case.
        //
        // Elle attendait « Corriger le sujet seul », et surtout un CURSEUR : la case seule
        // laisse le sujet neutre (voir CorrectionsSujet.IsNeutral), donc aucun détourage
        // n'était lancé. Les quatre secondes du réseau tombaient donc sur le premier
        // mouvement d'exposition — au pire moment, celui où l'opérateur cherche son réglage
        // et croit le curseur cassé.
        //
        // Ouvrir ce panneau EST la demande de détourer : on le fait tout de suite, la barre
        // d'attente s'affiche pendant qu'il calcule, et les curseurs répondent ensuite au
        // dixième de seconde puisque le masque est en mémoire.
        _corrections.Sujet.Actif = true;
        if (_current is not null) _current.Corrections = _corrections.Clone();

        RelireLeSujet();

        _ = DetecterLeSujetAsync();
    }

    /// <summary>
    /// Détoure la personne MAINTENANT, et range le masque : c'est tout ce que fait cette
    /// méthode. Elle ne change pas un pixel de l'aperçu — sans aucun réglage, il n'y a rien
    /// à montrer — elle paie d'avance le seul calcul lent de cet écran.
    ///
    /// <b>Elle passe par la même file que les aperçus</b> (<c>_apercuFile</c>). Deux
    /// détourages menés de front ne tiennent pas sur la Quadro P2000 des boutiques : le
    /// second se replie sur la découpe par la couleur, et le fond ressort dégradé — c'est le
    /// défaut relevé à Créteil sur le récapitulatif.
    /// </summary>
    private async Task DetecterLeSujetAsync()
    {
        var depart = _detoure ?? _displayBitmap;
        if (depart is null) return;

        // Mêmes pixels, même numéro, même clé que RecalculerLApercuCorrigeAsync : c'est la
        // condition pour que le masque calculé ici serve les curseurs juste après, au lieu
        // d'être rangé sous un nom que personne ne redemandera.
        if (!ReferenceEquals(_departSource, depart))
        {
            _departBgra = EnBgra(depart, out _departLargeur, out _departHauteur);
            _departSource = depart;
            _departNumero++;
        }

        var cle = _detoure is null
            ? CleDuFichier(_current?.Path)
            : $"{_current?.Path}#{_departNumero}";

        // déjà détouré : il n'y a rien à attendre, et une barre qui apparaîtrait pour rien
        // ferait croire à un travail à chaque ouverture du panneau
        if (cle is not null &&
            MasqueSujet.DejaEnMemoire(cle, (uint)_departLargeur, (uint)_departHauteur))
            return;

        var pixels = _departBgra;
        if (pixels is null) return;

        var largeur = _departLargeur;
        var hauteur = _departHauteur;
        var contour = _corrections.Sujet.ContourPx;
        var adoucir = _corrections.Sujet.AdoucissementPx;

        await _apercuFile.WaitAsync();
        try
        {
            CommencerLAttente("Détection de la personne");

            await Task.Run(() =>
            {
                var lecture = new ImageMagick.PixelReadSettings(
                    (uint)largeur, (uint)hauteur,
                    ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA);

                using var image = new ImageMagick.MagickImage(pixels, lecture);
                MasqueSujet.Calculer(image, contour, adoucir, cle)?.Dispose();
            });
        }
        catch (Exception ex)
        {
            // Le détourage n'est pas le tirage : on laisse l'écran tel quel. Le premier
            // curseur le retentera, et la phrase du panneau dit déjà par quoi la découpe
            // se fait sur ce poste.
            FileLog.Write("Détection du sujet impossible (photo d'identité)", ex);
        }
        finally
        {
            FinirLAttente();
            _apercuFile.Release();
        }
    }

    private void OnFermerLaSelectionDuSujet(object sender, RoutedEventArgs e)
    {
        SujetPanel.Visibility = Visibility.Collapsed;
        MontrerLePanneauDeCorrection(true);
    }

    /// <summary>Repose les contrôles du sujet d'après les réglages de la photo courante.</summary>
    private void RelireLeSujet()
    {
        var sujet = _corrections.Sujet;

        _relectureDesCorrections = true;
        try
        {
            IdSujetToggle.IsChecked = sujet.Actif;

            IdSujetContourSlider.Value = sujet.ContourPx;
            IdSujetAdoucirSlider.Value = sujet.AdoucissementPx;

            IdSujetExpoSlider.Value = sujet.Exposure;
            IdSujetContrasteSlider.Value = sujet.Contrast;
            IdSujetOmbresSlider.Value = sujet.Shadows;
            IdSujetHautesSlider.Value = sujet.Highlights;
            IdSujetSaturationSlider.Value = sujet.Saturation;
            IdSujetVibranceSlider.Value = sujet.Vibrance;
            IdSujetClarteSlider.Value = sujet.Clarity;
            IdSujetNettateSlider.Value = sujet.Sharpness;
        }
        finally
        {
            _relectureDesCorrections = false;
        }

        MettreLesEtiquettesDuSujet();
    }

    private void MettreLesEtiquettesDuSujet()
    {
        var sujet = _corrections.Sujet;

        IdSujetContourLabel.Text = $"Contour   {sujet.ContourPx:+0;-0;0} px";
        IdSujetAdoucirLabel.Text = $"Adoucir   {sujet.AdoucissementPx:0} px";

        IdSujetExpoLabel.Text = $"Exposition   {sujet.Exposure:+0.00;-0.00;0} IL";
        IdSujetContrasteLabel.Text = $"Contraste   {sujet.Contrast:+0;-0;0}";
        IdSujetOmbresLabel.Text = $"Ombres   {sujet.Shadows:+0;-0;0}";
        IdSujetHautesLabel.Text = $"Hautes lumières   {sujet.Highlights:+0;-0;0}";
        IdSujetSaturationLabel.Text = $"Saturation   {sujet.Saturation:+0;-0;0}";
        IdSujetVibranceLabel.Text = $"Vibrance   {sujet.Vibrance:+0;-0;0}";
        IdSujetClarteLabel.Text = $"Clarté   {sujet.Clarity:+0;-0;0}";
        IdSujetNettateLabel.Text = $"Netteté   {sujet.Sharpness:0}";

        MettreLEtatDuSujet();
    }

    /// <summary>
    /// La phrase du haut du panneau, qui dit ce qui se passe RÉELLEMENT.
    ///
    /// Elle existe parce que la découpe n'est pas la même partout : le réseau demande un
    /// modèle installé ET allumé dans les Paramètres, faute de quoi c'est la méthode par
    /// couleur qui découpe — elle marche, mais laisse un halo dans les cheveux. Sans cette
    /// phrase, l'opérateur verrait ce halo sans savoir d'où il vient.
    /// </summary>
    private void MettreLEtatDuSujet()
    {
        if (!_corrections.Sujet.Actif)
        {
            IdSujetEtat.Text =
                "Cochez « Corriger le sujet seul » pour détourer la personne et n'agir que sur elle.";
            return;
        }

        // « installé » ne suffit pas : le réseau se coupe aussi depuis les Paramètres, et
        // un poste qui l'a éteint découpe par la couleur sans que rien ne le dise.
        var parLeReseau = BiRefNetMatting.Actif && BiRefNetMatting.EstInstalle;

        IdSujetEtat.Text = parLeReseau
            ? "Ces réglages ne touchent que la personne. Le fond reste tel quel."
            : "Ces réglages ne touchent que la personne. Le détourage fin est éteint sur ce " +
              "poste : la découpe suit le fond, et peut laisser un liseré dans les cheveux — " +
              "« Adoucir » l'atténue.";
    }

    /// <summary>
    /// Un réglage du sujet a bougé. Même mécanique que le panneau principal, et le même
    /// report sur la photo : ces réglages lui appartiennent, pas à l'écran.
    /// </summary>
    private void OnIdSujetChanged(object sender, RoutedEventArgs e)
    {
        if (_relectureDesCorrections || !IsLoaded) return;

        var sujet = _corrections.Sujet;

        sujet.Actif = IdSujetToggle.IsChecked == true;

        sujet.ContourPx = IdSujetContourSlider.Value;
        sujet.AdoucissementPx = IdSujetAdoucirSlider.Value;

        sujet.Exposure = IdSujetExpoSlider.Value;
        sujet.Contrast = IdSujetContrasteSlider.Value;
        sujet.Shadows = IdSujetOmbresSlider.Value;
        sujet.Highlights = IdSujetHautesSlider.Value;
        sujet.Saturation = IdSujetSaturationSlider.Value;
        sujet.Vibrance = IdSujetVibranceSlider.Value;
        sujet.Clarity = IdSujetClarteSlider.Value;
        sujet.Sharpness = IdSujetNettateSlider.Value;

        MettreLesEtiquettesDuSujet();

        if (_current is not null) _current.Corrections = _corrections.Clone();

        // Le report compte DOUBLE ici : chacun de ces curseurs passe par le détourage, et
        // c'est ce panneau qui faisait tomber l'application quand on en glissait un.
        _ = RecalculerLApercuCorrigeAsync(DelaiDuCurseurMs);
    }

    private void OnIdSujetChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        OnIdSujetChanged(sender, (RoutedEventArgs)e);

    /// <summary>
    /// Remet le sujet à zéro — et lui seul. Les corrections d'ensemble ne bougent pas :
    /// elles ne sont pas dans ce panneau.
    /// </summary>
    private void OnIdSujetReset(object sender, RoutedEventArgs e)
    {
        _corrections.Sujet = new CorrectionsSujet();

        if (_current is not null) _current.Corrections = _corrections.Clone();

        RelireLeSujet();
        _ = RecalculerLApercuCorrigeAsync();
    }

    private void OnIdCorrectionsReset(object sender, RoutedEventArgs e)
    {
        // le noir et blanc et le fond blanc appartiennent à la barre du bas : on les
        // préserve, sans quoi « tout remettre à zéro » décocherait deux cases qui ne sont
        // pas dans ce panneau
        var noirEtBlanc = _corrections.Grayscale;
        var fondBlanc = _corrections.WhiteBackground;
        var fondGris = _corrections.GrayBackground;

        // Le sujet est préservé pour la MÊME raison : il a son propre panneau, et son
        // propre « remettre à zéro ». Un bouton n'efface que ce qu'il a sous les yeux.
        var sujet = _corrections.Sujet;

        _corrections = new ImageAdjustments
        {
            Grayscale = noirEtBlanc, WhiteBackground = fondBlanc, GrayBackground = fondGris,
            Sujet = sujet,
        };

        if (_current is not null) _current.Corrections = _corrections.Clone();

        RelireLesCorrections();
        _ = RecalculerLApercuCorrigeAsync();
    }

    /// <summary>
    /// Refait l'aperçu corrigé à partir de ce qui est déjà à l'écran, sur un fil de fond.
    ///
    /// Le calcul porte sur la vignette de 1600 px, pas sur l'original : quelques
    /// millisecondes, mais l'écran ne doit pas se figer pendant qu'un client regarde.
    /// Le TIRAGE, lui, refait tout en pleine résolution — c'est <see cref="ImagePipeline"/>
    /// qui applique les mêmes <see cref="ImageAdjustments"/>.
    ///
    /// <b>« Quelques millisecondes » ne vaut plus dès que la correction du sujet est
    /// allumée</b> : elle demande un détourage, qui coûte des SECONDES. D'où le report et
    /// la file ci-dessous — un calcul par curseur reposé, et un seul à la fois.
    /// </summary>
    /// <param name="delaiMs">
    /// Le temps laissé au curseur de se poser. Zéro pour tout ce qui se déclenche d'un
    /// seul geste — une case cochée, un bouton « remettre à zéro », un changement de
    /// photo : il n'y a rien à attendre, et attendre s'y verrait.
    /// </param>
    private async Task RecalculerLApercuCorrigeAsync(int delaiMs = 0)
    {
        MontrerLesCorrections();

        // Le calcul précédent ne sert plus à rien : ce qu'il montrera est déjà périmé.
        // (On est sur le fil de l'écran, seul à toucher ce champ — pas de course ici.)
        var precedent = _apercuCts;
        var cts = new CancellationTokenSource();
        _apercuCts = cts;
        precedent?.Cancel();

        var depart = _detoure ?? _displayBitmap;
        if (depart is null) return;

        if (_corrections.IsNeutral)
        {
            _corrige = null;
            ApplyGrayscalePreview();
            return;
        }

        var reglages = _corrections.Clone();
        var jeton = cts.Token;

        // les pixels de départ ne se relisent qu'au changement de photo — ou de fond, qui
        // en fabrique une autre
        if (!ReferenceEquals(_departSource, depart))
        {
            _departBgra = EnBgra(depart, out _departLargeur, out _departHauteur);
            _departSource = depart;

            // Ce numéro nomme CES pixels-là, et il change dès qu'ils changent — c'est
            // l'objet même de la ligne ci-dessus. Le détourage s'en sert pour reconnaître
            // une image qu'il a déjà découpée, au lieu de la relire tout entière pour s'en
            // assurer : 176 ms épargnées à chaque mouvement de curseur.
            _departNumero++;
        }

        // LA MÊME CLÉ QUE LE RÉCAPITULATIF ET LE TIRAGE, quand c'est la même photo.
        //
        // Cet écran nommait ses pixels `chemin#numéro`, un compteur de session ; le
        // récapitulatif, l'impression et le courriel les nomment `chemin|taille|date`
        // (CleDuFichier). Les deux ne se rencontraient JAMAIS. Le masque calculé pendant que
        // l'opérateur cadre — celui qu'il vient de regarder à l'écran — était donc jeté, et
        // le récapitulatif repayait un passage complet du réseau sur la même photo. C'est
        // précisément ce que la mémoire des masques existe pour éviter : elle ignore la
        // TAILLE depuis le 12/08/2026, exprès pour qu'un masque d'aperçu serve la planche
        // pleine résolution.
        //
        // Le compteur reste indispensable quand la photo affichée n'est PLUS celle du
        // fichier : la sélection du sujet fabrique une image déjà découpée (`_detoure`), et
        // ranger SON masque sous la clé du fichier empoisonnerait le tirage.
        reglages.CleDeLaPhoto = _detoure is null
            ? CleDuFichier(_current?.Path)
            : $"{_current?.Path}#{_departNumero}";

        var pixels = _departBgra!;
        var largeur = _departLargeur;
        var hauteur = _departHauteur;

        // Y a-t-il quelque chose à attendre ? Seulement si le sujet est demandé ET que sa
        // découpe n'est pas déjà en mémoire. Tous les autres aperçus se rendent en un
        // dixième de seconde, et une barre qui apparaîtrait pour eux ne ferait que
        // clignoter à chaque mouvement de curseur.
        // sans clé (fichier illisible, chemin trop long), on ne peut rien affirmer : mieux
        // vaut annoncer une attente qui n'aura pas lieu que figer l'écran sans un mot
        var vaDetourer = !reglages.Sujet.IsNeutral &&
                         (reglages.CleDeLaPhoto is null ||
                          !MasqueSujet.DejaEnMemoire(reglages.CleDeLaPhoto, (uint)largeur, (uint)hauteur));

        try
        {
            if (delaiMs > 0) await Task.Delay(delaiMs, jeton);

            await _apercuFile.WaitAsync(jeton);
            try
            {
                // Rien n'est calculé pour un aperçu déjà dépassé : c'est ICI que l'abandon
                // paie, puisque la file a pu faire attendre quelques secondes.
                jeton.ThrowIfCancellationRequested();

                if (vaDetourer) CommencerLAttente("Détourage de la personne");

                var octets = await Task.Run(() =>
                {
                    var lecture = new ImageMagick.PixelReadSettings(
                        (uint)largeur, (uint)hauteur,
                        ImageMagick.StorageType.Char, ImageMagick.PixelMapping.BGRA);

                    using var image = new ImageMagick.MagickImage(pixels, lecture);
                    ImageAdjuster.Apply(image, reglages);

                    // la correction du sujet retire la couche alpha en chemin ; on la
                    // remet opaque pour que la relecture rende bien quatre octets par pixel
                    image.Alpha(ImageMagick.AlphaOption.Opaque);

                    using var lus = image.GetPixels();
                    return lus.ToByteArray(ImageMagick.PixelMapping.BGRA)
                           ?? throw new InvalidOperationException("aperçu illisible");
                }, jeton);

                // un résultat arrivé après qu'un autre curseur a bougé ferait revenir à
                // l'écran une correction que l'opérateur vient de quitter
                if (jeton.IsCancellationRequested) return;

                _corrige = DepuisBgra(octets, largeur, hauteur);
            }
            finally
            {
                FinirLAttente();
                _apercuFile.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // un curseur a rebougé : le calcul suivant montrera la suite
            return;
        }
        catch (Exception ex)
        {
            // l'aperçu n'est pas le tirage : on montre la photo non corrigée plutôt que
            // de bloquer l'écran, et le journal garde de quoi comprendre
            _corrige = null;
            FileLog.Write("Aperçu corrigé impossible (photo d'identité)", ex);
        }

        ApplyGrayscalePreview();
    }

    // ----- l'attente du détourage -----

    /// <summary>
    /// Combien de temps le détourage va prendre, au mieux de ce qu'on en sait.
    ///
    /// La dernière durée mesurée sur ce poste d'abord : elle tient compte de la carte, du
    /// modèle installé et de la taille traitée. À défaut — au tout premier détourage depuis
    /// le démarrage — les ordres de grandeur relevés sur la Quadro P2000 de l'atelier le
    /// 03/08/2026, qui valent mieux que rien.
    /// </summary>
    /// <summary>
    /// Combien de temps le prochain détourage va durer, au mieux de ce qu'on en sait.
    ///
    /// <b>La MÉDIANE des dernières mesures, et non la dernière.</b> Celle-ci suffisait à
    /// faire mentir la barre en permanence : le premier détourage d'une séance paie le
    /// chargement du réseau et dure le double, un aperçu de cadrage est plus court qu'une
    /// planche, et l'estimation suivante héritait de cette mesure-là. Voir
    /// <see cref="MasqueSujet.DureeTypique"/>.
    /// </summary>
    private static TimeSpan EstimationDuDetourage() =>
        MasqueSujet.DureeTypique ??
        (BiRefNetMatting.Actif && BiRefNetMatting.EstInstalle
            ? TimeSpan.FromSeconds(4.3)
            : TimeSpan.FromSeconds(1.2));

    /// <summary>
    /// Montre la barre et la fait avancer. Sans effet si l'attente est déjà affichée — un
    /// aperçu abandonné puis relancé ne doit pas remettre la barre à zéro sous les yeux.
    /// </summary>
    /// <param name="quoi">
    /// Ce qu'on attend, dit à l'opérateur : « Détourage de la personne », « Pose du fond ».
    /// C'est le même calcul dessous, mais pas la même chose de son point de vue.
    /// </param>
    private void CommencerLAttente(string quoi)
    {
        if (_attenteTimer is not null) return;

        _attenteQuoi = quoi;
        _attenteEstimee = EstimationDuDetourage();
        _attenteChrono.Restart();

        AttenteBarre.Value = 0;
        AttenteOverlay.Visibility = Visibility.Visible;
        MettreLAttenteAJour();

        _attenteTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };

        _attenteTimer.Tick += (_, _) => MettreLAttenteAJour();
        _attenteTimer.Start();
    }

    private void MettreLAttenteAJour()
    {
        var ecoule = _attenteChrono.Elapsed;

        // L'ESTIMATION S'ALLONGE QUAND ELLE EST DÉPASSÉE, au lieu de rester fausse. Une
        // photo plus lourde que les précédentes fait déborder n'importe quelle médiane ;
        // sans ce rattrapage, la barre restait collée à 0,97 et le texte annonçait
        // « plus long que prévu » pendant des secondes, ce qui n'apprend rien à personne.
        if (ecoule > _attenteEstimee)
            _attenteEstimee = TimeSpan.FromSeconds(ecoule.TotalSeconds * 1.25);

        var estime = _attenteEstimee.TotalSeconds <= 0 ? 1 : _attenteEstimee.TotalSeconds;

        // Jamais tout à fait au bout tant que ce n'est pas fini : une barre pleine devant un
        // écran qui ne bouge pas ferait croire à un blocage.
        AttenteBarre.Value = Math.Min(0.97, ecoule.TotalSeconds / estime);

        var reste = _attenteEstimee - ecoule;

        AttenteTexte.Text =
            $"{_attenteQuoi}… encore {Math.Max(1, Math.Ceiling(reste.TotalSeconds)):0} s";
    }

    /// <summary>Range la barre. Appelée quoi qu'il arrive — fin normale, abandon ou panne.</summary>
    private void FinirLAttente()
    {
        _attenteTimer?.Stop();
        _attenteTimer = null;
        _attenteChrono.Reset();

        AttenteOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>Les pixels d'une image d'écran, en BGRA — la disposition qu'attend WPF.</summary>
    private static byte[] EnBgra(BitmapSource source, out int largeur, out int hauteur)
    {
        var lisible = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        largeur = lisible.PixelWidth;
        hauteur = lisible.PixelHeight;

        var parLigne = largeur * 4;
        var octets = new byte[parLigne * hauteur];
        lisible.CopyPixels(octets, parLigne, 0);
        return octets;
    }

    /// <summary>
    /// L'image d'écran correspondant à des pixels BGRA. Figée : elle est fabriquée sur le
    /// fil de calcul et affichée sur celui de l'écran.
    /// </summary>
    private static BitmapSource DepuisBgra(byte[] bgra, int largeur, int hauteur)
    {
        var image = BitmapSource.Create(
            largeur, hauteur, 96, 96, PixelFormats.Bgra32, null, bgra, largeur * 4);

        image.Freeze();
        return image;
    }

    /// <summary>Dit à l'opérateur que des corrections sont posées — sinon rien ne le montre.</summary>
    private void MontrerLesCorrections()
    {
        CorrectionsText.Text = _corrections.IsNeutral ? "" : "corrections posées";

        // Changer de photo change les réglages : le panneau doit suivre. Sans cela, il
        // montrerait ceux de la photo précédente, et le premier curseur touché les
        // reposerait sur la nouvelle. Vaut pour les DEUX panneaux — celui du sujet porte
        // des réglages qui appartiennent eux aussi à la photo.
        if (CorrectionsPanel.Visibility == Visibility.Visible) RelireLesCorrections();
        if (SujetPanel.Visibility == Visibility.Visible) RelireLeSujet();
    }

    // ----- gabarit et dessin -----

    private Rect DisplayRect()
    {
        if (_displayBitmap is null || Stage.ActualWidth <= 0 || Stage.ActualHeight <= 0)
            return Rect.Empty;
        var scale = Math.Min(Stage.ActualWidth / _displayBitmap.PixelWidth,
                             Stage.ActualHeight / _displayBitmap.PixelHeight);
        var w = _displayBitmap.PixelWidth * scale;
        var h = _displayBitmap.PixelHeight * scale;
        return new Rect((Stage.ActualWidth - w) / 2, (Stage.ActualHeight - h) / 2, w, h);
    }

    private void Redraw()
    {
        var display = DisplayRect();
        Overlay.Visibility = display.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (display.IsEmpty) return;

        var cropRect = new Rect(
            display.X + _crop.X * display.Width,
            display.Y + _crop.Y * display.Height,
            _crop.Width * display.Width,
            _crop.Height * display.Height);

        Canvas.SetLeft(CropBorder, cropRect.X);
        Canvas.SetTop(CropBorder, cropRect.Y);
        CropBorder.Width = cropRect.Width;
        CropBorder.Height = cropRect.Height;

        // lignes du gabarit : crâne à 4 mm, menton entre 36 et 40 mm du bord haut
        // les lignes du gabarit suivent la norme du document visé, pas une norme figée
        var reperesVisibles = _document.HasHeadBounds;
        CrownLine.Visibility = ChinMinLine.Visibility = ChinMaxLine.Visibility =
            reperesVisibles ? Visibility.Visible : Visibility.Collapsed;

        if (reperesVisibles)
        {
            PlaceGuide(CrownLine, cropRect, _document.TargetCrownMarginMm);
            PlaceGuide(ChinMinLine, cropRect, _document.TargetCrownMarginMm + _document.HeadMinMm);
            PlaceGuide(ChinMaxLine, cropRect, _document.TargetCrownMarginMm + _document.HeadMaxMm);
        }

        PlacerGabaritVisage(cropRect);
        PlacerLesPoignees(cropRect);
        UpdateCompliance();
    }

    /// <summary>
    /// Trace le gabarit du visage : deux ovales concentriques, l'un à la taille minimale
    /// de tête admise, l'autre à la maximale, plus l'axe vertical.
    ///
    /// C'est le repère de DiLand : l'opérateur amène le tour de tête entre les deux
    /// ovales et le visage sur l'axe, sans avoir à lire une mesure en millimètres.
    /// </summary>
    private void PlacerGabaritVisage(Rect cropRect)
    {
        var centreX = cropRect.X + cropRect.Width / 2;

        FaceAxis.X1 = FaceAxis.X2 = centreX;
        FaceAxis.Y1 = cropRect.Y;
        FaceAxis.Y2 = cropRect.Bottom;

        // Sans bornes de visage, il n'y a pas de gabarit à montrer : une trentaine des
        // 274 documents n'en donnent aucune. Dessiner des ovales quand même laisserait
        // croire à une norme qui n'existe pas.
        if (!_document.HasHeadBounds)
        {
            HeadMinOval.Visibility = HeadMaxOval.Visibility = Visibility.Collapsed;
            return;
        }

        HeadMinOval.Visibility = HeadMaxOval.Visibility = Visibility.Visible;

        // centre de la tête visée, exprimé dans les cotes du document
        var centreY = cropRect.Y + cropRect.Height
            * (_document.TargetCrownMarginMm + _document.TargetHeadMm / 2) / _document.HeightMm;

        PlacerOvale(HeadMinOval, cropRect, centreX, centreY, _document.HeadMinMm);
        PlacerOvale(HeadMaxOval, cropRect, centreX, centreY, _document.HeadMaxMm);
    }

    /// <summary>Un ovale de gabarit, dimensionné dans les millimètres du document visé.</summary>
    private void PlacerOvale(System.Windows.Shapes.Ellipse ovale, Rect cropRect,
        double centreX, double centreY, double hauteurMm)
    {
        const double largeurSurHauteur = 0.75;   // proportion moyenne d'un visage

        var hauteur = cropRect.Height * hauteurMm / _document.HeightMm;
        var largeur = cropRect.Width * (hauteurMm * largeurSurHauteur) / _document.WidthMm;

        ovale.Width = largeur;
        ovale.Height = hauteur;
        Canvas.SetLeft(ovale, centreX - largeur / 2);
        Canvas.SetTop(ovale, centreY - hauteur / 2);
    }

    private void PlaceGuide(System.Windows.Shapes.Line line, Rect cropRect, double mmFromTop)
    {
        var y = cropRect.Y + cropRect.Height * mmFromTop / _document.HeightMm;
        line.X1 = cropRect.X;
        line.X2 = cropRect.Right;
        line.Y1 = line.Y2 = y;
    }

    private void UpdateCompliance()
    {
        // LE CADRE SORT-IL DE LA PHOTO ? Avant tout le reste, et même sans visage détecté.
        //
        // Une photo prise trop serrée ne contient pas de quoi respecter la norme : il faut
        // de la marge au-dessus du crâne, et si elle n'y est pas, le cadre réglementaire
        // déborde de l'image. L'écran le laissait faire en silence — le gabarit dépassait
        // visiblement de la photo, et le bandeau continuait de commenter la hauteur de
        // tête comme si de rien n'était. L'opérateur imprimait alors une planche dont les
        // bords sont vides ou étirés, sans avoir été prévenu. Signalé par les collègues le
        // 08/08/2026.
        //
        // Ce défaut-là prime sur les autres : tant que le cadre n'est pas dans la photo,
        // juger la tête ou le centrage n'a pas de sens.
        if (!_crop.IsValid)
        {
            SetGuideBrush(WarnBrush);
            ComplianceText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            ComplianceText.Text =
                "Le cadre sort de la photo : elle est trop serrée pour cette norme. " +
                "Reculez le zoom, ou reprenez la photo de plus loin — telle quelle, " +
                "le tirage aura des bords vides.";
            return;
        }

        if (_head is null)
        {
            SetGuideBrush(NeutralBrush);
            ComplianceText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            ComplianceText.Text = "Visage non détecté — cadrez à l'œil avec le gabarit.";
            return;
        }

        var c = IdPhotoFr.Check(_crop, _head, _document);

        // sans borne de visage dans la norme, on ne peut pas juger : le dire plutôt
        // que d'annoncer « conforme » sur un document qu'on ne sait pas contrôler
        if (!c.CanBeChecked)
        {
            SetGuideBrush(NeutralBrush);
            ComplianceText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            ComplianceText.Text =
                $"{_document.Country} — {_document.Document} : cette norme ne fixe pas de hauteur de visage, " +
                "conformité non vérifiable.";
            return;
        }

        SetGuideBrush(c.Compliant ? OkBrush : WarnBrush);
        ComplianceText.Foreground = c.Compliant
            ? (Brush)Application.Current.Resources["OkBrush"]
            : (Brush)Application.Current.Resources["DangerBrush"];

        if (c.Compliant)
        {
            ComplianceText.Text =
                $"Conforme ✓ — tête {c.HeadHeightMm:0.0} mm ({_document.HeadMinMm:0.#}–{_document.HeadMaxMm:0.#} mm)";
            return;
        }

        var issues = new List<string>();
        if (!c.HeadHeightOk)
            issues.Add(c.HeadHeightMm > _document.HeadMaxMm
                ? $"tête trop grande ({c.HeadHeightMm:0.0} mm) : reculez le zoom"
                : $"tête trop petite ({c.HeadHeightMm:0.0} mm) : zoomez");
        if (!c.CrownOk)
            // bornes du DOCUMENT visé : le conseil doit désigner le bon sens, et sur un
            // 50 × 50 les millimètres français indiquaient l'inverse de ce qu'il fallait
            issues.Add(c.CrownMarginMm < _document.CrownMarginMinMm
                ? "crâne trop près du bord haut : descendez le cadre"
                : "trop d'espace au-dessus du crâne : montez le cadre");
        if (!c.CenteredOk)
            issues.Add(c.CenterOffsetMm > 0 ? "décalé : glissez vers la droite" : "décalé : glissez vers la gauche");
        ComplianceText.Text = string.Join(" · ", issues);
    }

    private void SetGuideBrush(Brush brush)
    {
        CropBorder.Stroke = brush;
        CrownLine.Stroke = brush;
        ChinMinLine.Stroke = brush;
        ChinMaxLine.Stroke = brush;
    }

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    // ----- redressement -----

    private bool _redressementArme;

    /// <summary>
    /// Le mode redressement est-il armé ?
    ///
    /// <b>Pourquoi une bascule et non une touche maintenue.</b> Il fallait tenir T ET
    /// rouler la molette ensemble ; au comptoir, une main est déjà prise. Et surtout,
    /// <c>Keyboard.IsKeyDown</c> lit le clavier tel que le voit l'élément qui a le FOCUS :
    /// sur cet écran, le focus tombe sur la liste des papiers dès qu'on a choisi son
    /// tirage, et la molette se remettait alors à zoomer sans prévenir. C'est ce qui
    /// faisait passer le redressement pour cassé.
    ///
    /// T bascule, Échap sort, et T maintenue continue de marcher : personne ne réapprend.
    /// </summary>
    private bool RedressementArme
    {
        get => _redressementArme;
        set
        {
            if (_redressementArme == value) return;
            _redressementArme = value;
            MontrerLeBandeauRedressement();
        }
    }

    private void OnFenetreKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat) return;

        // dans un champ de saisie, « t » est une lettre
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return;

        if (e.Key == Key.T)
        {
            RedressementArme = !RedressementArme;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && RedressementArme)
        {
            RedressementArme = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SujetPanel.Visibility == Visibility.Visible)
        {
            // Échap remonte d'un cran, il ne ferme pas tout : le sujet est un panneau
            // OUVERT DEPUIS « Corriger », et l'on y revient plutôt que de se retrouver
            // devant la photo nue sans savoir ce qu'on a fermé
            SujetPanel.Visibility = Visibility.Collapsed;
            MontrerLePanneauDeCorrection(true);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && _agrandi)
        {
            // le redressement d'abord : c'est un MODE, et Échap sort du plus imbriqué
            Agrandissement(false);
            e.Handled = true;
        }
    }

    private void MontrerLeBandeauRedressement()
    {
        BandeauRedressement.Visibility = RedressementArme ? Visibility.Visible : Visibility.Collapsed;
        BandeauRedressementTexte.Text =
            $"Redressement {RedressementText.Text} — molette pour régler · T ou Échap pour sortir";
    }

    private void OnRedresserGauche(object sender, RoutedEventArgs e) =>
        Redresser(_redressement - PasDeRedressement);

    private void OnRedresserDroite(object sender, RoutedEventArgs e) =>
        Redresser(_redressement + PasDeRedressement);

    private void OnRedressementRemiseAZero(object sender, RoutedEventArgs e) => Redresser(0);

    /// <summary>
    /// Pose l'angle de redressement et le montre.
    ///
    /// L'aperçu pivote à l'écran plutôt que de refabriquer l'image : sur une photo de
    /// 24 Mpx, refaire le bitmap à chaque cran rendrait le geste inutilisable. Le tirage,
    /// lui, applique la rotation pour de bon (RenderRequest.FineRotationDegrees), et c'est
    /// le rendu qui remplit de blanc les coins libérés.
    /// </summary>
    private void Redresser(double degres)
    {
        _redressement = Math.Clamp(degres, -RedressementMax, RedressementMax);
        if (_current is not null) _current.Redressement = _redressement;

        RedressementText.Text = Math.Abs(_redressement) < 0.01
            ? "0°"
            : $"{_redressement:+0.##;-0.##}°";

        MontrerLeBandeauRedressement();   // l'angle s'affiche dans le bandeau tant qu'il est là
        AppliquerLeRedressementALAffichage();
    }

    private void AppliquerLeRedressementALAffichage()
    {
        Photo.RenderTransformOrigin = new Point(0.5, 0.5);
        Photo.RenderTransform = Math.Abs(_redressement) < 0.01
            ? Transform.Identity
            : new RotateTransform(_redressement);
    }

    // ----- interactions (mêmes gestes que l'éditeur de recadrage) -----

    /// <summary>Proportions du cadre : celles du document visé, pas un 35/45 figé.</summary>
    private double TargetAspect => _document.WidthMm / _document.HeightMm;

    /// <summary>
    /// Le CADRE suit le doigt.
    ///
    /// Cet écran-ci ne bouge pas la photo : <c>Photo</c> reste posée telle quelle et c'est
    /// l'<c>Overlay</c> — le voile et le rectangle de cadrage — qui se redessine. Le geste
    /// était pourtant inversé (<c>-dx</c>), copié des deux autres écrans de recadrage où
    /// c'est bien la PHOTO qui glisse sous un cadre fixe : là-bas, pousser la photo à droite
    /// revient à reculer la fenêtre de cadrage, ici cela l'envoie à l'opposé du curseur.
    ///
    /// Signalé le 04/08/2026 : « lorsqu'on déplace le cadre, les mouvements sont inversés ».
    /// Voir <c>CropSurface.OnMouseMove</c>, qui garde son signe pour la raison inverse.
    /// </summary>
    private void Pan(double dxPx, double dyPx)
    {
        var display = DisplayRect();
        if (display.IsEmpty) return;
        _crop = CropMath.Pan(_crop, dxPx / display.Width, dyPx / display.Height);
        Redraw();
    }

    private void Zoom(double cropFactor)
    {
        if (_displayBitmap is null) return;
        _crop = CropMath.Zoom(_crop, cropFactor,
            _displayBitmap.PixelWidth, _displayBitmap.PixelHeight, TargetAspect);
        Redraw();
    }

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_current is null) return;
        _dragging = true;
        _dragLast = e.GetPosition(Stage);
        Stage.CaptureMouse();
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _poigneeDrag = null;
        Stage.ReleaseMouseCapture();
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        // un coin saisi a la priorité sur le déplacement du cadre
        if (TirerLaPoignee(e.GetPosition(Stage))) return;

        if (!_dragging) return;
        var pos = e.GetPosition(Stage);
        Pan(pos.X - _dragLast.X, pos.Y - _dragLast.Y);
        _dragLast = pos;
    }

    /// <summary>
    /// Un cran de molette = un pixel d'écran sur le cadrage, molette vers l'avant pour
    /// serrer. Même geste que sur les deux autres écrans qui recadrent.
    ///
    /// Le lisseur a disparu avec le pas proportionnel : il rendait le zoom saccadé, et
    /// surtout il continuait de s'appliquer une seconde après le dernier cran — de quoi
    /// défaire par-derrière un recadrage automatique ou une remise à zéro déclenchés
    /// juste après (voir <c>CropEditorView.OnStageWheel</c>).
    /// </summary>
    private void OnStageWheel(object sender, MouseWheelEventArgs e)
    {
        var crans = e.Delta / 120.0;
        if (crans == 0) return;

        // Mode armé (T), ou T maintenue : c'est le redressement qui prend la molette,
        // comme sur les autres écrans de recadrage (voir CropSurface)
        if (RedressementArme || Keyboard.IsKeyDown(Key.T))
        {
            Redresser(_redressement + crans * PasDeRedressement);
            e.Handled = true;
            return;
        }

        var display = DisplayRect();
        if (display.IsEmpty) return;

        var largeurEcran = _crop.Width * display.Width;
        if (largeurEcran <= 2) return;

        Zoom((largeurEcran - crans) / largeurEcran);
        e.Handled = true;
    }

    private void OnManipulationStarting(object? sender, ManipulationStartingEventArgs e)
    {
        e.ManipulationContainer = Stage;
        e.Mode = ManipulationModes.Translate | ManipulationModes.Scale;
    }

    private void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        Pan(e.DeltaManipulation.Translation.X, e.DeltaManipulation.Translation.Y);
        var scale = e.DeltaManipulation.Scale.X;
        if (Math.Abs(scale - 1) > 0.001)
            Zoom(1 / scale);

        e.Handled = true;
    }

    private void OnQuantityMinus(object sender, RoutedEventArgs e) => SetQuantity(_quantity - 1);
    private void OnQuantityPlus(object sender, RoutedEventArgs e) => SetQuantity(_quantity + 1);

    /// <summary>
    /// Planches identiques de la photo affichée. <b>Le plancher est ZÉRO, pas un</b> : c'est
    /// ainsi qu'on retire du lot une photo qu'on a ouverte pour la regarder. Le récapitulatif
    /// admet zéro depuis toujours (<c>IdSheetRecapView.Quantite</c>, borné 0..20) ; cet
    /// écran-ci l'interdisait, si bien qu'une photo ouverte ne pouvait plus jamais en sortir.
    /// </summary>
    private void SetQuantity(int value)
    {
        _quantity = Math.Clamp(value, 0, 20);
        QuantityText.Text = _quantity.ToString();

        // la vignette porte « ×N » : sans ce report, la bande annoncerait encore l'ancienne
        // quantité et l'opérateur ne verrait son geste que sur le compteur
        if (_current is not null) _current.Quantite = _quantity;
        AnnoncerLeLot();
    }

    private void OnCopiesMinus(object sender, RoutedEventArgs e) => SetCopies(_copies - 1);
    private void OnCopiesPlus(object sender, RoutedEventArgs e) => SetCopies(_copies + 1);

    /// <summary>Nombre de photos sur la planche, borné par ce qui tient réellement sur le tirage.</summary>
    private void SetCopies(int value)
    {
        _copies = Math.Clamp(value, 1, Math.Max(1, MaxCopiesForSelectedProduct()));
        CopiesText.Text = _copies.ToString();

        if (_current is not null) _current.Copies = _copies;
    }

    private int MaxCopiesForSelectedProduct() =>
        ProductCombo.SelectedItem is ProductChoice choice ? choice.Capacite : 1;

    /// <summary>
    /// D'où repart le compteur quand rien n'a encore été réglé : le nombre demandé par le
    /// raccourci s'il y en a un, la planche pleine sinon.
    ///
    /// <see cref="SetCopies"/> borne de toute façon à ce que le papier porte : une planche
    /// de six demandée sur un papier qui n'en prend que quatre en donnera quatre, plutôt
    /// qu'une impression refusée après l'annonce du prix.
    /// </summary>
    private int CopiesParDefaut() => _copiesVoulues ?? MaxCopiesForSelectedProduct();

    /// <summary>
    /// Changer de produit repart de la planche PLEINE.
    ///
    /// Le nombre de copies inscrit au produit (« planche de 8 ») vaut pour le format
    /// français ; sur un document plus petit, il laisserait des places vides payées au même
    /// prix — la planche est facturée au papier. L'opérateur peut toujours descendre.
    /// </summary>
    private void OnProductChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProductCombo.SelectedItem is not ProductChoice choice) return;

        // Le papier vaut pour TOUT le lot — c'est le rouleau chargé, il n'y en a qu'un.
        // Les photos qu'on n'a pas encore rouvertes doivent donc repartir de la planche
        // pleine du NOUVEAU papier : garder « 8 » sur un papier qui en porte 12 laisserait
        // des places vides payées au même prix, sans que rien ne le dise.
        foreach (var photo in _photos)
            if (!ReferenceEquals(photo, _current))
                photo.Copies = 0;

        SetCopies(_copiesVoulues ?? choice.Capacite);
        ShowFinishes(choice.Product);
    }

    /// <summary>Le choix de finition n'apparaît que si le produit en propose (voir Catalogue → Finitions).</summary>
    private void ShowFinishes(Product product)
    {
        var names = product.Finishes.Select(f => f.Name).ToList();
        FinishCombo.ItemsSource = names;
        if (names.Count > 0) FinishCombo.SelectedIndex = 0;

        var visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FinishCombo.Visibility = visibility;
        FinishLabel.Visibility = visibility;
    }

    // ----- les deux seules sorties de la page -----

    /// <summary>
    /// Changer de pays ou de format, sans quitter le travail en cours.
    ///
    /// La norme se choisissait sur un ÉCRAN qui précédait celui-ci : pour passer d'un
    /// passeport français à un visa américain, il fallait tout recommencer depuis
    /// l'accueil. Les photos déjà ouvertes sont reprises telles quelles — seul le gabarit
    /// change, et c'est bien ce qu'on demande.
    /// </summary>
    private void OnChangerDeDocument(object sender, RoutedEventArgs e)
    {
        var chemins = _photos.Select(p => p.Path).ToList();

        Navigator.Go(
            new IdDocumentPickerView(
                // Une NORME : la page se refait sur le nouveau gabarit, avec les mêmes
                // photos — et avec le nombre que le raccourci demande, s'il en demande un.
                (document, photos) => Revenir(new IdPhotoView(chemins, document, photos)),

                // UN PRODUIT tiré tel quel — l'E-Photo.
                //
                // CETTE TUILE-LÀ ÉTAIT INVISIBLE DEPUIS CET ÉCRAN, et c'est exactement
                // l'oubli signalé le 17/08/2026 : le picker MASQUE ses raccourcis
                // « produit » quand l'appelant ne dit pas où les envoyer, et cet appel-ci ne
                // le disait pas. Sur Studio Photo Identité, où changer de document est le
                // SEUL chemin vers le picker, l'E-Photo n'existait donc nulle part — alors
                // qu'elle figure dans les raccourcis par défaut depuis le 03/08/2026.
                //
                // Elle ne passe pas par le gabarit d'identité : la photo part ENTIÈRE sur un
                // 10×15, bords blancs compris, et c'est l'écran des tirages qui la sert.
                //
                // ⚠ LE MÊME PARCOURS QUE LE STUDIO COMPLET, pas une copie. La première
                // version recopiait ce chemin ici en sautant le choix du support quand une
                // carte était insérée — or la photo d'une E-Photo n'est justement JAMAIS sur
                // la carte : elle arrive par courriel ou par téléphone, dans Téléchargements.
                // Voir ParcoursIdentite.OuvrirUnProduit.
                ParcoursIdentite.OuvrirUnProduit),
            "Choisir le document");
    }

    /// <summary>
    /// Ouvrir d'autres photos : carte mémoire, téléphone, dossier.
    ///
    /// <b>C'est le seul détour qui reste, et il est justifié</b> — parcourir un support ne
    /// tient pas dans un panneau. La norme en cours est emportée : on ne la redemande pas.
    /// </summary>
    private void OnOuvrirDesPhotos(object sender, RoutedEventArgs e)
    {
        var document = _document;

        // ON VA DIRECTEMENT AUX PHOTOS quand on sait où elles sont.
        //
        // Au comptoir, le client tend sa carte : l'écran « choisir le support » ne
        // proposait alors qu'une chose, et il fallait quand même la toucher. Le réglage du
        // poste tranche — un dossier fixe s'il en a un, la carte insérée sinon — et l'écran
        // des supports ne reste que pour les cas où la question se pose vraiment.
        if (DepartDesPhotos() is { } depart)
        {
            Navigator.Go(new IdPhotoPickerView(depart, document, avecSousDossiers: true),
                $"{document.Country} — choisir les photos");
            return;
        }

        Navigator.Go(
            new SourcePickerView((racine, profond) =>
                Navigator.Go(new IdPhotoPickerView(racine, document, profond),
                    $"{document.Country} — choisir les photos")),
            "Choisir le support");
    }

    /// <summary>
    /// La page telle qu'elle doit S'OUVRIR : directement sur les photos quand on sait où
    /// elles sont — la carte du client, ou le dossier réglé sur le poste.
    ///
    /// Sur Studio Photo Identité, cette page EST l'application. Ouvrir sur une bande vide
    /// et attendre un appui sur « Ouvrir des photos » alors que la carte est déjà dans le
    /// lecteur, c'est un geste de plus à chaque client, cinquante fois par jour.
    /// </summary>
    public static IdPhotoView Ouverture() =>
        DepartDesPhotos() is { } depart
            ? new IdPhotoView(depart, avecSousDossiers: true)
            : new IdPhotoView([]);

    /// <summary>
    /// Le dossier où commencer, ou null quand il faut poser la question.
    ///
    /// Le dossier fixe l'emporte s'il existe ENCORE : un chemin réseau réglé il y a six mois
    /// et devenu injoignable ne doit pas envoyer l'opérateur dans le vide — on retombe alors
    /// sur la carte, qui est de toute façon ce qu'il a en main.
    /// </summary>
    private static string? DepartDesPhotos()
    {
        var reglages = App.Services.Identite;
        if (reglages.DossierFixeUtilisable) return reglages.DossierPhotos;

        var supports = RemovableDriveWatcher.GetDrives();
        return supports.Count == 1 ? supports[0].RootPath : null;
    }

    /// <summary>
    /// Revient à la page de travail. Sur le poste identité elle EST l'application : la pile
    /// se vide, sinon chaque changement de norme y laisserait un écran de plus. Dans le
    /// Studio complet, le chemin d'où l'on vient doit rester praticable.
    /// </summary>
    private static void Revenir(IdPhotoView page)
    {
        if (AccueilStudio.EnIdentiteVerrouille)
            Navigator.Home(page, "Photos d'identité");
        else
            Navigator.Go(page, "Photo d'identité");
    }

    // ----- impression -----

    /// <summary>
    /// Passe au récapitulatif : la planche s'y regarde AVANT d'engager le papier.
    ///
    /// Ce bouton imprimait directement, et c'est ce qui coûtait une feuille à chaque
    /// mauvaise surprise — l'écran de cadrage montre le CADRE sur la photo, pas la
    /// planche : ni la disposition, ni le nombre de vignettes, ni l'horodatage. Voir
    /// <see cref="IdSheetRecapView"/>, qui porte désormais l'impression.
    /// </summary>
    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        // ne jamais sortir en silence : sans produit planche activé, le bouton semblait mort
        if (ProductCombo.SelectedItem is not ProductChoice choice)
        {
            MessageBox.Show(
                $"Aucun papier du catalogue ne peut porter une photo de " +
                $"{_document.WidthMm:0.#} × {_document.HeightMm:0.#} mm.\n\n" +
                "Ouvrez Catalogue et activez (ou créez) un produit de type planche assez grand " +
                "pour ce document.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_current is null)
        {
            MessageBox.Show("Choisissez d'abord une photo dans la bande de gauche.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // le travail de la photo affichée n'est pas encore déposé : sans cela, le
        // récapitulatif montrerait le cadrage d'AVANT la dernière retouche
        SauverDansLaPhoto();

        // Avertit sans bloquer : l'opérateur reste juge (visage non détecté, photo
        // médiocre…). Le contrôle porte sur TOUT le lot — n'examiner que la photo à
        // l'écran laisserait passer les autres sans un mot.
        //
        // DEUX DÉFAUTS DISTINCTS, et le premier ne se testait pas.
        //
        // Le cadre qui SORT DE LA PHOTO passait entièrement au travers : le contrôle
        // n'examinait que les photos dont le visage avait été détecté, et ne jugeait que
        // la géométrie du visage. Une photo trop serrée pour la norme — donc au cadre
        // débordant — pouvait afficher une tête parfaitement dimensionnée et partir sans
        // un mot, pour sortir avec des bords vides. Signalé par les collègues le
        // 08/08/2026.
        // ⚠ LE CONTRÔLE DE CONFORMITÉ AU GABARIT NE BLOQUE PLUS — retiré le 12/08/2026, à la
        // demande de l'exploitant. Il s'appuyait sur la détection de visage
        // (`IdPhotoFr.Check`) pour juger la hauteur de tête et le centrage, et il annonçait
        // « mal cadré » sur des photos qui ne l'étaient pas — assez souvent pour qu'on
        // réponde « oui » sans lire, ce qui vaut moins que rien : une boîte qu'on acquitte
        // par réflexe ne protège de rien et fait perdre un geste à chaque planche.
        //
        // Le jugement RESTE À L'ÉCRAN, dans le bandeau de `UpdateCompliance` — hauteur de
        // tête en millimètres, bornes de la norme, guide vert ou orange. C'est là qu'il est
        // utile : pendant qu'on cadre, et sans rien exiger.
        //
        // CE QUI RESTE BLOQUANT, et il faut qu'il le reste : le cadre qui SORT DE LA PHOTO.
        // Ce n'est pas une appréciation, c'est une mesure — le gabarit dépasse de l'image,
        // et le tirage sortira avec des bords vides ou étirés. Aucun faux positif possible,
        // et c'est le défaut que les collègues avaient signalé le 08/08/2026.
        // Sur les photos RETENUES seulement : celles que la bande ne fait que montrer n'ont
        // pas de cadre à juger, et les signaler ferait lire un avertissement sans objet.
        var horsPhoto = _photos.Where(p => LotIdentite.EstRetenue(p.Quantite) && !p.Crop.IsValid).ToList();

        if (horsPhoto.Count > 0)
        {
            var reponse = MessageBox.Show(
                $"Le cadre SORT DE LA PHOTO sur {horsPhoto.Count} photo(s) : " +
                $"{string.Join(", ", horsPhoto.Select(p => p.Name))}.\n" +
                "Elles sont trop serrées pour cette norme, et le tirage aura des bords vides." +
                "\n\nContinuer quand même ?",
                "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (reponse != MessageBoxResult.Yes) return;
        }

        var finition = FinishCombo.SelectedItem as string;

        // ⚠ ON N'IMPRIME QUE CE QUI A ÉTÉ RETENU.
        //
        // Cette ligne fabriquait une planche pour CHAQUE photo de la bande, et le
        // `Math.Max(1, p.Quantite)` remontait à une planche celles que l'opérateur n'avait
        // jamais ouvertes — donc jamais demandées. Sur Studio Photo Identité, qui s'ouvre
        // directement sur la carte du client, la bande porte toute la carte : toucher
        // « Imprimer » sortait quatre-vingts planches. Signalé depuis Arcueil le 17/08/2026.
        //
        // Le garde-fou existait pourtant en aval — TirageIdentite laisse de côté les planches
        // à zéro exemplaire, c'est écrit dans sa documentation — mais ce Math.Max le
        // désarmait : plus aucune planche n'arrivait à zéro.
        var planches = _photos
            .Where(p => LotIdentite.EstRetenue(p.Quantite))
            .Select(p => new IdSheetRecapView.Planche(
                p.Path,
                p.Crop,
                p.Redressement,
                ReglagesDe(p),
                p.Copies > 0 ? p.Copies : choice.Capacite,
                p.Quantite,
                choice.Product,
                finition,
                p.Rang))
            .ToList();

        if (planches.Count == 0)
        {
            MessageBox.Show(
                "Aucune photo n'est retenue : il n'y a rien à imprimer.\n\n" +
                "Ouvrez la photo du client dans la bande de gauche — elle entre alors dans " +
                "le lot — ou remontez le compteur « Planches ».",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // POSTE IDENTITÉ : on imprime, sans écran intermédiaire.
        //
        // Le récapitulatif existe pour le Studio complet, où le bouton d'impression est au
        // bout d'un parcours de tirages et où la planche ne se voit nulle part avant le
        // papier. Sur le poste identité, l'écran ne fait QUE des planches, l'opérateur en
        // sort cinquante par jour, et un écran de confirmation de plus est exactement ce qui
        // sépare ce logiciel de la fluidité d'ID Maker. La planche se regarde dans le
        // panneau, puis on imprime.
        if (AccueilStudio.EnIdentiteVerrouille)
        {
            PrintButton.IsEnabled = false;
            if (!await TirageIdentite.LancerAsync(planches, _document, _attenteId))
                PrintButton.IsEnabled = true;
            return;
        }

        Navigator.Go(
            new IdSheetRecapView(planches, _document,
                surModifier: RevenirSurLaPhoto,
                surRemplacer: RevenirAuChoixDesPhotos,
                attenteId: _attenteId),
            planches.Count == 1 ? "Récapitulatif de la planche" : "Récapitulatif des planches");
    }

    /// <summary>
    /// Tout ce qui sera appliqué aux pixels d'une photo du lot : ses corrections, plus ses
    /// deux cases.
    ///
    /// Jumelle de <see cref="ReglagesRetenus"/>, qui ne sait lire que l'écran. Les deux
    /// existent parce que l'envoi par courriel porte sur la photo AFFICHÉE, tandis que le
    /// récapitulatif porte sur toutes.
    /// </summary>
    private static ImageAdjustments ReglagesDe(StripItem photo)
    {
        var reglages = photo.Corrections.Clone();
        reglages.Grayscale = photo.NoirEtBlanc;
        reglages.WhiteBackground = photo.FondBlanc;
        reglages.GrayBackground = photo.FondGris;

        // même clé que sa jumelle : c'est elle qui fait que la planche, l'impression et le
        // courriel se partagent UN détourage au lieu d'en payer un chacun
        reglages.CleDeLaPhoto = CleDuFichier(photo.Path);

        return reglages;
    }

    /// <summary>Retour du récapitulatif sur une photo précise, pour la recadrer.</summary>
    private void RevenirSurLaPhoto(int rang)
    {
        Navigator.Back();

        var photo = _photos.FirstOrDefault(p => p.Rang == rang);
        if (photo is not null) _ = OuvrirLaPhotoAsync(photo);
    }

    /// <summary>
    /// Retour au choix des photos.
    ///
    /// Deux retours quand on vient de l'écran de sélection, un seul sinon : « Modifier »
    /// depuis les commandes du jour ouvre cet écran directement, et un second retour
    /// remonterait à la liste des commandes — c'est-à-dire ailleurs que là où le bouton
    /// promet d'aller.
    /// </summary>
    private void RevenirAuChoixDesPhotos()
    {
        Navigator.Back();
        if (_cheminsImposes is not null) Navigator.Back();
    }

    // ----- voir la photo en grand -----

    /// <summary>Vrai quand la bande et les réglages sont escamotés au profit de la photo.</summary>
    private bool _agrandi;

    /// <summary>
    /// Escamote la bande de gauche et la barre de réglages pour ne garder que la photo.
    ///
    /// <b>Pourquoi ce bouton existe.</b> Une photo d'identité est en portrait, et la scène
    /// de cadrage est large et basse : c'est la HAUTEUR qui la limite, et elle est prise
    /// par le titre, la barre de réglages et le bandeau des machines. On a resserré tout ce
    /// qui pouvait l'être — bande à 160 px, boutons à 40, marges réduites —, mais le vrai
    /// gain est là : sans la barre, la photo double presque de taille.
    ///
    /// <b>Rien n'est perdu en chemin</b> : les repères, le cadrage et les réglages
    /// continuent de vivre dans la photo courante. Le mode ne fait que cacher des panneaux.
    ///
    /// Échap en sort, comme du mode redressement — c'est le même réflexe.
    /// </summary>
    private void OnBasculerAgrandissement(object sender, RoutedEventArgs e) =>
        Agrandissement(!_agrandi);

    private void Agrandissement(bool actif)
    {
        _agrandi = actif;

        var panneaux = actif ? Visibility.Collapsed : Visibility.Visible;
        BandePhotos.Visibility = panneaux;
        BarreReglages.Visibility = panneaux;
        AideText.Visibility = panneaux;

        // la colonne elle-même doit disparaître : masquer son contenu lui laisserait ses
        // 160 px, c'est-à-dire précisément ce qu'on vient chercher
        BandeColonne.Width = actif ? new GridLength(0) : new GridLength(160);

        // un seul espace : XAML normalise les blancs de son contenu littéral, pas le C#, et
        // le libellé aurait changé d'espacement en basculant
        AgrandirButton.Content = actif ? "⛶ Réduire" : "⛶ Agrandir";

        // la scène change de taille : le cadre et les anneaux se replacent dessus
        Redraw();
    }

    // ----- mise en attente : servir quelqu'un d'autre, puis reprendre -----

    /// <summary>
    /// L'identité de CETTE planche, qu'elle ait déjà été mise de côté ou non. Fixe pour
    /// toute la vie de l'écran : deux mises en attente successives mettent à jour la même
    /// entrée au lieu d'en empiler deux sur l'accueil.
    /// </summary>
    private readonly Guid _attenteId = Guid.NewGuid();

    /// <summary>Le travail repris, s'il y en a un : sert à garder son titre.</summary>
    private readonly TravailEnAttente? _enAttente;

    /// <inheritdoc/>
    public string ResumeDeLAttente
    {
        get
        {
            var prêtes = _photos.Count(p => p.Prete);
            var morceaux = new List<string>
            {
                _document.Country == "France"
                    ? $"identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
                    : $"{_document.Country} — {_document.Document}",
                $"{_photos.Count} photo(s)",
            };

            if (prêtes > 0) morceaux.Add($"{prêtes} cadrée(s)");
            return string.Join(" · ", morceaux);
        }
    }

    /// <inheritdoc/>
    public bool EnregistrerPourReprise()
    {
        if (_photos.Count == 0) return false;

        // ce que l'écran porte appartient à la photo courante : sans ce dépôt, le cadrage
        // et les repères qu'on vient de poser partiraient à la corbeille
        SauverDansLaPhoto();

        try
        {
            var travail = ConstruireLAttente();
            App.Services.CommandesEnAttente.Enregistrer(travail);

            FileLog.Write($"Planche d'identité mise en attente ({travail.Resume}) — " +
                          $"{travail.PhotosDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write("Mise en attente de la planche d'identité impossible", ex);
            return false;
        }
    }

    private TravailEnAttente ConstruireLAttente() => new()
    {
        Id = _attenteId,
        SavedAt = DateTimeOffset.Now,

        // Le dossier sert à VÉRIFIER que les photos sont encore là avant de rouvrir. Avec
        // des photos imposées par la sélection, il peut être vide — on prend alors le
        // dossier de la première, qui est celui que le client a apporté.
        PhotosDirectory = !string.IsNullOrWhiteSpace(_rootPath)
            ? _rootPath
            : System.IO.Path.GetDirectoryName(_photos[0].Path) ?? "",
        AvecSousDossiers = _avecSousDossiers,
        Titre = _enAttente?.Titre is { Length: > 0 } deja ? deja : TitreDeLaPlanche(),
        Resume = ResumeDeLAttente,
        Identite = new IdentiteEnAttente
        {
            Country = _document.Country,
            Document = _document.Document,
            WidthMm = _document.WidthMm,
            HeightMm = _document.HeightMm,
            HeadMinMm = _document.HeadMinMm,
            HeadMaxMm = _document.HeadMaxMm,
            CrownMarginMm = _document.CrownMarginMm,
            TargetHeadOverrideMm = _document.TargetHeadOverrideMm,
            Chemins = _cheminsImposes is null ? [] : [.. _cheminsImposes],
            PhotoCourante = _current?.Name,
            Photos = _photos.Select(p => new PhotoIdentiteEnAttente
            {
                FileName = p.Name,
                Selected = p.Selected,
                Quantity = p.Quantite,
                Copies = p.Copies,
                Prete = p.Prete,
                CropX = p.Crop.X,
                CropY = p.Crop.Y,
                CropWidth = p.Crop.Width,
                CropHeight = p.Crop.Height,
                CrownX = p.Crown?.X,
                CrownY = p.Crown?.Y,
                ChinX = p.Chin?.X,
                ChinY = p.Chin?.Y,
                HeadX = p.Head?.X,
                HeadY = p.Head?.Y,
                HeadWidth = p.Head?.Width,
                HeadHeight = p.Head?.Height,
                AxeVisage = p.AxeVisage,
                Redressement = p.Redressement,
                NoirEtBlanc = p.NoirEtBlanc,
                FondBlanc = p.FondBlanc,
                Corrections = p.Corrections.Clone(),
            }).ToList(),
        },
    };

    private string TitreDeLaPlanche() => _document.Country == "France"
        ? $"Identité {_document.WidthMm:0.#}×{_document.HeightMm:0.#}"
        : $"Identité {_document.Country} — {_document.Document}";

    /// <summary>La norme telle qu'elle a été mise de côté.</summary>
    private static IdDocumentSpec DocumentDe(IdentiteEnAttente identite) => new(
        identite.Country, identite.Document,
        identite.WidthMm, identite.HeightMm,
        identite.HeadMinMm, identite.HeadMaxMm,
        identite.CrownMarginMm, identite.TargetHeadOverrideMm);

    /// <summary>
    /// Repose sur les photos de la bande le travail mis de côté.
    ///
    /// Les photos sont retrouvées par leur NOM DE FICHIER : le rang se décalerait dès
    /// qu'un fichier manque, et on reprendrait le cadrage du voisin sans que rien ne le
    /// dise. Une photo disparue est simplement sautée.
    ///
    /// <b><see cref="StripItem.Prete"/> est reposé en dernier</b>, avec le reste : c'est
    /// lui qui empêche la détection de visage de se relancer et d'écraser les repères
    /// qu'on vient justement de reprendre.
    /// </summary>
    private void AppliquerLAttente(IdentiteEnAttente identite)
    {
        var parNom = identite.Photos.ToDictionary(p => p.FileName, StringComparer.OrdinalIgnoreCase);

        foreach (var photo in _photos)
        {
            if (!parNom.TryGetValue(photo.Name, out var garde)) continue;

            photo.Crop = new CropSpec(garde.CropX, garde.CropY, garde.CropWidth, garde.CropHeight);
            photo.Crown = garde.CrownX is { } cx && garde.CrownY is { } cy ? new NormPoint(cx, cy) : null;
            photo.Chin = garde.ChinX is { } mx && garde.ChinY is { } my ? new NormPoint(mx, my) : null;
            photo.Head = garde.HeadX is { } hx && garde.HeadY is { } hy
                         && garde.HeadWidth is { } hw && garde.HeadHeight is { } hh
                ? new NormRect(hx, hy, hw, hh)
                : null;
            photo.AxeVisage = garde.AxeVisage;
            photo.Redressement = garde.Redressement;
            photo.NoirEtBlanc = garde.NoirEtBlanc;
            photo.FondBlanc = garde.FondBlanc;

            // ⚠ LE FOND GRIS ÉTAIT OUBLIÉ ICI. Il est arrivé après le blanc et n'avait été
            // branché que d'un côté : une planche mise de côté en fond gris revenait avec le
            // fond du studio, sans rien d'anormal à l'écran ni au journal. C'est la TROISIÈME
            // fois que ce champ-là manque à un endroit (voir la 1.5.3, ReglagesRetenus).
            photo.FondGris = garde.FondGris;

            photo.Corrections = garde.Corrections.Clone();
            photo.Quantite = garde.Quantity;
            photo.Copies = garde.Copies;
            photo.Prete = garde.Prete;
        }
    }

    /// <summary>
    /// Une photo de la bande, avec TOUT ce que l'opérateur a posé dessus.
    ///
    /// L'écran ne traitait qu'une photo à la fois et jetait son travail dès qu'on en
    /// choisissait une autre — repères, cadrage, corrections, redressement, tout repartait
    /// à neutre. Pour deux personnes d'une même famille, il fallait imprimer la première
    /// avant de toucher à la seconde, donc deux commandes et deux passages en caisse.
    ///
    /// Le travail vit donc ICI, et l'écran n'en est que la vue : il DÉPOSE l'état courant
    /// avant de changer de photo (<c>SauverDansLaPhoto</c>) et le REPREND ensuite
    /// (<c>ReprendreDeLaPhoto</c>).
    /// </summary>
    private sealed class StripItem : ObservableObject
    {
        private ImageSource? _thumbnail;
        private bool _selected;
        private int _quantite = 1;

        public StripItem(string path)
        {
            Path = path;
            Name = System.IO.Path.GetFileName(path);
        }

        /// <summary>
        /// Où lire les pixels. Change une fois, quand la photo est mise à l'abri du retrait
        /// de la carte — voir <c>MettreALAbriAsync</c>.
        /// </summary>
        public string Path { get; private set; }

        /// <summary>
        /// Le nom que l'opérateur reconnaît. Figé à l'ouverture : il doit rester celui du
        /// fichier du client, pas celui de la copie de travail — c'est lui qu'on retrouve
        /// dans la commande et dans les messages d'erreur.
        /// </summary>
        public string Name { get; }

        /// <summary>La photo est désormais lue depuis la copie locale.</summary>
        public void SuivreLaCopie(string chemin) => Path = chemin;

        /// <summary>Rang dans le lot, à partir de 1 — celui de l'écran de sélection.</summary>
        public int Rang { get; init; }

        /// <summary>
        /// Vrai dès que les repères ont été posés au moins une fois. Sans ce drapeau, une
        /// photo rouverte relancerait la détection de visage et écraserait le placement
        /// manuel que l'opérateur venait justement de corriger.
        /// </summary>
        public bool Prete { get; set; }

        // — le travail de l'opérateur, photo par photo —

        public CropSpec Crop { get; set; } = CropSpec.Full;
        public NormPoint? Crown { get; set; }
        public NormPoint? Chin { get; set; }
        public NormRect? Head { get; set; }
        public double AxeVisage { get; set; } = 0.5;
        public double Redressement { get; set; }
        public ImageAdjustments Corrections { get; set; } = new();
        public bool NoirEtBlanc { get; set; }
        public bool FondBlanc { get; set; }

        /// <summary>Le même détourage, posé sur du gris clair. Exclusif de <see cref="FondBlanc"/>.</summary>
        public bool FondGris { get; set; }

        /// <summary>Photos par planche. Recalé sur la capacité du papier au premier passage.</summary>
        public int Copies { get; set; }

        /// <summary>
        /// Nombre de planches identiques pour CETTE photo. Deux personnes commandent
        /// rarement la même quantité, et c'est ce que la bande affiche (« ×2 »).
        /// </summary>
        public int Quantite
        {
            get => _quantite;
            set
            {
                if (!Set(ref _quantite, value)) return;
                OnPropertyChanged(nameof(Etiquette));
            }
        }

        /// <summary>Ce qui se lit sur la vignette : le rang et le nombre de planches.</summary>
        public string Etiquette => $"{Rang}  ·  ×{Quantite}";

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set => Set(ref _thumbnail, value);
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(BorderBrush));
            }
        }

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;
    }
}
