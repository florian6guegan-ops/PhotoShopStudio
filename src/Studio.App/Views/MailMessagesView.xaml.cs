using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Mail;

namespace Studio.App.Views;

/// <summary>
/// Réglage des mots prédéfinis joints aux photos envoyées par courriel.
///
/// L'opérateur écrit trois fois par jour la même phrase — « vos photos sont conformes »,
/// « merci de votre visite » — et la retape à chaque fois, fautes comprises. Cet écran les
/// pose une fois pour toutes ; l'écran d'envoi les propose dans une liste.
///
/// Ils vivent dans <c>mail-messages.json</c>, à part des réglages du serveur : voir
/// <see cref="MailMessages"/> pour la raison.
/// </summary>
public partial class MailMessagesView : UserControl
{
    private readonly List<MessagePredefini> _messages;

    /// <summary>
    /// Le message repris par « Modifier ». Il est RETIRÉ de la liste au moment où on le
    /// reprend : sans cela, « Ajouter » en poserait un second à côté de l'ancien, et
    /// l'opérateur se retrouverait avec deux entrées presque identiques.
    /// </summary>
    private int _rangRepris = -1;

    public MailMessagesView()
    {
        InitializeComponent();
        _messages = MailMessages.Load(App.Services.ConfigDir).ToList();

        Loaded += (_, _) =>
        {
            Afficher();
            LibelleBox.Focus();
        };
    }

    private void Afficher()
    {
        MessagesList.ItemsSource = _messages.Select(m => new Ligne(m)).ToList();
        AjouterButton.Content = _rangRepris >= 0 ? "✔ Remplacer" : "➕ Ajouter";
    }

    private void OnAjouter(object sender, RoutedEventArgs e)
    {
        var libelle = LibelleBox.Text.Trim();
        var texte = TexteBox.Text.Trim();

        if (libelle.Length == 0)
        {
            MessageText.Text = "Donnez un intitulé : c'est lui qu'on lit dans la liste au moment d'envoyer.";
            LibelleBox.Focus();
            return;
        }

        if (texte.Length == 0)
        {
            MessageText.Text = "Le message est vide.";
            TexteBox.Focus();
            return;
        }

        var nouveau = new MessagePredefini(libelle, texte);

        // un message repris reprend SA place : le remettre à la fin changerait l'ordre de
        // la liste déroulante à chaque correction de faute de frappe
        if (_rangRepris >= 0 && _rangRepris <= _messages.Count)
        {
            _messages.Insert(_rangRepris, nouveau);
            _rangRepris = -1;
        }
        else
        {
            _messages.Add(nouveau);
        }

        LibelleBox.Clear();
        TexteBox.Clear();
        MessageText.Text = "";
        Afficher();
        LibelleBox.Focus();
    }

    private void OnModifier(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Ligne ligne) return;

        var rang = _messages.IndexOf(ligne.Message);
        if (rang < 0) return;

        // On le sort de la liste : la saisie EST le message tant qu'on le modifie, et
        // « Remplacer » le remettra à sa place. Le laisser affiché ferait croire que la
        // modification est déjà prise.
        _messages.RemoveAt(rang);
        _rangRepris = rang;

        LibelleBox.Text = ligne.Message.Libelle;
        TexteBox.Text = ligne.Message.Texte;
        MessageText.Text = "Message repris dans la saisie — « Remplacer » le remettra à sa place.";
        Afficher();
        LibelleBox.Focus();
    }

    private void OnMonter(object sender, RoutedEventArgs e) => Deplacer(sender, -1);

    private void OnDescendre(object sender, RoutedEventArgs e) => Deplacer(sender, +1);

    private void Deplacer(object sender, int pas)
    {
        if ((sender as Button)?.Tag is not Ligne ligne) return;

        var rang = _messages.IndexOf(ligne.Message);
        var cible = rang + pas;
        if (rang < 0 || cible < 0 || cible >= _messages.Count) return;

        _messages.RemoveAt(rang);
        _messages.Insert(cible, ligne.Message);
        Afficher();
    }

    private void OnRetirer(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not Ligne ligne) return;
        _messages.Remove(ligne.Message);
        Afficher();
    }

    private void OnDefauts(object sender, RoutedEventArgs e)
    {
        _messages.Clear();
        _messages.AddRange(MailMessages.Defaults);
        _rangRepris = -1;
        MessageText.Text = "Messages par défaut rétablis — pensez à enregistrer.";
        Afficher();
    }

    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        // un message repris mais non validé serait perdu sans un mot : on le dit avant
        if (_rangRepris >= 0 && (LibelleBox.Text.Trim().Length > 0 || TexteBox.Text.Trim().Length > 0))
        {
            var reponse = MessageBox.Show(
                "Le message en cours de modification n'a pas été remis dans la liste : il sera perdu.\n\n" +
                "Enregistrer quand même ?",
                "Messages prédéfinis", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (reponse != MessageBoxResult.Yes) return;
        }

        try
        {
            MailMessages.Save(App.Services.ConfigDir, _messages);
            MessageBox.Show(
                "Messages enregistrés. Ils sont proposés à la prochaine ouverture de l'écran d'envoi.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);
            Navigator.Back();
        }
        catch (Exception ex)
        {
            FileLog.Write("Enregistrement des messages prédéfinis impossible", ex);
            MessageBox.Show($"Enregistrement impossible : {ex.Message}", "Studio Photo",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private sealed record Ligne(MessagePredefini Message)
    {
        public string Libelle => Message.Libelle;

        /// <summary>
        /// La première ligne du message, coupée : la liste doit tenir à l'écran, et c'est
        /// l'intitulé qui identifie l'entrée.
        /// </summary>
        public string Apercu
        {
            get
            {
                var plat = Message.Texte.ReplaceLineEndings(" ").Trim();
                return plat.Length <= 160 ? plat : plat[..160] + "…";
            }
        }
    }
}
