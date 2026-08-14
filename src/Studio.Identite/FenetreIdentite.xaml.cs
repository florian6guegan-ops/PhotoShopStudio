using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Studio.App.Infrastructure;
using Studio.App.Views;

namespace Studio.Identite;

/// <summary>
/// La fenêtre de Studio Photo Identité : l'en-tête de la maquette, et dessous l'écran
/// courant.
///
/// <b>Elle héberge les écrans du Studio</b> — <c>Navigator</c> n'est qu'une pile et un
/// événement, sans lien avec une fenêtre particulière. C'est ce qui permet à ce logiciel-ci
/// d'être neuf sans rien réécrire du parcours d'identité, qui sert en boutique depuis des
/// semaines. Les écrans seront remplacés un à un par ceux de la maquette.
///
/// <b>Pas de sortie vers le Studio complet.</b> Sur un poste identité verrouillé du Studio,
/// cinq appuis dans le coin plus le PIN déverrouillaient le Studio de la boutique. Ici il
/// n'y a pas de Studio derrière : le geste ne fait rien, et c'est juste. L'engrenage du
/// réglage courriel, lui, fonctionne.
/// </summary>
public partial class FenetreIdentite : Window
{
    public FenetreIdentite()
    {
        InitializeComponent();

        Navigator.Navigated += SurNavigation;

        Loaded += (_, _) => AccueilStudio.Rentrer();
        Closed += (_, _) => Navigator.Navigated -= SurNavigation;
    }

    private void SurNavigation(UserControl ecran, string titre)
    {
        HoteEcran.Content = ecran;
        TitreEcran.Text = string.IsNullOrWhiteSpace(titre) ? "Photos d'identité" : titre;
    }

    // ----- réglages, derrière le code staff -----

    private string _pin = "";

    private void OnReglages(object sender, RoutedEventArgs e)
    {
        _pin = "";
        PinDots.Text = "";
        PinPanel.Visibility = Visibility.Visible;
    }

    private void OnPinDigit(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string chiffre) return;

        _pin += chiffre;
        PinDots.Text = new string('●', _pin.Length);

        var attendu = Studio.App.App.Services.Mode.StaffPin;
        if (_pin.Length < attendu.Length) return;

        if (_pin == attendu)
        {
            PinPanel.Visibility = Visibility.Collapsed;
            Navigator.Go(new CourrielSettingsView(), "Envoi par courriel");
        }
        else
        {
            _pin = "";
            PinDots.Text = "✗";
        }
    }

    private void OnPinClear(object sender, RoutedEventArgs e)
    {
        _pin = "";
        PinDots.Text = "";
    }

    private void OnPinCancel(object sender, RoutedEventArgs e) =>
        PinPanel.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Repartir de zéro. C'est le geste le plus fréquent du comptoir — un client part, le
    /// suivant se présente — et il valait un aller-retour par le menu.
    /// </summary>
    private void OnClientSuivant(object sender, RoutedEventArgs e) => AccueilStudio.Rentrer();

    private void OnQuitter(object sender, RoutedEventArgs e)
    {
        var reponse = MessageBox.Show(
            "Fermer Studio Photo Identité ?",
            "Studio Photo Identité", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);

        if (reponse == MessageBoxResult.Yes) Close();
    }

    /// <summary>
    /// Échap ne doit pas fermer le poste : la fenêtre n'a pas de bordure, et un appui
    /// malheureux devant un client laisserait un bureau Windows nu.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.F4) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }
}
