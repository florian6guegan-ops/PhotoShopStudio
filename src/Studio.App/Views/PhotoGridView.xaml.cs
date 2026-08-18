using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;
using Studio.Imaging;
using Studio.Imaging.Geometry;
using Studio.Sources;
using Studio.Store;
using Studio.Store.DiLand;

namespace Studio.App.Views;

public partial class PhotoGridView : UserControl, ITravailReprenable
{
    private readonly string _rootPath;
    private readonly bool _avecSousDossiers;
    private readonly List<PhotoItem> _photos = new();
    private CancellationTokenSource? _thumbnailCts;
    private int _quantity = 1;
    private readonly long? _commandeBorne;

    /// <param name="rootPath">Dossier des photos à proposer.</param>
    /// <param name="produitParDefaut">
    /// Format déjà choisi en amont, comme dans le parcours de DiLand. Vide = premier
    /// produit du catalogue, l'opérateur choisira dans la liste.
    /// </param>
    /// <param name="commandeBorne">
    /// OID de la commande de borne à l'origine de ces photos, s'il y en a une. Elle reste
    /// affichée dans « Commandes des bornes » jusqu'à ce que le tirage soit sorti : c'est
    /// l'impression réussie, ici, qui la fait basculer dans l'historique.
    /// </param>
    /// <param name="avecSousDossiers">
    /// Descendre ou non sous <paramref name="rootPath"/>. Un support ou une commande de
    /// borne, oui ; un dossier désigné dans l'explorateur, seulement si l'opérateur l'a
    /// demandé — sans quoi désigner un dossier parent revenait à lire tout un disque.
    /// </param>
    /// <param name="taillePerso">
    /// Format « personnalisé » : la taille voulue pour chaque photo. Le choix du produit
    /// disparaît alors de l'écran — le papier est décidé à la validation, d'après la
    /// quantité — et les photos partent en planches.
    /// </param>
    /// <param name="cadragesBorne">
    /// Ce que le CLIENT a réglé à la borne, par nom de fichier.
    ///
    /// Sans ce paramètre, ouvrir une commande de borne repartait d'un cadrage centré : cet
    /// écran ne voit qu'un dossier d'images, et le recadrage validé à la borne n'y était
    /// nulle part. Voir <see cref="AppliquerLeCadrageDeLaBorne"/>.
    /// </param>
    /// <param name="enAttente">
    /// Une commande mise de côté, qu'on reprend. Elle est appliquée APRÈS le cadrage de la
    /// borne et le remplace : c'est ce que l'OPÉRATEUR a décidé, il l'emporte sur ce que le
    /// client avait réglé.
    /// </param>
    /// <param name="montageFeuille">
    /// Montage des agrandissements : la feuille sur laquelle composer les tirages de
    /// <paramref name="produitParDefaut"/>, ou null pour un fichier par tirage.
    ///
    /// Il ne s'applique QU'À ce format : l'opérateur qui change le produit d'une photo en
    /// sort, et elle repart en tirage ordinaire. Voir <see cref="FeuilleDeMontagePour"/>.
    /// </param>
    public PhotoGridView(
        string rootPath, string? produitParDefaut = null, long? commandeBorne = null,
        bool avecSousDossiers = true, CustomSize? taillePerso = null,
        IReadOnlyDictionary<string, DiLandImporter.CadrageBorne>? cadragesBorne = null,
        TravailEnAttente? enAttente = null, string? montageFeuille = null)
    {
        _rootPath = rootPath;
        _avecSousDossiers = avecSousDossiers;
        _commandeBorne = commandeBorne ?? enAttente?.KioskOid;
        _taillePerso = taillePerso;
        _cadragesBorne = cadragesBorne;
        _enAttente = enAttente;
        _produitParDefaut = produitParDefaut;
        _montageFeuille = montageFeuille;

        // reprendre une commande en attente REMET À JOUR la même entrée, elle n'en crée pas
        // une seconde : sans cela, chaque aller-retour laisserait un doublon sur l'accueil
        _attenteId = enAttente?.Id ?? Guid.NewGuid();

        InitializeComponent();

        // LA TAILLE LIBRE FIGURE DANS LA LISTE, et pas seulement derrière « Modifier ».
        // Elle n'était atteignable que depuis l'écran d'édition : au comptoir, l'opérateur
        // qui vient de choisir ses photos cherche le format ICI, ne le trouve pas, et
        // conclut que le logiciel ne sait pas le faire. Signalé le 13/08/2026.
        //
        // En QUEUE de liste : la première entrée est celle que la liste choisit par défaut,
        // et ce doit rester un vrai format.
        var choix = App.Services.Catalog.Enabled
            .Select(p => (object)new ProductChoice(p))
            .ToList();

        choix.Add(new ChoixTailleLibre());
        choix.Add(new ChoixAgrandissementLibre());

        ProductCombo.ItemsSource = choix;

        AttachShortcuts();

        var prechoisi = produitParDefaut is null
            ? -1
            : choix.FindIndex(c => c is ProductChoice pc
                                   && pc.Product.Code.Equals(produitParDefaut, StringComparison.OrdinalIgnoreCase));
        ProductCombo.SelectedIndex = prechoisi >= 0 ? prechoisi : 0;
        _indexProduitPrecedent = ProductCombo.SelectedIndex;

        if (taillePerso is not null) PasserEnTaillePersonnalisee(taillePerso);

        // posé dès l'ouverture, et pas seulement au changement de produit : la commande
        // 04-024 est partie sur la mauvaise machine sans que rien n'ait jamais été changé
        AfficherLaSortie();

        Loaded += async (_, _) =>
        {
            await ScanAndLoadAsync();

            Focus(); // sans le focus, aucune touche ne nous parvient
            await LoadMachinesAsync();
        };
        Unloaded += (_, _) => _thumbnailCts?.Cancel();
    }

    // Une commande de borne arrive DÉCOCHÉE, comme n'importe quel dossier de photos.
    //
    // Elle a d'abord été ouverte avec tout de coché, puis directement dans « Modifier » —
    // l'exploitant l'a refusé le 01/08/2026 : cet écran est celui où il CONTRÔLE la
    // commande avant d'engager du papier, et il veut choisir ce qu'il tire. Il coche donc
    // lui-même, puis passe par « Modifier » comme d'habitude.
    //
    // Conséquence à connaître : une commande de borne ne s'imprime pas toute seule, rien
    // ne part tant que rien n'est coché.

    private sealed record ProductChoice(Product Product)
    {
        public string Label => $"{Product.Name} — {Product.Price:0.00} €";
    }

    /// <summary>
    /// Les deux entrées de la liste qui ne désignent PAS un produit mais ouvrent un écran.
    ///
    /// Elles portent un <c>Label</c> comme les autres — la liste affiche cette propriété —
    /// et se reconnaissent à leur type, jamais à leur texte : celui-ci est de l'affichage,
    /// et le comparer reviendrait à faire dépendre le comportement d'une formulation.
    ///
    /// Les deux sont distinctes, et ce n'est pas un détail : la taille libre compose des
    /// planches sur du papier minilab, l'agrandissement sort un tirage unique en fichier
    /// pour l'Epson. Les confondre enverrait un A2 au minilab, qui le refuserait.
    /// </summary>
    private sealed record ChoixTailleLibre
    {
        public string Label => "Personnalisé…  (taille au choix)";
    }

    private sealed record ChoixAgrandissementLibre
    {
        public string Label => "Agrandissement personnalisé…  (A2, A3…)";
    }

    /// <summary>
    /// Où la liste était posée avant qu'on y choisisse une entrée d'ACTION.
    ///
    /// Ces entrées ne sont pas un format : après les avoir ouvertes, la liste doit retrouver
    /// le produit d'avant. Sans cela, elle resterait sur « Personnalisé… », et
    /// <c>DefaultProduct</c> — qui la lit — rendrait null : le bouton « Appliquer aux
    /// cochées » ne ferait plus rien, sans rien dire.
    /// </summary>
    private int _indexProduitPrecedent;

    // — format personnalisé —

    /// <summary>
    /// Taille demandée, ou null pour le parcours ordinaire.
    ///
    /// Elle peut arriver en cours de route : une commande de borne s'ouvre en 10×15 et le
    /// client demande du 5,5×8 au comptoir. Voir <see cref="BasculerEnTaillePersonnalisee"/>.
    /// </summary>
    private CustomSize? _taillePerso;

    /// <summary>
    /// Le produit de travail du format personnalisé : il n'est PAS au catalogue.
    ///
    /// Il n'existe que pour donner à tout l'écran — cadres de recadrage, vignettes, écran
    /// « Modifier », aperçus — le rapport largeur/hauteur voulu. Sans lui, il faudrait
    /// enseigner la taille personnalisée à chacun de ces endroits. À la validation, il est
    /// remplacé par le PAPIER retenu, qui, lui, est un vrai produit du catalogue : rien de
    /// ce code fantôme ne descend jusqu'à la commande.
    /// </summary>
    private Product? _produitPerso;

    /// <summary>Un papier proposé à l'opérateur, ou le choix automatique.</summary>
    /// <param name="Papier">Null = « Automatique », le logiciel décide au meilleur prix.</param>
    private sealed record PaperChoice(PaperOption? Papier, int ParPlanche)
    {
        public string? Code => Papier?.Code;

        public string Label => Papier is null
            ? "Automatique (au meilleur prix)"
            : $"{Papier.Name} — {Papier.UnitPrice:0.00} € — {ParPlanche} par planche";
    }

    private void PasserEnTaillePersonnalisee(CustomSize taille)
    {
        _produitPerso = new Product
        {
            Code = "perso",
            Name = $"Personnalisé {taille.Libelle}",
            WidthMm = taille.WidthMm,
            HeightMm = taille.HeightMm,
            Dpi = 300,
            Price = 0,
            DefaultFit = FitMode.Fill,
            Output = ProductOutput.FujiMinilab,
        };

        // La liste des produits cède la place à celle des PAPIERS : l'opérateur peut
        // imposer le format de sortie au lieu de subir le calcul — il est le seul à savoir
        // quel rouleau est chargé, et ce qu'il veut vendre. Le nombre de photos par planche
        // est affiché sur chaque entrée : c'est ce qui rend le choix évident.
        var papiers = CustomSizeView.PapiersDisponibles()
            .Select(p => new
            {
                Papier = p,
                Capacite = CustomSheetLayout.CapacityOf(p, taille.WidthMm, taille.HeightMm).PerSheet,
            })
            .Where(x => x.Capacite > 0)
            .OrderBy(x => x.Papier.AreaMm2)
            .Select(x => new PaperChoice(x.Papier, x.Capacite))
            .ToList();

        var choix = new List<PaperChoice> { new(null, 0) };
        choix.AddRange(papiers);

        ProductLabelText.Text = "Papier :";
        ProductCombo.ItemsSource = choix;
        ProductCombo.SelectedIndex = Math.Max(0,
            choix.FindIndex(c => c.Code is not null
                                 && c.Code.Equals(taille.PaperCode, StringComparison.OrdinalIgnoreCase)));

        TaillePersoText.Text = taille.Libelle;
        TaillePersoText.Visibility = Visibility.Visible;

        // une planche index se tire à un format du catalogue : elle n'a pas de sens ici
        IndexButton.Visibility = Visibility.Collapsed;
    }

    private bool EnTaillePersonnalisee => _taillePerso is not null;

    /// <summary>
    /// Pose le contour de découpe demandé à la saisie de la taille libre.
    ///
    /// <b>Une valeur de DÉPART, pas un verrou</b> : l'écran d'édition garde la main photo par
    /// photo. Et rien n'est posé hors taille libre — un tirage du catalogue tient son
    /// contour de son propre réglage, et le forcer ici tracerait des traits sur des 10×15
    /// que personne ne découpe.
    /// </summary>
    private void AppliquerLeContourPerso(PhotoItem photo)
    {
        if (_taillePerso is { ContourNoir: true }) photo.CutBorder = true;
    }

    /// <summary>
    /// Demande une taille libre, puis y bascule les photos DÉJÀ ouvertes.
    ///
    /// C'est le cas du comptoir : la commande arrive d'une borne en 10×15, le client la voit
    /// à l'écran et demande autre chose. Repartir de l'accueil pour retrouver le dossier
    /// ferait perdre recadrages et corrections — ici, ils sont conservés, seul le cadre
    /// change de rapport.
    /// </summary>
    private void DemanderUneTaillePersonnalisee()
    {
        Navigator.Go(new CustomSizeView(BasculerEnTaillePersonnalisee), "Taille personnalisée");
    }

    /// <summary>
    /// Demande un agrandissement à taille libre, et applique le format retenu.
    ///
    /// Bien plus simple que la bascule en planche : le format d'agrandissement est un VRAI
    /// produit du catalogue, créé à la validation. Il n'y a donc rien à enseigner au reste
    /// de l'écran — juste à le poser sur les photos visées.
    /// </summary>
    /// <param name="appliquer">Ce qu'on fait du produit : une photo, la sélection…</param>
    private static void DemanderUnAgrandissement(Action<Product> appliquer) =>
        Navigator.Go(new CustomEnlargementView(appliquer), "Agrandissement personnalisé");

    private void BasculerEnTaillePersonnalisee(CustomSize taille)
    {
        _taillePerso = taille;
        PasserEnTaillePersonnalisee(taille);

        // le format de TOUTES les photos change, cochées ou non : la planche est une seule
        // ligne de commande, elle ne peut pas mélanger deux tailles
        foreach (var photo in _photos)
        {
            photo.Product = _produitPerso;

            // le contour demandé avec la taille suit la bascule : une planche dont la moitié
            // des photos porte le trait ne se coupe pas
            AppliquerLeContourPerso(photo);
        }

        FileLog.Write($"Bascule en taille personnalisée {taille.Libelle} sur {_photos.Count} photo(s)");
        UpdateSummary();
    }

    /// <summary>Papier imposé par l'opérateur, ou null s'il laisse le logiciel décider.</summary>
    private string? PapierImpose =>
        EnTaillePersonnalisee ? (ProductCombo.SelectedItem as PaperChoice)?.Code : null;

    private Product? DefaultProduct =>
        _produitPerso ?? (ProductCombo.SelectedItem as ProductChoice)?.Product;

    private async Task ScanAndLoadAsync()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts = new CancellationTokenSource();
        var ct = _thumbnailCts.Token;

        if (_photos.Count == 0)
        {
            // Les PDF sont éclatés en une image par page AVANT le tri : chaque page devient
            // une vignette ordinaire, et rien en aval ne sait qu'un PDF existe. C'est fait
            // ici, sur le fil de fond qui parcourt déjà le dossier — le rendu PDFium coûte
            // quelques dizaines de millisecondes par page.
            //
            // Ensuite la plus récente en premier : c'est ce que le client vient de prendre,
            // et c'est ce qu'il veut tirer. Le bouton « trier » bascule vers le nom.
            var files = await Task.Run(
                () => PhotoScanner.TrierParDateDecroissante(
                    PdfPages.Developper(
                        PhotoScanner.Scan(_rootPath, _avecSousDossiers, PhotoScanner.MaxAffichable, ct),
                        App.Services.CacheDir)),
                ct);
            foreach (var file in files)
            {
                var photo = new PhotoItem(file, OnCartChanged);

                // Le contour demandé à la saisie de la taille libre, AVANT la reprise d'un
                // brouillon : ce que l'opérateur avait décidé photo par photo l'emporte sur
                // une valeur de départ.
                AppliquerLeContourPerso(photo);

                // le cadrage du client d'abord, le brouillon par-dessus : le second est ce
                // que l'OPÉRATEUR a décidé, il l'emporte
                AppliquerLeCadrageDeLaBorne(photo);
                AppliquerLAttente(photo);

                _photos.Add(photo);
            }

            RecreerLesDoublonsEnAttente();

            PhotosGrid.ItemsSource = _photos;
            AfficherEtatDuDossier();
            UpdateSummary();
        }

