using System.Windows;
using System.Windows.Controls;

namespace Studio.App.Infrastructure;

/// <summary>
/// Demande une adresse de courriel, en une boîte et sans quitter l'écran.
///
/// <b>Pourquoi pas un écran de plus.</b> L'adresse se prend au comptoir, client devant soi,
/// entre deux gestes : partir sur un autre écran ferait perdre la sélection de photos et
/// obligerait à revenir. Une boîte suffit, et c'est le seul cas de l'application où l'on en
/// ouvre une pour SAISIR quelque chose.
///
/// Rendre une chaîne VIDE est une réponse valable : c'est ainsi qu'on retire une adresse
/// déjà posée. Null veut dire « annulé », et ne touche à rien.
/// </summary>
public static class SaisirLAdresse
{
    /// <param name="actuelle">Adresse déjà connue, proposée à la modification.</param>
    /// <returns>L'adresse saisie, une chaîne vide pour l'effacer, ou null si annulé.</returns>
    public static string? Demander(string? actuelle = null)
    {
        var champ = new TextBox
        {
            Text = actuelle ?? "",
            FontSize = 22,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 12, 0, 0),
        };

        var aide = new TextBlock
        {
            Text = "Dès que la commande sera sortie de la machine, un courriel dira au " +
                   "client qu'elle l'attend en magasin. Sans les photos : l'envoi des " +
                   "fichiers reste une prestation à part.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            Margin = new Thickness(0, 12, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedBrush"],
        };

        var contenu = new StackPanel { Margin = new Thickness(20) };
        contenu.Children.Add(new TextBlock
        {
            Text = "Adresse du client",
            FontSize = 17,
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["TextBrush"],
        });
        contenu.Children.Add(champ);
        contenu.Children.Add(aide);

        var valider = new Button
        {
            Content = "Valider",
            Style = (Style)Application.Current.Resources["BigButton"],
            MinWidth = 160,
            MinHeight = 56,
            IsDefault = true,
        };
        var effacer = new Button
        {
            Content = "Ne pas prévenir",
            Style = (Style)Application.Current.Resources["FlatButton"],
            MinWidth = 200,
            MinHeight = 56,
            Margin = new Thickness(0, 0, 12, 0),
        };
        var annuler = new Button
        {
            Content = "Annuler",
            Style = (Style)Application.Current.Resources["FlatButton"],
            MinWidth = 140,
            MinHeight = 56,
            Margin = new Thickness(0, 0, 12, 0),
            IsCancel = true,
        };

        var boutons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(20, 0, 20, 20),
        };
        boutons.Children.Add(annuler);
        boutons.Children.Add(effacer);
        boutons.Children.Add(valider);

        var grille = new Grid();
        grille.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grille.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(contenu, 0);
        Grid.SetRow(boutons, 1);
        grille.Children.Add(contenu);
        grille.Children.Add(boutons);

        var fenetre = new Window
        {
            Title = "Prévenir le client",
            Content = grille,
            SizeToContent = SizeToContent.Height,
            Width = 640,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = (System.Windows.Media.Brush)Application.Current.Resources["PageBrush"],
        };

        string? reponse = null;

        valider.Click += (_, _) => { reponse = champ.Text.Trim(); fenetre.Close(); };
        effacer.Click += (_, _) => { reponse = ""; fenetre.Close(); };

        fenetre.Loaded += (_, _) => { champ.Focus(); champ.SelectAll(); };
        fenetre.ShowDialog();

        return reponse;
    }
}
