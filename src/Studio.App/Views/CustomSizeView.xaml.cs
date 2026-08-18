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
/// <param name="BorderMm">
/// Marge blanche à l'intérieur du tirage, en millimètres — la famille « cadre blanc » en
/// taille libre. Zéro = la photo remplit sa case.
///
/// <b>La taille demandée reste celle du TIRAGE FINI</b>, marge comprise : un 10 × 15 à bord
/// blanc occupe toujours 10 × 15 sur la planche, la photo se posant en retrait. C'est la
/// même convention que les produits à bord blanc du catalogue, où <c>Product.WidthMm</c>
/// donne le papier et <c>ImageArea</c> en retire deux fois la marge.
/// </param>
/// <param name="ContourNoir">
/// Trace un trait noir de 0,2 mm sur le bord de chaque photo, à suivre aux ciseaux.
///
/// <b>Il se décide ICI et non photo par photo.</b> Un format libre sort presque toujours en
/// planche, plusieurs tirages sur une même feuille, et c'est bien le trait qui dit où
/// couper : le cocher ensuite dans « Modifier », photo par photo, était le geste que
/// l'opérateur oubliait — et une planche sans trait se coupe à l'œil. Demandé le
/// 18/08/2026, avec la compensation d'impression.
///
/// La case n'existait qu'à l'écran d'édition, où elle porte sur la SÉLECTION visée : celle
/// d'ici pose la valeur de départ des photos ouvertes dans ce format, et l'édition reste
/// libre de la changer photo par photo.
/// </param>
public sealed record CustomSize(
    double WidthMm, double HeightMm, string? PaperCode = null, double BorderMm = 0,
    bool ContourNoir = false)
{
    /// <summary>Libellé en centimètres, l'unité du comptoir.</summary>
    public string Libelle =>
        $"{WidthMm / 10:0.##} × {HeightMm / 10:0.##} cm".Replace('.', ',')
        + (BorderMm > 0 ? " à bord blanc" : "");
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
    private readonly double _bordMm;
    private CustomSize? _taille;

    /// <param name="surValidation">
    /// Ce qu'on fait de la taille retenue. Null = le parcours habituel, on enchaîne sur le
    /// choix du dossier. Fourni, l'écran ne sert que de saisie et rend la main à l'appelant —
    /// c'est ainsi qu'on bascule en taille libre des photos DÉJÀ ouvertes, sans les perdre.
    /// </param>
    /// <param name="bordMm">
    /// Marge blanche du tirage. Zéro pour l'impression rapide ; la marge de la famille pour
    /// le cadre blanc, qui n'avait pas de « Personnalisé » du tout jusqu'au 13/08/2026 —
    /// l'opérateur devait se contenter des formats du catalogue.
    /// </param>
    public CustomSizeView(Action<CustomSize>? surValidation = null, double bordMm = 0)
    {
        _surValidation = surValidation;
        _bordMm = bordMm;
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

    /// <summary>
    /// La case du contour passe par le même recalcul que la taille : c'est <c>_taille</c>
    /// qui part à l'écran suivant, et elle n'est reconstruite que là. Sans ce rappel, cocher
    /// après avoir tapé les centimètres ne changeait rien — le défaut le plus discret qui
    /// soit, puisque la case reste cochée à l'écran.
    /// </summary>
    private void OnContourNoirChange(object sender, RoutedEventArgs e) => Recalculer();

    // ----- le sens de la photo -----

    private void OnPortrait(object sender, RoutedEventArgs e) => ImposerLeSens(debout: true);

    private void OnPaysage(object sender, RoutedEventArgs e) => ImposerLeSens(debout: false);

    /// <summary>
    /// Range la largeur et la hauteur dans le sens demandé.
    ///
    /// <b>Le sens se décidait par le seul ORDRE des deux nombres</b>, et rien ne le disait.
    /// « 8 × 6,5 » donne des photos couchées, « 6,5 × 8 » des photos debout : celui qui
    /// pense « du 8 sur 6,5 » sans y voir un sens obtient un cadrage coupé en travers.
    /// Arrivé à Créteil le 14/08/2026, commande 14-018 — deux portraits repris couchés, et
    /// rien à l'écran ne permettait de s'en apercevoir avant le papier.
    ///
    /// Les deux nombres ne sont pas retapés : on les remet dans l'ordre.
    /// </summary>
    private void ImposerLeSens(bool debout)
    {
        var largeur = LireCm(LargeurBox.Text);
        var hauteur = LireCm(HauteurBox.Text);
        if (largeur <= 0 || hauteur <= 0) return;

        var petit = Math.Min(largeur, hauteur);
        var grand = Math.Max(largeur, hauteur);

        var (voulueL, voulueH) = debout ? (petit, grand) : (grand, petit);

        // Un seul des deux champs change le plus souvent : les réécrire tous les deux
        // déclencherait deux recalculs, dont un sur une taille intermédiaire absurde.
        var texteL = EcrireCm(voulueL);
        var texteH = EcrireCm(voulueH);

        if (LargeurBox.Text != texteL) LargeurBox.Text = texteL;
        if (HauteurBox.Text != texteH) HauteurBox.Text = texteH;
    }

    private static string EcrireCm(double cm) => cm.ToString("0.##", CultureInfo.CurrentCulture);

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

        // Une marge qui mange tout le tirage ne rendrait qu'un rectangle blanc, et le
        // calcul de la zone d'image passerait en négatif sans que rien ne proteste.
        if (_bordMm > 0 && Math.Min(largeur, hauteur) <= 2 * _bordMm)
        {
            VerdictText.Text =
                $"Un bord blanc de {_bordMm:0.#} mm ne laisse aucune image sur un tirage de " +
                $"{largeur / 10:0.##} × {hauteur / 10:0.##} cm. Demandez un format plus grand.";
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

        _taille = new CustomSize(largeur, hauteur, BorderMm: _bordMm,
            ContourNoir: ContourNoirCheck.IsChecked == true);
        ContinuerButton.IsEnabled = true;

        // LE SENS SUIT LES CADRAGES, ET L'ÉCRAN LE DIT.
        //
        // Depuis la 1.5.16, ce n'est plus l'ordre des deux nombres qui décide du sens des
        // cases mais le CADRAGE posé sur chaque photo (voir PrintOrchestrator.SensDesCadrages).
        // L'opérateur n'a donc rien à choisir ici — mais cet écran annonce un nombre de
        // photos par planche, donc un PRIX, et il l'annonçait d'après le sens saisi : faux
        // dès que l'autre sens n'a pas le même rendement.
        //
        // On regarde donc les DEUX sens. Même rendement : on rassure, il n'y a rien à faire.
        // Rendements différents : on donne les deux, puisqu'on ne saura qu'au cadrage.
        var autre = CustomSheetLayout.Choose(1, hauteur, largeur, papiers);
        var memeRendement = autre is null || autre.PerSheet == plan.PerSheet;

        var motDuSens = memeRendement
            ? "\nLe sens des photos suivra vos cadrages — vous n'avez pas à le choisir ici."
            : $"\n⚠ Le sens des photos suivra vos cadrages, et le rendement en dépend : " +
              $"{plan.PerSheet} par planche en {(largeur > hauteur ? "couché" : "debout")}, " +
              $"{autre!.PerSheet} en {(largeur > hauteur ? "debout" : "couché")}. " +
              "Annoncez le prix une fois les photos cadrées.";

        VerdictText.Text = (plan.PerSheet == 1
            ? $"Une photo par planche {plan.Paper.Name} : à cette taille, il n'y a pas de place gagnée."
            : $"{plan.PerSheet} photos par planche {plan.Paper.Name}." +
              "\nLe papier se choisit à l'écran suivant : ici, c'est le moins cher qui est montré.")
            + motDuSens
            // La marge se DIT, parce qu'elle mange l'image : sans cette ligne, l'opérateur
            // annonce un 9 × 13 et le client reçoit une photo de 8 × 12 dans du blanc.
            + (_bordMm > 0
                ? $"\nBord blanc de {_bordMm:0.#} mm : l'image occupe " +
                  $"{(largeur - 2 * _bordMm) / 10:0.##} × {(hauteur - 2 * _bordMm) / 10:0.##} cm."
                : "");

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

        // La marge n'est PAS retenue avec la taille : elle appartient à la famille, pas au
        // format. Sans cela, un 9 × 13 demandé une fois en cadre blanc reviendrait bordé
        // en impression rapide, où l'écran ne montre même pas la marge.
        // Le CONTOUR non plus : il tient à la façon de couper du jour — ciseaux ou massicot
        // sur repères —, pas au format. Une taille reproposée le porterait sans que la case
        // le montre, puisqu'elle repart décochée.
        _recentes.Insert(0, taille with { BorderMm = 0, ContourNoir = false });
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
        AccueilStudio.Rentrer();
}