        await ChargerLesVignettesAsync(ct);
    }

    // ----- ce que le client a réglé à la borne, et ce que l'opérateur a mis de côté -----

    private readonly IReadOnlyDictionary<string, DiLandImporter.CadrageBorne>? _cadragesBorne;
    private readonly TravailEnAttente? _enAttente;
    private readonly string? _produitParDefaut;

    /// <summary>
    /// Feuille de montage retenue à l'écran précédent, ou null. Elle vaut pour
    /// <see cref="_produitParDefaut"/> et pour lui seul.
    /// </summary>
    private readonly string? _montageFeuille;

    /// <summary>
    /// La feuille sur laquelle monter les tirages de ce produit, ou null.
    ///
    /// <b>Le montage suit le FORMAT, pas la photo.</b> Il a été choisi pour un format précis,
    /// à un écran où l'opérateur voyait combien de tirages tenaient sur la feuille. Une photo
    /// passée à un autre produit — c'est un geste courant au comptoir — n'a rien à voir avec
    /// ce choix et repart en tirage ordinaire, ce qui est aussi ce qui garde la grille pleine :
    /// une ligne montée ne mélange qu'un seul format.
    /// </summary>
    private string? FeuilleDeMontagePour(Product produit) =>
        _montageFeuille is not null && _produitParDefaut is not null
        && produit.Code.Equals(_produitParDefaut, StringComparison.OrdinalIgnoreCase)
            ? _montageFeuille
            : null;

    /// <summary>
    /// L'identité de CETTE préparation de commande, qu'elle ait déjà été mise de côté ou
    /// non. Fixe pour toute la vie de l'écran : deux mises en attente successives mettent à
    /// jour la même entrée au lieu d'en empiler deux sur l'accueil.
    /// </summary>
    private readonly Guid _attenteId;

    /// <summary>
    /// Repose sur une photo le recadrage validé par le client à la borne.
    ///
    /// <b>L'ORDRE DES QUATRE AFFECTATIONS EST IMPOSÉ, et c'est tout l'objet de cette
    /// méthode</b> — trois mutateurs de <see cref="PhotoItem"/> remettent le cadrage à
    /// zéro, et dans le mauvais ordre ils effacent ce qu'on vient de poser :
    ///
    /// <list type="table">
    /// <item><term><c>Product</c></term><description>appelle <c>OublierCadre()</c> → <c>Crop = Full</c></description></item>
    /// <item><term><c>RotationQuarterTurns</c></term><description>jette le cadre ET le recadrage</description></item>
    /// <item><term><c>FitOverride</c></term><description>jette le cadre</description></item>
    /// </list>
    ///
    /// Le produit est posé ICI, à la création, et pas seulement parce qu'il vient en
    /// premier : sans lui, le <c>photo.Product ??= DefaultProduct</c> d'<c>OnModify</c> le
    /// poserait plus tard et emporterait le recadrage avec lui. C'est précisément ce qui
    /// faisait que « Modifier » perdait le cadrage du client.
    /// </summary>
    private void AppliquerLeCadrageDeLaBorne(PhotoItem photo)
    {
        if (_cadragesBorne is null) return;
        if (!_cadragesBorne.TryGetValue(photo.Name, out var cadrage)) return;

        // 1. le produit — en taille personnalisée c'est le produit fantôme qui l'emporte,
        //    une planche ne peut pas mélanger deux formats
        photo.Product = EnTaillePersonnalisee
            ? _produitPerso
            : ProduitDuCatalogue(cadrage.CodeProduit) ?? DefaultProduct;

        // 1 bis. la finition que le client a choisie à la borne. Hors de la liste des
        //    quatre affectations sensibles : elle ne touche ni au cadre ni au recadrage,
        //    et le passeur de Product ne l'efface pas. C'est elle qui décide du ROULEAU,
        //    donc de la machine — sans elle, « Modifier » ouvrait la commande sans
        //    finition et le tirage repartait sur le rouleau de la bonne largeur.
        photo.Finish = cadrage.Finition;

        // 2. les quarts de tour
        photo.RotationQuarterTurns = cadrage.QuartsDeTour;

        // 3. le redressement fin (le « Tilt » de DiLand)
        photo.FineRotationDegrees = cadrage.RedressementDegres;

        // 4. le recadrage, en DERNIER. Un rectangle incohérent est ignoré pour CETTE photo
        //    seulement : mieux vaut une photo cadrée au centre que l'ouverture qui échoue
        photo.PoserLeCadrageDOrigine(cadrage.Crop);

        // La quantité commandée, mais la case reste DÉCOCHÉE : cet écran est celui où
        // l'opérateur contrôle avant d'engager du papier (décision du 01/08/2026). Rien ne
        // part tant qu'il n'a pas coché.
        photo.Quantity = Math.Clamp(cadrage.Quantite, 1, 99);
    }

    /// <summary>
    /// Repose sur une photo la commande mise en attente. Même ordre imposé que ci-dessus.
    ///
    /// Une photo absente de l'attente arrive dans son état par défaut, et une photo de
    /// l'attente absente du dossier est simplement ignorée : les deux listes n'ont aucune
    /// raison de coïncider un mois plus tard.
    /// </summary>
    /// <summary>
    /// Les entrées de l'attente qui n'ont pas encore trouvé leur vignette, par nom de
    /// fichier.
    ///
    /// <b>Une file, et non une simple recherche</b> : depuis le bouton « Dupliquer », une
    /// même photo peut figurer DEUX fois dans la commande — en 10×15 et en 15×20. Les deux
    /// entrées portent le même nom de fichier, et un <c>FirstOrDefault</c> aurait donné la
    /// première aux deux vignettes : le second format était perdu à la reprise.
    /// </summary>
    private Dictionary<string, Queue<PhotoEnAttente>>? _attentesARendre;

    private Dictionary<string, Queue<PhotoEnAttente>> AttentesARendre =>
        _attentesARendre ??= _enAttente is null
            ? new Dictionary<string, Queue<PhotoEnAttente>>(StringComparer.OrdinalIgnoreCase)
            : _enAttente.Photos
                .GroupBy(p => p.FileName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => new Queue<PhotoEnAttente>(g),
                    StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Recrée les photos dupliquées d'une commande mise de côté.
    ///
    /// Le dossier ne porte qu'UN fichier par photo : le balayage ne fabrique donc qu'une
    /// vignette, alors que l'attente peut en compter deux — la même photo en 10×15 et en
    /// 15×20. Sans cette reprise, le doublon disparaissait à la réouverture, et avec lui
    /// la moitié de la commande.
    ///
    /// Les entrées restantes sont celles qu'<see cref="AppliquerLAttente"/> n'a pas
    /// consommées : chacune reçoit sa vignette, posée juste après l'originale.
    /// </summary>
    private void RecreerLesDoublonsEnAttente()
    {
        if (_enAttente is null) return;

        foreach (var (nom, file) in AttentesARendre.Where(e => e.Value.Count > 0).ToList())
        {
            var originale = _photos.FirstOrDefault(
                p => p.Name.Equals(nom, StringComparison.OrdinalIgnoreCase));

            // le fichier a disparu du dossier : rien à dupliquer, et la commande s'ouvre
            // quand même avec ce qu'il en reste
            if (originale is null) continue;

            // La position AVANCE d'un doublon à l'autre. Insérer chaque fois juste après
            // l'originale les remettrait dans l'ordre inverse : sur une photo tirée en
            // 10×15, 13×18 puis 15×20, les deux derniers formats se seraient croisés.
            var rang = _photos.IndexOf(originale);

            while (file.Count > 0)
            {
                var copie = new PhotoItem(originale.Path, OnCartChanged);
                AppliquerLAttente(copie);   // consomme l'entrée suivante de ce nom

                _photos.Insert(++rang, copie);
            }
        }
    }

    private void AppliquerLAttente(PhotoItem photo)
    {
        if (_enAttente is null) return;

        if (!AttentesARendre.TryGetValue(photo.Name, out var file) || file.Count == 0) return;
        var enregistree = file.Dequeue();

        photo.Product = EnTaillePersonnalisee
            ? _produitPerso
            : ProduitDuCatalogue(enregistree.ProductCode) ?? photo.Product ?? DefaultProduct;

        photo.Finish = enregistree.Finish;
        photo.RotationQuarterTurns = enregistree.RotationQuarterTurns;
        photo.FineRotationDegrees = enregistree.FineRotationDegrees;
        photo.FitOverride = enregistree.Fit;
        photo.CutBorder = enregistree.CutBorder;
        photo.Adjustments = enregistree.Adjustments;

        photo.PoserLeCadrageDOrigine(enregistree.Crop);

        photo.Quantity = Math.Clamp(enregistree.Quantity, 1, 99);

        // l'attente rend AUSSI les cases cochées : c'est le travail de l'opérateur, pas la
        // commande brute du client — il avait déjà décidé ce qu'il tirait
        photo.Selected = enregistree.Selected;
        if (photo.Selected) _photoCourante = photo;
    }

    /// <summary>Un produit du catalogue par son code, ou null — un code disparu ne doit rien casser.</summary>
    private static Product? ProduitDuCatalogue(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : App.Services.Catalog.Enabled.FirstOrDefault(
                p => p.Code.Equals(code, StringComparison.OrdinalIgnoreCase));

    // ----- mise en attente : servir quelqu'un d'autre, puis reprendre -----

    /// <summary>
    /// Met la commande de côté et revient à l'accueil.
    ///
    /// <b>Le geste du comptoir</b> : un client hésite ou s'absente, un autre attend
    /// derrière. On revient à l'accueil parce que c'est là qu'on sert le suivant, et c'est
    /// aussi là que la commande mise de côté réapparaît.
    ///
    /// <b>Explicite, jamais automatique.</b> Mettre en attente en quittant l'écran ferait
    /// s'accumuler sur l'accueil des commandes qu'on n'a fait qu'ouvrir, et la liste ne
    /// voudrait plus rien dire.
    /// </summary>
    internal void MettreEnAttente()
    {
        if (!EnregistrerPourReprise())
        {
            MessageBox.Show("La commande n'a pas pu être mise en attente — voir le journal.",
                "En attente", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(
            "Commande mise en attente.\n\n" +
            "Elle vous attend sur l'accueil, sous « En attente » : « Reprendre » la " +
            "rouvrira telle que vous la laissez.",
            "En attente", MessageBoxButton.OK, MessageBoxImage.Information);

        AccueilStudio.Rentrer();
    }

    /// <summary>
    /// L'enregistrement seul, sans message ni navigation : c'est ce que le bouton
    /// « Accueil » de l'en-tête appelle, lui qui a sa propre suite.
    ///
    /// Une grille VIDE n'est pas mise de côté : ouvrir un dossier sans rien y faire puis
    /// revenir à l'accueil ne doit pas déposer une ligne « 0 photo » à chaque fois.
    /// </summary>
    public bool EnregistrerPourReprise()
    {
        if (_photos.Count == 0) return false;

        try
        {
            var travail = ConstruireLAttente();
            App.Services.CommandesEnAttente.Enregistrer(travail);

            FileLog.Write($"Commande mise en attente ({travail.Resume}) — " +
                          $"{travail.PhotosDirectory}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write("Mise en attente impossible", ex);
            return false;
        }
    }

    /// <inheritdoc/>
    public string ResumeDeLAttente => ResumerPourLAccueil();

    private void OnMettreEnAttente(object sender, RoutedEventArgs e) => MettreEnAttente();

    // ----- prévenir le client à la fin du tirage -----

    /// <summary>
    /// Adresse à prévenir dès que la commande sera SORTIE, ou null.
    ///
    /// Prise ICI, avant l'impression, parce que c'est le seul moment où le client est
    /// encore devant le comptoir. Le message part tout seul quand la machine a fini :
    /// l'opérateur n'a rien à surveiller, et c'est tout l'intérêt.
    /// </summary>
    private string? _adresseAPrevenir;

    private void OnPrevenirALaFin(object sender, RoutedEventArgs e)
    {
        if (!App.Services.Mail.EstUtilisable)
        {
            MessageBox.Show(
                "L'envoi par courriel n'est pas configuré : il manque " +
                App.Services.Mail.CeQuiManque() + ".\n\n" +
                "Ouvrez Paramètres → Envoi par courriel.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var saisie = SaisirLAdresse.Demander(_adresseAPrevenir);
        if (saisie is null) return;   // annulé : on ne touche à rien

        _adresseAPrevenir = saisie.Length == 0 ? null : saisie;
        AfficherLAdresseAPrevenir();
    }

    /// <summary>Le bouton dit ce qui est armé : sinon rien ne distingue les deux états.</summary>
    private void AfficherLAdresseAPrevenir()
    {
        PrevenirButton.Content = _adresseAPrevenir is null
            ? "🔔  Prévenir à la fin"
            : $"🔔  {_adresseAPrevenir}";

        PrevenirButton.ToolTip = _adresseAPrevenir is null
            ? "Saisir l'adresse du client : dès que la commande sera sortie, un courriel " +
              "lui dira qu'elle l'attend en magasin."
            : $"Le client sera prévenu à {_adresseAPrevenir} dès que la commande sera " +
              "sortie. Cliquez pour changer ou retirer l'adresse.";
    }

    /// <summary>Ce qu'on affiche sur l'accueil : de quoi reconnaître la commande d'un coup d'œil.</summary>
    private string ResumerPourLAccueil()
    {
        var cochees = _photos.Count(p => p.Selected);

        var morceaux = new List<string>
        {
            $"{_photos.Count} photo(s)",
            cochees == 0 ? "aucune cochée" : $"{cochees} cochée(s)",
        };

        if (_taillePerso is not null) morceaux.Add(_taillePerso.Libelle);
        else if (_photos.FirstOrDefault(p => p.Selected)?.Product is { } produit)
            morceaux.Add(produit.Name);

        if (!string.IsNullOrWhiteSpace(TotalText.Text)) morceaux.Add(TotalText.Text);

        return string.Join(" · ", morceaux);
    }

    private TravailEnAttente ConstruireLAttente() => new()
    {
        Id = _attenteId,
        SavedAt = DateTimeOffset.Now,
        PhotosDirectory = _rootPath,
        AvecSousDossiers = _avecSousDossiers,
        ProduitParDefaut = _produitParDefaut,
        KioskOid = _commandeBorne,
        Titre = _enAttente?.Titre is { Length: > 0 } deja ? deja : TitreDeLEcran(),
        Resume = ResumerPourLAccueil(),
        CustomWidthMm = _taillePerso?.WidthMm ?? 0,
        CustomHeightMm = _taillePerso?.HeightMm ?? 0,
        PaperCode = PapierImpose,
        MontageSheetCode = _montageFeuille,
        Photos = _photos
            // les planches d'index sont FABRIQUÉES par l'écran, pas trouvées dans le
            // dossier : les enregistrer ferait reprendre un fichier du cache qui aura pu
            // disparaître, et la bascule du bouton les refait de toute façon
            .Where(p => !_planchesIndex.Contains(p))
            .Select(p =>
            {
                // le cadre porte la vérité : sans ce report, une photo jamais ouverte
                // partirait en attente en « pleine image » (même piège qu'à l'impression)
                p.AppliquerCadre();

                return new PhotoEnAttente
                {
                    FileName = p.Name,
                    Selected = p.Selected,
                    Quantity = p.Quantity,
                    ProductCode = p.Product?.Code,
                    Finish = p.Finish,
                    CropX = p.Crop.X,
                    CropY = p.Crop.Y,
                    CropWidth = p.Crop.Width,
                    CropHeight = p.Crop.Height,
                    RotationQuarterTurns = p.RotationQuarterTurns,
                    FineRotationDegrees = p.FineRotationDegrees,
                    Fit = p.FitOverride,
                    CutBorder = p.CutBorder,
                    Adjustments = p.Adjustments,
                };
            })
            .ToList(),
    };

    /// <summary>
    /// De quoi renommer la commande sur l'accueil : le numéro de borne s'il y en a un,
    /// sinon le nom du dossier — c'est ce que l'opérateur reconnaîtra.
    /// </summary>
    private string TitreDeLEcran() => _commandeBorne is not null
        ? "Commande de borne"
        : Path.GetFileName(_rootPath.TrimEnd('\\', '/')) is { Length: > 0 } dossier
            ? dossier
            : "Tirages";

    /// <summary>Ce qu'une photo a donné : sa vignette et sa définition, ou l'échec de lecture.</summary>
    private sealed record VignetteLue(PhotoItem Photo, byte[]? Jpeg, int Largeur, int Hauteur);

    /// <summary>
    /// Remplit la grille de ses vignettes.
    ///
    /// <b>En parallèle</b>, et non plus une par une : sur les 36 photos de 39 Mpx d'une commande
    /// réelle, le chargement séquentiel prenait 5 594 ms contre 1 177 ms ici. C'est ce temps que
    /// l'opérateur passait devant une planche grise — et c'est aussi lui qui rendait la planche
    /// d'index lente, puisqu'elle part du cache que ce chargement remplit.
    ///
    /// <b>Une seule ouverture par photo — pour de bon.</b> La définition venait d'un appel
    /// de plus à <c>GetOrientedSize</c>, soit un second parcours du fichier, payé même
    /// quand la vignette était déjà en cache : rouvrir un dossier déjà vu touchait les
    /// trente-trois originaux pour rien. Elle vient maintenant de la lecture qui fabrique
    /// la vignette, et voyage avec elle dans le cache — voir <c>ThumbnailService.Lire</c>.
    ///
    /// <b>Par tranches, dans l'ordre de la planche.</b> Un seul grand lot laisserait la grille
    /// vide jusqu'au bout — jusqu'à 1200 photos peuvent être affichées
    /// (<see cref="PhotoScanner.MaxAffichable"/>). Les vignettes se posent donc de haut en bas,
    /// tranche par tranche, comme avant, mais chaque tranche est lue sur tous les cœurs.
    /// </summary>
    private async Task ChargerLesVignettesAsync(CancellationToken ct)
    {
        var thumbnails = App.Services.Thumbnails;
        var aLire = _photos.Where(p => p.Thumbnail is null).ToList();
        if (aLire.Count == 0) return;

        // assez large pour occuper tous les cœurs, assez court pour que la planche se
        // remplisse sous les yeux au lieu d'apparaître d'un bloc
        var tranche = Math.Max(8, Environment.ProcessorCount * 2);
        var illisibles = new List<PhotoItem>();

        for (var debut = 0; debut < aLire.Count; debut += tranche)
        {
            if (ct.IsCancellationRequested) return;

            // GetRange et non Skip/Take : sur 1200 photos, le Skip reparcourait la liste
            // depuis le début à chaque tranche
            var lot = aLire.GetRange(debut, Math.Min(tranche, aLire.Count - debut));
            var lues = new VignetteLue[lot.Count];

            try
            {
                await Task.Run(() => Parallel.For(0, lot.Count,
                    new ParallelOptions { CancellationToken = ct }, i =>
                    {
                        var photo = lot[i];
                        try
                        {
                            // définition et rapport, affichés sur la vignette comme chez
                            // DiLand : l'opérateur voit tout de suite ce qu'il peut tirer
                            // sans perte. Ils viennent de la MÊME lecture que la vignette —
                            // voir ThumbnailService.Lire.
                            var lue = thumbnails.Lire(photo.Path);
                            lues[i] = new VignetteLue(photo, lue.Jpeg, lue.SourceWidth, lue.SourceHeight);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // Fichier que le moteur d'image n'ouvre pas : extension trompeuse,
                            // JPEG tronqué par une carte défaillante, fichier verrouillé. Il
                            // RESTAIT dans la planche, sans vignette mais cochable — donc mis
                            // dans une commande, pour échouer au rendu une fois le client parti.
                            FileLog.Write($"Photo écartée, illisible : {photo.Path}", ex);
                            lues[i] = new VignetteLue(photo, null, 0, 0);
                        }
                    }), ct);
            }
            catch (OperationCanceledException) { return; }

            if (ct.IsCancellationRequested) return;

            foreach (var lue in lues)
            {
                if (lue is null) continue;

                if (lue.Jpeg is null)
                {
                    illisibles.Add(lue.Photo);
                    continue;
                }

                lue.Photo.SetSourceThumbnail(ToBitmap(lue.Jpeg));
                lue.Photo.SetSourceSize(lue.Largeur, lue.Hauteur);
            }
        }

        if (illisibles.Count > 0) Ecarter(illisibles);
    }

    /// <summary>
    /// Retire de la planche ce dont aucune imprimante ne fera rien, et le dit.
    ///
    /// Une seule reconstruction de la liste à la fin du chargement : la planche n'est pas
    /// virtualisée, la reconstruire à chaque fichier fautif coûterait plus cher que le
    /// chargement lui-même.
    /// </summary>
    private void Ecarter(List<PhotoItem> illisibles)
    {
        foreach (var photo in illisibles) _photos.Remove(photo);

        // la photo visée par « Recadrer » vient peut-être d'être écartée
        if (_photoCourante is { } courante && !_photos.Contains(courante)) _photoCourante = null;

        PhotosGrid.ItemsSource = null;
        PhotosGrid.ItemsSource = _photos;

        _illisibles = illisibles.Count;
        AfficherEtatDuDossier();
        UpdateSummary();
    }

    /// <summary>Nombre de fichiers écartés parce qu'illisibles, pour le dire à l'opérateur.</summary>
    private int _illisibles;

    /// <summary>
    /// Ce que le dossier a donné, dit explicitement.
    ///
    /// Un dossier sans photos affichait une planche vide, sans un mot : l'opérateur
    /// restait sur un écran muet devant le client. Et un dossier trop gros ne disait pas
    /// davantage qu'il était tronqué — il faisait tomber l'application.
    /// </summary>
    private void AfficherEtatDuDossier()
    {
        var vide = _photos.Count == 0;
        EmptyPanel.Visibility = vide ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = _avecSousDossiers
            ? $"Aucune photo dans « {Path.GetFileName(_rootPath.TrimEnd('\\', '/'))} », " +
              "ni dans ses sous-dossiers."
            : $"Aucune photo directement dans « {Path.GetFileName(_rootPath.TrimEnd('\\', '/'))} ». " +
              "Elles sont peut-être dans un sous-dossier.";

        var avis = new List<string>();

        if (_photos.Count + _illisibles >= PhotoScanner.MaxAffichable)
            avis.Add($"Ce dossier contient plus de {PhotoScanner.MaxAffichable} photos : seules " +
                     $"les {PhotoScanner.MaxAffichable} premières sont affichées. Ouvrez un " +
                     "sous-dossier pour voir les autres.");

        if (_illisibles > 0)
            avis.Add($"{_illisibles} fichier{(_illisibles > 1 ? "s" : "")} écarté" +
                     $"{(_illisibles > 1 ? "s" : "")} : illisible" +
                     $"{(_illisibles > 1 ? "s" : "")}, aucune imprimante n'en sortirait rien.");

        TruncatedBanner.Visibility = avis.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TruncatedText.Text = string.Join("  ", avis);
    }

    /// <summary>Retour à l'écran précédent depuis l'état « aucune photo ».</summary>
    private void OnChangeFolder(object sender, RoutedEventArgs e) => Navigator.Back();

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

    private void OnPhotoClicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.Tag is not PhotoItem photo) return;

        // Maj+clic prend toute la PLAGE depuis la dernière photo touchée : sur une carte de
        // soixante photos dont le client en veut quarante d'affilée, c'est quarante clics
        // en moins. Le geste est celui de l'explorateur Windows, donc il n'a pas à
        // s'apprendre.
        //
        // L'ancre retombe sur la dernière photo TOUCHÉE quand aucun clic simple ne l'a
        // encore posée : après un Ctrl+A ou un « tout », le Maj+clic ne faisait rien du
        // tout et le geste passait pour absent.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) &&
            (_ancreSelection ?? _photoCourante) is { } ancre)
        {
            SelectionnerLaPlage(ancre, photo);
            return;
        }

        // un clic coche ou décoche, comme le SelectionMode.Multiple de DiLand : ici on
        // compose la commande, il n'y a rien à restreindre. Le tri à la touche Ctrl est
        // dans l'écran Modifier, là où il sert à viser quelques photos d'un réglage.
        Toggle(photo);

        // l'ancre suit le dernier clic SIMPLE, coché ou décoché : c'est de là que partira
        // la prochaine plage
        _ancreSelection = photo;
    }

    /// <summary>
    /// D'où part la prochaine sélection par plage. Nulle tant que rien n'a été cliqué : un
    /// Maj+clic isolé ne doit pas prendre toutes les photos depuis le début du dossier.
    /// </summary>
    private PhotoItem? _ancreSelection;

    /// <summary>
    /// Coche toutes les photos entre deux vignettes, bornes comprises.
    ///
    /// <b>Elle COCHE, elle ne bascule pas.</b> Basculer chaque photo de la plage
    /// décocherait celles qui étaient déjà prises — sur une plage qui en recouvre une autre,
    /// l'opérateur perdrait son travail au lieu de l'étendre.
    ///
    /// L'ordre est celui de la GRILLE, qui n'est pas celui du disque : les photos se
    /// présentent de la plus récente à la plus ancienne, ou par nom si l'opérateur a trié.
    /// On travaille donc sur <c>_photos</c>, qui porte l'ordre affiché.
    /// </summary>
    private void SelectionnerLaPlage(PhotoItem depuis, PhotoItem jusqua)
    {
        var debut = _photos.IndexOf(depuis);
        var fin = _photos.IndexOf(jusqua);
        if (debut < 0 || fin < 0) return;

        if (debut > fin) (debut, fin) = (fin, debut);

        for (var i = debut; i <= fin; i++) Prendre(_photos[i]);

        _photoCourante = jusqua;
        UpdateSummary();
    }

    /// <summary>
    /// Fait entrer une photo dans la commande.
    ///
    /// <b>Le format et la quantité du bandeau ne s'appliquent qu'aux photos qui n'ont
    /// RIEN.</b> Une photo venue d'une borne arrive avec le format et le nombre
    /// d'exemplaires choisis par le client : les écraser en la cochant lui ferait tirer
    /// autre chose que ce qu'il a commandé. C'est la même règle que pour le format seul —
    /// la sélection ne décide de rien, elle prend.
    /// </summary>
    private void Prendre(PhotoItem photo)
    {
        if (photo.Selected) return;

        if (photo.Product is null)
        {
            photo.Product = DefaultProduct;
            photo.Quantity = _quantity;
        }

        photo.Selected = true;
    }

    /// <summary>Ajoute une photo à la commande, ou l'en retire.</summary>
    private void Toggle(PhotoItem photo)
    {
        if (!photo.Selected && photo.Product is null)
        {
            // première sélection : la photo prend le produit et la quantité du bandeau
            photo.Product = DefaultProduct;
            photo.Quantity = _quantity;
        }

        photo.Selected = !photo.Selected;

        // la dernière photo touchée est celle que « Recadrer » vise
        if (photo.Selected) _photoCourante = photo;
        else if (ReferenceEquals(_photoCourante, photo)) _photoCourante = null;
    }

    // — barre d'outils de la planche (réduire, agrandir, tout, aucun, trier) —

    private void OnSmaller(object sender, RoutedEventArgs e) => Zoom(1 / 1.15);
    private void OnBigger(object sender, RoutedEventArgs e) => Zoom(1.15);

    /// <summary>
    /// Échelle des vignettes. Elle vit ici plutôt que dans un <c>ScaleTransform</c> du
    /// gabarit : la planche est virtualisée, et c'est le panneau qui a besoin de connaître
    /// la taille réelle d'une tuile pour savoir combien en tiennent à l'écran.
    /// </summary>
    private double _echelleVignettes = 1.0;

    private void Zoom(double facteur)
    {
        // bornes larges mais réelles : sous 0,5 on ne distingue plus un visage, au-delà
        // de 2 on ne voit plus assez de photos pour choisir
        _echelleVignettes = Math.Clamp(_echelleVignettes * facteur, 0.5, 2.0);
        Controls.PlancheVirtualisee.SetEchelle(PhotosGrid, _echelleVignettes);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        if (_photos.All(p => p.Selected)) return; // déjà tout pris : le bouton ne défait pas
        SelectAll();
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var photo in _photos) photo.Selected = false;
        _photoCourante = null;
    }

    /// <summary>
    /// Le classement en vigueur. La planche arrive dans l'ordre du chargement — la plus
    /// récente d'abord, c'est-à-dire ce que le client vient de prendre.
    /// </summary>
    private CritereDeTri _tri = CritereDeTri.DateRecente;

    /// <summary>Déroule les classements. Voir <see cref="MenuDeTri"/>.</summary>
    private void OnSort(object sender, RoutedEventArgs e) =>
        MenuDeTri.Ouvrir(SortButton, _tri, Trier);

    private void Trier(CritereDeTri critere)
    {
        _tri = critere;
        SortButton.Content = "⇅  " + MenuDeTri.Libelle(critere);

        var triees = MenuDeTri.Appliquer(_photos, critere, p => p.Path, p => p.Name);

        _photos.Clear();
        _photos.AddRange(triees);

        PhotosGrid.ItemsSource = null;
        PhotosGrid.ItemsSource = _photos;
        UpdateSummary();
    }

    /// <summary>Ctrl+A : toute la planche d'un coup, comme chez DiLand.</summary>
    private void SelectAll()
    {
        // tout est déjà pris : on décoche, pour que la même touche défasse
        var toutPris = _photos.Count > 0 && _photos.All(p => p.Selected);

        foreach (var photo in _photos)
        {
            if (toutPris)
            {
                photo.Selected = false;
                continue;
            }

            // même règle qu'au clic : on ne touche ni au format ni à la quantité d'une
            // photo qui les tient déjà de la borne
            Prendre(photo);
        }

        if (toutPris) _photoCourante = null;
        else _photoCourante ??= _photos.LastOrDefault();

        // la plage repart de zéro : l'ancre d'avant appartenait à une sélection qu'on
        // vient de refaire d'un bloc
        _ancreSelection = null;

        FileLog.Write(toutPris
            ? "Ctrl+A : sélection annulée"
            : $"Ctrl+A : {_photos.Count} photo(s) prises");

        UpdateSummary();
    }

    private void OnCartChanged()
    {
        UpdateSummary();

        // la finition d'une photo a pu changer — de produit, de sélection : la machine
        // annoncée doit suivre, sans quoi la barre resterait sur ce qu'elle disait à
        // l'ouverture
        RafraichirLAutomatique();
    }

    /// <summary>
    /// Une machine du minilab, avec le papier qui y est chargé.
    /// <see cref="Automatique"/> = aucune machine imposée, le rouleau décide.
    /// </summary>
    private sealed record MachineChoice(char Id, string Label)
    {
        /// <summary>Identifiant réservé au choix « automatique » et aux lignes d'excuse.</summary>
        public const char Aucune = ' ';

        public bool Automatique => Id == Aucune;
    }

    /// <summary>
    /// Charge les machines du minilab et le papier de chacune. L'opérateur choisit sur
    /// quelle machine tirer, et voit du même coup ce qui y est chargé : imprimer un 13×18
    /// sur un rouleau de 152 mm ne donne rien de bon.
    ///
    /// <b>« Automatique » est en tête, et c'est ce qui est retenu par défaut.</b> La liste
    /// se posait auparavant sur sa première ligne, ce qui IMPOSAIT la machine A à chaque
    /// ouverture de la grille sans que personne ne l'ait demandé — un choix imposé
    /// court-circuite la recherche du bon rouleau (voir
    /// <c>PrintOrchestrator.ChoisirMachineEtRouleau</c>). Le 21×29,7 était donc refusé
    /// « le rouleau chargé dans la machine A fait 152 mm » pendant que le rouleau de 210
    /// tournait dans la machine B, à côté. Constaté sur les commandes 04-010, 04-014 et
    /// 04-019 du 04/08/2026.
    /// </summary>
    private async Task LoadMachinesAsync()
    {
        // Ce que l'opérateur avait imposé, s'il l'avait fait. Relevé AVANT de toucher à la
        // liste : reposer les lignes déclenche OnMachineChanged, qui l'écraserait.
        var imposeeAvant = App.Services.Printer.PreferredMinilabMachine;

        try
        {
            var etats = await App.Services.Minilab.SnapshotAsync();

            var machines = etats
                .Where(e => e.Status != De100PrinterStatus.Offline)
                .Select(e => new MachineChoice(e.MachineId, DecrireMachine(e)))
                .ToList();

            if (machines.Count == 0)
            {
                App.Services.Printer.PreferredMinilabMachine = null;
                MachineCombo.ItemsSource = new[]
                {
                    new MachineChoice(MachineChoice.Aucune, "aucune machine en ligne"),
                };
                MachineCombo.SelectedIndex = 0;
                MachineCombo.IsEnabled = false;
                return;
            }

            // retenues pour pouvoir réécrire la ligne « Automatique » quand la finition du
            // panier change, sans réinterroger le minilab
            _machinesEnLigne = etats.Where(e => e.Status != De100PrinterStatus.Offline).ToList();

            var choix = new List<MachineChoice>
            {
                new(MachineChoice.Aucune, LibelleAutomatique()),
            };
            choix.AddRange(machines);

            MachineCombo.ItemsSource = choix;
            MachineCombo.IsEnabled = true;

            // Le choix de l'opérateur SURVIT à la navigation. C'est un geste explicite, et
            // il le reste quand on revient sur l'écran : l'effacer obligeait à redésigner
            // la machine à chaque aller-retour, alors qu'on vient justement d'y monter un
            // rouleau. Une machine passée hors ligne entre-temps retombe sur
            // « Automatique », préférence comprise — imposer une machine absente ferait
            // refuser la commande en nommant une machine éteinte.
            var rang = imposeeAvant is null
                ? 0
                : choix.FindIndex(c => !c.Automatique && c.Id.ToString() == imposeeAvant);

            MachineCombo.SelectedIndex = rang >= 0 ? rang : 0;
            if (rang < 0) App.Services.Printer.PreferredMinilabMachine = null;
        }
        catch (Exception ex)
        {
            FileLog.Write("Liste des machines du minilab indisponible", ex);
            App.Services.Printer.PreferredMinilabMachine = null;
            MachineCombo.ItemsSource = new[]
            {
                new MachineChoice(MachineChoice.Aucune, "minilab injoignable"),
            };
            MachineCombo.SelectedIndex = 0;
            MachineCombo.IsEnabled = false;
        }
    }

    private static string DecrireMachine(De100PrinterInfo info)
    {
        if (info.Media is not { } media) return $"{info.MachineId} — papier inconnu";

        var restant = info.Formats.FirstOrDefault(f => !f.Format.IsVariable);
        var suffixe = restant is null ? "" : $", ~{restant.RemainingPrints} × {restant.Format.Name}";

        // « brillant » et non « Glossy » : c'est le mot que l'opérateur emploie avec le
        // client, et celui que porteront les avertissements du tirage
        return $"{info.MachineId} — {media.PaperWidthMm} mm {PrintOrchestrator.Dire(media.Surface)}{suffixe}";
    }

    /// <summary>
    /// Les machines en ligne au dernier chargement, gardées pour réécrire la ligne
    /// « Automatique » sans réinterroger le minilab.
    /// </summary>
    private IReadOnlyList<De100PrinterInfo> _machinesEnLigne = [];

    /// <summary>
    /// La finition que le panier réclame, ou null s'il n'en réclame aucune — ou s'il en
    /// mélange plusieurs.
    ///
    /// Le mélange rend null à dessein : la commande partira en DEUX enveloppes, sur deux
    /// machines (voir <c>OrderService</c>), et annoncer une seule machine mentirait.
    ///
    /// <b>Les photos cochées, et TOUTES les photos tant que rien n'est coché.</b> C'est le
    /// cas normal à l'ouverture d'une commande de borne : les cases restent volontairement
    /// décochées, cet écran étant celui où l'opérateur contrôle avant d'engager du papier.
    /// Ne regarder que la sélection laissait donc la barre sur « selon le rouleau » devant
    /// vingt-et-une vignettes toutes marquées « Lustré » — ce qui était précisément
    /// l'information qu'on venait chercher.
    /// </summary>
    private De100Surface? FinitionDuPanier()
    {
        var retenues = _photos.Where(p => p.Selected).ToList();
        if (retenues.Count == 0) retenues = _photos;

        var surfaces = retenues
            .Select(p => PrintOrchestrator.FinitionMinilab(p.Finish))
            .Where(s => s is not null)
            .Distinct()
            .ToList();

        return surfaces.Count == 1 ? surfaces[0] : null;
    }

    /// <summary>
    /// Ce que dit la ligne « Automatique » : non pas seulement qu'on laisse décider, mais
    /// CE QUI SERA DÉCIDÉ.
    ///
    /// <b>Pourquoi nommer la machine sans la choisir.</b> Une commande de borne en lustré
    /// part bien toute seule sur la machine lustrée, mais la barre affichait
    /// « Automatique — selon le rouleau » : l'opérateur n'avait aucun moyen de le vérifier
    /// avant d'engager le papier, et croyait le choix ignoré. Sélectionner la machine à sa
    /// place serait pire : un choix IMPOSÉ court-circuite la recherche du bon rouleau, et
    /// c'est exactement ce qui faisait refuser les 21×29,7 du 04/08/2026.
    /// </summary>
    private string LibelleAutomatique()
    {
        if (FinitionDuPanier() is not { } voulue) return "Automatique — selon le rouleau";

        var nom = PrintOrchestrator.Dire(voulue);

        var porteuse = _machinesEnLigne
            .FirstOrDefault(m => m.Media is { } media && media.Surface == voulue);

        return porteuse is null
            ? $"Automatique — aucun rouleau {nom} chargé"
            : $"Automatique — machine {porteuse.MachineId} ({nom})";
    }

    /// <summary>
    /// Réécrit la ligne « Automatique » quand la finition du panier a changé — l'opérateur
    /// qui bascule une photo de brillant à lustré doit voir la machine suivre.
    ///
    /// Ne touche à rien tant que le libellé ne change pas : reposer les lignes rejoue
    /// <see cref="OnMachineChanged"/>, et le faire à chaque décochage d'une vignette
    /// ferait clignoter la liste pour rien.
    /// </summary>
    private void RafraichirLAutomatique()
    {
        if (MachineCombo.ItemsSource is not List<MachineChoice> choix) return;
        if (choix.Count == 0 || !choix[0].Automatique) return;

        var libelle = LibelleAutomatique();
        if (choix[0].Label == libelle) return;

        // la sélection est repérée par la MACHINE et non par son rang : reposer les lignes
        // remet l'index à -1, et un rang mémorisé désignerait la mauvaise ligne si la liste
        // avait changé entre-temps
        var retenue = (MachineCombo.SelectedItem as MachineChoice)?.Id;

        var neuf = new List<MachineChoice> { new(MachineChoice.Aucune, libelle) };
        neuf.AddRange(choix.Skip(1));

        MachineCombo.ItemsSource = neuf;

        var rang = retenue is null ? 0 : neuf.FindIndex(c => c.Id == retenue.Value);
        MachineCombo.SelectedIndex = rang >= 0 ? rang : 0;
    }

    /// <summary>
    /// La machine imposée pour la session, ou null pour laisser le rouleau décider.
    ///
    /// « Automatique » remet bien la préférence à NULL : sans cela, revenir sur ce choix
    /// après avoir désigné une machine ne l'aurait pas relâchée.
    /// </summary>
    private void OnMachineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MachineCombo.SelectedItem is not MachineChoice choix) return;

        App.Services.Printer.PreferredMinilabMachine = choix.Automatique ? null : choix.Id.ToString();
    }

    private void UpdateSummary()
    {
        var selected = _photos.Where(p => p.Selected).ToList();
        CountText.Text = selected.Count == 0
            ? $"{_photos.Count} photos trouvées"
            : $"{selected.Count} sélectionnée{(selected.Count > 1 ? "s" : "")} sur {_photos.Count}";

        RafraichirLeBoutonProduit();
        if (EnTaillePersonnalisee)
        {
            ResumerLaPlanche(selected);
        }
        else
        {
            var total = selected.Sum(p => (p.Product?.Price ?? 0) * p.Quantity);
            TotalText.Text = selected.Count == 0 ? "" : $"{total:0.00} €";
            PrintButton.IsEnabled = selected.Count > 0;
        }

        ModifyButton.IsEnabled = selected.Count > 0;
        ModifyButton.Content = selected.Count > 1 ? $"Modifier ({selected.Count})" : "Modifier";

        // Une planche est là : le bouton la reprend, il n'en fabrique pas une seconde.
        // Il en refaisait une à chaque appui, et l'opérateur qui rappuyait par réflexe se
        // retrouvait avec deux, trois planches empilées en tête de grille, à décocher une à une.
        if (_planchesIndex.Count > 0)
        {
            IndexButton.IsEnabled = true;
            IndexButton.Content = _planchesIndex.Count > 1
                ? $"Retirer les planches index ({_planchesIndex.Count})"
                : "Retirer la planche index";
            IndexButton.ToolTip = "Retire la planche index de la grille — rappuyer ensuite en refera une";
            return;
        }

        // une planche d'une seule vignette n'aurait pas de sens : on indexe un lot.
        // Le bouton reste actif dès que le DOSSIER en porte deux, même sans rien de coché :
        // c'est justement le cas du premier passage, où le client veut voir avant de choisir.
        IndexButton.IsEnabled = _photos.Count > 1;
        IndexButton.Content = "Planche index";
        IndexButton.ToolTip = "Une planche avec les vignettes NUMÉROTÉES — de la sélection ou " +
                              "de tout le dossier, au choix. Le client coche, on tire ensuite.";
    }

    /// <summary>
    /// Le papier retenu pour la sélection en cours, ou null si rien n'est coché — ou si la
    /// taille demandée ne tient sur aucun papier du catalogue.
    /// </summary>
    private (CustomSheetPlan Plan, Product Papier)? PlancheRetenue(IReadOnlyList<PhotoItem> selection)
    {
        if (_taillePerso is not { } taille || selection.Count == 0) return null;

        var cases = selection.Sum(p => p.Quantity);
        if (cases < 1) return null;

        var plan = CustomSheetLayout.Choose(
            cases, taille.WidthMm, taille.HeightMm, CustomSizeView.PapiersDisponibles(),
            forcedPaperCode: PapierImpose);

        if (plan is null) return null;

        var papier = App.Services.Catalog.Find(plan.Paper.Code);
        return papier is null ? null : (plan, papier);
    }

    /// <summary>
    /// Ce que la sélection donnera : combien de planches, sur quel papier, et pour quel prix.
    ///
    /// C'est le seul endroit où l'opérateur voit le papier retenu avant d'imprimer. Le prix
    /// suit le PAPIER — une planche 13×18 coûte un tirage 13×18, quel que soit le nombre de
    /// photos qu'on y a casées.
    /// </summary>
    private void ResumerLaPlanche(IReadOnlyList<PhotoItem> selection)
    {
        if (selection.Count == 0)
        {
            TotalText.Text = "";
            PrintButton.IsEnabled = false;
            return;
        }

        var cases = selection.Sum(p => p.Quantity);

        if (PlancheRetenue(selection) is not { } retenue)
        {
            TotalText.Text = "";
            CountText.Text = PapierImpose is { } impose
                ? $"{_taillePerso!.Libelle} ne tient pas sur le {impose} : choisissez un autre papier"
                : $"{_taillePerso!.Libelle} : aucun papier du catalogue ne convient";
            PrintButton.IsEnabled = false;
            return;
        }

        var (plan, papier) = retenue;

        CountText.Text =
            $"{cases} photo{(cases > 1 ? "s" : "")} en {_taillePerso!.Libelle} → " +
            $"{plan.Sheets} planche{(plan.Sheets > 1 ? "s" : "")} {papier.Name} " +
            $"({plan.PerSheet} par planche)" +
            (PapierImpose is null ? "" : " · papier imposé");

        TotalText.Text = $"{plan.Paper.TotalPrice(plan.Sheets):0.00} €";
        PrintButton.IsEnabled = true;
    }

    /// <summary>
    /// Les planches d'index posées dans la grille, pour pouvoir les en retirer.
    ///
    /// C'est ce qui fait du bouton une bascule : sans mémoire de ce qu'il a produit, il ne
    /// savait que produire encore.
    /// </summary>
    private readonly List<PhotoItem> _planchesIndex = new();

    /// <summary>
    /// Fabrique la planche d'index de la sélection et la remet dans la planche-contact
    /// comme une photo ordinaire.
    ///
    /// C'est le geste du comptoir : le client arrive avec une pellicule, on lui tire une
    /// planche, il coche ce qu'il veut, on tire ensuite. La planche revient donc dans la
    /// grille avec son produit et sa quantité — elle se règle, se corrige et s'imprime
    /// comme n'importe quel tirage, sans qu'aucun autre écran ait à la connaître.
    ///
    /// Ce que DiLand rate et qui est corrigé ici : ses vignettes portent le nom du
    /// fichier, coupé à la largeur de la cellule — sur les commandes de la boutique, les
    /// vingt-sept portaient toutes « kodakREAPHOT », donc le client ne pouvait désigner
    /// aucune photo. Et sa grille étant fixe, vingt-sept photos sortaient sur deux 10×15
    /// dont le second à trois vignettes.
    /// </summary>
    /// <summary>
    /// Envoyer les photos au client par Dropbox plutôt que de les tirer.
    ///
    /// Le CHOIX du lot — la sélection ou le dossier entier — se fait à l'écran suivant et
    /// non ici : c'est là que les deux comptes s'affichent côte à côte, et « tout envoyer »
    /// sur un dossier de mariage ne se décide pas sans voir le nombre. Le bouton reste donc
    /// actif même sans photo cochée.
    /// </summary>
    private void OnDropbox(object sender, RoutedEventArgs e)
    {
        var choisies = _photos.Where(p => p.Selected).Select(p => p.Path).ToList();

        Navigator.Go(
            new DropboxSendView(choisies, _rootPath, _avecSousDossiers),
            "Envoyer les photos au client");
    }

    private void OnIndexSheet(object sender, RoutedEventArgs e)
    {
        // deuxième appui : on défait. Voir UpdateSummary.
        if (_planchesIndex.Count > 0)
        {
            RetirerLesPlanchesIndex();
            return;
        }

        var choisies = _photos.Where(p => p.Selected).ToList();
        var toutes = _photos.ToList();

        // Deux planches d'index, et ce ne sont pas les mêmes gestes.
        //
        // La SÉLECTION sert quand le client a déjà écarté ce qu'il ne veut pas — on lui
        // tire l'index de ce qui reste. LE DOSSIER sert au premier passage, quand il
        // arrive avec une carte et veut voir ce qu'il a avant de choisir : lui demander de
        // tout cocher d'abord, sur quatre-vingts photos, n'a pas de sens.
        //
        // Le compte est écrit sur chaque entrée : c'est la seule chose qui les distingue
        // quand tout est coché, et l'opérateur doit pouvoir le voir avant de cliquer.
        var menu = new ContextMenu();

        var surSelection = new MenuItem
        {
            Header = $"La sélection — {choisies.Count} photo{(choisies.Count > 1 ? "s" : "")}",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            IsEnabled = choisies.Count > 1,
        };
        surSelection.Click += (_, _) => FabriquerLaPlanche(choisies);
        menu.Items.Add(surSelection);

        var surDossier = new MenuItem
        {
            Header = $"Tout le dossier — {toutes.Count} photo{(toutes.Count > 1 ? "s" : "")}",
            FontSize = 18,
            IsEnabled = toutes.Count > 1,
        };
        surDossier.Click += (_, _) => FabriquerLaPlanche(toutes);
        menu.Items.Add(surDossier);

        menu.PlacementTarget = IndexButton;
        menu.IsOpen = true;
    }

    /// <summary>
    /// Fabrique la planche d'index d'un lot de photos et la pose dans la grille.
    ///
    /// Le lot vient de la sélection ou du dossier entier (voir <see cref="OnIndexSheet"/>) :
    /// à partir d'ici, rien ne les distingue.
    /// </summary>
    private async void FabriquerLaPlanche(List<PhotoItem> choisies)
    {
        if (choisies.Count < 2) return;

        if (DefaultProduct is not { } produit)
        {
            MessageBox.Show("Choisissez d'abord le produit : c'est lui qui donne le format de la planche.",
                "Planche index", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var services = App.Services;
        var chemins = choisies.Select(p => p.Path).ToList();

        // la grille a déjà lu la définition de chaque photo pour l'afficher sur la vignette :
        // la planche s'en sert au lieu de rouvrir les fichiers. 0 = pas encore connue, elle
        // sera lue. Voir IndexSheet.Request.Aspects.
        var rapports = choisies.Select(p => p.SourceSizeKnown ? p.SourceAspect : 0).ToList();

        var largeur = MmPx.ToPixels(produit.WidthMm, produit.Dpi);
        var hauteur = MmPx.ToPixels(produit.HeightMm, produit.Dpi);

        // la planche suit l'orientation du lot : une pellicule de paysages sur un tirage
        // debout perdrait la moitié de la place
        if (largeur < hauteur) (largeur, hauteur) = (hauteur, largeur);

        var dossier = Path.Combine(services.DataRoot, "cache", "index",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        IndexButton.IsEnabled = false;
        Mouse.OverrideCursor = CurseurStudio.Attente;
        try
        {
            var resultat = await Task.Run(() => IndexSheet.Render(
                new IndexSheet.Request(chemins, largeur, hauteur, produit.Dpi,
                    "Index", DateTime.Now, rapports),
                services.Thumbnails, dossier));

            for (var i = 0; i < resultat.Files.Count; i++)
            {
                var planche = new PhotoItem(resultat.Files[i], OnCartChanged)
                {
                    Product = produit,
                    Quantity = 1,
                    Selected = true,
                };

                // vignette rendue avec la planche : plus de relecture du fichier écrit
                planche.SetSourceThumbnail(ToBitmap(resultat.Thumbnails[i]));

                _photos.Insert(0, planche);
                _planchesIndex.Add(planche);
            }

            // insertion en tête : la grille ne suit pas toute seule un ItemsSource qu'elle
            // ne surveille pas
            PhotosGrid.ItemsSource = null;
            PhotosGrid.ItemsSource = _photos;

            FileLog.Write($"Planche index : {chemins.Count} photos, {resultat.Files.Count} planche(s) " +
                          $"de {resultat.PerSheet} ({resultat.Columns}×{resultat.Rows}) au format {produit.Name}");

            PhotosGrid.BringIntoView();
        }
        catch (Exception ex)
        {
            FileLog.Write("Planche index impossible", ex);
            MessageBox.Show($"Planche impossible : {ex.Message}", "Planche index",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Mouse.OverrideCursor = null;
            UpdateSummary();
        }
    }

    /// <summary>
    /// Retire les planches d'index de la grille. Les fichiers rendus restent dans le cache :
    /// ils ne coûtent rien et une planche déjà tirée peut avoir à être retirée du carton.
    /// </summary>
    private void RetirerLesPlanchesIndex()
    {
        foreach (var planche in _planchesIndex)
        {
            _photos.Remove(planche);
            if (ReferenceEquals(_photoCourante, planche)) _photoCourante = null;
        }

        _planchesIndex.Clear();

        PhotosGrid.ItemsSource = null;
        PhotosGrid.ItemsSource = _photos;
        UpdateSummary();
    }

    /// <summary>
    /// Ouvre l'écran de travail sur la sélection : recadrage et corrections, la planche
    /// restant sous les yeux. L'impression part de là, une fois tout réglé.
    /// </summary>
    /// <summary>
    /// Reproduit une photo dans la commande, avec tous ses réglages, pour qu'on puisse la
    /// tirer une seconde fois dans un AUTRE format.
    ///
    /// <b>C'est la GRILLE qui duplique, et pas l'écran « Modifier »</b> : elle seule tient
    /// la liste que l'impression parcourt. Un doublon ajouté à la seule liste de « Modifier »
    /// se serait affiché, se serait réglé, et ne serait jamais sorti.
    ///
    /// Le doublon est posé JUSTE APRÈS son original : sur soixante photos, le retrouver en
    /// fin de planche demanderait de faire défiler toute la grille.
    ///
    /// Il est coché d'office — on ne duplique une photo que pour la tirer — et garde le
    /// même fichier source : rien n'est recopié sur le disque, seule la ligne de commande
    /// est doublée.
    ///
    /// <b>La vignette et la définition sont RECOPIÉES depuis l'original</b>, et c'est ce
    /// qui manquait. Elles n'arrivent d'ordinaire qu'à la lecture du dossier, que le
    /// doublon a manquée : sans vignette il s'affichait comme une case vide dans la
    /// planche comme dans la bande de « Modifier » — le bouton passait pour mort — et
    /// sans définition <see cref="PhotoItem.Cadre"/> ne pouvait pas naître, si bien que
    /// le doublon partait au tirage en pleine image, sans le cadrage de son original.
    /// </summary>
    /// <returns>Le doublon, à insérer dans la bande de l'écran appelant.</returns>
    private PhotoItem DupliquerPhoto(PhotoItem origine)
    {
        var copie = new PhotoItem(origine.Path, OnCartChanged)
        {
            Product = origine.Product,
            Finish = origine.Finish,
            RotationQuarterTurns = origine.RotationQuarterTurns,
            FineRotationDegrees = origine.FineRotationDegrees,
            FitOverride = origine.FitOverride,
            CutBorder = origine.CutBorder,
            // une COPIE des corrections, jamais la même instance : corriger le doublon
            // retoucherait sinon l'original du même coup
            Adjustments = origine.Adjustments.Clone(),
            Quantity = origine.Quantity,
        };

        // le cadrage en DERNIER, comme partout : les mutateurs ci-dessus le remettent à zéro
        copie.PoserLeCadrageDOrigine(origine.Crop);
        copie.Selected = true;

        // La définition AVANT la vignette : c'est elle qui autorise le cadre à naître, et
        // la vignette se dessine avec le cadre par-dessus.
        var (largeur, hauteur) = origine.SourcePixels;
        if (largeur > 0 && hauteur > 0) copie.SetSourceSize(largeur, hauteur);
        if (origine.SourceThumbnail is { } vignette) copie.SetSourceThumbnail(vignette);

        var rang = _photos.IndexOf(origine);
        if (rang < 0) _photos.Add(copie);
        else _photos.Insert(rang + 1, copie);

        // _photos est une List : sans cette remise en place, le doublon n'apparaîtrait
        // nulle part (voir les notes d'architecture)
        PhotosGrid.ItemsSource = null;
        PhotosGrid.ItemsSource = _photos;

        copie.RefreshThumbnail();
        UpdateSummary();

        FileLog.Write($"Photo « {origine.Name} » dupliquée dans la commande");
        return copie;
    }

    private void OnModify(object sender, RoutedEventArgs e)
    {
        var choisies = _photos.Where(p => p.Selected).ToList();
        if (choisies.Count == 0) return;

        // un produit est indispensable pour connaître la forme du cadre
        foreach (var photo in choisies) photo.Product ??= DefaultProduct;

        Navigator.Go(
            new EditSelectionView(choisies, () => OnPrint(this, new RoutedEventArgs()),
                // depuis « Modifier » aussi : c'est là que l'opérateur voit la photo en
                // grand, donc là qu'on décide souvent de changer de format
                personnalise: EnTaillePersonnalisee ? null : DemanderUneTaillePersonnalisee,
                // et là aussi qu'on s'interrompt : c'est l'écran où l'on passe le plus de
                // temps, donc celui où un client peut demander à revenir plus tard.
                // C'est la GRILLE qui enregistre — elle seule connaît les photos non
                // cochées, et les perdre reviendrait à ne mettre de côté qu'une moitié.
                mettreEnAttente: MettreEnAttente,
                // et c'est encore elle qui duplique : elle seule tient la liste que
                // l'impression parcourt
                dupliquer: DupliquerPhoto),
            $"Modifier — {choisies.Count} photo(s)");
    }

    // ----- bandeau : s'applique à toutes les photos cochées -----

    private void OnDefaultProductChanged(object sender, SelectionChangedEventArgs e)
    {
        // Une entrée d'ACTION : on remet la liste où elle était, PUIS on ouvre l'écran.
        // Remettre d'abord évite que « Personnalisé… » reste affiché pendant que l'autre
        // écran est ouvert, et que l'opérateur qui revient en arrière croie son format
        // choisi. La réaffectation rentre à nouveau ici, avec un vrai produit cette fois :
        // il n'y a pas de boucle.
        switch (ProductCombo.SelectedItem)
        {
            case ChoixTailleLibre:
                ProductCombo.SelectedIndex = _indexProduitPrecedent;
                DemanderUneTaillePersonnalisee();
                return;

            case ChoixAgrandissementLibre:
                ProductCombo.SelectedIndex = _indexProduitPrecedent;
                DemanderUnAgrandissement(AppliquerLAgrandissement);
                return;
        }

        _indexProduitPrecedent = ProductCombo.SelectedIndex;

        AfficherLaSortie();

        // en taille personnalisée, cette liste ne désigne plus un produit mais le PAPIER de
        // sortie : les photos gardent leur format, seul le récapitulatif change
        if (EnTaillePersonnalisee)
        {
            UpdateSummary();
            return;
        }

        // <b>La liste ne touche plus aux photos déjà cochées.</b> Une commande de borne
        // arrive avec un format PAR PHOTO — le client en a choisi plusieurs — et le seul
        // fait de dérouler la liste les ramenait toutes au même : le multi-format ne
        // survivait pas à la sélection. Le report est passé sur un bouton, où il se voit et
        // ne se déclenche pas tout seul (décision de l'exploitant, 05/08/2026).
        UpdateSummary();
    }

    /// <summary>
    /// Donne le format de la liste à toutes les photos cochées — le geste explicite qui a
    /// remplacé le report automatique.
    ///
    /// Il s'applique aux photos COCHÉES et non à toutes : c'est ainsi qu'on passe cinq
    /// photos d'une commande en 15×20 en laissant les dix autres en 10×15.
    /// </summary>
    private void OnAppliquerLeProduit(object sender, RoutedEventArgs e)
    {
        if (DefaultProduct is null || EnTaillePersonnalisee) return;

        var cochees = _photos.Where(p => p.Selected).ToList();
        if (cochees.Count == 0) return;

        foreach (var photo in cochees) photo.Product = DefaultProduct;

        FileLog.Write($"Format « {DefaultProduct.Name} » appliqué à {cochees.Count} photo(s) cochée(s)");
        UpdateSummary();
    }

    /// <summary>
    /// Pose l'agrandissement qui vient d'être saisi sur les photos COCHÉES — ou sur toutes
    /// si rien n'est coché, parce qu'on ne demande pas un A2 pour ne l'appliquer à rien.
    ///
    /// Contrairement à la taille libre, il n'y a rien à enseigner à l'écran : un
    /// agrandissement est un vrai produit du catalogue, créé à la validation de l'écran de
    /// saisie. Il faut en revanche RELIRE la liste, que ce nouveau produit vient d'allonger.
    /// </summary>
    private void AppliquerLAgrandissement(Product produit)
    {
        if (produit is null || EnTaillePersonnalisee) return;

        var cochees = _photos.Where(p => p.Selected).ToList();
        var cibles = cochees.Count > 0 ? cochees : _photos.ToList();

        foreach (var photo in cibles) photo.Product = produit;

        FileLog.Write($"Agrandissement « {produit.Name} » sur {cibles.Count} photo(s)");

        RelireLaListeDesProduits(produit.Code);
        UpdateSummary();
    }

    /// <summary>
    /// Refabrique la liste des formats et s'arrête sur celui-ci.
    ///
    /// Appelée après qu'un agrandissement a été ajouté au catalogue : sans elle, la liste
    /// garde l'inventaire d'avant et le format qu'on vient de créer n'y figure pas — alors
    /// même que les photos le portent déjà.
    /// </summary>
    private void RelireLaListeDesProduits(string codeARetenir)
    {
        var choix = App.Services.Catalog.Enabled
            .Select(p => (object)new ProductChoice(p))
            .ToList();

        choix.Add(new ChoixTailleLibre());
        choix.Add(new ChoixAgrandissementLibre());

        ProductCombo.ItemsSource = choix;

        var index = choix.FindIndex(c => c is ProductChoice pc
                                         && pc.Product.Code.Equals(codeARetenir, StringComparison.OrdinalIgnoreCase));

        ProductCombo.SelectedIndex = index >= 0 ? index : 0;
        _indexProduitPrecedent = ProductCombo.SelectedIndex;
    }

    /// <summary>
    /// Le bouton de report ne s'allume que s'il a de quoi travailler : sans photo cochée,
    /// il ne ferait rien et laisserait croire que le format n'a pas été pris.
    /// </summary>
    private void RafraichirLeBoutonProduit()
    {
        if (AppliquerProduitButton is null) return;

        AppliquerProduitButton.IsEnabled = !EnTaillePersonnalisee && _photos.Any(p => p.Selected);
    }

    /// <summary>
    /// Dit SUR QUELLE MACHINE le tirage va sortir, en toutes lettres et en couleur.
    ///
    /// Le catalogue porte deux produits nommés « 10x15 » — l'un au minilab, l'autre à la
    /// DS620 — et rien ici ne disait lequel était retenu : la commande 04-024 du
    /// 04/08/2026 est partie sur la mauvaise machine, onze tirages, et personne ne pouvait
    /// s'en apercevoir avant que le papier ne sorte.
    ///
    /// Le choix de machine du minilab disparaît quand le produit n'y va pas : il ne
    /// commandait rien et laissait croire le contraire.
    /// </summary>
    private void AfficherLaSortie()
    {
        // Une planche en taille libre sort toujours du minilab, quel que soit le papier
        // retenu dans la liste : la liste y désigne le PAPIER, pas la machine.
        if (EnTaillePersonnalisee)
        {
            SortieText.Text = "Sortie : minilab DE100";
            SortieBadge.Background = (Brush)Application.Current.Resources["AccentDarkBrush"];
            MachineLabel.Visibility = Visibility.Visible;
            MachineCombo.Visibility = Visibility.Visible;
            return;
        }

        // les mêmes teintes que les tuiles de format et le bandeau des machines : le
        // minilab en bleu, la sublimation en violet, l'Epson en vert
        var (texte, fond, minilab) = DefaultProduct?.Output switch
        {
            ProductOutput.FujiMinilab =>
                ("Sortie : minilab DE100", (Brush)Application.Current.Resources["AccentDarkBrush"], true),
            ProductOutput.ManualFile =>
                ("Sortie : fichier pour l'Epson", Pinceau(0x2E, 0x6B, 0x33), false),
            null =>
                ("Sortie : à choisir", Pinceau(0x4A, 0x4A, 0x4A), false),
            _ =>
                ($"Sortie : {DefaultProduct!.PrinterName}", Pinceau(0x6A, 0x4C, 0x93), false),
        };

        SortieText.Text = texte;
        SortieBadge.Background = fond;

        var visible = minilab ? Visibility.Visible : Visibility.Collapsed;
        MachineLabel.Visibility = visible;
        MachineCombo.Visibility = visible;

        static Brush Pinceau(byte r, byte v, byte b) => new SolidColorBrush(Color.FromRgb(r, v, b));
    }

    private void OnQuantityMinus(object sender, RoutedEventArgs e) => SetQuantity(_quantity - 1);
    private void OnQuantityPlus(object sender, RoutedEventArgs e) => SetQuantity(_quantity + 1);

    private void SetQuantity(int value)
    {
        _quantity = Math.Clamp(value, 1, 99);
        QuantityText.Text = _quantity.ToString();
        foreach (var photo in _photos.Where(p => p.Selected))
            photo.Quantity = _quantity;
        UpdateSummary();
    }

    // ----- bandeau de la vignette : produit et quantité de cette photo -----

    /// <summary>
    /// Un cran de moins — et à un exemplaire, la photo SORT de la commande.
    ///
    /// Le bouton s'arrêtait à 1 sans rien faire de plus : pour retirer une photo, il fallait
    /// deviner qu'il fallait décocher la case. Descendre la quantité jusqu'à zéro est le
    /// geste naturel, et c'est déjà celui de la touche 0 (voir
    /// <see cref="SetQuantityOnTargets"/>). La quantité est laissée à 1 : si l'opérateur la
    /// recoche, elle revient à un exemplaire et non à zéro.
    /// </summary>
    private void OnTileMinus(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not PhotoItem photo) return;

        if (photo.Quantity <= 1)
        {
            photo.Quantity = 1;
            photo.Selected = false;
            return;
        }

        photo.Quantity = Math.Clamp(photo.Quantity - 1, 1, 99);
    }

    private void OnTilePlus(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is PhotoItem photo)
            photo.Quantity = Math.Clamp(photo.Quantity + 1, 1, 99);
    }

    /// <summary>
    /// Dernière photo que l'opérateur a cochée. Elle sert de cible au bouton « Recadrer »
    /// de la barre du bas : cocher une photo la désigne, la décocher ne la désigne pas.
    /// </summary>
    private PhotoItem? _photoCourante;

    // — raccourcis clavier, repris de DiLand —

    /// <summary>
    /// Les photos sur lesquelles agit un raccourci : celles qui sont cochées, ou à défaut
    /// la dernière touchée. C'est la règle de DiLand, qui applique ses commandes à
    /// <c>SelectedItems</c> — on règle une planche entière d'un geste.
    /// </summary>
    private List<PhotoItem> Cibles()
    {
        var choisies = _photos.Where(p => p.Selected).ToList();
        if (choisies.Count > 0) return choisies;

        return _photoCourante is null ? [] : [_photoCourante];
    }

    /// <summary>Fixe la quantité des photos visées, en les ajoutant à la commande si besoin.</summary>
    private void SetQuantityOnTargets(int quantite)
    {
        foreach (var photo in Cibles())
        {
            if (quantite <= 0)
            {
                photo.Selected = false;
                continue;
            }

            if (!photo.Selected)
            {
                photo.Product ??= DefaultProduct;
                photo.Selected = true;
            }

            photo.Quantity = quantite;
        }
    }

    /// <summary>
    /// Un cran de plus ou de moins au clavier. Comme le bouton de la vignette, descendre
    /// sous un exemplaire RETIRE la photo — les deux gestes doivent faire la même chose,
    /// sans quoi la touche et le bouton ne se comporteraient pas pareil.
    /// </summary>
    private void ChangeQuantityOnTargets(int delta)
    {
        foreach (var photo in Cibles())
        {
            if (!photo.Selected) continue;

            if (delta < 0 && photo.Quantity + delta < 1)
            {
                photo.Quantity = 1;
                photo.Selected = false;
                continue;
            }

            photo.Quantity = Math.Clamp(photo.Quantity + delta, 1, 99);
        }
    }

    /// <summary>
    /// Corrections au clavier, sur les photos visées.
    ///
    /// DiLand règle trois canaux indépendants (R, V, B) ; notre modèle d'image travaille
    /// en température et teinte, qui couvrent le même espace autrement. La correspondance
    /// est donc approchée, et assumée : les touches font ce que l'opérateur attend — plus
    /// rouge, plus vert, plus bleu — sans ajouter un axe au pipeline pour trois raccourcis.
    /// </summary>
    private void Correct(Action<ImageAdjustments> reglage)
    {
        foreach (var photo in Cibles())
        {
            reglage(photo.Adjustments);
            photo.RefreshThumbnail();
        }
    }

    private const double PasLumiere = 0.15;  // en diaphragmes, comme l'exposition
    private const double PasCouleur = 8;     // sur l'échelle −100..100

    private void AttachShortcuts()
    {
        Focusable = true;

        var map = new KeyMap()
            .OnCtrl(Key.A, SelectAll)
            .On(Key.C, () => OnCropCurrent(this, new RoutedEventArgs()))
            .On([Key.Add, Key.OemPlus], () => ChangeQuantityOnTargets(1))
            .On([Key.Subtract, Key.OemMinus], () => ChangeQuantityOnTargets(-1))
            .On([Key.D0, Key.NumPad0], () => SetQuantityOnTargets(0))
            .On(Key.F, () => Correct(r => r.Exposure += PasLumiere))
            .On(Key.V, () => Correct(r => r.Exposure -= PasLumiere))
            .On(Key.G, () => Correct(r => r.Temperature = Borne(r.Temperature + PasCouleur)))
            .On(Key.B, () => Correct(r => r.Temperature = Borne(r.Temperature - PasCouleur)))
            .On(Key.H, () => Correct(r => r.Tint = Borne(r.Tint - PasCouleur)))
            .On(Key.N, () => Correct(r => r.Tint = Borne(r.Tint + PasCouleur)))
            .On(Key.J, () => Correct(r => r.Temperature = Borne(r.Temperature - PasCouleur)))
            .On(Key.M, () => Correct(r => r.Temperature = Borne(r.Temperature + PasCouleur)))
            .OnCtrl(Key.W, () => Correct(r => r.Grayscale = !r.Grayscale))
            .OnCtrl(Key.O, () => Correct(r =>
            {
                var neutre = new ImageAdjustments();
                foreach (var p in typeof(ImageAdjustments).GetProperties().Where(p => p.CanWrite))
                    p.SetValue(r, p.GetValue(neutre));
            }))
            .OnCtrl(Key.Left, () => Rotate(-1))
            .OnCtrl(Key.Right, () => Rotate(1));

        // 1 à 9 : la quantité directement, comme chez DiLand
        for (var chiffre = 1; chiffre <= 9; chiffre++)
        {
            var valeur = chiffre;
            map.On([Key.D0 + chiffre, Key.NumPad0 + chiffre], () => SetQuantityOnTargets(valeur));
        }

        map.Attach(this);
    }

    private static double Borne(double valeur) => Math.Clamp(valeur, -100, 100);

    private void Rotate(int direction)
    {
        foreach (var photo in Cibles())
        {
            photo.RotationQuarterTurns = (photo.RotationQuarterTurns + direction + 4) % 4;
            photo.RefreshThumbnail();
        }
    }

    private void OnCropCurrent(object sender, RoutedEventArgs e)
    {
        if (_photoCourante is { } photo && photo.Selected)
            EditCrop(photo, onClosed: null);
    }

    /// <summary>
    /// Passe en revue toutes les photos cochées, l'une après l'autre : c'est le
    /// « modifier tout » de DiLand. Sur une commande de vingt tirages, régler chaque
    /// cadrage en repassant par la grille serait interminable.
    /// </summary>
    /// <summary>
    /// Applique un même cadrage à toutes les photos cochées, en une fois.
    ///
    /// C'est le « modifier tout » de DiLand. On ne défile PLUS les photos une par une :
    /// le cadre de chacune se lit sur sa vignette, donc l'opérateur voit tout de suite
    /// celles qui demandent une reprise, et n'ouvre que celles-là.
    /// </summary>
    private void OnCropAll(object sender, RoutedEventArgs e)
    {
        var aRegler = _photos.Where(p => p.Selected && p.Product is not null).ToList();
        if (aRegler.Count == 0) return;

        // on règle sur la photo courante, ou à défaut la première cochée
        var modele = _photoCourante is { Product: not null } courante && courante.Selected
            ? courante
            : aRegler[0];

        var produit = modele.Product!;
        var depart = new CropEditorView.State(
            modele.Crop, modele.RotationQuarterTurns, modele.FitOverride ?? produit.DefaultFit);

        Navigator.Go(new CropEditorView(modele.Path, produit, depart, resultat =>
        {
            foreach (var photo in aRegler)
            {
                // le quart de tour et l'oubli du cadre d'abord : tous deux font repartir
                // le cadrage du centre, et écraseraient celui que l'éditeur vient de rendre
                photo.RotationQuarterTurns = resultat.RotationQuarterTurns;
                photo.OublierCadre();
                photo.Crop = resultat.Crop;
                photo.FitOverride = resultat.Fit == produit.DefaultFit ? null : resultat.Fit;
                photo.RefreshThumbnail();
            }
        }), $"Recadrage appliqué à {aRegler.Count} photo(s)");
    }

    /// <summary>
    /// Corrections appliquées à toutes les photos cochées d'un coup — le geste de DiLand,
    /// avec des réglages qui vont plus loin que la luminosité et le contraste.
    ///
    /// Les réglages de départ sont ceux de la première photo cochée : rouvrir l'écran
    /// après une correction doit montrer où on en était, pas repartir de zéro.
    /// </summary>
    private void OnAdjust(object sender, RoutedEventArgs e)
    {
        var aCorriger = _photos.Where(p => p.Selected).ToList();
        if (aCorriger.Count == 0) return;

        var depart = (_photoCourante is { Selected: true } courante ? courante : aCorriger[0]).Adjustments;

        Navigator.Go(new AdjustView(
                aCorriger.Select(p => p.Path).ToList(),
                depart,
                reglages =>
                {
                    foreach (var photo in aCorriger)
                    {
                        // un exemplaire par photo : un objet partagé ferait qu'un réglage
                        // ultérieur sur l'une déborderait sur toutes les autres
                        photo.Adjustments = reglages.Clone();
                        photo.RefreshThumbnail();
                    }
                }),
            aCorriger.Count > 1 ? $"Corrections — {aCorriger.Count} photos" : "Corrections");
    }

    private void EditCrop(PhotoItem photo, Action? onClosed, string titre = "Recadrage")
    {
        if (photo.Product is not { } product) return;

        var initial = new CropEditorView.State(
            photo.Crop, photo.RotationQuarterTurns, photo.FitOverride ?? product.DefaultFit);

        Navigator.Go(new CropEditorView(photo.Path, product, initial, result =>
        {
            // même ordre qu'au lot : ce qui remet le cadrage au centre passe en premier
            photo.RotationQuarterTurns = result.RotationQuarterTurns;
            photo.OublierCadre();
            photo.Crop = result.Crop;
            photo.FitOverride = result.Fit == product.DefaultFit ? null : result.Fit;
            photo.RefreshThumbnail();
            onClosed?.Invoke();
        }), titre);
    }

    private void OnPickProduct(object sender, RoutedEventArgs e)
    {
        // en taille personnalisée, toute la sélection part au même format : changer le
        // produit d'une seule photo ferait une planche aux cases inégales
        if (EnTaillePersonnalisee) return;

        if (sender is not Button button || button.Tag is not PhotoItem photo) return;

        ProductMenu.Ouvrir(button, photo.Product, photo.Finish, (produit, finition) =>
        {
            photo.Product = produit;
            photo.Finish = finition;
        },
        personnalise: DemanderUneTaillePersonnalisee,
        agrandissement: () => DemanderUnAgrandissement(produit =>
        {
            photo.Product = produit;
            photo.Finish = null;   // un agrandissement engendré ne déclare aucune finition
        }));
    }

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        var selected = _photos.Where(p => p.Selected && p.Product is not null).ToList();
        if (selected.Count == 0) return;

        var services = App.Services;

        // taille personnalisée : le produit fantôme cède la place au PAPIER retenu, et
        // chaque photo devient une case de planche
        CustomSheetSpec? planche = null;
        var produitDeLaLigne = (Product?)null;
        if (EnTaillePersonnalisee)
        {
            if (PlancheRetenue(selected) is not { } retenue)
            {
                MessageBox.Show(
                    $"Une photo de {_taillePerso!.Libelle} ne tient sur aucun papier du catalogue. " +
                    "Rien n'a été commandé.",
                    "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            produitDeLaLigne = retenue.Papier;
            planche = new CustomSheetSpec(
                _taillePerso!.WidthMm, _taillePerso.HeightMm, retenue.Plan.Sheets,
                _taillePerso.BorderMm);
        }

        var items = selected
            .Select(p =>
            {
                var produit = produitDeLaLigne ?? p.Product!;
                return new DraftItem(p.Path, produit, p.Quantity, p.Crop,
                    p.RotationQuarterTurns, p.FineRotationDegrees, p.FitOverride, p.Adjustments, null,
                    p.Finish, p.CutBorder, planche,
                    MontageSheetCode: FeuilleDeMontagePour(produit));
            })
            .ToList();

        PrintButton.IsEnabled = false;
        Mouse.OverrideCursor = CurseurStudio.Attente;
        try
        {
            // La commande est créée tout de suite — c'est court : un numéro, les
            // enveloppes, et la copie des originaux. C'est le RENDU et l'envoi aux
            // machines qui prennent des minutes, et eux partent en tâche de fond.
            var order = await Task.Run(() =>
            {
                var created = services.Orders.CreateOrder(
                    _commandeBorne is null ? "Operateur" : DiLandImporter.SourceName, items);

                // L'adresse prise au comptoir voyage AVEC la commande, et non dans l'écran :
                // le message part quand la machine a fini, bien après que cet écran a été
                // quitté, et une commande enregistrée survit à un redémarrage.
                if (_adresseAPrevenir is { Length: > 0 } adresse)
                {
                    created.CustomerEmail = adresse;
                    services.Store.Save(created);
                }

                // le lien est noté AVANT d'imprimer : si Studio est coupé en cours de
                // tirage, la commande de borne reste rattachée à sa commande Studio et
                // se referme toute seule au prochain passage dans la liste
                if (_commandeBorne is { } borne)
                    services.DiLandImport.Journal.AttachStudioOrder(borne, created.Id);

                // La commande est passée en caisse : ce qui attendait en son nom n'a plus
                // d'objet. Le laisser ferait proposer « Reprendre » sur l'accueil pour une
                // commande déjà tirée, et on la tirerait deux fois.
                services.CommandesEnAttente.Effacer(_attenteId);

                return created;
            });

            // Les agrandissements ne partent pas tout seuls : ils se tirent à la main sur
            // l'Epson. On rend donc les fichiers TOUT DE SUITE — c'est ce rendu qui pose
            // le format et applique les corrections — puis on ouvre la boîte
            // d'impression dessus, sans passer par la file d'attente. L'opérateur qui
            // vient de recadrer et corriger enchaîne sur le tirage, au lieu de repartir
            // par l'accueil pour retrouver son travail.
            if (selected.All(p => p.Product!.Output == ProductOutput.ManualFile))
            {
                await Task.Run(() =>
                {
                    foreach (var envelope in order.Envelopes)
                        services.Printer.PrintEnvelope(order, envelope);
                });

                Mouse.OverrideCursor = null;
                OuvrirLaBoiteGrandFormat(order);
                return;
            }

            Mouse.OverrideCursor = null;

            // On rend la main IMMÉDIATEMENT : le poste doit rester libre pour le client
            // suivant pendant que les tirages partent. Plus de boîte de dialogue non
            // plus — l'avancement se lit dans le bandeau du haut.
            var oid = _commandeBorne;
            services.Impressions.Lancer(order,
                imprimer: (avancement, arret) =>
                {
                    foreach (var envelope in order.Envelopes)
                        services.Printer.PrintEnvelope(order, envelope,
                            progression: avancement, ct: arret);
                },
                apresSucces: () =>
                {
                    // seul un tirage réellement sorti ferme la commande de borne : une
                    // enveloppe en attente d'impression manuelle (Epson) ne compte pas
                    if (oid is { } borne &&
                        order.Envelopes.All(e => e.Status == EnvelopeStatus.Printed))
                        services.DiLandImport.MarkPrinted(borne, order.Id);
                });

            AccueilStudio.Rentrer();
        }
        catch (Exception ex)
        {
            // seule la création de la commande peut encore échouer ici ; l'impression,
            // elle, se plaint dans le bandeau
            Mouse.OverrideCursor = null;
            FileLog.Write("Échec de la création de la commande (grille photos)", ex);
            MessageBox.Show($"Commande impossible à créer : {ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            PrintButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Enchaîne la boîte d'impression grand format sur les tirages qu'on vient de rendre.
    ///
    /// La boîte ne montre qu'un fichier à la fois — c'est ce qu'on veut, chaque
    /// agrandissement ayant son papier et sa mise en page. On les fait donc défiler l'un
    /// après l'autre, puis on demande une seule fois si tout est sorti.
    ///
    /// La boîte prévient de la même façon qu'on ait imprimé ou renoncé : on passe donc au
    /// suivant dans les deux cas, et c'est la confirmation finale — non répondue, ou
    /// « non » — qui laisse l'enveloppe dans « Agrandissements ». Rien ne se perd.
    /// </summary>
    private void OuvrirLaBoiteGrandFormat(Order order)
    {
        var services = App.Services;

        var enveloppes = order.Envelopes
            .Select(e => (Enveloppe: e, Fichiers: services.Printer.ManualPrintFiles(order, e)))
            .Where(x => x.Fichiers.Count > 0)
            .ToList();

        var fichiers = enveloppes.SelectMany(x => x.Fichiers).ToList();

        if (fichiers.Count == 0)
        {
            MessageBox.Show(
                "Les fichiers d'agrandissement n'ont pas été trouvés. La commande reste dans " +
                "« Agrandissements », d'où elle peut être tirée.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            AccueilStudio.Rentrer();
            return;
        }

        void Suivant(int rang)
        {
            if (rang >= fichiers.Count)
            {
                Terminer();
                return;
            }

            var titre = fichiers.Count == 1
                ? $"Commande {order.DisplayNumber}"
                : $"Commande {order.DisplayNumber} — tirage {rang + 1}/{fichiers.Count}";

            Navigator.Go(
                new LargeFormatPrintView(fichiers[rang], services.CatalogDir, titre,
                    onDone: () =>
                    {
                        Navigator.Back();
                        Suivant(rang + 1);
                    }),
                "Impression grand format");
        }

        void Terminer()
        {
            var reponse = MessageBox.Show(
                $"Les {fichiers.Count} agrandissement(s) de la commande {order.DisplayNumber} " +
                "sont-ils bien sortis de l'Epson ?\n\n" +
                "Non les laisse dans « Agrandissements », où ils pourront être retirés.",
                "Agrandissements", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (reponse == MessageBoxResult.Yes)
            {
                try
                {
                    foreach (var (enveloppe, _) in enveloppes)
                        services.Printer.ConfirmPrinted(order, enveloppe);

                    if (_commandeBorne is { } borne)
                        services.DiLandImport.MarkPrinted(borne, order.Id);
                }
                catch (Exception ex)
                {
                    FileLog.Write("Confirmation des agrandissements impossible", ex);
                }
            }

            AccueilStudio.Rentrer();
        }

        Suivant(0);
    }

    /// <summary>Une photo de la grille et, si elle est cochée, sa ligne de panier.</summary>
    internal sealed class PhotoItem : ObservableObject
    {
        private readonly Action _cartChanged;
        private ImageSource? _thumbnail;
        private bool _selected;
        private Product? _product;
        private string? _finish;
        private int _quantity = 1;

        public PhotoItem(string path, Action cartChanged)
        {
            Path = path;
            _cartChanged = cartChanged;
            Cle = $"{path}#{System.Threading.Interlocked.Increment(ref _dernierRang)}";
        }

        private static int _dernierRang;

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);

        /// <summary>
        /// Ce qui distingue CETTE ligne de commande de toutes les autres, doublons compris.
        ///
        /// Le chemin ne suffit plus depuis qu'une même photo peut figurer deux fois dans une
        /// commande (bouton « Dupliquer ») : les caches d'aperçu de l'écran « Modifier »
        /// étaient rangés par chemin, et l'original en noir et blanc rendait donc son image
        /// au doublon resté en couleur.
        /// </summary>
        public string Cle { get; }

        /// <summary>
        /// Ce que le tirage retiendra de la photo — la traduction du <see cref="Cadre"/>,
        /// et la seule forme que le rendu comprenne.
        /// </summary>
        public CropSpec Crop { get; set; } = CropSpec.Full;

        /// <summary>
        /// Le recadrage vient d'une DÉCISION — celle du client à la borne, ou celle de
        /// l'opérateur dans un brouillon — et non d'un cadre calculé.
        /// </summary>
        /// <remarks>
        /// Ce drapeau existe pour un seul cas, et il compte : les tirages « bord blanc »
        /// sont en mode « photo entière », où <see cref="Cadre"/> IGNORE le recadrage
        /// enregistré. La règle est juste dans son cas d'origine — un cadrage hérité du
        /// mode « remplir » ferait déborder la photo du format — mais elle jetait aussi le
        /// cadrage que le client avait validé à la borne sur ses dix produits bord blanc,
        /// et le correctif du cadrage se serait arrêté à mi-chemin.
        ///
        /// Il tombe dès que l'OPÉRATEUR reprend la main sur le format (produit ou
        /// remplir/entier) : à partir de là, le cadrage n'est plus celui qui accompagnait
        /// la commande.
        /// </remarks>
        public bool CadrageImpose { get; private set; }

        /// <summary>
        /// Le cadrage automatique sur le visage est déjà passé sur cette photo.
        ///
        /// Il ne doit passer QU'UNE fois : rouvrir « Modifier » sur une commande déjà réglée
        /// remettrait sinon le cadre sur le visage et effacerait ce que l'opérateur y avait
        /// fait. Voir <c>EditSelectionView.CadrerSurLesVisages</c>.
        /// </summary>
        public bool CadrageAutoFait { get; private set; }

        /// <summary>À appeler après avoir posé le cadre sur le visage.</summary>
        public void MarquerCadrageAuto() => CadrageAutoFait = true;

        /// <summary>
        /// Pose le recadrage qui accompagnait la commande — voir <see cref="CadrageImpose"/>.
        /// À appeler EN DERNIER : produit, quarts de tour et mode le remettent à zéro.
        /// </summary>
        public void PoserLeCadrageDOrigine(CropSpec crop)
        {
            if (!crop.IsValid || crop.IsFull) return;

            Crop = crop;
            CadrageImpose = true;
        }

        private int _quartsDeTour;

        public int RotationQuarterTurns
        {
            get => _quartsDeTour;
            set
            {
                if (_quartsDeTour == value) return;
                _quartsDeTour = value;

                // les deux côtés de la photo s'échangent : le cadre est à refaire, et le
                // cadrage repart du centre — ses repères viennent de tourner avec elle
                OublierCadre();
            }
        }

        private double _redressement;

        /// <summary>
        /// Redressement fin, en degrés — le « Tilt » de DiLand (touche T), qu'il stocke
        /// sous <c>FineRotationAngle</c>. Distinct des quarts de tour : on redresse un
        /// horizon penché de deux degrés, on ne le fait pas basculer de quatre-vingt-dix.
        /// </summary>
        public double FineRotationDegrees
        {
            get => _redressement;
            set
            {
                _redressement = value;
                // le cadre doit le savoir : c'est l'angle qui dit sur quel canevas ses
                // fractions se comptent, et de combien la photo doit grandir pour ne pas
                // laisser de coin vide
                if (_cadre is not null) _cadre.RotationDegrees = value;
            }
        }

        /// <summary>Définition du fichier d'origine, une fois orienté.</summary>
        public (int Width, int Height) SourcePixels => (_sourceWidth, _sourceHeight);

        /// <summary>
        /// La photo telle qu'on la VOIT : quarts de tour compris.
        ///
        /// <see cref="SourcePixels"/> donne le fichier, qui garde ses côtés dans le même
        /// ordre quoi qu'on fasse. Un cadre bâti dessus après un quart de tour
        /// raisonnerait sur une photo couchée alors qu'elle est debout.
        /// </summary>
        public (int Width, int Height) PixelsVus => RotationQuarterTurns % 2 == 0
            ? (_sourceWidth, _sourceHeight)
            : (_sourceHeight, _sourceWidth);

        private FramedCrop? _cadre;

        /// <summary>
        /// Le cadre du tirage : fixe, au format du produit, la photo bougeant derrière.
        ///
        /// Il naît ici, dès qu'on connaît le produit ET la définition — et non plus
        /// seulement quand l'opérateur ouvre la photo dans « Modifier ». C'est ce qui
        /// manquait : les photos jamais ouvertes partaient à l'impression avec un
        /// recadrage « pleine image », et c'était le rendu qui décidait seul du cadrage,
        /// sans rapport avec le cadre affiché à l'écran. Constaté sur le papier le
        /// 01/08/2026, et lisible dans les commandes du jour (six sur onze en « pleine »).
        /// </summary>
        public FramedCrop? Cadre
        {
            get
            {
                if (_cadre is not null) return _cadre;
                if (_product is null) return null;

                var (largeurPx, hauteurPx) = PixelsVus;
                if (largeurPx <= 0 || hauteurPx <= 0) return null; // définition pas encore lue

                // Polaroid : le cadre montré est la FENÊTRE du film — presque carrée — et
                // non la feuille. C'est elle que l'opérateur remplit ; lui montrer un
                // 10×15 lui ferait cadrer sur des bords qui seront coupés.
                // Polaroid comme BORD BLANC : ce qu'on cadre est la FENÊTRE, pas la feuille.
                // Sur un « bord blanc 10×15 », le papier fait 102 × 152 mais la photo n'en
                // occupe que 92 × 142 ; cadrer au rapport du papier faisait perdre une
                // bande au tirage, puisque ce n'est pas le rectangle où la photo atterrit.
                var polaroid = (FitOverride ?? _product.DefaultFit) == FitMode.Polaroid;
                var (largeur, hauteur) = polaroid
                    ? (PolaroidFrame.WindowWidthMm, PolaroidFrame.WindowHeightMm)
                    : _product.FenetreMm;

                // Orientation du cadre : celle d'un cadrage déjà posé s'il y en a un —
                // l'opérateur a pu demander un tirage en travers de la photo, et la
                // reprendre à la photo la lui reprendrait des mains. Sinon celle de la
                // photo, comme le fait le rendu (OrientCanvas).
                var paysage = Crop.IsFull
                    ? largeurPx >= hauteurPx
                    : Crop.Width * largeurPx >= Crop.Height * hauteurPx;

                if (paysage != largeur >= hauteur) (largeur, hauteur) = (hauteur, largeur);

                _cadre = new FramedCrop(largeurPx, hauteurPx, largeur, hauteur)
                {
                    RotationDegrees = FineRotationDegrees,

                    // « photo entière » : la photo tient dans le format et le reste sort
                    // blanc, exactement ce que fait le rendu (ImagePipeline complète en
                    // blanc). Sans cette ligne le mode ne changeait rien à l'écran : le
                    // cadre forçait la photo à couvrir le format quoi qu'il arrive, et
                    // l'opérateur ne voyait jamais les marges qu'il venait de demander.
                    //
                    // Le BORD BLANC en est exclu bien qu'il partage le même FitMode : sa
                    // photo REMPLIT la fenêtre, et c'est le liseré — posé au rendu — qui
                    // fait le blanc. Lui laisser des marges ici, c'en aurait fait deux.
                    AllowsWhiteMargins = (FitOverride ?? _product.DefaultFit) == FitMode.Fit
                                         && !_product.ABordBlanc,
                };

                // le drapeau ci-dessus arrive après le Reset du constructeur : c'est ici
                // qu'on retombe sur la bonne taille de départ
                _cadre.Reset();

                // On reprend le cadrage déjà enregistré, s'il y en a un — sauf en « photo
                // entière », où il n'y a rien à reprendre : la photo est posée dans le
                // format, et un cadrage hérité du mode « remplir » la ferait déborder.
                //
                // Un cadrage IMPOSÉ fait exception, et c'est tout l'objet du drapeau : sur
                // un « bord blanc », le client a bel et bien choisi sa zone à la borne, et
                // ce n'est pas un cadrage hérité d'un autre mode. Contraindre() ne fait
                // rien quand le blanc est permis : la géométrie posée ici tient.
                if (!Crop.IsFull && (CadrageImpose || !_cadre.AllowsWhiteMargins))
                    _cadre.SetFromCropSpec(Crop);

                return _cadre;
            }
        }

        /// <summary>Reporte le cadre sur le recadrage — le seul point de conversion.</summary>
        public void AppliquerCadre()
        {
            if (Cadre is { } cadre) Crop = cadre.ToCropSpec();
        }

        /// <summary>Remplace le cadre — le pivot du cadre en construit un nouveau.</summary>
        public void RemplacerCadre(FramedCrop cadre)
        {
            _cadre = cadre;
            Crop = cadre.ToCropSpec();
        }

        /// <summary>Oublie le cadre : le suivant repartira du centre, au format du produit.</summary>
        public void OublierCadre()
        {
            _cadre = null;
            Crop = CropSpec.Full;

            // le cadrage d'origine s'en va avec : il n'y a plus de recadrage à imposer
            CadrageImpose = false;
        }

        /// <summary>Rapport largeur/hauteur du fichier d'origine, une fois orienté.</summary>
        public double SourceAspect => _sourceHeight == 0 ? 1 : _sourceWidth / (double)_sourceHeight;
        private FitMode? _fit;

        /// <summary>
        /// « Remplir le format » ou « photo entière », quand l'opérateur ne veut pas de
        /// celui du produit ; null = celui du produit.
        ///
        /// Le cadre est jeté à chaque changement : les deux modes n'ont pas la même taille
        /// de départ — couvrir le format d'un côté, tenir dedans de l'autre — et garder
        /// l'ancien cadre faisait que la bascule ne changeait rien à l'écran. Le recadrage
        /// enregistré, lui, est conservé : c'est le <see cref="Cadre"/> qui décide s'il a
        /// encore un sens dans le nouveau mode.
        /// </summary>
        public FitMode? FitOverride
        {
            get => _fit;
            set
            {
                if (_fit == value) return;
                _fit = value;
                _cadre = null;

                // l'opérateur reprend la main sur le format : le cadrage n'est plus celui
                // qui accompagnait la commande, et le cadre redevient seul juge
                CadrageImpose = false;
            }
        }
        /// <summary>
        /// Contour noir sur le bord de la photo, à suivre aux ciseaux.
        ///
        /// N'a de sens qu'en « photo entière » : c'est le seul mode où le tirage sort avec des
        /// marges blanches, donc le seul où l'on ne sait pas où couper.
        /// </summary>
        public bool CutBorder { get; set; }

        public ImageAdjustments Adjustments { get; set; } = new();

        private BitmapSource? _sourceThumbnail;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            private set => Set(ref _thumbnail, value);
        }

        public void SetSourceThumbnail(BitmapSource source)
        {
            _sourceThumbnail = source;
            RefreshThumbnail();
        }

        /// <summary>
        /// Vignette affichée = vignette source + rotation utilisateur + recadrage choisi
        /// + corrections, pour que la grille montre la photo telle qu'elle sortira.
        /// </summary>
        public void RefreshThumbnail()
        {
            if (_sourceThumbnail is null) return;

            // le cadre d'abord : c'est lui qui porte la vérité, et c'est ici qu'il
            // devient le recadrage que l'impression lira. Sans cette ligne, une photo
            // qu'on n'a jamais ouverte part en « pleine image ».
            AppliquerCadre();

            Thumbnail = Compose(_sourceThumbnail);
        }

        /// <summary>La vignette telle qu'elle a été lue, sans rien par-dessus.</summary>
        public BitmapSource? SourceThumbnail => _sourceThumbnail;

        /// <summary>
        /// Construit l'image affichable à partir d'une source : rotation, corrections,
        /// puis le cadre du tirage par-dessus.
        ///
        /// Isolé pour que la vignette et le grand aperçu suivent EXACTEMENT le même
        /// chemin, à la seule différence de la définition de la source — sinon l'un
        /// montrerait autre chose que l'autre.
        /// </summary>
        public ImageSource Compose(BitmapSource source)
        {
            var photo = ComposePhoto(source);

            // le redressement fin après les quarts de tour, comme au rendu final
            if (Math.Abs(FineRotationDegrees) > 0.01)
                photo = RedresserFin(photo, FineRotationDegrees);

            // puis le cadre du tirage PAR-DESSUS la photo entière, au lieu de découper.
            // C'est la vue d'ensemble de DiLand : d'un coup d'œil sur la planche, on voit
            // ce que chaque tirage gardera ET ce qu'il coupera. Une vignette déjà rognée
            // ne dit pas ce qu'on perd, et oblige à ouvrir les photos une par une.
            return DessinerCadre(photo, Crop);
        }

        /// <summary>
        /// La photo seule : quarts de tour et corrections, mais NI redressement NI cadre.
        ///
        /// C'est ce que la surface de recadrage veut afficher : elle rend le redressement
        /// et le cadre elle-même, à la volée. Refabriquer une image tournée à chaque degré
        /// rendrait le geste poussif, et le cadre n'a rien à faire dans les pixels quand
        /// il est dessiné par-dessus.
        /// </summary>
        public BitmapSource ComposePhoto(BitmapSource source) =>
            ComposerPhoto(source, RotationQuarterTurns, Adjustments);

        /// <summary>
        /// La même chose, mais sur des valeurs figées — donc appelable HORS du fil de
        /// l'interface.
        ///
        /// Les corrections d'un aperçu de 1600 px se comptent en dizaines de
        /// millisecondes : les faire sur le fil de l'interface fige l'écran à chaque cran
        /// de curseur. L'appelant en prend un instantané (<c>Adjustments.Clone()</c>) et
        /// le calcule à côté, pendant que l'opérateur continue de régler.
        /// </summary>
        public static BitmapSource ComposerPhoto(
            BitmapSource source, int quartsDeTour, ImageAdjustments reglages)
        {
            BitmapSource display = source;
            if (quartsDeTour != 0)
                display = new TransformedBitmap(display, new RotateTransform(90 * quartsDeTour));

            if (display.CanFreeze) display.Freeze();

            return ThumbnailAdjuster.Apply(display, reglages);
        }

        /// <summary>
        /// Redressement de quelques degrés, rendu à la main.
        ///
        /// PAS avec <c>TransformedBitmap</c> : WPF n'y accepte que des échelles, des
        /// retournements et des rotations à 90°, et refuse tout autre angle par une
        /// exception — « La transformation doit être une combinaison d'échelles, de
        /// retournements et de rotations à 90 degrés ». Elle remontait à chaque redessin
        /// et faisait tomber d'un coup C, C+clic droit et T+molette (01/08/2026).
        ///
        /// Le canevas est agrandi pour contenir l'image inclinée, sans quoi les coins
        /// seraient coupés au lieu d'être laissés au cadrage.
        /// </summary>
        private static BitmapSource RedresserFin(BitmapSource source, double degres)
        {
            var radians = degres * Math.PI / 180;
            double largeur = source.PixelWidth;
            double hauteur = source.PixelHeight;

            var cos = Math.Abs(Math.Cos(radians));
            var sin = Math.Abs(Math.Sin(radians));
            var largeurRendue = (int)Math.Ceiling(largeur * cos + hauteur * sin);
            var hauteurRendue = (int)Math.Ceiling(largeur * sin + hauteur * cos);

            var visuel = new DrawingVisual();
            using (var dessin = visuel.RenderOpen())
            {
                dessin.PushTransform(new TranslateTransform(largeurRendue / 2.0, hauteurRendue / 2.0));
                dessin.PushTransform(new RotateTransform(degres));
                dessin.DrawImage(source, new Rect(-largeur / 2, -hauteur / 2, largeur, hauteur));
                dessin.Pop();
                dessin.Pop();
            }

            var rendu = new RenderTargetBitmap(largeurRendue, hauteurRendue, 96, 96, PixelFormats.Pbgra32);
            rendu.Render(visuel);
            rendu.Freeze();
            return rendu;
        }

        /// <summary>Dessine le cadre jaune du tirage sur la vignette, comme DiLand.</summary>
        private static ImageSource DessinerCadre(ImageSource photo, CropSpec crop)
        {
            if (photo is not BitmapSource source) return photo;
            if (!crop.IsValid) return photo;

            var largeur = source.PixelWidth;
            var hauteur = source.PixelHeight;
            if (largeur <= 0 || hauteur <= 0) return photo;

            var visuel = new DrawingVisual();
            using (var dessin = visuel.RenderOpen())
            {
                dessin.DrawImage(source, new Rect(0, 0, largeur, hauteur));

                var cadre = new Rect(
                    crop.X * largeur, crop.Y * hauteur,
                    crop.Width * largeur, crop.Height * hauteur);

                // Ce qui sera coupé n'est PLUS assombri : le voile rendait illisible la
                // moitié d'une vignette, et c'est justement cette moitié qu'on regarde
                // pour décider de la rattraper. Le cadre jaune, épais, dit la limite.
                var trait = new Pen(Brushes.Yellow, Math.Max(2, largeur / 90.0));
                trait.Freeze();
                dessin.DrawRectangle(null, trait, cadre);
            }

            var rendu = new RenderTargetBitmap(largeur, hauteur, 96, 96, PixelFormats.Pbgra32);
            rendu.Render(visuel);
            rendu.Freeze();
            return rendu;
        }

        public bool Selected
        {
            get => _selected;
            set
            {
                if (!Set(ref _selected, value)) return;
                OnPropertyChanged(nameof(BorderBrush));
                OnPropertyChanged(nameof(TileBrush));
                OnPropertyChanged(nameof(CheckVisibility));
                OnPropertyChanged(nameof(CartVisibility));
                _cartChanged();
            }
        }

        public Product? Product
        {
            get => _product;
            set
            {
                if (_product?.Code == value?.Code) return;
                _product = value;

                // le format change, donc le cadre : on repart d'un cadrage centré au
                // nouveau format, et la vignette le montre aussitôt
                OublierCadre();
                RefreshThumbnail();

                OnPropertyChanged(nameof(ProductLabel));
                OnPropertyChanged(nameof(FormatLabel));
                OnPropertyChanged(nameof(FormatVisibility));
                _cartChanged();
            }
        }

        /// <summary>Finition choisie (voir Product.Finishes) ; null = DEVMODE par défaut du produit.</summary>
        public string? Finish
        {
            get => _finish;
            set
            {
                if (!Set(ref _finish, value)) return;
                OnPropertyChanged(nameof(ProductLabel));
                OnPropertyChanged(nameof(FormatLabel));
            }
        }

        public int Quantity
        {
            get => _quantity;
            set
            {
                if (!Set(ref _quantity, value)) return;
                OnPropertyChanged(nameof(QuantityLabel));
                _cartChanged();
            }
        }

        /// <summary>
        /// Le prix n'est affiché QUE s'il y en a un : le format personnalisé est facturé au
        /// papier de la planche, qui n'est choisi qu'à la validation. Annoncer « 0,00 € » sur
        /// chaque vignette ferait croire à un tirage gratuit.
        /// </summary>
        public string ProductLabel => _product is null
            ? "Produit…"
            : $"{_product.Name}{(_finish is null ? "" : $" · {_finish}")}" +
              (_product.Price > 0 ? $" · {_product.Price:0.00} €" : "");

        /// <summary>
        /// Le FORMAT du tirage, sans le prix — le badge de la vignette.
        ///
        /// Depuis qu'une commande peut mélanger les formats, c'est la seule chose qui
        /// distingue à l'œil une 10×15 d'une 15×20 dans la planche. Le prix, lui, n'a rien
        /// à faire sur une vignette : il est déjà au total de la barre du bas, et répété
        /// sur soixante photos il mange la place du reste.
        /// </summary>
        public string FormatLabel => _product is null
            ? ""
            : $"{_product.Name}{(_finish is null ? "" : $" · {_finish}")}";

        /// <summary>Le badge de format n'apparaît que sur les photos qui en ont un.</summary>
        public Visibility FormatVisibility =>
            _product is null ? Visibility.Collapsed : Visibility.Visible;

        public string QuantityLabel => _quantity.ToString();

        public Brush BorderBrush => Selected
            ? (Brush)Application.Current.Resources["AccentBrush"]
            : Brushes.Transparent;

        /// <summary>
        /// Fond de la vignette : orange dès qu'elle est retenue, comme chez DiLand. Le
        /// choix saute aux yeux d'un bout à l'autre de la planche, ce qu'un liseré fin
        /// ne permet pas — c'est ce qui compte quand on sert un client au comptoir.
        /// </summary>
        public Brush TileBrush => Selected
            ? (Brush)Application.Current.Resources["TitleBrush"]
            : (Brush)Application.Current.Resources["PanelBrush"];

        private bool _ciblee;

        /// <summary>
        /// Visée par les réglages de l'écran « Modifier ».
        ///
        /// À NE PAS confondre avec <see cref="Selected"/>, qui dit ce qui part à
        /// l'impression. Les deux ne faisaient qu'un : écarter une photo d'un réglage par
        /// Ctrl+clic la retirait du même coup de la commande, sans que rien ne le dise —
        /// on tirait alors moins de photos qu'on ne croyait.
        /// </summary>
        public bool Ciblee
        {
            get => _ciblee;
            set
            {
                if (!Set(ref _ciblee, value)) return;
                OnPropertyChanged(nameof(CibleBrush));
            }
        }

        /// <summary>Fond de la vignette dans la bande de « Modifier » : visée ou non.</summary>
        public Brush CibleBrush => Ciblee
            ? (Brush)Application.Current.Resources["TitleBrush"]
            : (Brush)Application.Current.Resources["PanelBrush"];

        private int _sourceWidth;
        private int _sourceHeight;

        /// <summary>Définition du fichier d'origine, notée à la lecture de la vignette.</summary>
        public void SetSourceSize(int width, int height)
        {
            _sourceWidth = width;
            _sourceHeight = height;
            OnPropertyChanged(nameof(SizeLabel));
            OnPropertyChanged(nameof(RatioLabel));
            OnPropertyChanged(nameof(BadgeVisibility));

            // la définition arrive après la vignette : c'est seulement maintenant que le
            // cadre peut être bâti, et la vignette doit le montrer
            RefreshThumbnail();
        }

        public string SizeLabel => _sourceWidth == 0 ? "" : $"{_sourceWidth} x {_sourceHeight}";

        /// <summary>
        /// La définition du fichier est-elle déjà connue ?
        ///
        /// <see cref="SourceAspect"/> répond 1 quand elle ne l'est pas, ce qui passe pour un
        /// carré : la planche index taillerait sa grille sur des photos carrées imaginaires.
        /// Les appelants qui se servent du rapport doivent donc demander d'abord.
        /// </summary>
        public bool SourceSizeKnown => _sourceWidth > 0 && _sourceHeight > 0;

        /// <summary>Rapport du plus grand côté au plus petit, comme l'affiche DiLand (« 2.3 »).</summary>
        public string RatioLabel
        {
            get
            {
                if (_sourceWidth == 0 || _sourceHeight == 0) return "";
                var grand = Math.Max(_sourceWidth, _sourceHeight);
                var petit = Math.Min(_sourceWidth, _sourceHeight);
                return (grand / (double)petit).ToString("0.0",
                    System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        public Visibility BadgeVisibility => _sourceWidth == 0 ? Visibility.Collapsed : Visibility.Visible;

        public Visibility CheckVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
        public Visibility CartVisibility => Selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
