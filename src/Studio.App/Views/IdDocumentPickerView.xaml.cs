using System.IO;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Imaging.Geometry;

namespace Studio.App.Views;

/// <summary>
/// Choix du document d'identité parmi les 274 normes reprises de DiLand.
///
/// DiLand présente une grille de pays que l'opérateur fait défiler. Avec 274 entrées,
/// une recherche est plus rapide : deux lettres suffisent à trouver « Espagne » ou
/// « visa ». La grille reste, pour rester lisible d'un coup d'œil.
/// </summary>
public partial class IdDocumentPickerView : UserControl
{
    private readonly Action<IdDocumentSpec> _onChoisi;
    private IReadOnlyList<IdDocumentSpec> _documents = [];

    /// <param name="onChoisi">Appelé avec le document retenu.</param>
    public IdDocumentPickerView(Action<IdDocumentSpec> onChoisi)
    {
        InitializeComponent();
        _onChoisi = onChoisi;

        Loaded += (_, _) =>
        {
            Charger();
            Afficher();
            SearchBox.Focus();
        };
    }

    private void Charger()
    {
        try
        {
            var chemin = Path.Combine(App.Services.CatalogDir, "diland-id-documents.json");
            if (!File.Exists(chemin))
            {
                // le référentiel vit dans le dépôt tant qu'il n'a pas été installé
                var depot = TrouverDansLeDepot();
                if (depot is not null) chemin = depot;
            }

            _documents = File.Exists(chemin)
                ? IdDocumentCatalog.Load(chemin)
                : [IdDocumentSpec.France];
        }
        catch (Exception ex)
        {
            FileLog.Write("Référentiel des documents d'identité illisible", ex);
            _documents = [IdDocumentSpec.France];
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

    private void Afficher()
    {
        var trouves = IdDocumentCatalog.Search(_documents, SearchBox.Text).ToList();

        DocumentsList.ItemsSource = trouves.Select(d => new DocumentRow(d)).ToList();
        CountText.Text = trouves.Count == _documents.Count
            ? $"{_documents.Count} documents"
            : $"{trouves.Count} sur {_documents.Count}";
    }

    private void OnSearchChanged(object sender, RoutedEventArgs e) => Afficher();

    private void OnDocumentChoisi(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DocumentRow ligne)
            _onChoisi(ligne.Spec);
    }

    private void OnBack(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnCancel(object sender, RoutedEventArgs e) => Navigator.Home(new HomeView(), "Studio Photo");

    private sealed record DocumentRow(IdDocumentSpec Spec)
    {
        public string Pays => Spec.Country;
        public string Document => Spec.Document;
        public string Titre => Spec.Label;
        public string Cotes => $"{Spec.WidthMm:0.#} × {Spec.HeightMm:0.#} mm";

        /// <summary>
        /// Bornes du visage quand la norme les précise. Une trentaine de documents n'en
        /// donnent pas : mieux vaut le dire que laisser croire à un contrôle.
        /// </summary>
        public string Visage => Spec.HasHeadBounds
            ? $"visage {Spec.HeadMinMm:0.#} à {Spec.HeadMaxMm:0.#} mm"
            : "hauteur de visage non normée";
    }
}
