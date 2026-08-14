using System;
using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;

namespace Studio.App.Views;

/// <summary>
/// Accueil du poste identité (mode <c>identite</c>) : plein écran, un seul geste pour
/// démarrer le parcours des photos d'identité.
///
/// Décliné de <see cref="KioskHomeView"/>, à une différence près qui fait tout : la sortie
/// staff (5 appuis dans le coin haut-gauche + PIN) ne FERME pas l'application, elle
/// déverrouille vers le Studio complet le temps de la session — voir
/// <see cref="MainWindow.DeverrouillerVersOperateur"/>. Le comptoir dépanne puis referme le
/// poste, sans reconfigurer <c>mode.json</c>.
/// </summary>
public partial class IdentiteHomeView : UserControl
{
    private int _cornerTaps;
    private DateTime _firstTap = DateTime.MinValue;
    private string _pin = "";

    /// <summary>
    /// Ce que le bon code doit ouvrir. Le pavé sert DEUX portes — le Studio complet par le
    /// coin, le réglage du courriel par l'engrenage — et il ne doit pas exister deux pavés :
    /// c'est la même vérification, le même code, et un second exemplaire finirait par
    /// diverger du premier.
    /// </summary>
    private Action _apresPin = () => { };

    public IdentiteHomeView() => InitializeComponent();

    /// <summary>
    /// Le parcours identité complet, tel qu'il existe déjà : document → support → photos →
    /// cadrage → planche. L'écran du document propose aussi l'E-Photo.
    /// </summary>
    private void OnStart(object sender, RoutedEventArgs e) => ParcoursIdentite.Ouvrir();

    // ----- sortie staff : 5 appuis dans le coin, puis PIN -----

    private void OnCornerTap(object sender, RoutedEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _firstTap).TotalSeconds > 4)
        {
            _firstTap = now;
            _cornerTaps = 0;
        }
        if (++_cornerTaps < 5) return;

        _cornerTaps = 0;
        DemanderLeCode(
            "Code staff — accès au Studio complet",
            () => (Application.Current.MainWindow as MainWindow)?.DeverrouillerVersOperateur());
    }

    /// <summary>
    /// Le réglage de l'envoi par courriel — le seul dont ce poste ait vraiment besoin, et
    /// qui demandait jusqu'ici de traverser tout le Studio complet.
    /// </summary>
    private void OnCourriel(object sender, RoutedEventArgs e) =>
        DemanderLeCode(
            "Code staff — réglage du courriel",
            () => Navigator.Go(new CourrielSettingsView(), "Envoi par courriel"));

    private void DemanderLeCode(string titre, Action apres)
    {
        _apresPin = apres;
        _pin = "";
        PinTitre.Text = titre;
        PinDots.Text = "";
        PinPanel.Visibility = Visibility.Visible;
    }

    private void OnPinDigit(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string digit) return;
        _pin += digit;
        PinDots.Text = new string('●', _pin.Length);

        var attendu = App.Services.Mode.StaffPin;
        if (_pin.Length < attendu.Length) return;

        if (_pin == attendu)
        {
            PinPanel.Visibility = Visibility.Collapsed;
            _apresPin();
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
}
