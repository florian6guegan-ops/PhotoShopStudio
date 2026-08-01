using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Store.DiLand;

namespace Studio.App.Views;

/// <summary>
/// Les commandes que les bornes ont déposées dans DiLand.
///
/// Présentation reprise de l'écran « Ordres » de DiLand : les opérateurs la connaissent —
/// numéro en orange, heure, client, et à droite ce qu'il y a à tirer.
///
/// Une commande RESTE affichée tant que le tirage n'est pas sorti, même si elle a été
/// ouverte ou reprise : c'est le tirage, et lui seul, qui la fait basculer dans l'onglet
/// Historique, où elle se conserve un mois.
///
/// Reprendre ne prive DiLand de rien : on lit une copie de sa base, ses photos restent en
/// place, et il peut tirer la commande de son côté comme si de rien n'était.
/// </summary>
public partial class KioskOrdersView : UserControl
{
    /// <summary>Une commande à traiter, telle qu'affichée dans l'onglet Ordres.</summary>
    private sealed record Row(
        DiLandOrder Order,
        string Number,
        string When,
        string Total,
        string Customer,
        Visibility CustomerVisibility,
        IReadOnlyList<string> Products,
        string StateGlyph,
        string StateLabel,
        Visibility StateVisibility,
        Brush StateBrush,
        bool CanImport);

    /// <summary>Une commande close, telle qu'affichée dans l'onglet Historique.</summary>
    private sealed record HistoryRow(
        KioskOrderEntry Entry,
        string Number,
        string When,
        string Total,
        string Customer,
        string Summary,
        string StateLabel,
        Brush StateBrush);

    public KioskOrdersView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private static Brush Brosse(string cle) => (Brush)Application.Current.Resources[cle];

    private bool HistoriqueAffiche => OngletHistorique.IsChecked == true;

    private void Refresh()
    {
        if (HistoriqueAffiche) RefreshHistory();
        else RefreshOrders();

        OrdersScroll.Visibility = HistoriqueAffiche ? Visibility.Collapsed : Visibility.Visible;
        HistoryScroll.Visibility = HistoriqueAffiche ? Visibility.Visible : Visibility.Collapsed;
        ActionsOrdres.Visibility = HistoriqueAffiche ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshOrders()
    {
        var importateur = App.Services.DiLandImport;
        var commandes = importateur.Pending();

        OrdersList.ItemsSource = commandes.Select(commande =>
        {
            var suivi = importateur.Journal.Find(commande.Oid);
            var enCours = suivi?.Stage == KioskOrderStage.InProgress;
            var resume = importateur.Summarize(commande);

            return new Row(
                commande,
                commande.Number.ToString(),
                commande.Date.ToString("dd/MM/yyyy HH:mm:ss"),
                $"{resume.Total:0.00} €",
                commande.EndUserName ?? "",
                string.IsNullOrWhiteSpace(commande.EndUserName) ? Visibility.Collapsed : Visibility.Visible,
                resume.Lines,
                enCours ? "⏱" : "▶",
                enCours ? "EN COURS" : "",
                enCours ? Visibility.Visible : Visibility.Collapsed,
                Brosse(enCours ? "TitleBrush" : "AccentBrush"),
                // une commande déjà reprise a sa commande Studio : la reprendre encore
                // ferait un doublon de tirage
                suivi?.StudioOrderId is null);
        }).ToList();

        var enAttente = commandes.Count(c => importateur.StageOf(c) == KioskOrderStage.Waiting);

        StatusText.Text = commandes.Count == 0
            ? "Rien à tirer. Les commandes du comptoir ne sont pas reprises : elles sont déjà saisies ici."
            : $"{commandes.Count} commande(s) à traiter, dont {enAttente} pas encore ouverte(s). " +
              "Une commande reste ici tant que le tirage n'est pas sorti.";
    }

    private void RefreshHistory()
    {
        var historique = App.Services.DiLandImport.History();

        HistoryList.ItemsSource = historique.Select(entree =>
        {
            var tiree = entree.Stage == KioskOrderStage.Printed;
            var quand = entree.ClosedAt?.ToString("dd/MM à HH:mm") ?? "—";

            return new HistoryRow(
                entree,
                entree.Number > 0 ? entree.Number.ToString() : $"oid {entree.Oid}",
                entree.OrderedAt == default ? "" : entree.OrderedAt.ToString("dd/MM/yyyy HH:mm"),
                entree.Total > 0 ? $"{entree.Total:0.00} €" : "",
                entree.CustomerName,
                entree.Summary,
                tiree ? $"Imprimée le {quand}" : $"Retirée le {quand}",
                Brosse(tiree ? "OkBrush" : "MutedBrush"));
        }).ToList();

        StatusText.Text = historique.Count == 0
            ? "Aucune commande de borne close pour l'instant."
            : $"{historique.Count} commande(s) close(s). L'historique se conserve " +
              $"{KioskOrderJournal.Retention.TotalDays:0} jours, puis s'efface tout seul.";
    }

    /// <summary>
    /// Ouvre les photos de la commande pour les recadrer et les corriger avant de tirer.
    ///
    /// Rien n'est créé ici : la commande Studio naîtra à l'impression, comme pour une
    /// commande faite au comptoir. La commande de borne passe « en cours » et reste
    /// affichée — si l'opérateur abandonne en route, elle ne se perd pas.
    /// </summary>
    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row ligne) return;

