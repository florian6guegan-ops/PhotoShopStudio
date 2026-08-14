using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.App.Views;

/// <summary>
/// « Sur quelle feuille ces agrandissements sortent-ils ? »
///
/// <b>Ce que cet écran fait gagner.</b> Un agrandissement rendait un fichier par tirage :
/// deux 24×30 donnaient deux feuilles de 40×60, dont la moitié partait à la chute. Les deux
/// tiennent sur une seule. C'est une économie de papier pour la boutique — le client paie
/// toujours deux 24×30, et l'écran le dit.
///
/// <b>Pourquoi c'est l'opérateur qui choisit.</b> Lui seul sait quel rouleau est chargé, et
/// ce qu'il veut vendre. Le logiciel se contente d'annoncer ce que chaque feuille donnerait.
///
/// L'écran ne s'affiche que s'il y a quelque chose à proposer : sans feuille où le format
/// tienne au moins deux fois, on enchaîne directement sur le choix des photos, exactement
/// comme avant. Voir <see cref="Proposer"/>.
/// </summary>
public partial class MontageFeuilleView : UserControl
{
    private readonly Action<string?> _surChoix;

    /// <param name="format">Le format d'agrandissement demandé.</param>
    /// <param name="plans">Les feuilles candidates, la plus petite d'abord.</param>
    /// <param name="surChoix">
    /// Reçoit le code de la feuille retenue, ou null pour « une par feuille » — le
    /// comportement d'avant.
    /// </param>
    public MontageFeuilleView(Product format, IReadOnlyList<PlanMontage> plans,
        Action<string?> surChoix)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(plans);
        _surChoix = surChoix ?? throw new ArgumentNullException(nameof(surChoix));

        InitializeComponent();

        TitreText.Text = $"{format.Name} — montage";
        ExplicationText.Text =
            "Le prix ne change pas : ces tirages restent facturés à l'unité. " +
            "Seul le papier utilisé change — vous massicotez la feuille à la sortie.";

        var choix = new List<FeuilleRow> { FeuilleRow.UneParFeuille(format) };
        choix.AddRange(plans.Select(p => new FeuilleRow(p, format)));
        FeuillesList.ItemsSource = choix;
    }

    /// <summary>
    /// Propose le montage si quelque chose peut l'être, sinon enchaîne directement.
    ///
    /// ⚠ <b>C'est ici que se joue la non-régression.</b> Un format qui ne tient pas deux fois
    /// sur une feuille du catalogue — la plupart des grands formats — ne voit jamais cet
    /// écran, et son parcours est celui d'avant, à l'écran près.
    /// </summary>
    /// <param name="suite">Ce qu'on fait une fois la feuille connue (null = une par feuille).</param>
    public static void Proposer(Product format, Action<string?> suite)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(suite);

        var plans = MontageFeuille.Candidats(FeuillesDisponibles(format), format.WidthMm, format.HeightMm);
        if (plans.Count == 0)
        {
            suite(null);
            return;
        }

        Navigator.Go(new MontageFeuilleView(format, plans, suite), $"{format.Name} — montage");
    }

    /// <summary>
    /// Les feuilles sur lesquelles un montage peut sortir : les tirages GRAND FORMAT du
    /// catalogue, planches et marges blanches exclues — ces dernières rogneraient les cases.
    ///
    /// Le format lui-même en est retiré : se monter sur soi n'a pas de sens, et le proposer
    /// ferait douter.
    /// </summary>
    private static IReadOnlyList<PaperOption> FeuillesDisponibles(Product format) =>
        App.Services.Catalog.Enabled
            .Where(p => p.Output == ProductOutput.ManualFile)
            .Where(p => p.Sheet is null && p.BorderMm <= 0)
            .Where(p => !p.Code.Equals(format.Code, StringComparison.OrdinalIgnoreCase))
            // le prix ne sert à rien ici — c'est le tirage qui est facturé, pas la feuille
            .Select(p => new PaperOption(p.Code, p.Name, p.WidthMm, p.HeightMm, p.Dpi))
            .ToList();

    private void OnFeuilleChoisie(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not FeuilleRow ligne) return;

        // on quitte AVANT d'enchaîner : sans cela, « Précédent » ramènerait sur cet écran
        // depuis le choix des photos, et l'opérateur repasserait par le montage à chaque
        // aller-retour
        Navigator.Back();
        _surChoix(ligne.Code);
    }

    /// <param name="Plan">Null pour « une par feuille ».</param>
    private sealed record FeuilleRow(PlanMontage? Plan, Product? Format = null)
    {
        public static FeuilleRow UneParFeuille(Product format) => new(null, format);

        public string? Code => Plan?.Feuille.Code;

        public string Titre => Plan is null
            ? "Une par feuille (comme d'habitude)"
            : $"{Plan.Feuille.Name} — {Plan.ParFeuille} par feuille";

        public string Detail => Plan is null
            ? $"Un fichier par tirage, au format {Format!.Name}. Rien à massicoter."
            : $"{Plan.ParFeuille} tirages de {Format!.Name} composés sur une " +
              $"feuille {Plan.Feuille.WidthMm:0} × {Plan.Feuille.HeightMm:0} mm, " +
              "avec les traits de coupe." +
              (Plan.CelluleTournee
                  ? " Les tirages y sont posés en travers ; ils retrouvent leur sens une fois coupés."
                  : "");
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) =>
        AccueilStudio.Rentrer();
}
