using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.App.Views;

/// <summary>Une taille demandée par l'opérateur, en millimètres.</summary>
/// <param name="PaperCode">
/// Papier imposé par l'opérateur, ou null pour le choix automatique. Il l'emporte sur le
/// calcul : c'est l'opérateur qui sait quel rouleau est chargé et ce qu'il veut vendre.
/// </param>
public sealed record CustomSize(double WidthMm, double HeightMm, string? PaperCode = null)
{
    /// <summary>Libellé en centimètres, l'unité du comptoir.</summary>
    public string Libelle =>
        $"{WidthMm / 10:0.##} × {HeightMm / 10:0.##} cm".Replace('.', ',');
}

/// <summary>
/// Saisie d'un format qui n'est pas au catalogue : « je veux ces photos en 5,5 × 8 cm ».
///
/// <b>Ce que l'écran doit dire AVANT de choisir les photos.</b> Sur quel papier ça sortira,
/// et combien de photos par planche. C'est ce qui permet d'annoncer un prix au client sans
/// avoir rien engagé — et de voir tout de suite qu'une taille passe mal (une photo par
/// planche, tout le reste en chute).
///
/// Le papier n'est pas décidé ici mais à la validation de la sélection : il dépend de la
/// QUANTITÉ, que l'opérateur n'a pas encore réglée. L'aperçu montre donc plusieurs
/// quantités courantes.
/// </summary>
public partial class CustomSizeView : UserControl
{
    /// <summary>Quantités montrées dans l'aperçu : celles qu'on voit passer au comptoir.</summary>
    private static readonly int[] QuantitesTypes = [1, 5, 10, 20];

    private const int MaximumRecentes = 5;

    private readonly List<CustomSize> _recentes;
    private readonly Action<CustomSize>? _surValidation;
    private CustomSize? _taille;

    /// <param name="surValidation">
    /// Ce qu'on fait de la taille retenue. Null = le parcours habituel, on enchaîne sur le
    /// choix du dossier. Fourni, l'écran ne sert que de saisie et rend la main à l'appelant —
    /// c'est ainsi qu'on bascule en taille libre des photos DÉJÀ ouvertes, sans les perdre.
    /// </param>
    public CustomSizeView(Action<CustomSize>? surValidation = null)
    {
        _surValidation = surValidation;
        InitializeComponent();

        _recentes = LireLesRecentes();
        RecentesList.ItemsSource = _recentes;

        // la dernière taille demandée est presque toujours la bonne
        if (_recentes.Count > 0) Poser(_recentes[0]);

        Loaded += (_, _) => LargeurBox.Focus();
    }

    /// <summary>
    /// Les papiers sur lesquels une planche peut sortir : les tirages du minilab, sans
    /// planche identité ni marge blanche imposée — cette dernière rognerait les cases.
    /// </summary>
    internal static IReadOnlyList<PaperOption> PapiersDisponibles() =>
        App.Services.Catalog.Enabled
            .Where(p => p.Output == ProductOutput.FujiMinilab)
            .Where(p => p.Sheet is null && p.BorderMm <= 0)
            // le prix suit le papier : c'est lui, et non la surface, qui décide du format
            // retenu — deux 10×15 à 0,60 € coûtent moins qu'un 13×18 à 1,50 €
            .Select(p => new PaperOption(p.Code, p.Name, p.WidthMm, p.HeightMm, p.Dpi,
                p.Price, p.PriceTiers))
            .ToList();

    private void Poser(CustomSize taille)
    {
        LargeurBox.Text = (taille.WidthMm / 10).ToString("0.##", CultureInfo.CurrentCulture);
        HauteurBox.Text = (taille.HeightMm / 10).ToString("0.##", CultureInfo.CurrentCulture);
    }

    private static double LireCm(string texte) =>
        double.TryParse(texte, NumberStyles.Float, CultureInfo.CurrentCulture, out var v)
        || double.TryParse(texte, NumberStyles.Float, CultureInfo.InvariantCulture, out v)
            ? v
            : 0;

    private void OnTailleTapee(object sender, TextChangedEventArgs e) => Recalculer();

