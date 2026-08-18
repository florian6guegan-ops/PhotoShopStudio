using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.App.Views;

/// <summary>
/// Le profil couleur de la DNP, choisi une fois pour la machine.
///
/// <b>Pourquoi cet écran.</b> Le profil se réglait produit par produit, dans le Catalogue —
/// écran que Studio Photo Identité n'a pas. Sur les trois postes, seule la planche
/// d'identité en portait un : l'E-Photo et le 10×15 partaient sans gestion des couleurs,
/// sur la même machine et le même rouleau. Et le profil retenu pour la planche se trouvait
/// être la variante « Vivid » de DiLand, quand DiLand lui-même n'applique pas forcément
/// celle-là — d'où l'écart de rendu signalé le 18/08/2026 entre les deux logiciels.
///
/// Le même écran pour les deux logiciels, comme le détourage : <b>les BOUTONS se doublent,
/// ce qu'ils font, non.</b> La règle qui décide de ce qui est touché vit dans
/// <see cref="ProfilCouleurDnp"/>, où elle se vérifie sans imprimante.
/// </summary>
public partial class ReglagesCouleurDnpView : UserControl
{
    /// <summary>Entrée « aucun profil » : la couleur est alors laissée au pilote.</summary>
    private const string SansProfil = "Aucun — le pilote gère la couleur";

    /// <summary>
    /// Une ligne de la liste déroulante. Elle porte le profil, et affiche son libellé :
    /// une liste qui mêle « aucun » et des profils ne peut pas s'appuyer sur
    /// DisplayMemberPath, faute de quoi l'opérateur lirait « Choix { Libelle = … } ».
    /// </summary>
    private sealed record Choix(string Libelle, IccProfiles.Entry? Profil)
    {
        public override string ToString() => Libelle;
    }

    /// <summary>Les produits DNP du catalogue, relevés une fois à l'ouverture.</summary>
    private IReadOnlyList<Product> _produits = [];

    /// <summary>Vrai le temps de poser la liste : le gestionnaire doit alors se taire.</summary>
    private bool _chargement;

    public ReglagesCouleurDnpView()
    {
        InitializeComponent();
        Loaded += (_, _) => Montrer();
    }

    private void Montrer()
    {
        _produits = ProfilCouleurDnp.Produits(App.Services.Catalog.All);

        RemplirLaListe(ProfilCouleurDnp.Lire(_produits).Profil);
        DecrireLesProduits();
        DireOuEnEst();

        // Rien à régler sans machine : mieux vaut une phrase qu'une liste vide qu'on
        // remplirait sans effet.
        var aRegler = _produits.Count > 0;
        ProfilCombo.IsEnabled = aRegler;
        EnregistrerButton.IsEnabled = aRegler;
    }

    /// <summary>
    /// Tous les profils utilisables : ceux du catalogue d'abord, puis ceux que les pilotes
    /// ont fait installer par Windows — c'est là que vivent les profils DNP tant qu'on ne
    /// les a pas importés.
    /// </summary>
    private void RemplirLaListe(string? retenu)
    {
        _chargement = true;
        try
        {
            var choix = new List<Choix> { new(SansProfil, null) };
            choix.AddRange(IccProfiles.Available(App.Services.CatalogDir)
                .Select(p => new Choix(p.Label, p)));

            ProfilCombo.ItemsSource = choix;
            ProfilCombo.SelectedItem =
                choix.FirstOrDefault(c => c.Profil is { } p &&
                                          string.Equals(p.Name, retenu, StringComparison.OrdinalIgnoreCase))
                ?? choix[0];
        }
        finally
        {
            _chargement = false;
        }
    }

    /// <summary>Le profil choisi à l'écran, ou null pour « aucun ».</summary>
    private IccProfiles.Entry? Saisie() => (ProfilCombo.SelectedItem as Choix)?.Profil;

    private void OnProfilChange(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _chargement) return;

