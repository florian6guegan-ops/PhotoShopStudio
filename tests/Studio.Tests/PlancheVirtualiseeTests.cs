using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Studio.App.Controls;

namespace Studio.Tests;

/// <summary>
/// La planche virtualisée, mesurée et disposée pour de vrai.
///
/// <see cref="GrilleVirtuelleTests"/> vérifie le calcul ; ici on vérifie que le PANNEAU s'en
/// sert — c'est-à-dire qu'il ne construit vraiment qu'une poignée de tuiles là où le
/// <c>WrapPanel</c> les construisait toutes. C'est le seul essai qui puisse le montrer :
/// le nombre de conteneurs fabriqués ne se lit nulle part ailleurs.
///
/// Tout se passe sur un fil STA avec son répartiteur : WPF n'en démarre pas moins.
/// </summary>
public class PlancheVirtualiseeTests
{
    private const double LargeurTuile = 216;
    private const double HauteurTuile = 236;

    /// <summary>Le gabarit réel de la planche : une tuile aux cotes fixes.</summary>
    private static DataTemplate GabaritDeTuile()
    {
        var tuile = new FrameworkElementFactory(typeof(Border));
        tuile.SetValue(FrameworkElement.WidthProperty, 210d);
        tuile.SetValue(FrameworkElement.HeightProperty, 230d);
        tuile.SetValue(FrameworkElement.MarginProperty, new Thickness(3));

        return new DataTemplate { VisualTree = tuile };
    }

    /// <summary>
    /// Monte la liste, la mesure et la dispose, puis rend ce que l'essai veut en savoir.
    /// </summary>
    private static T SurUnFilWpf<T>(Func<ItemsControl, PlancheVirtualisee, T> mesure,
        int photos, double largeur = 1740, double hauteur = 780)
    {
        T? resultat = default;
        Exception? echec = null;

        var fil = new Thread(() =>
        {
            try
            {
                var liste = new ItemsControl
                {
                    ItemsSource = Enumerable.Range(0, photos).Select(i => $"photo {i}").ToList(),
                    ItemTemplate = GabaritDeTuile(),
                    ItemsPanel = new ItemsPanelTemplate(
                        new FrameworkElementFactory(typeof(PlancheVirtualisee))),
                };

                // la liste doit vivre dans un arbre pour que la mesure descende jusqu'au panneau
                var fenetre = new Border { Child = liste, Width = largeur, Height = hauteur };
                fenetre.Measure(new Size(largeur, hauteur));
                fenetre.Arrange(new Rect(0, 0, largeur, hauteur));
                fenetre.UpdateLayout();

                var planche = Descendant<PlancheVirtualisee>(liste)
                    ?? throw new InvalidOperationException("La planche n'a pas été construite.");

                resultat = mesure(liste, planche);
            }
            catch (Exception ex)
            {
                echec = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });

        fil.SetApartmentState(ApartmentState.STA);
        fil.Start();
        Assert.True(fil.Join(TimeSpan.FromSeconds(30)), "Le fil WPF ne s'est pas terminé.");

        if (echec is not null) throw new Xunit.Sdk.XunitException(echec.ToString());
        return resultat!;
    }

    private static T? Descendant<T>(DependencyObject racine) where T : DependencyObject
    {
        if (racine is T trouve) return trouve;

        var enfants = System.Windows.Media.VisualTreeHelper.GetChildrenCount(racine);
        for (var i = 0; i < enfants; i++)
        {
            if (Descendant<T>(System.Windows.Media.VisualTreeHelper.GetChild(racine, i)) is { } sous)
                return sous;
        }

        return null;
    }

    /// <summary>Nombre de tuiles réellement construites.</summary>
    private static int TuilesConstruites(PlancheVirtualisee planche) =>
        System.Windows.Media.VisualTreeHelper.GetChildrenCount(planche);

    /// <summary>
    /// L'essai qui justifie tout le reste : douze cents photos, une quarantaine de tuiles.
    /// </summary>
    [Fact]
    public void Une_carte_pleine_ne_construit_qu_une_poignee_de_tuiles()
    {
        var construites = SurUnFilWpf((_, planche) => TuilesConstruites(planche), photos: 1200);

        Assert.InRange(construites, 1, 64);
    }

    [Fact]
    public void La_planche_annonce_la_hauteur_de_toutes_ses_rangees()
    {
        var etendue = SurUnFilWpf((_, planche) => planche.ExtentHeight, photos: 1200);

        // 1200 photos, 8 colonnes sur 1740 px de large : 150 rangées
        Assert.Equal(150 * HauteurTuile, etendue, precision: 0);
    }

    [Fact]
    public void Le_defilement_change_les_tuiles_construites_sans_les_multiplier()
    {
        var (avant, apres, construites) = SurUnFilWpf((liste, planche) =>
        {
            var premieres = TuilesConstruites(planche);

            planche.SetVerticalOffset(60 * HauteurTuile);
            liste.UpdateLayout();

            return (premieres, planche.VerticalOffset, TuilesConstruites(planche));
        }, photos: 1200);

        Assert.InRange(avant, 1, 64);
        Assert.Equal(60 * HauteurTuile, apres, precision: 0);
        Assert.InRange(construites, 1, 64);
    }

    /// <summary>
    /// Un dossier plus court que l'écran se montre en entier : la virtualisation ne doit
    /// rien cacher quand il n'y a rien à cacher.
    /// </summary>
    [Fact]
    public void Un_petit_dossier_est_montre_en_entier()
    {
        var construites = SurUnFilWpf((_, planche) => TuilesConstruites(planche), photos: 9);

        Assert.Equal(9, construites);
    }

    [Fact]
    public void Un_dossier_vide_ne_construit_rien_et_ne_tombe_pas()
    {
        var construites = SurUnFilWpf((_, planche) => TuilesConstruites(planche), photos: 0);

        Assert.Equal(0, construites);
    }

    /// <summary>
    /// Le zoom passe par la propriété héritée : posé sur la LISTE, il doit atteindre le
    /// panneau et changer le nombre de colonnes.
    /// </summary>
    [Fact]
    public void Le_zoom_pose_sur_la_liste_recompose_la_grille()
    {
        var (normal, reduit) = SurUnFilWpf((liste, planche) =>
        {
            var pleineTaille = planche.ExtentHeight;

            PlancheVirtualisee.SetEchelle(liste, 0.5);
            liste.UpdateLayout();

            return (pleineTaille, planche.ExtentHeight);
        }, photos: 1200);

        // des tuiles deux fois plus petites : plus de colonnes, donc moins de rangées
        Assert.Equal(150 * HauteurTuile, normal, precision: 0);
        Assert.True(reduit < normal,
            $"une planche réduite doit être moins haute ({reduit} contre {normal})");
    }

    [Fact]
    public void La_largeur_de_la_fenetre_decide_du_nombre_de_colonnes()
    {
        var large = SurUnFilWpf((_, planche) => planche.ExtentHeight, photos: 100, largeur: 1740);
        var etroite = SurUnFilWpf((_, planche) => planche.ExtentHeight, photos: 100, largeur: 900);

        // 8 colonnes contre 4 : 13 rangées contre 25
        Assert.Equal(13 * HauteurTuile, large, precision: 0);
        Assert.Equal(25 * HauteurTuile, etroite, precision: 0);
        Assert.True(LargeurTuile > 0);
    }
}
