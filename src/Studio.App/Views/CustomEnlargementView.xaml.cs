using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.App.Views;

/// <summary>
/// Un agrandissement qui n'est pas au catalogue : « je veux ce tirage en A2 ».
///
/// <b>Ce n'est PAS le « Personnalisé » de l'impression rapide.</b> Celui-là compose des
/// planches sur du papier minilab (<c>CustomSizeView</c>) ; ici, un tirage unique sort en
/// fichier pour l'Epson, comme les autres agrandissements.
///
/// <b>Le prix est celui du format du catalogue dans lequel la taille tient</b> — règle posée
/// par l'exploitant le 02/08/2026. Un A3 (29,7 × 42) entre dans un 30×45 : il coûte un
/// 30×45. Rien à saisir, rien à tenir à jour.
///
/// <b>Le format demandé devient un vrai produit du catalogue</b>, ajouté à la validation.
/// C'est ce qui permet à tout le reste de fonctionner sans rien apprendre : la grille, l'écran
/// « Modifier », le rendu, la boîte grand format, et surtout <c>ProductCatalog.Require</c>,
/// qui devra retrouver le code des semaines plus tard pour une réimpression. Conséquence
/// voulue : la deuxième commande d'A2 le trouve déjà dans la liste des agrandissements.
/// </summary>
public partial class CustomEnlargementView : UserControl
{
    private const int MaximumRecentes = 5;

    private readonly List<CustomSize> _recentes;
    private readonly Action<Product>? _surValidation;
    private (double LargeurMm, double HauteurMm, Product Papier, string? Nom)? _retenu;

    /// <param name="surValidation">
    /// Ce qu'on fait du format retenu. Null = le parcours habituel, on enchaîne sur le choix
    /// du dossier. Fourni, l'écran ne sert que de saisie et rend la main à l'appelant — c'est
    /// ainsi qu'on bascule en A2 des photos DÉJÀ ouvertes, sans perdre recadrages ni
    /// corrections. Même mécanique que <see cref="CustomSizeView"/>.
    /// </param>
    public CustomEnlargementView(Action<Product>? surValidation = null)
    {
        _surValidation = surValidation;
        InitializeComponent();

        StandardsList.ItemsSource = EnlargementSizes.Standards
            .Select(s => new FormatNormalise(s))
            .ToList();

        _recentes = LireLesRecentes();
        RecentesList.ItemsSource = _recentes;

        if (_recentes.Count > 0) Poser(_recentes[0].WidthMm, _recentes[0].HeightMm);

        Loaded += (_, _) => LargeurBox.Focus();
    }

    /// <summary>Une tuile de format normalisé (A4, A3, A2…).</summary>
    private sealed record FormatNormalise(StandardSize Taille)
    {
        public string Libelle =>
            $"{Taille.Name}\n{Taille.WidthMm / 10:0.#} × {Taille.HeightMm / 10:0.#} cm".Replace('.', ',');
    }

    /// <summary>
    /// Les agrandissements du catalogue : ce sont eux qui donnent le prix. Les désactivés
    /// sont écartés — un format retiré de la vente ne doit pas continuer à tarifer.
    /// </summary>
    private static IReadOnlyList<Product> Agrandissements() =>
        App.Services.Catalog.Enabled
            .Where(p => p.Output == ProductOutput.ManualFile && p.Sheet is null)
            .ToList();

    private void Poser(double largeurMm, double hauteurMm)
    {
        LargeurBox.Text = (largeurMm / 10).ToString("0.##", CultureInfo.CurrentCulture);
        HauteurBox.Text = (hauteurMm / 10).ToString("0.##", CultureInfo.CurrentCulture);
    }