        var importateur = App.Services.DiLandImport;
        var travail = Path.Combine(App.Services.DataRoot, "diland", "travail");

        try
        {
            var prete = importateur.Stage(ligne.Order, travail);

            if (prete.PhotoCount == 0)
            {
                MessageBox.Show("Aucune photo n'a pu être récupérée pour cette commande.",
                    "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            importateur.MarkInProgress(ligne.Order);

            Navigator.Go(
                new PhotoGridView(prete.PhotosDirectory, prete.ProductCode, ligne.Order.Oid),
                $"Borne #{ligne.Order.Number} — {prete.PhotoCount} photo(s)");
        }
        catch (Exception ex)
        {
            FileLog.Write("Commandes des bornes : ouverture impossible", ex);
            MessageBox.Show($"Ouverture impossible : {ex.Message}",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Refresh();
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

        Import(lignes.Where(l => l.CanImport).Select(l => l.Order).ToList());
    }

    /// <summary>
    /// Retire une commande de la liste sans la tirer — typiquement une commande que DiLand
    /// a déjà imprimée. Sans cette porte de sortie, elle resterait affichée pour toujours.
    /// </summary>
    private void OnDismiss(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row ligne) return;

        var reponse = MessageBox.Show(
            $"Retirer la commande #{ligne.Order.Number} sans la tirer ?\n\n" +
            "Elle passera dans l'historique. À utiliser si DiLand l'a déjà imprimée.",
            "Commandes des bornes", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (reponse != MessageBoxResult.Yes) return;

        App.Services.DiLandImport.Dismiss(ligne.Order);
        Refresh();
    }

    private void OnReopen(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow ligne) return;

        App.Services.DiLandImport.Journal.Reopen(ligne.Entry.Oid);
        OngletOrdres.IsChecked = true;
        Refresh();
    }

    private void Import(IReadOnlyList<DiLandOrder> commandes)
    {
        if (commandes.Count == 0) return;

        var reprises = 0;
        var avertissements = new List<string>();

        foreach (var commande in commandes)
        {
            var resultat = App.Services.DiLandImport.Import(
                commande, Path.Combine(App.Services.DataRoot, "diland", "travail"));
            if (resultat.Succeeded) reprises++;

            foreach (var avertissement in resultat.Warnings)
                avertissements.Add($"#{commande.Number} : {avertissement}");
        }

        Refresh();

        var message = $"{reprises} commande(s) reprise(s) dans Studio.\n\n" +
                      "Elles restent affichées ici jusqu'à ce que le tirage soit sorti.";
        if (avertissements.Count > 0)
            message += "\n\nÀ vérifier :\n" + string.Join("\n", avertissements.Take(12));

        MessageBox.Show(message, "Commandes des bornes",
            MessageBoxButton.OK,
            avertissements.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
