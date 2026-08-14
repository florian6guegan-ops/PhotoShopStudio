using System.IO;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;
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
    private readonly Action<Product>? _onProduit;
    private IReadOnlyList<IdDocumentSpec> _documents = [];

    /// <param name="onChoisi">Appelé avec le document retenu.</param>
    /// <param name="onProduit">
    /// Appelé quand le raccourci retenu désigne un PRODUIT et non une norme — l'E-Photo,
    /// qui sort la photo entière sur un 10×15 sans passer par le gabarit d'identité.
    /// Null : ces raccourcis-là sont alors masqués plutôt que menant à un cul-de-sac.
    /// </param>
    public IdDocumentPickerView(Action<IdDocumentSpec> onChoisi, Action<Product>? onProduit = null)
    {
        InitializeComponent();
        _onChoisi = onChoisi;
        _onProduit = onProduit;

        Loaded += (_, _) =>
        {
            Charger();
            ChargerRaccourcis();
            Afficher();
        };
    }

    /// <summary>
    /// Pose les tuiles de raccourci. Un raccourci qui ne correspond plus à rien — document
    /// retiré du référentiel, produit supprimé du catalogue — est simplement omis : mieux
    /// vaut une tuile en moins qu'un bouton qui échoue au clic.
    /// </summary>
    private void ChargerRaccourcis()
    {
        var tuiles = new List<RaccourciTuile>();

        foreach (var raccourci in IdShortcuts.Load(App.Services.CatalogDir))
        {
            switch (raccourci.Kind)
            {
                case IdShortcutKind.Document:
                    if (TrouverDocument(raccourci.Cle) is { } spec)
                        tuiles.Add(new RaccourciTuile(raccourci.Libelle,
                            $"{spec.WidthMm:0.#} × {spec.HeightMm:0.#} mm", spec, null));
                    break;

                case IdShortcutKind.Produit:
                    if (_onProduit is null) break;
                    if (App.Services.Catalog.Find(raccourci.Cle) is { Enabled: true } produit)
                        tuiles.Add(new RaccourciTuile(raccourci.Libelle,
                            $"{produit.WidthMm:0.#} × {produit.HeightMm:0.#} mm — photo entière, " +
                            "bords blancs", null, produit));
                    break;
            }
        }

        RaccourcisList.ItemsSource = tuiles;

        // Sans raccourci utilisable, la recherche EST l'écran : l'ouvrir d'emblée évite
        // une page vide surmontée d'un bouton « Autres formats… » sans autre choix.
        if (tuiles.Count == 0) MontrerTousLesFormats();
    }

    private IdDocumentSpec? TrouverDocument(string cle) =>
        IdDocumentCatalog.FindByKey(_documents, cle);

    private void OnRaccourciChoisi(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not RaccourciTuile tuile) return;

        if (tuile.Spec is { } spec) _onChoisi(spec);
        else if (tuile.Produit is { } produit) _onProduit?.Invoke(produit);
    }

    private void OnAutresFormats(object sender, RoutedEventArgs e) => MontrerTousLesFormats();

    private void MontrerTousLesFormats()
    {
        RecherchePanel.Visibility = Visibility.Visible;
        DocumentsScroll.Visibility = Visibility.Visible;
        AutresButton.Visibility = Visibility.Collapsed;
        SearchBox.Focus();
    }

    /// <param name="Spec">Norme visée, ou null si la tuile désigne un produit.</param>
    /// <param name="Produit">Produit tiré tel quel, ou null si la tuile désigne une norme.</param>
    private sealed record RaccourciTuile(string Libelle, string Detail,
        IdDocumentSpec? Spec, Product? Produit);

    private void Charger() => _documents = ReferentielIdentite.Charger();

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

    private void OnCancel(object sender, RoutedEventArgs e) => AccueilStudio.Rentrer();

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
