using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;
using Studio.Store;

namespace Studio.App.Views;

/// <summary>
/// Dernier écran du parcours identité : la planche telle qu'elle sortira, photo par photo,
/// avant d'engager le papier.
///
/// Il manquait, et c'est ce qui coûtait cher : l'écran de cadrage montre le CADRE sur la
/// photo, pas la planche. On découvrait donc la disposition, le nombre de vignettes et
/// l'horodatage sur le tirage lui-même — refaire une planche identité coûte une feuille et
/// un client qui attend.
///
/// Toutes les photos partent en UNE seule commande : c'est ce que le parcours d'avant ne
/// savait pas faire, et deux personnes d'une même famille donnaient deux commandes, deux
/// tickets et deux passages en caisse.
/// </summary>
public partial class IdSheetRecapView : UserControl
{
    /// <summary>
    /// Une planche à sortir : la photo, son cadrage, ses réglages, et ce qu'on en tire.
    /// </summary>
    /// <param name="Rang">Numéro de la photo dans le lot, à partir de 1.</param>
    /// <param name="Copies">Vignettes sur une planche.</param>
    /// <param name="Quantite">Planches identiques à tirer.</param>
    public sealed record Planche(
        string SourcePath,
        CropSpec Crop,
        double RedressementDegres,
        ImageAdjustments Reglages,
        int Copies,
        int Quantite,
        Product Produit,
        string? Finition,
        int Rang)
    {
        /// <summary>La quantité change sur cet écran : elle n'est pas figée à l'arrivée.</summary>
        public int Quantite { get; set; } = Quantite;
    }

    /// <summary>
    /// Prix d'UNE planche, décidé par le pays du document : 10 € pour un document français,
    /// 15 € pour un étranger (réglable dans Paramètres). Voir <see cref="TarifsIdentite"/>.
    /// </summary>
    private decimal PrixDeLaPlanche => App.Services.TarifsIdentite.Pour(_document.Country);

    private readonly List<Planche> _planches;
    private readonly IdDocumentSpec _document;
    private readonly Action<int>? _surModifier;
    private readonly Action? _surRemplacer;

    /// <summary>La planche mise de côté que cette impression solde, s'il y en a une.</summary>
    private readonly Guid? _attenteId;

    /// <summary>Aperçus déjà rendus, par rang de planche. Un rendu par photo, pas par affichage.</summary>
    private readonly Dictionary<int, BitmapImage> _apercus = [];

    private int _page;
    private bool _impressionLancee;

    /// <param name="planches">Les planches à récapituler, dans l'ordre du lot.</param>
    /// <param name="document">Norme visée — elle fixe la taille des cases.</param>
    /// <param name="surModifier">
    /// Retour au cadrage, sur la photo de ce rang. Null = le bouton ne fait qu'un retour.
    /// </param>
    /// <param name="surRemplacer">Retour au choix des photos. Null = simple retour.</param>
    /// <param name="attenteId">
    /// La planche mise de côté que cette impression solde, s'il y en a une. Sans elle,
    /// l'accueil continuerait de proposer « Reprendre » sur une planche déjà tirée — et on
    /// la tirerait deux fois.
    /// </param>
    public IdSheetRecapView(
        IReadOnlyList<Planche> planches,
        IdDocumentSpec document,
        Action<int>? surModifier = null,
        Action? surRemplacer = null,
        Guid? attenteId = null)
    {
        ArgumentNullException.ThrowIfNull(planches);

        _planches = [.. planches];
        _document = document;
        _surModifier = surModifier;
        _surRemplacer = surRemplacer;
        _attenteId = attenteId;

        InitializeComponent();

        TitleText.Text = _planches.Count == 1
            ? "Récapitulatif de la planche"
            : $"Récapitulatif — {_planches.Count} planches";

        Loaded += async (_, _) =>
        {
            Afficher();
            await RendreLesApercusAsync();
        };
    }

    // ----- aperçus -----