        DireOuEnEst();
    }

    /// <summary>
    /// La liste des produits touchés, avec ce que chacun applique AUJOURD'HUI.
    ///
    /// C'est la seule façon de voir d'un coup d'œil qu'une machine sort deux couleurs — et
    /// c'est exactement ce qui se passait : la planche avec profil, l'E-Photo sans.
    /// </summary>
    private void DecrireLesProduits()
    {
        if (_produits.Count == 0)
        {
            ProduitsText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            ProduitsText.Text = "Aucun produit de ce poste ne sort sur une imprimante DNP : " +
                                "il n'y a rien à régler ici.";
            return;
        }

        var lignes = _produits.Select(p =>
            $"·  {p.Name} ({p.Code}) — {DecrireLeProfilDe(p)}");

        ProduitsText.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        ProduitsText.Text = string.Join("\n", lignes);
    }

    /// <summary>
    /// Ce qu'un produit applique réellement : le profil de sa finition s'il en a une —
    /// elle couvre celui du produit —, le sien sinon.
    /// </summary>
    private static string DecrireLeProfilDe(Product produit)
    {
        var etat = ProfilCouleurDnp.Lire([produit]);

        if (!etat.Accord) return "plusieurs profils selon la finition";
        return etat.Profil is null ? "aucun profil aujourd'hui" : $"« {etat.Profil} »";
    }

    /// <summary>Ce que le réglage fera, tel que l'écran est posé — en une phrase.</summary>
    private void DireOuEnEst()
    {
        if (_produits.Count == 0)
        {
            EtatText.Text = "";
            return;
        }

        var actuel = ProfilCouleurDnp.Lire(_produits);
        var voulu = Saisie()?.Name;

        if (!actuel.Accord)
        {
            EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            EtatText.Text = "Les produits de cette machine n'appliquent pas tous le même profil : " +
                            "elle sort donc deux couleurs selon ce qu'on imprime. Enregistrer les " +
                            "remettra d'accord.";
            return;
        }

        if (string.Equals(actuel.Profil, voulu, StringComparison.OrdinalIgnoreCase))
        {
            EtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            EtatText.Text = voulu is null
                ? "C'est le réglage actuel : aucun profil, la couleur est laissée au pilote."
                : $"C'est le réglage actuel : « {voulu} » sur tout ce qui sort de la DNP.";
            return;
        }

        EtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
        EtatText.Text = voulu is null
            ? "À enregistrer : plus aucun profil, la couleur repassera au pilote."
            : $"À enregistrer : « {voulu} » sur les {_produits.Count} produits ci-dessous.";
    }

    /// <summary>
    /// Ajoute un profil posé sur le poste — dossier couleur de Windows, clef USB, ou le
    /// dossier de DiLand. La liste montre déjà ceux de Windows : ce bouton sert à ceux qui
    /// vivent ailleurs.
    /// </summary>
    private void OnImporter(object sender, RoutedEventArgs e)
    {
        var boite = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choisir un profil couleur",
            Filter = "Profils couleur (*.icc;*.icm)|*.icc;*.icm",
            InitialDirectory = IccProfiles.WindowsColorDir,
        };
        DossiersFavoris.Epingler(boite);
        if (boite.ShowDialog() != true) return;

        try
        {
            var nom = IccProfiles.Import(App.Services.CatalogDir, boite.FileName);
            RemplirLaListe(nom);
            DireOuEnEst();
        }
        catch (Exception ex)
        {
            FileLog.Write("Import du profil couleur impossible", ex);
            MessageBox.Show($"Ce profil n'a pas pu être copié : {ex.Message}",
                "Profil couleur de la DNP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Pose le profil sur tous les produits DNP et enregistre le catalogue.
    ///
    /// Le profil est d'abord COPIÉ dans <c>catalog\icc</c> quand il vient du dossier couleur
    /// de Windows : le catalogue nomme un fichier qu'il doit trouver chez lui, sans quoi le
    /// rendu échouerait sur le poste voisin — c'est déjà arrivé, voir <c>CatalogueLivre</c>.
    /// </summary>
    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        if (_produits.Count == 0)
        {
            Navigator.Back();
            return;
        }

        string? nom;
        try
        {
            var choisi = Saisie();
            nom = choisi is null
                ? null
                : choisi.FromCatalog ? choisi.Name : IccProfiles.Import(App.Services.CatalogDir, choisi.Path);
        }
        catch (Exception ex)
        {
            FileLog.Write("Import du profil couleur avant enregistrement impossible", ex);
            MessageBox.Show($"Ce profil n'a pas pu être copié dans le catalogue : {ex.Message}",
                "Profil couleur de la DNP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var changes = ProfilCouleurDnp.Appliquer(_produits, nom);

        try
        {
            ProductCatalog.Save(App.Services.ProductsJson, App.Services.Catalog.All);
            App.Services.ReloadCatalog();
        }
        catch (Exception ex)
        {
            FileLog.Write("Enregistrement du profil couleur de la DNP impossible", ex);
            MessageBox.Show($"Le catalogue n'a pas pu être enregistré : {ex.Message}",
                "Profil couleur de la DNP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        FileLog.Write($"Profil couleur de la DNP : « {nom ?? "aucun"} » posé sur " +
                      $"{changes.Count} produit(s) — {string.Join(", ", changes.Select(p => p.Code))}.");

        MessageBox.Show(
            changes.Count == 0
                ? "C'était déjà le réglage : rien n'a changé."
                : $"Profil enregistré sur {changes.Count} produit(s). Il s'applique au tirage " +
                  "suivant, sans redémarrer.",
            "Profil couleur de la DNP", MessageBoxButton.OK, MessageBoxImage.Information);

        Navigator.Back();
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();
}
