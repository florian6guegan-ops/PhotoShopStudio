using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Studio.App.Infrastructure;

namespace Studio.App.Views;

/// <summary>Numéro client en très grand, retour automatique à l'accueil.</summary>
public partial class KioskDoneView : UserControl
{
    private readonly DispatcherTimer _autoHome = new() { Interval = TimeSpan.FromSeconds(25) };

    public KioskDoneView(string displayNumber)
    {
        InitializeComponent();
        NumberText.Text = displayNumber;
        _autoHome.Tick += (_, _) => GoHome();
        Loaded += (_, _) => _autoHome.Start();
        Unloaded += (_, _) => _autoHome.Stop();
    }

    private void OnFinish(object sender, RoutedEventArgs e) => GoHome();

    private void GoHome()
    {
        _autoHome.Stop();
        Navigator.Home(new KioskHomeView(), "Bienvenue");
    }
}