    private void OnFormatNormalise(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FormatNormalise format) return;
        _nomDemande = format.Taille.Name;
        Poser(format.Taille.WidthMm, format.Taille.HeightMm);
    }

    /// <summary>
    /// Nom du format normalisé retenu, s'il vient d'une tuile. « A2 » est plus parlant que
    /// « 42 × 59,4 cm » dans la liste des agrandissements et sur la commande.
    /// </summary>
    private string? _nomDemande;

    private void OnTailleRecente(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not CustomSize taille) return;
        _nomDemande = null;
        Poser(taille.WidthMm, taille.HeightMm);
    }

    private void OnTailleTapee(object sender, TextChangedEventArgs e) => Recalculer();

    private static double LireCm(string texte) =>
        double.TryParse(texte, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)
        || double.TryParse(texte, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            ? v
            : 0;

    private void Recalculer()
    {
        if (VerdictText is null) return;   // appelé pendant InitializeComponent

        var largeur = LireCm(LargeurBox.Text) * 10;
        var hauteur = LireCm(HauteurBox.Text) * 10;

        _retenu = null;
        ContinuerButton.IsEnabled = false;
        PrixText.Text = "";
        PaliersText.Text = "";

        if (largeur <= 0 || hauteur <= 0)
        {
            VerdictText.Text = "Donnez une largeur et une hauteur.";
            return;
        }

        var formats = Agrandissements();
        if (formats.Count == 0)
        {
            VerdictText.Text = "Aucun agrandissement n'est activé au catalogue : impossible d'en tarifer un.";
            return;
        }

        var papier = EnlargementSizes.PaperFor(largeur, hauteur, formats);
        if (papier is null)
        {
            var plusGrand = formats.Where(p => p.Price > 0).OrderByDescending(p => p.WidthMm * p.HeightMm).First();
            VerdictText.Text =
                $"{largeur / 10:0.#} × {hauteur / 10:0.#} cm dépasse tous les formats du catalogue : " +
                $"le plus grand est le {plusGrand.Name} ({plusGrand.WidthMm / 10:0.#} × {plusGrand.HeightMm / 10:0.#} cm).";
            return;
        }

        _retenu = (largeur, hauteur, papier, _nomDemande);
        ContinuerButton.IsEnabled = true;

        VerdictText.Text =
            $"{largeur / 10:0.#} × {hauteur / 10:0.#} cm tient dans un {papier.Name} " +
            $"({papier.WidthMm / 10:0.#} × {papier.HeightMm / 10:0.#} cm) — c'est ce format qui le tarife.";

        PrixText.Text = $"{papier.Price:0.00} €";

        var paliers = papier.PriceTiers.Where(t => t.FromQuantity > 1).ToList();
        PaliersText.Text = paliers.Count == 0
            ? "Tiré sur l'Epson : le fichier est préparé, l'impression se lance depuis « Agrandissements »."
            : string.Join("  ·  ", paliers.Select(t => $"à partir de {t.FromQuantity} : {t.UnitPrice:0.00} €"));
    }

    private void OnContinuer(object sender, RoutedEventArgs e)
    {
        if (_retenu is not { } choix) return;

        Product produit;
        try
        {
            produit = EnregistrerLeProduit(choix.LargeurMm, choix.HauteurMm, choix.Papier, choix.Nom);
        }
        catch (Exception ex)
        {
            FileLog.Write("Format d'agrandissement non enregistré", ex);
            MessageBox.Show(
                $"Le format n'a pas pu être ajouté au catalogue : {ex.Message}\n\nRien n'a été engagé.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Retenir(new CustomSize(choix.LargeurMm, choix.HauteurMm));

        if (_surValidation is not null)
        {
            // l'appelant a déjà ses photos : on revient sur son écran, pas sur un choix de
            // dossier qui les lui ferait rechercher
            Navigator.Back();
            _surValidation(produit);
            return;
        }

        // MÊME GESTE QU'AU CATALOGUE. Un agrandissement choisi dans la liste passe par le
        // choix de la feuille (PrintFormatView) ; celui-ci, saisi à la main, ne le faisait
        // pas — et c'est justement le cas où le montage rapporte le plus, puisqu'un format
        // libre tombe rarement pile sur une feuille. Signalé depuis la boutique le
        // 13/08/2026. L'écran ne s'affiche que si un montage est possible, donc un format
        // qui ne tient pas deux fois garde exactement le parcours d'avant.
        MontageFeuilleView.Proposer(produit, feuille =>
            Navigator.Go(new SourcePickerView((root, profond) =>
                Navigator.Go(
                    new PhotoGridView(root, produit.Code, avecSousDossiers: profond,
                        montageFeuille: feuille),
                    $"{produit.Name} — choisir les photos")),
                $"{produit.Name} — choisir le support"));
    }

    /// <summary>
    /// Ajoute le format au catalogue, ou retrouve celui qui y est déjà.
    ///
    /// Le code est déterministe (<c>agr-297x420</c>) : redemander deux fois le même format
    /// retombe sur le même produit au lieu d'en semer un par commande. Un produit existant
    /// n'est PAS retarifé — son prix a pu être ajusté à la main, et une commande passée ne
    /// doit pas changer de montant parce qu'on refait le même format.
    /// </summary>
    private static Product EnregistrerLeProduit(double largeurMm, double hauteurMm, Product papier, string? nom)
    {
        var code = EnlargementSizes.CodeFor(largeurMm, hauteurMm);

        if (App.Services.Catalog.Find(code) is { } existant)
        {
            if (existant.Enabled) return existant;

            // réactivé plutôt que dupliqué : l'opérateur l'avait retiré de la vente
            existant.Enabled = true;
            ProductCatalog.Save(App.Services.ProductsJson, App.Services.Catalog.All);
            App.Services.ReloadCatalog();
            return App.Services.Catalog.Require(code);
        }

        var produit = EnlargementSizes.Create(largeurMm, hauteurMm, papier,
            nom is null ? null : $"{nom} ({largeurMm / 10:0.#} × {hauteurMm / 10:0.#} cm)");

        ProductCatalog.Save(App.Services.ProductsJson, App.Services.Catalog.All.Append(produit));
        App.Services.ReloadCatalog();
        FileLog.Write($"Format d'agrandissement ajouté au catalogue : {produit.Code} " +
                      $"« {produit.Name} » à {produit.Price:0.00} € (tarifé comme {papier.Name})");

        return App.Services.Catalog.Require(code);
    }

    // — mémoire des tailles demandées —

    private static string FichierRecentes =>
        Path.Combine(App.Services.DataRoot, "config", "tailles-agrandissement.json");

    private static List<CustomSize> LireLesRecentes()
    {
        try
        {
            var chemin = FichierRecentes;
            if (!File.Exists(chemin)) return [];

            return JsonSerializer.Deserialize<List<CustomSize>>(
                       File.ReadAllText(chemin), ProductCatalog.JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            // liste d'agrément : illisible, on repart de zéro plutôt que de bloquer l'écran
            FileLog.Write("Tailles d'agrandissement récentes illisibles", ex);
            return [];
        }
    }

    private void Retenir(CustomSize taille)
    {
        _recentes.RemoveAll(t => Math.Abs(t.WidthMm - taille.WidthMm) < 0.05
                                 && Math.Abs(t.HeightMm - taille.HeightMm) < 0.05);
        _recentes.Insert(0, taille);
        if (_recentes.Count > MaximumRecentes)
            _recentes.RemoveRange(MaximumRecentes, _recentes.Count - MaximumRecentes);

        try
        {
            File.WriteAllText(FichierRecentes,
                JsonSerializer.Serialize(_recentes, ProductCatalog.JsonOptions));
        }
        catch (IOException ex)
        {
            FileLog.Write("Tailles d'agrandissement récentes non enregistrées", ex);
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) =>
        AccueilStudio.Rentrer();
}
