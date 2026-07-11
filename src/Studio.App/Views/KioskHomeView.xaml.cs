using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;

namespace Studio.App.Views;

/// <summary>Accueil borne : un seul bouton. Sortie staff par 5 appuis coin haut-gauche + PIN.</summary>
public partial class KioskHomeView : UserControl
{
    private int _cornerTaps;
    private DateTime _firstTap = DateTime.MinValue;
    private string _pin = "";

    public KioskHomeView() => InitializeComponent();

    private void OnStart(object sender, RoutedEventArgs e) =>
        Navigator.Go(new SourcePickerView(root =>
            Navigator.Go(new KioskGridView(root), "Choisissez vos photos")),
            "Où sont vos photos ?");

    // ----- sortie staff -----

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
        _pin = "";
        PinDots.Text = "";
        PinPanel.Visibility = Visibility.Visible;
    }

    private void OnPinDigit(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string digit) return;
        _pin += digit;
        PinDots.Text = new string('●', _pin.Length);

        var expected = App.Services.Mode.StaffPin;
        if (_pin.Length < expected.Length) return;

        if (_pin == expected)
        {
            Application.Current.Shutdown();
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