    /// <summary>
    /// Résolution des aperçus. C'est la MOITIÉ des 300 ppp du tirage : sur les papiers de
    /// la boutique, la capacité de la planche y tombe sur le même compte (8 cases de
    /// 35 × 45 sur un 156 × 105), et le rendu coûte le quart.
    ///
    /// La capacité est malgré tout revérifiée : elle se compte en pixels entiers, et un
    /// document exotique pourrait perdre une case à cette résolution. Voir
    /// <see cref="PppDApercu"/>.
    /// </summary>
    private const int PppApercuVoulu = 150;

    /// <summary>
    /// La résolution à laquelle rendre l'aperçu de CETTE planche : celle voulue si la
    /// planche y porte encore ses cases, celle du tirage sinon.
    ///
    /// Montrer sept cases là où huit sortiront serait pire que pas d'aperçu du tout —
    /// c'est justement le compte que l'opérateur vient vérifier.
    /// </summary>
    private int PppDApercu(Planche planche)
    {
        var capacite = IdSheetLayout.MaxCopies(
            MmPx.ToPixels(planche.Produit.WidthMm, PppApercuVoulu),
            MmPx.ToPixels(planche.Produit.HeightMm, PppApercuVoulu),
            MmPx.ToPixels(_document.WidthMm, PppApercuVoulu),
            MmPx.ToPixels(_document.HeightMm, PppApercuVoulu),
            MmPx.ToPixels(planche.Produit.Sheet?.LayoutGapMm ?? SheetSpec.DefaultGapMm, PppApercuVoulu));

        return capacite >= planche.Copies ? PppApercuVoulu : planche.Produit.Dpi;
    }

