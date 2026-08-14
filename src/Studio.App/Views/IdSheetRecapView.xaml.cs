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
        // la bande basse compte dans la capacité, comme au rendu : sans elle ici, l'aperçu
        // se croirait plus large qu'il ne sera et montrerait une case de trop
        var bande = planche.Produit.Sheet?.DateStamp == true
            ? SheetFooterLayout.ReserveMinimalePx(
                SheetFooter.Pour(DateTime.Now, App.Services.Marque), PppApercuVoulu)
            : 0;

        var capacite = IdSheetLayout.MeilleureCapacite(
            MmPx.ToPixels(planche.Produit.WidthMm, PppApercuVoulu),
            MmPx.ToPixels(planche.Produit.HeightMm, PppApercuVoulu),
            MmPx.ToPixels(_document.WidthMm, PppApercuVoulu),
            MmPx.ToPixels(_document.HeightMm, PppApercuVoulu),
            MmPx.ToPixels(planche.Produit.Sheet?.LayoutGapMm ?? SheetSpec.DefaultGapMm, PppApercuVoulu),
            bande).Copies;

        return capacite >= planche.Copies ? PppApercuVoulu : planche.Produit.Dpi;
    }

    private async Task RendreLesApercusAsync()
    {
        var dossier = Path.Combine(App.Services.DataRoot, "cache", "identite",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));

        var aFaire = _planches.Where(p => !_apercus.ContainsKey(p.Rang)).ToList();
        if (aFaire.Count == 0)
        {
            Afficher();
            return;
        }

        EtatText.Text = aFaire.Count == 1
            ? "Préparation de la planche…"
            : $"Préparation des planches… (0/{aFaire.Count})";
        EtatText.Visibility = Visibility.Visible;

        // TOUTES EN MÊME TEMPS, et non l'une après l'autre.
        //
        // Chaque planche coûte quelques centaines de millisecondes — le rendu d'une case
        // puis autant de compositions —, et elles ne se doivent rien entre elles : elles
        // portent des photos différentes et écrivent des fichiers différents. En file
        // indienne, un récapitulatif de quatre planches faisait attendre quatre fois ce
        // temps-là avant d'être complet. Le poste a de quoi les mener de front.
        //
        // Celle qu'on REGARDE reste prioritaire à l'affichage : chacune paraît dès qu'elle
        // arrive, dans l'ordre où elles finissent.
        var enCours = aFaire
            .Select(planche => Task.Run(() =>
            {
                try
                {
                    return (planche.Rang, Octets: (byte[]?)RendreUnePlanche(planche, dossier));
                }
                catch (Exception ex)
                {
                    FileLog.Write(
                        $"Aperçu de planche impossible ({Path.GetFileName(planche.SourcePath)})", ex);
                    return (planche.Rang, Octets: null);
                }
            }))
            .ToList();

        var faites = 0;

        while (enCours.Count > 0)
        {
            var finie = await Task.WhenAny(enCours);
            enCours.Remove(finie);

            var (rang, octets) = await finie;
            if (octets is not null) _apercus[rang] = ToBitmap(octets);

            faites++;
            if (aFaire.Count > 1)
                EtatText.Text = $"Préparation des planches… ({faites}/{aFaire.Count})";

            Afficher();
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

        return AvecLeBonSens(feuille, planche, ppp);
    }

    /// <summary>
    /// Redresse l'aperçu d'une planche tirée DEBOUT.
    ///
    /// Le fichier remis à la machine est toujours aux cotes du produit : une planche
    /// composée verticalement en sort couchée, photos tournées d'un quart de tour. C'est ce
    /// qu'il faut à la machine, mais pas ce qu'il faut à l'œil — l'opérateur vérifie ici un
    /// cadrage et un compte de photos, et les juger la tête de côté n'a aucun sens.
    ///
    /// On la remet donc debout POUR L'AFFICHAGE seulement. Le tirage, lui, part du fichier
    /// tel que la machine l'attend.
    /// </summary>
    private byte[] AvecLeBonSens(string feuille, Planche planche, int ppp)
    {
        var octets = File.ReadAllBytes(feuille);

        var sheet = planche.Produit.Sheet ?? new SheetSpec();

        var bande = sheet.DateStamp
            ? SheetFooterLayout.ReserveMinimalePx(
                SheetFooter.Pour(DateTime.Now, App.Services.Marque), ppp)
            : 0;

        var (_, debout) = IdSheetLayout.MeilleureCapacite(
            MmPx.ToPixels(planche.Produit.WidthMm, ppp),
            MmPx.ToPixels(planche.Produit.HeightMm, ppp),
            MmPx.ToPixels(_document.WidthMm, ppp),
            MmPx.ToPixels(_document.HeightMm, ppp),
            MmPx.ToPixels(sheet.LayoutGapMm, ppp),
            bande);

        if (!debout) return octets;

        using var image = new ImageMagick.MagickImage(octets);
        image.Rotate(-90);
        return image.ToByteArray(ImageMagick.MagickFormat.Png);
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

        DireSiLeDetourageAChange();
    }

    /// <summary>
    /// Signale que le détourage n'a pas pu tenir la méthode du cadrage.
    ///
    /// <b>C'est l'écran où le défaut se voit</b>, et c'était l'écran qui n'en disait rien.
    /// La planche est rendue ici à la taille d'impression, donc sous une autre empreinte de
    /// cache que l'aperçu du cadrage : le réseau repasse, et c'est là qu'il peut manquer de
    /// mémoire. À Créteil, le 12/08/2026, le fond était parfait au cadrage et ne l'était
    /// plus ici — sans un mot, le journal seul portant la trace du repli.
    /// </summary>
    private void DireSiLeDetourageAChange()
    {
        var repli = Studio.Imaging.BiRefNetMatting.DernierRepli;

        RepliDetourageText.Text = repli ?? "";
        RepliDetourageText.Visibility = repli is null ? Visibility.Collapsed : Visibility.Visible;
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
        AccueilStudio.Rentrer();

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

        _impressionLancee = true;
        ImprimerButton.IsEnabled = false;

        // Le tirage lui-même vit dans TirageIdentite : le poste identité imprime sans passer
        // par cet écran, et les deux chemins doivent produire exactement la même commande.
        if (await TirageIdentite.LancerAsync(_planches, _document, _attenteId)) return;

        _impressionLancee = false;
        ImprimerButton.IsEnabled = true;
    }
}
