using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;

namespace Studio.Tests;

/// <summary>
/// La PLACE que chaque partie de l'écran prend, mesurée sur les gabarits réels.
///
/// <b>Pourquoi ces essais existent.</b> Sur l'écran de 1080 px de la boutique, la planche de
/// photos n'avait plus qu'une rangée et demie de haut : l'en-tête, la barre d'action et le
/// bandeau des machines lui prenaient tout. C'est un défaut de HAUTEUR, et rien dans le code
/// ne le disait — il ne se voyait qu'à l'écran, une fois l'application lancée.
///
/// Ils chargent les vrais fichiers XAML — sans code-behind, donc sans toucher aux services —
/// et mesurent ce que chaque bande occupe. Une barre qui regrossirait se verrait ici.
/// </summary>
public class PlaceALEcranTests
{
    /// <summary>Hauteur utile d'un écran de comptoir : 1080 moins la barre des tâches.</summary>
    private const double HauteurEcran = 1040;

    private const double LargeurEcran = 1920;

    /// <summary>
    /// Charge un fichier XAML de l'application, code-behind mis à part.
    ///
    /// <c>x:Class</c> et les <c>Click=</c> empêchent <see cref="XamlReader"/> de charger le
    /// fichier tel quel : on les retire, ce qui laisse exactement la MISE EN PAGE — la seule
    /// chose que ces essais mesurent.
    /// </summary>
    private static FrameworkElement ChargerLaVue(string chemin)
    {
        var xaml = File.ReadAllText(CheminDuDepot(chemin));

        const string evenements =
            @"\s(x:Class|Click|SelectionChanged|TextChanged|MouseLeftButtonUp|MouseLeftButtonDown)=""[^""]*""";

        xaml = System.Text.RegularExpressions.Regex.Replace(xaml, evenements, "");

        // Un « clr-namespace » sans assembly désigne CELUI QUI ANALYSE — ici le projet
        // d'essais, où Studio.App.Controls n'existe pas. Le type se résolvait donc à null, et
        // le gabarit levait une erreur sans rapport (« key cannot be null »). On nomme
        // l'assembly, ce que le compilateur XAML fait tout seul en temps normal.
        xaml = System.Text.RegularExpressions.Regex.Replace(
            xaml, @"clr-namespace:(Studio\.[\w.]*)(?=""|;)", "clr-namespace:$1;assembly=Studio.App");

        // Les styles d'App.xaml sont injectés DANS la vue, en tête.
        //
        // Les fusionner après coup ne servirait à rien : StaticResource se résout au moment
        // de l'analyse, en remontant l'arbre — et à ce moment-là il n'y a pas encore d'arbre
        // ni d'Application. Il faut donc que le dictionnaire soit là avant que la première
        // balise ne le demande.
        var racine = System.Text.RegularExpressions.Regex.Match(xaml, @"<(\w+)").Groups[1].Value;
        var finDeLaBalise = xaml.IndexOf('>', xaml.IndexOf('<', StringComparison.Ordinal));

        xaml = xaml[..(finDeLaBalise + 1)]
               + $"<{racine}.Resources>{StylesDeLApplication()}</{racine}.Resources>"
               + xaml[(finDeLaBalise + 1)..];

        return (FrameworkElement)XamlReader.Parse(xaml);
    }

    /// <summary>
    /// Le contenu du thème, brut, prêt à être injecté dans les ressources d'une vue.
    ///
    /// Il vivait dans <c>Application.Resources</c> d'<c>App.xaml</c> ; il est dans
    /// <c>Theme.xaml</c> depuis le 14/08/2026, pour que Studio Photo Identité — une autre
    /// application sur le même moteur — puisse le fusionner lui aussi.
    /// </summary>
    private static string StylesDeLApplication()
    {
        var theme = File.ReadAllText(CheminDuDepot("src/Studio.App/Theme.xaml"));

        // le corps du dictionnaire : après la balise ouvrante, avant la fermante
        var debut = theme.IndexOf('>', theme.IndexOf("<ResourceDictionary", StringComparison.Ordinal)) + 1;
        var fin = theme.LastIndexOf("</ResourceDictionary>", StringComparison.Ordinal);

        Assert.True(debut > 0 && fin > debut, "Theme.xaml n'a pas la forme attendue.");
        return theme[debut..fin];
    }