    private async Task RendreLesApercusAsync()
    {
        var dossier = Path.Combine(App.Services.DataRoot, "cache", "identite",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        for (var i = 0; i < _planches.Count; i++)
        {
            var planche = _planches[i];
            if (_apercus.ContainsKey(planche.Rang)) continue;

            EtatText.Text = _planches.Count == 1
                ? "Préparation de la planche…"
                : $"Préparation des planches… ({i + 1}/{_planches.Count})";
            EtatText.Visibility = Visibility.Visible;

            try
            {
                var octets = await Task.Run(() => RendreUnePlanche(planche, dossier));
                _apercus[planche.Rang] = ToBitmap(octets);
            }
            catch (Exception ex)
            {
                FileLog.Write($"Aperçu de planche impossible ({Path.GetFileName(planche.SourcePath)})", ex);
            }

            if (i == _page) Afficher();
        }

        Afficher();
    }

    /// <summary>
    /// Rend une planche et rend ses octets PNG.
    ///
    /// La source est la VIGNETTE, pas l'original : une planche pleine résolution prend une
    /// quinzaine de secondes sur une photo de 24 Mpx (relevé au journal du 04/08/2026), et
    /// il en faudrait autant par photo du lot avant de montrer quoi que ce soit. Le
    /// cadrage est en coordonnées relatives : il tombe au même endroit quelle que soit la
    /// définition de la source. Le TIRAGE, lui, repart toujours de l'original.
    /// </summary>
    private byte[] RendreUnePlanche(Planche planche, string dossier)
    {
        Directory.CreateDirectory(dossier);

        var ppp = PppDApercu(planche);
        var feuille = Path.Combine(dossier, $"planche-{planche.Rang:00}.png");

        // la vignette est écrite à côté : ImagePipeline travaille sur des fichiers
        var source = Path.Combine(dossier, $"source-{planche.Rang:00}.jpg");
        File.WriteAllBytes(source, App.Services.Thumbnails.GetJpeg(planche.SourcePath, 1440));

        var sheet = planche.Produit.Sheet ?? new SheetSpec();

        ImagePipeline.RenderIdSheetToFile(
            new RenderRequest(
                source,
                MmPx.ToPixels(_document.WidthMm, ppp),
                MmPx.ToPixels(_document.HeightMm, ppp),
                planche.Crop, 0, planche.RedressementDegres,
                FitMode.Fill, 0,
                // la correction propre à la machine est celle du tirage : l'aperçu doit
                // montrer ce qui sortira, pas ce que l'écran de cadrage affichait
                AvecLaCorrectionDuProduit(planche.Reglages, planche.Produit)),
            planche.Copies, sheet.GapMm, sheet.CutMarks,
            MmPx.ToPixels(planche.Produit.WidthMm, ppp),
            MmPx.ToPixels(planche.Produit.HeightMm, ppp),
            feuille, ppp,
            sheet.CutBorder,
            // la MÊME bande que le tirage, marque comprise : l'aperçu existe pour montrer
            // ce qui sortira, et une bande qui n'apparaîtrait qu'au tirage serait
            // exactement la mauvaise surprise que cet écran est là pour éviter
            sheet.DateStamp ? SheetFooter.Pour(DateTime.Now, App.Services.Marque) : null,
            sheet.FullBleed);

        return File.ReadAllBytes(feuille);
    }

    /// <summary>
    /// La même règle que l'impression : voir <c>PrintOrchestrator.AvecLaCorrectionDuProduit</c>.
    /// Reprise ici pour que l'aperçu ne puisse pas en diverger sans qu'on s'en aperçoive —
    /// un aperçu plus sombre que le tirage ferait corriger à la main ce qui est déjà corrigé.
    /// </summary>
    private static ImageAdjustments AvecLaCorrectionDuProduit(ImageAdjustments reglages, Product produit) =>
        Studio.Printing.PrintOrchestrator.AvecLaCorrectionDuProduit(reglages, produit);

    private static BitmapImage ToBitmap(byte[] octets)
    {
        using var flux = new MemoryStream(octets);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = flux;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ----- affichage -----

    private Planche? Courante =>
        _page >= 0 && _page < _planches.Count ? _planches[_page] : null;

    private void Afficher()
    {
        if (Courante is not { } planche)
        {
            EtatText.Text = "Aucune planche.";
            EtatText.Visibility = Visibility.Visible;
            ImprimerButton.IsEnabled = false;
            return;
        }

        if (_apercus.TryGetValue(planche.Rang, out var apercu))
        {
            Feuille.Source = apercu;
            Feuille.Visibility = Visibility.Visible;
            EtatText.Visibility = Visibility.Collapsed;
        }
        else
        {
            Feuille.Visibility = Visibility.Collapsed;
            EtatText.Visibility = Visibility.Visible;
        }

        PageText.Text = _planches.Count == 1
            ? Path.GetFileName(planche.SourcePath)
            : $"planche {_page + 1} de {_planches.Count}";

        QuantiteText.Text = planche.Quantite.ToString();

        PremiereButton.IsEnabled = PrecedenteButton.IsEnabled = _page > 0;
        SuivanteButton.IsEnabled = DerniereButton.IsEnabled = _page < _planches.Count - 1;

        DetailText.Text =
            $"{Path.GetFileName(planche.SourcePath)} — {planche.Copies} photo(s) de " +
            $"{_document.WidthMm:0.#} × {_document.HeightMm:0.#} mm sur {planche.Produit.Name}" +
            (string.IsNullOrWhiteSpace(planche.Finition) ? "" : $" ({planche.Finition})");

        var feuilles = _planches.Sum(p => p.Quantite);

        // Le prix vient du DOCUMENT, pas du papier : 10 € pour un document français, 15 €
        // pour un étranger. C'est le même produit du catalogue dans les deux cas — voir
        // TarifsIdentite.
        var total = PrixDeLaPlanche * feuilles;

        TotalText.Text = $"{feuilles} planche{(feuilles > 1 ? "s" : "")} · {total:0.00} €" +
                         (feuilles > 1 ? $"  ({PrixDeLaPlanche:0.00} € la planche)" : "");
        ImprimerButton.IsEnabled = !_impressionLancee && feuilles > 0;
    }

    private void OnPremiere(object sender, RoutedEventArgs e) => AllerA(0);

    private void OnPrecedente(object sender, RoutedEventArgs e) => AllerA(_page - 1);

    private void OnSuivante(object sender, RoutedEventArgs e) => AllerA(_page + 1);

    private void OnDerniere(object sender, RoutedEventArgs e) => AllerA(_planches.Count - 1);

    private void AllerA(int page)
    {
        _page = Math.Clamp(page, 0, Math.Max(0, _planches.Count - 1));
        Afficher();
    }

    private void OnQuantiteMoins(object sender, RoutedEventArgs e) => Quantite(-1);

    private void OnQuantitePlus(object sender, RoutedEventArgs e) => Quantite(+1);

    /// <summary>
    /// La quantité descend jusqu'à 0 : c'est ainsi qu'on RETIRE une planche du lot sans
    /// revenir en arrière — le client change d'avis sur l'une des trois, et rien d'autre
    /// ne le permettait. Le compte et le total suivent.
    /// </summary>
    private void Quantite(int pas)
    {
        if (Courante is not { } planche) return;

        planche.Quantite = Math.Clamp(planche.Quantite + pas, 0, 20);
        Afficher();
    }

    private void OnModifierLaPhoto(object sender, RoutedEventArgs e)
    {
        if (Courante is not { } planche || _surModifier is null)
        {
            Navigator.Back();
            return;
        }

        _surModifier(planche.Rang);
    }

    private void OnRemplacerLaPhoto(object sender, RoutedEventArgs e)
    {
        if (_surRemplacer is null)
        {
            Navigator.Back();
            return;
        }

        _surRemplacer();
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnAnnuler(object sender, RoutedEventArgs e) =>
        Navigator.Home(new HomeView(), "Studio Photo");

    // ----- impression -----

    /// <summary>
    /// UNE commande pour tout le lot.
    ///
    /// Comme sur les tirages, seule la CRÉATION de la commande est attendue — c'est court :
    /// un numéro, une enveloppe, la copie des originaux. Le rendu des planches et l'envoi à
    /// la machine partent en tâche de fond, et l'avancement se lit dans le bandeau du haut.
    /// </summary>
    private async void OnImprimer(object sender, RoutedEventArgs e)
    {
        if (_impressionLancee) return;

        var aTirer = _planches.Where(p => p.Quantite > 0).ToList();
        if (aTirer.Count == 0)
        {
            MessageBox.Show(
                "Toutes les planches sont à zéro : il n'y a rien à imprimer.\n\n" +
                "Remontez la quantité d'au moins une planche.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var services = App.Services;

        var articles = aTirer
            .Select(p => new DraftItem(
                p.SourcePath, p.Produit, p.Quantite, p.Crop, 0, p.RedressementDegres,
                null, p.Reglages, p.Copies, p.Finition,
                // la case suit le DOCUMENT, jamais celle inscrite au produit
                SheetCell: new SheetCellSize(_document.WidthMm, _document.HeightMm),
                // et le PRIX aussi : c'est le document qui le fixe, pas le papier
                UnitPriceOverride: PrixDeLaPlanche))
            .ToList();

        _impressionLancee = true;
        ImprimerButton.IsEnabled = false;
        Mouse.OverrideCursor = CurseurStudio.Attente;

        try
        {
            var commande = await Task.Run(() => services.Orders.CreateOrder("Operateur", articles));

            // La planche est passée en caisse : ce qui attendait en son nom n'a plus
            // d'objet. Le laisser ferait proposer « Reprendre » sur l'accueil pour une
            // planche déjà tirée, et on la tirerait deux fois.
            if (_attenteId is { } attente) services.CommandesEnAttente.Effacer(attente);

            Mouse.OverrideCursor = null;

            services.Impressions.Lancer(commande,
                imprimer: (avancement, arret) =>
                {
                    foreach (var enveloppe in commande.Envelopes)
                        services.Printer.PrintEnvelope(commande, enveloppe,
                            progression: avancement, ct: arret);
                });

            Navigator.Home(new HomeView(), "Studio Photo");
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Échec de la création de la commande (planches identité)", ex);
            MessageBox.Show($"Commande impossible à créer : {ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);

            _impressionLancee = false;
            ImprimerButton.IsEnabled = true;
        }
    }
}
