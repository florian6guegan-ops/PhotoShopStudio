using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // DiLand fermé, les bornes ne peuvent plus déposer : c'est LUI qui écoute le
        // réseau. Studio continue de servir ce qui est déjà sur le disque, mais l'opérateur
        // doit savoir que rien de neuf n'arrivera — sinon il attendra une commande qui ne
        // viendra jamais.
        if (!Studio.Printing.Devices.Dnp.DiLandPresence.IsRunning())
            StatusText.Text +=
                "\n\n⚠ DiLand est fermé. Les commandes déjà déposées restent lisibles, mais " +
                "les bornes ne peuvent PLUS en envoyer : c'est DiLand qui écoute le réseau.";
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
        if ((sender as FrameworkElement)?.Tag is Row ligne) Ouvrir(ligne, taille: null);
    }

    /// <summary>
    /// Ouvre la commande dans une taille qui n'est pas au catalogue.
    ///
    /// Les bornes ne proposent que des formats standard : un client qui veut du 5,5 × 8 cm
    /// commande donc du 10×15 et le dit au comptoir. On demande la taille, puis on ouvre ses
    /// photos directement dedans, sans passer par le format commandé.
    /// </summary>
    private void OnOpenCustom(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row ligne) return;

        Navigator.Go(new CustomSizeView(taille => Ouvrir(ligne, taille)), "Taille personnalisée");
    }

    private void Ouvrir(Row ligne, CustomSize? taille)
    {
        var importateur = App.Services.DiLandImport;

        try
        {
            // les photos sont rangées chez NOUS, pour trente jours : l'écran travaille sur
            // notre copie et non sur les dossiers de DiLand, qu'il purge quand il veut
            var prete = importateur.Archiver(ligne.Order);

            if (prete.PhotoCount == 0)
            {
                MessageBox.Show("Aucune photo n'a pu être récupérée pour cette commande.",
                    "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            importateur.MarkInProgress(ligne.Order);

            // en taille libre, le format commandé n'a plus cours : c'est la taille saisie
            // qui décide, et le papier sera choisi d'après la quantité
            Navigator.Go(
                new PhotoGridView(prete.PhotosDirectory,
                    taille is null ? prete.ProductCode : null,
                    ligne.Order.Oid,
                    taillePerso: taille),
                taille is null
                    ? $"Borne #{ligne.Order.Number} — {prete.PhotoCount} photo(s)"
                    : $"Borne #{ligne.Order.Number} — {taille.Libelle} — {prete.PhotoCount} photo(s)");
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

    // ----- retourner aux photos d'une commande close -----

    /// <summary>
    /// Le dossier où Studio garde les photos de cette commande close, ou null en le disant.
    ///
    /// <b>On ne redescend pas chez DiLand.</b> Studio archive les photos à la prise en
    /// charge et les garde trente jours, le temps que l'historique les montre. C'est ce qui
    /// permet de reservir un client trois semaines plus tard, DiLand fermé, purgé, ou
    /// réinstallé.
    ///
    /// Le rattrapage par DiLand ne concerne que les entrées d'AVANT l'archivage ; il
    /// disparaîtra de lui-même quand elles auront un mois.
    /// </summary>
    private static string? DossierDesPhotos(HistoryRow ligne)
    {
        var importateur = App.Services.DiLandImport;

        if (importateur.ArchiveDe(ligne.Entry) is { } archive) return archive;
        if (importateur.ArchiverDepuisDiLand(ligne.Entry) is { } rattrapee) return rattrapee;

        MessageBox.Show(
            $"Les photos de la commande #{ligne.Entry.Number} ne sont plus disponibles.\n\n" +
            "Studio garde les photos des commandes de bornes pendant " +
            $"{KioskOrderJournal.Retention.TotalDays:0} jours ; cette commande est plus " +
            "ancienne, ou date d'avant la mise en place de cette conservation.\n\n" +
            "L'historique en garde le contenu et le prix.",
            "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>
    /// Recopie les photos dans les téléchargements, <b>même si elles l'ont déjà été</b> :
    /// c'est tout l'objet du bouton. Le dossier est refait plutôt que réutilisé, sans quoi
    /// on rouvrirait une copie périmée sans rien dire.
    /// </summary>
    private void OnHistoryDownload(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow ligne) return;
        if (DossierDesPhotos(ligne) is not { } source) return;

        var telechargements = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        if (!Directory.Exists(telechargements))
            telechargements = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        var quand = ligne.Entry.OrderedAt == default ? DateTime.Now : ligne.Entry.OrderedAt;
        var destination = Path.Combine(
            telechargements, $"Borne-{ligne.Entry.Number}-{quand:yyyy-MM-dd-HHmm}");

        try
        {
            Mouse.OverrideCursor = Cursors.Wait;

            Directory.CreateDirectory(destination);
            var combien = 0;
            foreach (var fichier in Directory.EnumerateFiles(source))
            {
                File.Copy(fichier, Path.Combine(destination, Path.GetFileName(fichier)),
                    overwrite: true);
                combien++;
            }

            Mouse.OverrideCursor = null;

            if (combien == 0)
            {
                MessageBox.Show("Aucune photo n'a pu être récupérée pour cette commande.",
                    "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // on ouvre le dossier : sans cela l'opérateur doit aller le chercher, et rien
            // à l'écran ne lui dit où il est
            Process.Start(new ProcessStartInfo(destination) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Mouse.OverrideCursor = null;
            FileLog.Write("Historique des bornes : téléchargement impossible", ex);
            MessageBox.Show($"Téléchargement impossible : {ex.Message}",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Rouvre les photos d'une commande close pour les retoucher et les retirer.
    ///
    /// La commande NE REVIENT PAS dans la liste du jour : elle a été servie, et la revoir
    /// le lendemain matin ferait croire à un tirage en retard. <c>MarkInProgress</c> refuse
    /// déjà de rouvrir une entrée close — on s'appuie dessus plutôt que d'ajouter une
    /// seconde règle qui pourrait la contredire.
    /// </summary>
    private void OnHistoryModify(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not HistoryRow ligne) return;
        if (DossierDesPhotos(ligne) is not { } source) return;

        var combien = Directory.EnumerateFiles(source).Count();
        if (combien == 0)
        {
            MessageBox.Show("Aucune photo n'a pu être récupérée pour cette commande.",
                "Commandes des bornes", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Navigator.Go(
            new PhotoGridView(source, produitParDefaut: null, ligne.Entry.Oid),
            $"Borne #{ligne.Entry.Number} (historique) — {combien} photo(s)");
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

        var message = $"{reprises} commande(s) reprise(s) dans Studio.\n\n" +
                      "Elles restent affichées ici jusqu'à ce que le tirage soit sorti.";
        if (avertissements.Count > 0)
            message += "\n\nÀ vérifier :\n" + string.Join("\n", avertissements.Take(12));

        MessageBox.Show(message, "Commandes des bornes",
            MessageBoxButton.OK,
            avertissements.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