    private static string CheminDuDepot(string relatif)
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PhotoShopStudio.sln")))
            dossier = dossier.Parent;

        Assert.NotNull(dossier);
        return Path.Combine(dossier!.FullName, relatif.Replace('/', Path.DirectorySeparatorChar));
    }

    private static T SurUnFilWpf<T>(Func<T> travail)
    {
        T? resultat = default;
        Exception? echec = null;

        var fil = new Thread(() =>
        {
            try { resultat = travail(); }
            catch (Exception ex) { echec = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });

        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();
        Assert.True(fil.Join(TimeSpan.FromSeconds(30)), "Le fil WPF ne s'est pas terminé.");

        if (echec is not null) throw new Xunit.Sdk.XunitException(echec.ToString());
        return resultat!;
    }

    /// <summary>
    /// Ce que la barre d'action et l'en-tête laissent à la planche.
    ///
    /// La grille de <c>PhotoGridView</c> a trois rangées : outils (Auto), planche (*),
    /// barre d'action (Auto). Ce que les deux « Auto » prennent est exactement ce que la
    /// planche perd.
    /// </summary>
    [Fact]
    public void La_planche_garde_au_moins_deux_rangees_et_demie_de_photos()
    {
        var (outils, action, planche) = SurUnFilWpf(() =>
        {
            var vue = ChargerLaVue("src/Studio.App/Views/PhotoGridView.xaml");

            // l'écran est posé dans MainWindow avec une marge de 24 et le bandeau des machines
            vue.Width = LargeurEcran - 48;
            vue.Height = HauteurEcran - 76 - 48 - 92;   // en-tête, marges, bandeau compact
            vue.Measure(new Size(vue.Width, vue.Height));
            vue.Arrange(new Rect(0, 0, vue.Width, vue.Height));
            vue.UpdateLayout();

            var grille = (Grid)((UserControl)vue).Content;
            return (grille.RowDefinitions[0].ActualHeight,
                    grille.RowDefinitions[2].ActualHeight,
                    grille.RowDefinitions[1].ActualHeight);
        });

        // Une tuile fait 236 px, marges comprises.
        //
        // Mesuré sur cet écran de 1040 px avant/après le dégraissage : la planche passe de
        // 383 à 652 px, soit de 1,6 à 2,8 rangées de photos. Les outils sont tombés de 119 à
        // 46 px (le titre en double a sauté) et la barre d'action de 264 à 126 px (elle
        // tenait sur trois lignes de 52 px).
        Assert.True(planche >= 2.5 * 236,
            $"la planche ne fait que {planche:0} px — outils {outils:0}, barre d'action {action:0}");

        // et le garde-fou dans l'autre sens : la barre d'action tenait sur trois lignes
        Assert.True(action <= 150, $"la barre d'action reprend de la hauteur : {action:0} px");
        Assert.True(outils <= 60, $"la barre d'outils reprend de la hauteur : {outils:0} px");
    }

    /// <summary>
    /// Le bandeau des machines occupait 150 px en permanence, sur tous les écrans.
    /// </summary>
    [Fact]
    public void Le_bandeau_des_machines_reste_compact()
    {
        var hauteur = SurUnFilWpf(() =>
        {
            var vue = ChargerLaVue("src/Studio.App/Views/MachineBarView.xaml");

            // les TROIS machines de la boutique, garnies : un bandeau vide ne mesurerait
            // que ses marges et ne dirait rien de ce qu'on veut vérifier
            var liste = (ItemsControl)vue.FindName("MachinesList");
            liste.ItemsSource = new[] { "A", "B", "D" }.Select(lettre => new
            {
                Lettre = lettre,
                Fond = System.Windows.Media.Brushes.DarkSlateGray,
                Nom = "FUJIFILM DE100-2",
                Etat = "Hors ligne — vérifiez qu'elle est allumée et raccordée au réseau",
                Papier = "consommables lisibles seulement DiLand fermé",
                Restant = "rien dans la file",
                Encres = new[] { 0.4, 0.8, 0.6 }.Select(h => new
                {
                    Info = "Jaune 40 %",
                    Couleur = System.Windows.Media.Brushes.Yellow,
                    Hauteur = h * 36,
                }).ToList(),
                PurgeVisible = Visibility.Visible,
                ArretVisible = Visibility.Visible,
                ArretTexte = "Arrêter",
                ArretPossible = true,
                TravailVisible = Visibility.Collapsed,
                TravailTexte = "",
                Fraction = 0.0,
                Indetermine = false,
            }).ToList();

            vue.Measure(new Size(LargeurEcran, double.PositiveInfinity));
            vue.Arrange(new Rect(0, 0, LargeurEcran, vue.DesiredSize.Height));
            vue.UpdateLayout();
            return vue.DesiredSize.Height;
        });

        // Il en occupait 131, en permanence et sur tous les écrans ; il en fait 101.
        //
        // Ce n'est plus la tuile qui commande — MinHeight vaut 66 — mais ses QUATRE lignes de
        // texte : nom, état, papier, file. Elles restent, parce qu'elles sont ce qu'on vient
        // lire ; ce sont leurs corps qui ont maigri.
        Assert.InRange(hauteur, 1, 110);
    }

    /// <summary>
    /// Les tuiles de format sont centrées, comme toutes les autres grilles de tuiles.
    /// </summary>
    [Fact]
    public void Les_tuiles_de_format_sont_centrees()
    {
        var alignement = SurUnFilWpf(() =>
        {
            var vue = ChargerLaVue("src/Studio.App/Views/PrintFormatView.xaml");
            var liste = (ItemsControl)vue.FindName("FormatsList");
            var panneau = (FrameworkElement)liste.ItemsPanel.LoadContent();
            return panneau.HorizontalAlignment;
        });

        Assert.Equal(HorizontalAlignment.Center, alignement);
    }

    /// <summary>
    /// Un nom de produit long — il y en a — doit tenir dans sa tuile de 300 × 190.
    ///
    /// « Photos d'identité — planche 10×15 » débordait des deux côtés du cadre : le
    /// <c>TextBlock</c> était en 34 px, sans renvoi à la ligne ni réduction.
    /// </summary>
    [Theory]
    [InlineData("10x15")]
    [InlineData("Bord blanc 21x29,7")]
    [InlineData("Photos d'identité — planche 10×15")]
    [InlineData("Envoi des photos par courriel")]
    [InlineData("A3 (29,7 × 42 cm)")]
    public void Un_nom_de_format_long_tient_dans_sa_tuile(string nom)
    {
        var (largeur, hauteur) = SurUnFilWpf(() =>
        {
            var vue = ChargerLaVue("src/Studio.App/Views/PrintFormatView.xaml");
            var liste = (ItemsControl)vue.FindName("FormatsList");

            var tuile = (FrameworkElement)liste.ItemTemplate.LoadContent();
            tuile.DataContext = new
            {
                Nom = nom,
                Dimensions = "100 × 150 mm",
                Tarif = "0,60 € l'unité — 0,50 € à partir de 31",
                Destination = "Minilab DE100",
                DestinationBrush = System.Windows.Media.Brushes.Teal,
            };

            tuile.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tuile.Arrange(new Rect(tuile.DesiredSize));
            tuile.UpdateLayout();

            var contenu = (FrameworkElement)System.Windows.Media.VisualTreeHelper.GetChild(tuile, 0);
            return (contenu.ActualWidth, contenu.ActualHeight);
        });

        // la tuile fait 300 × 190 : rien de son contenu ne doit dépasser
        Assert.True(largeur <= 300, $"« {nom} » déborde en largeur : {largeur:0} px");
        Assert.True(hauteur <= 190, $"« {nom} » déborde en hauteur : {hauteur:0} px");
    }
}
