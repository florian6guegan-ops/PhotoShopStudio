using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Store.DiLand;

namespace Studio.App.Views;

/// <summary>
/// Les commandes que les bornes ont déposées dans DiLand, et qui n'ont pas encore été
/// reprises dans Studio.
///
/// Reprendre ne prive DiLand de rien : on lit une copie de sa base, ses photos restent en
/// place, et il peut tirer la commande de son côté comme si de rien n'était.
/// </summary>
public partial class KioskOrdersView : UserControl
{
    /// <summary>Une commande en attente, telle qu'affichée dans la liste.</summary>
    private sealed record Row(DiLandOrder Order, string Header, string Detail);

    public KioskOrdersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var importateur = App.Services.DiLandImport;
        var attente = importateur.Pending();

        // le détail vient des lignes : sans le produit et le nombre de tirages,
        // l'opérateur ne sait pas ce qu'il reprend
        OrdersList.ItemsSource = attente.Select(commande => new Row(
            commande,
            $"#{commande.Number} — {commande.Date:HH:mm}",
            Describe(importateur, commande))).ToList();

        StatusText.Text = attente.Count == 0
            ? "Aucune commande de borne en attente. Les commandes du comptoir ne sont pas reprises : elles sont déjà saisies ici."
            : $"{attente.Count} commande(s) de borne à reprendre. DiLand garde les siennes.";
    }

    private static string Describe(DiLandImporter importateur, DiLandOrder commande)
    {
        var lignes = importateur.Describe(commande);
        return lignes.Count == 0 ? "commande vide" : string.Join("   ·   ", lignes);
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnImport(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row ligne) return;

        Import([ligne.Order]);
    }

    private void OnImportAll(object sender, RoutedEventArgs e)
    {
        if (OrdersList.ItemsSource is not IEnumerable<Row> lignes) return;

        Import(lignes.Select(l => l.Order).ToList());
    }

    private void Import(IReadOnlyList<DiLandOrder> commandes)
    {
        if (commandes.Count == 0) return;

        var reprises = 0;
        var avertissements = new List<string>();

        foreach (var commande in commandes)
        {
            var resultat = App.Services.DiLandImport.Import(commande);
            if (resultat.Succeeded) reprises++;

            foreach (var avertissement in resultat.Warnings)
                avertissements.Add($"#{commande.Number} : {avertissement}");
        }

        Refresh();

        var message = $"{reprises} commande(s) reprise(s) dans Studio.";
        if (avertissements.Count > 0)
            message += "\n\nÀ vérifier :\n" + string.Join("\n", avertissements.Take(12));

        MessageBox.Show(message, "Commandes des bornes",
            MessageBoxButton.OK,
            avertissements.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