    private void OnTailleRecente(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is CustomSize taille) Poser(taille);
    }

    private void Recalculer()
    {
        // l'écran s'appelle avant que les champs existent, pendant InitializeComponent
        if (VerdictText is null) return;

        var largeur = LireCm(LargeurBox.Text) * 10;
        var hauteur = LireCm(HauteurBox.Text) * 10;

        _taille = null;
        ApercuList.ItemsSource = null;
        ContinuerButton.IsEnabled = false;

        if (largeur <= 0 || hauteur <= 0)
        {
            VerdictText.Text = "Donnez une largeur et une hauteur.";
            return;
        }

        var papiers = PapiersDisponibles();
        if (papiers.Count == 0)
        {
            VerdictText.Text = "Aucun papier du catalogue ne peut porter une planche. " +
                               "Vérifiez que les tirages du minilab sont activés.";
            return;
        }

        var plan = CustomSheetLayout.Choose(1, largeur, hauteur, papiers);
        if (plan is null)
        {
            var plusGrand = papiers.OrderByDescending(p => p.AreaMm2).First();
            VerdictText.Text =
                $"Une photo de {largeur / 10:0.##} × {hauteur / 10:0.##} cm ne tient sur aucun papier : " +
                $"le plus grand est le {plusGrand.Name} ({plusGrand.WidthMm:0} × {plusGrand.HeightMm:0} mm). " +
                "Pour cette taille, passez par les agrandissements.";
            return;
        }

        _taille = new CustomSize(largeur, hauteur);
        ContinuerButton.IsEnabled = true;

        VerdictText.Text = plan.PerSheet == 1
            ? $"Une photo par planche {plan.Paper.Name} : à cette taille, il n'y a pas de place gagnée."
            : $"{plan.PerSheet} photos par planche {plan.Paper.Name}" +
              (plan.CellRotated ? " (photos posées en travers)." : ".") +
              "\nLe papier se choisit à l'écran suivant : ici, c'est le moins cher qui est montré.";

        ApercuList.ItemsSource = QuantitesTypes
            .Select(q => new ApercuRow(q, largeur, hauteur, papiers))
            .ToList();
    }

    /// <summary>Une ligne d'aperçu : ce que donnerait telle quantité.</summary>
    private sealed record ApercuRow
    {
        public ApercuRow(int quantite, double largeurMm, double hauteurMm,
            IReadOnlyList<PaperOption> papiers)
        {
            Quantite = $"{quantite} photo{(quantite > 1 ? "s" : "")}";

            var plan = CustomSheetLayout.Choose(quantite, largeurMm, hauteurMm, papiers);
            if (plan is null)
            {
                Resultat = "ne tient sur aucun papier";
                return;
            }

            var perdues = plan.WastedCells(quantite);
            Resultat =
                $"{plan.Sheets} planche{(plan.Sheets > 1 ? "s" : "")} {plan.Paper.Name}" +
                $" ({plan.PerSheet} par planche" +
                (perdues > 0 ? $", {perdues} place{(perdues > 1 ? "s" : "")} perdue{(perdues > 1 ? "s" : "")}" : "") +
                $")   —   {plan.Paper.TotalPrice(plan.Sheets):0.00} €";
        }

        public string Quantite { get; }
        public string Resultat { get; }
    }

    private void OnContinuer(object sender, RoutedEventArgs e)
    {
        if (_taille is not { } taille) return;

        Retenir(taille);

        if (_surValidation is not null)
        {
            // l'appelant a déjà ses photos : on revient sur son écran, pas sur un choix de
            // dossier qui les lui ferait rechercher
            Navigator.Back();
            _surValidation(taille);
            return;
        }

        Navigator.Go(new SourcePickerView((root, profond) =>
            Navigator.Go(new PhotoGridView(root, avecSousDossiers: profond, taillePerso: taille),
                $"{taille.Libelle} — choisir les photos")),
            $"{taille.Libelle} — choisir le support");
    }

    // — mémoire des tailles demandées —

    private static string FichierRecentes =>
        Path.Combine(App.Services.DataRoot, "config", "tailles-perso.json");

    private static List<CustomSize> LireLesRecentes()
    {
        try
        {
            var chemin = FichierRecentes;
            if (!File.Exists(chemin)) return new List<CustomSize>();

            return JsonSerializer.Deserialize<List<CustomSize>>(
                       File.ReadAllText(chemin), ProductCatalog.JsonOptions)
                   ?? new List<CustomSize>();
        }
        catch (Exception ex)
        {
            // liste d'agrément : illisible, on repart de zéro plutôt que de bloquer l'écran
            FileLog.Write("Tailles personnalisées récentes illisibles", ex);
            return new List<CustomSize>();
        }
    }

    private void Retenir(CustomSize taille)
    {
        _recentes.RemoveAll(t => Math.Abs(t.WidthMm - taille.WidthMm) < 0.05
                                 && Math.Abs(t.HeightMm - taille.HeightMm) < 0.05);
        _recentes.Insert(0, taille);
        if (_recentes.Count > MaximumRecentes) _recentes.RemoveRange(MaximumRecentes,
            _recentes.Count - MaximumRecentes);

        try
        {
            File.WriteAllText(FichierRecentes,
                JsonSerializer.Serialize(_recentes, ProductCatalog.JsonOptions));
        }
        catch (IOException ex)
        {
            FileLog.Write("Tailles personnalisées récentes non enregistrées", ex);
        }
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) =>
        Navigator.Home(new HomeView(), "Studio Photo");
}
