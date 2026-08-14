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

    /// <summary>
    /// Les réglages, SANS code.
    ///
    /// Ils étaient derrière le code staff, par prudence : le mot de passe du compte courriel
    /// y figure. Mais ce logiciel n'est pas une borne face au client — c'est l'outil de
    /// celui qui tient le comptoir, et lui demander un code pour changer le dossier des
    /// photos, c'est un obstacle sans contrepartie. Retiré à la demande, le 14/08/2026.
    /// </summary>
    private void OnReglages(object sender, RoutedEventArgs e) =>
        Navigator.Go(new ReglagesIdentiteView(), "Réglages");

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
