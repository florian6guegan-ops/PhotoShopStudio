using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
    private readonly Action<IdDocumentSpec, int?, GenreDePlanche> _onChoisi;
    private readonly Action<Product>? _onProduit;
    private IReadOnlyList<IdDocumentSpec> _documents = [];

    /// <param name="onChoisi">
    /// Appelé avec le document retenu, le nombre de photos que le raccourci impose à la
    /// planche — null quand il n'en impose aucun, c'est-à-dire pour tout ce qui vient de la
    /// recherche et pour les raccourcis d'avant le 17/08/2026 — et le GENRE de planche
    /// qu'il fabrique : ordinaire, rentrée, ou planche accompagnée d'un 10×15.
    /// </param>
    /// <param name="onProduit">
    /// Appelé quand le raccourci retenu désigne un PRODUIT et non une norme — l'E-Photo,
    /// qui sort la photo entière sur un 10×15 sans passer par le gabarit d'identité.
    /// Null : ces raccourcis-là sont alors masqués plutôt que menant à un cul-de-sac.
    /// </param>
    public IdDocumentPickerView(
        Action<IdDocumentSpec, int?, GenreDePlanche> onChoisi, Action<Product>? onProduit = null)
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

        foreach (var raccourci in IdShortcuts.Load(App.Services.CatalogDir, Logiciel.EstIdentite))
        {
            switch (raccourci.Kind)
            {
                case IdShortcutKind.Document:
                    if (TrouverDocument(raccourci.Cle) is { } spec)
                    {
                        // Le nombre est dit SUR LA TUILE : deux raccourcis peuvent viser la
                        // même norme et ne différer que par lui — « France » et « France —
                        // planche de 6 » — et rien d'autre ne les distinguerait.
                        var cotes = $"{spec.WidthMm:0.#} × {spec.HeightMm:0.#} mm";
                        var detail = raccourci.Photos is { } photos
                            ? $"{cotes} — planche de {photos}"
                            : cotes;

                        // Le genre passe AVANT le nombre dans ce qu'on lit : sur la tuile de
                        // rentrée, « 4 photos d'identité » sans dire qu'il y a un portrait à
                        // côté décrirait une planche à moitié vide.
                        detail = raccourci.Planche switch
                        {
                            GenreDePlanche.Rentree =>
                                $"{cotes} — {raccourci.Photos ?? PlancheDeRentree.IdentitesParDefaut} " +
                                "photos d'identité + 1 grande, sur la même feuille",
                            GenreDePlanche.PlancheEtTirage =>
                                $"{detail}, plus un tirage 10×15 à part",
                            _ => detail,
                        };

                        tuiles.Add(new RaccourciTuile(raccourci.Libelle, detail, spec,
                            null, raccourci.Photos, raccourci.Planche));
                    }
                    break;

                case IdShortcutKind.Produit:
                    if (_onProduit is null) break;
                    if (App.Services.Catalog.Find(raccourci.Cle) is { Enabled: true } produit)
                        tuiles.Add(new RaccourciTuile(raccourci.Libelle,
                            $"{produit.WidthMm:0.#} × {produit.HeightMm:0.#} mm — photo entière, " +
                            "bords blancs", null, produit, null, GenreDePlanche.Standard));
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

        if (tuile.Spec is { } spec) _onChoisi(spec, tuile.Photos, tuile.Planche);
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
    /// <param name="Photos">Photos imposées à la planche, ou null pour la planche pleine.</param>
    /// <param name="Planche">Ce que la tuile fabrique. Voir <see cref="GenreDePlanche"/>.</param>
    /// <summary>
    /// Une tuile de raccourci. Les deux formats de la rentrée s'y montrent en SCHÉMA plutôt
    /// qu'en toutes lettres — voir <see cref="VignetteDePlanche"/> pour le pourquoi.
    /// </summary>
    private sealed record RaccourciTuile(string Libelle, string Detail,
        IdDocumentSpec? Spec, Product? Produit, int? Photos, GenreDePlanche Planche)
    {
        /// <summary>Le schéma de la planche, ou null quand la tuile s'explique en mots.</summary>
        public ImageSource? Schema { get; } = VignetteDePlanche.Pour(Planche, Spec, Photos);

        /// <summary>Le dessin remplace le texte long, il ne s'y ajoute pas.</summary>
        public Visibility DetailVisible =>
            Schema is null ? Visibility.Visible : Visibility.Collapsed;

        public Visibility SchemaVisible =>
            Schema is null ? Visibility.Collapsed : Visibility.Visible;
    }

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
        // La recherche ne dit rien du nombre ni du genre : la planche part pleine et
        // ordinaire, comme avant. Les formats de la rentrée ont leurs tuiles.
        if ((sender as Button)?.Tag is DocumentRow ligne)
            _onChoisi(ligne.Spec, null, GenreDePlanche.Standard);
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
