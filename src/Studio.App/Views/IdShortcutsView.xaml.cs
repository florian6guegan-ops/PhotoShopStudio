using System.IO;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.App.Views;

/// <summary>
/// Réglage des raccourcis de l'écran « photo d'identité ».
///
/// Le référentiel compte 274 normes et le catalogue une trentaine de produits ; la
/// boutique en tire deux toute la journée. Cet écran décide lesquels passent devant —
/// ce qui se vend change avec la saison, et recompiler pour déplacer une tuile n'aurait
/// pas de sens.
/// </summary>
public partial class IdShortcutsView : UserControl
{
    private readonly List<IdShortcut> _raccourcis;
    private IReadOnlyList<IdDocumentSpec> _documents = [];

    public IdShortcutsView()
    {
        InitializeComponent();
        _raccourcis = IdShortcuts.Load(App.Services.CatalogDir).ToList();

        Loaded += (_, _) =>
        {
            ChargerDocuments();
            RemplirListesDAjout();
            Afficher();
        };
    }

    /// <summary>
    /// Charge le référentiel des documents. Sans lui, seuls les raccourcis « produit »
    /// restent réglables — on le dit plutôt que de présenter une liste vide sans raison.
    /// </summary>
    private void ChargerDocuments()
    {
        try
        {
            var chemin = Path.Combine(App.Services.CatalogDir, "diland-id-documents.json");
            if (!File.Exists(chemin) && TrouverDansLeDepot() is { } depot) chemin = depot;

            _documents = File.Exists(chemin) ? IdDocumentCatalog.Load(chemin) : [IdDocumentSpec.France];
        }
        catch (Exception ex)
        {
            FileLog.Write("Référentiel des documents d'identité illisible (raccourcis)", ex);
            _documents = [IdDocumentSpec.France];
            MessageText.Text = "Référentiel des documents illisible : seuls les produits sont proposés.";
        }
    }

    private static string? TrouverDansLeDepot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidat = Path.Combine(dir.FullName, "catalog", "diland-id-documents.json");
            if (File.Exists(candidat)) return candidat;
            dir = dir.Parent;
        }
        return null;
    }

    private void RemplirListesDAjout()
    {
        // la norme de la boutique en tête : c'est celle qu'on remet le plus souvent
        DocumentCombo.ItemsSource = IdDocumentCatalog.AvecNormeBoutique(_documents)
            .Select(d => new ChoixDocument(d))
            .ToList();

        ProduitCombo.ItemsSource = App.Services.Catalog.Enabled
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new ChoixProduit(p))
            .ToList();

        if (DocumentCombo.Items.Count > 0) DocumentCombo.SelectedIndex = 0;
        if (ProduitCombo.Items.Count > 0) ProduitCombo.SelectedIndex = 0;
    }

    private void Afficher() =>
        RaccourcisList.ItemsSource = _raccourcis.Select(Decrire).ToList();

    /// <summary>
    /// Décrit un raccourci pour l'affichage, en résolvant sa cible. Une cible disparue est
    /// signalée sans faire échouer l'écran : c'est justement ici qu'on vient la retirer.
    /// </summary>
    private Ligne Decrire(IdShortcut raccourci)
    {
        if (raccourci.Kind == IdShortcutKind.Produit)
        {
            var produit = App.Services.Catalog.Find(raccourci.Cle);
            return new Ligne(raccourci,
                produit is null
                    ? $"Produit « {raccourci.Cle} » introuvable au catalogue"
                    : $"Produit — {produit.WidthMm:0.#} × {produit.HeightMm:0.#} mm, {produit.Price:0.00} €");
        }

        var spec = IdDocumentCatalog.FindByKey(_documents, raccourci.Cle);

        return new Ligne(raccourci,
            spec is null
                ? $"Document « {raccourci.Cle} » absent du référentiel"
                : $"Document — {spec.Country}, {spec.Document} ({spec.WidthMm:0.#} × {spec.HeightMm:0.#} mm)");
    }

    private void OnAjouterDocument(object sender, RoutedEventArgs e)
    {
        if (DocumentCombo.SelectedItem is not ChoixDocument choix) return;

        var cle = IdShortcuts.DocumentKey(choix.Spec.Country, choix.Spec.Document);
        if (Existe(IdShortcutKind.Document, cle)) return;

        _raccourcis.Add(new IdShortcut(IdShortcutKind.Document, cle, choix.Spec.Country));
        Afficher();
    }

    private void OnAjouterProduit(object sender, RoutedEventArgs e)
    {
        if (ProduitCombo.SelectedItem is not ChoixProduit choix) return;
        if (Existe(IdShortcutKind.Produit, choix.Produit.Code)) return;

        _raccourcis.Add(new IdShortcut(IdShortcutKind.Produit, choix.Produit.Code, choix.Produit.Name));
        Afficher();
    }

    /// <summary>Un même format deux fois ne ferait que deux tuiles identiques.</summary>
    private bool Existe(IdShortcutKind genre, string cle)
    {
        var deja = _raccourcis.Any(r =>
            r.Kind == genre && r.Cle.Equals(cle, StringComparison.OrdinalIgnoreCase));

        if (deja) MessageText.Text = "Ce format est déjà dans les raccourcis.";
        return deja;
    }

    private void OnMonter(object sender, RoutedEventArgs e) => Deplacer(sender, -1);

    private void OnDescendre(object sender, RoutedEventArgs e) => Deplacer(sender, +1);

    private void Deplacer(object sender, int pas)
    {
        if ((sender as Button)?.Tag is not Ligne ligne) return;

        var rang = _raccourcis.IndexOf(ligne.Raccourci);
        var cible = rang + pas;
        if (rang < 0 || cible < 0 || cible >= _raccourcis.Count) return;

        _raccourcis.RemoveAt(rang);
        _raccourcis.Insert(cible, ligne.Raccourci);
        Afficher();
    }

    private void OnRetirer(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Ligne ligne) return;
        _raccourcis.Remove(ligne.Raccourci);
        Afficher();
    }

    private void OnDefauts(object sender, RoutedEventArgs e)
    {
        _raccourcis.Clear();
        _raccourcis.AddRange(IdShortcuts.Defaults);
        MessageText.Text = "Raccourcis par défaut rétablis — pensez à enregistrer.";
        Afficher();
    }

    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        try
        {
            IdShortcuts.Save(App.Services.CatalogDir, _raccourcis);
            MessageBox.Show(
                "Raccourcis enregistrés. Ils s'appliquent à la prochaine ouverture du module " +
                "photo d'identité.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigator.Back();
        }
        catch (Exception ex)
        {
            FileLog.Write("Enregistrement des raccourcis d'identité impossible", ex);
            MessageBox.Show($"Enregistrement impossible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private sealed record ChoixDocument(IdDocumentSpec Spec)
    {
        public string Libelle => Spec.Label;
    }

    private sealed record ChoixProduit(Product Produit)
    {
        public string Libelle => $"{Produit.Name} — {Produit.WidthMm:0.#} × {Produit.HeightMm:0.#} mm";
    }

    private sealed record Ligne(IdShortcut Raccourci, string Detail)
    {
        public string Libelle => Raccourci.Libelle;
    }
}
