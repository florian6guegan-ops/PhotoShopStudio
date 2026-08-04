using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Imaging;
using Studio.Core.Mail;
using Studio.Imaging;
using Studio.Printing;

namespace Studio.App.Views;

/// <summary>
/// Les réglages propres à CE poste : l'envoi des photos par courriel, et le détourage du
/// fond blanc.
///
/// Ils vivent dans les données du poste (<c>config\mail.json</c>, <c>config\detourage.json</c>)
/// et non dans le dépôt, qui est public : le mot de passe d'application n'a rien à y faire,
/// et un secret poussé par mégarde ne se rattrape pas.
///
/// Le détourage y est pour une autre raison : la bonne réponse dépend de la MACHINE. Un
/// poste à Quadro P2000 et un poste mieux doté n'ont pas le même réglage, et c'est ce que
/// l'écran doit dire — d'où les chiffres affichés plutôt qu'une case nue.
///
/// C'est ce qui permet d'installer un second poste opérateur sans toucher au code : on
/// ouvre cet écran, on saisit, on essaie.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Montrer(App.Services.Mail);
            MontrerLeDetourage(App.Services.Detourage);
        };
    }

    private void Montrer(MailSettings reglages)
    {
        ServeurBox.Text = reglages.Serveur;
        PortBox.Text = reglages.Port.ToString();
        ExpediteurBox.Text = reglages.Expediteur;
        NomBox.Text = reglages.NomExpediteur;
        MotDePasseBox.Password = reglages.MotDePasseApplication;
        ActifCheck.IsChecked = reglages.Actif;

        DireOuEnEstLaConfiguration(reglages);
    }

    /// <summary>
    /// Ce qui manque, en clair et en permanence — plutôt qu'un refus au moment d'envoyer,
    /// devant le client.
    /// </summary>
    private void DireOuEnEstLaConfiguration(MailSettings reglages)
    {
        if (reglages.EstUtilisable)
        {
            EtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            EtatText.Text = "L'envoi par courriel est configuré. " +
                            "Faites un essai pour vérifier que le serveur accepte.";
            return;
        }

        EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        EtatText.Text = "Envoi impossible pour l'instant : il manque " + reglages.CeQuiManque() + ".";
    }

    private void OnActifChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        DireOuEnEstLaConfiguration(Saisie());
    }

    /// <summary>Les réglages tels qu'ils sont saisis à l'écran, sans les enregistrer.</summary>
    private MailSettings Saisie() => new(
        Serveur: ServeurBox.Text.Trim(),
        // un port illisible vaut 0, ce que CeQuiManque() sait dire — plutôt que de lever
        // sur une frappe en cours
        Port: int.TryParse(PortBox.Text.Trim(), out var port) ? port : 0,
        Expediteur: ExpediteurBox.Text.Trim(),
        NomExpediteur: NomBox.Text.Trim(),
        MotDePasseApplication: MotDePasseBox.Password,
        Actif: ActifCheck.IsChecked == true);

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        var reglages = Saisie();
        App.Services.SaveMail(reglages);
        DireOuEnEstLaConfiguration(reglages);

        // le détourage s'applique SANS redémarrage : SaveDetourage réinitialise la session
        // ONNX, sans quoi le nouveau modèle n'aurait cours qu'au prochain lancement
        App.Services.SaveDetourage(SaisieDetourage());

        MessageBox.Show("Réglages enregistrés.", "Paramètres",
            MessageBoxButton.OK, MessageBoxImage.Information);
        Navigator.Back();
    }

    // ----- détourage du fond blanc -----

    /// <summary>
    /// Vrai le temps de poser les cases à l'ouverture.
    ///
    /// Sans lui, cocher le modèle puissant depuis le code déclenchait l'avertissement
    /// destiné au CLIC de l'opérateur : on ouvrait Paramètres et une boîte de dialogue
    /// sautait au visage, sur un réglage qu'on venait juste de relire.
    /// </summary>
    private bool _chargementDesReglages;

    private void MontrerLeDetourage(DetourageSettings reglages)
    {
        _chargementDesReglages = true;
        try
        {
            CouleurRadio.IsChecked = !reglages.Actif;
            ReseauLegerRadio.IsChecked = reglages.Actif && !reglages.ModelePuissant;
            ReseauPuissantRadio.IsChecked = reglages.Actif && reglages.ModelePuissant;
        }
        finally
        {
            _chargementDesReglages = false;
        }

        DecrireLeMateriel();
        DecrireLesModeles();
        DireOuEnEstLeDetourage(reglages);
    }

    /// <summary>Les réglages de détourage tels qu'ils sont à l'écran, sans les enregistrer.</summary>
    private DetourageSettings SaisieDetourage() => new(
        Actif: CouleurRadio.IsChecked != true,
        ModelePuissant: ReseauPuissantRadio.IsChecked == true);

    private void OnDetourageChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _chargementDesReglages) return;

        DireOuEnEstLeDetourage(SaisieDetourage());
    }

    /// <summary>
    /// Cocher le modèle puissant avertit sur-le-champ quand la machine ne suivra pas, ou
    /// quand le fichier n'est pas là.
    ///
    /// <b>Le réglage n'est pas refusé pour autant</b> : le poste peut changer de carte, et
    /// le fichier peut être posé cinq minutes plus tard. C'est à l'exploitant de décider —
    /// on lui donne les chiffres, pas un verrou.
    /// </summary>
    private void OnModelePuissantChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _chargementDesReglages) return;

        OnDetourageChanged(sender, e);

        var avertissements = new List<string>();

        if (BiRefNetMatting.CheminDuModele(DetourageSettings.ModelePuissantFichier) is null)
            avertissements.Add(
                $"Le fichier « {DetourageSettings.ModelePuissantFichier} » n'est pas installé " +
                $"sur ce poste.\n\nIl doit être posé dans :\n{DossierDesModeles()}\n\n" +
                "En son absence, c'est le modèle « lite » qui sera utilisé.");

        if (CarteGraphique.Principale() is { MemoireGo: { } go } carte &&
            go < DetourageSettings.MemoireVideoMinimaleGo)
            avertissements.Add(
                $"La carte de ce poste ({carte.Nom}) n'a que {go:0.#} Go de mémoire vidéo, " +
                $"pour {DetourageSettings.MemoireVideoRecommandeeGo:0} Go recommandés.\n\n" +
                "Le modèle puissant réussira probablement la première photo puis échouera " +
                "sur la suivante, faute de mémoire. Studio retombera alors sur la méthode " +
                "par couleur — sans rien perdre, mais après avoir fait attendre.");

        if (avertissements.Count == 0) return;

        MessageBox.Show(string.Join("\n\n———\n\n", avertissements),
            "Détourage du fond blanc", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>Le dossier où poser les modèles, celui que le moteur regarde en premier.</summary>
    private static string DossierDesModeles() =>
        BiRefNetMatting.DossiersCherches.FirstOrDefault()
        ?? Path.Combine(App.Services.DataRoot, "models");

    private void DecrireLeMateriel()
    {
        var carte = CarteGraphique.Principale();

        if (carte is null)
        {
            MaterielText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            MaterielText.Text = "Carte graphique : non identifiée sur ce poste.";
            return;
        }

        var assez = carte.MemoireGo is null ||
                    carte.MemoireGo >= DetourageSettings.MemoireVideoMinimaleGo;

        MaterielText.Foreground = (Brush)Application.Current.Resources[assez ? "OkBrush" : "TitleBrush"];
        MaterielText.Text = $"Carte de ce poste : {carte}." +
                            (assez ? "" : " Le modèle puissant y est déconseillé.");
    }

    private void DecrireLesModeles()
    {
        var leger = BiRefNetMatting.CheminDuModele(DetourageSettings.ModeleLeger);
        var puissant = BiRefNetMatting.CheminDuModele(DetourageSettings.ModelePuissantFichier);

        var lignes = new List<string>
        {
            $"« {DetourageSettings.ModeleLeger} » : " + (leger is null ? "absent" : "installé"),
            $"« {DetourageSettings.ModelePuissantFichier} » : " + (puissant is null ? "absent" : "installé"),
        };

        ModelesText.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        ModelesText.Text = string.Join("   ·   ", lignes) +
                           $"\nLes modèles se posent à la main dans {DossierDesModeles()} — " +
                           "ils ne sont pas dans le logiciel, un demi-gigaoctet n'a rien à faire " +
                           "dans un dépôt public.";
    }

    /// <summary>Ce que va faire le détourage, tel que l'écran est réglé — en une phrase.</summary>
    private void DireOuEnEstLeDetourage(DetourageSettings reglages)
    {
        if (!reglages.Actif)
        {
            DetourageEtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            DetourageEtatText.Text =
                "Détourage par la méthode couleur : environ une seconde par photo, " +
                "aucune exigence matérielle.";
            return;
        }

        var demande = reglages.ModeleDemande;

        // ce que le moteur retiendra RÉELLEMENT avec ces réglages, et non ce qu'on lui
        // demande : les deux diffèrent dès qu'un fichier manque
        var precedent = BiRefNetMatting.ModelePrefere;
        BiRefNetMatting.ModelePrefere = demande;
        var retenu = BiRefNetMatting.ModeleRetenu;
        BiRefNetMatting.ModelePrefere = precedent;

        if (retenu is not null && Path.GetFileName(retenu).Equals(demande, StringComparison.OrdinalIgnoreCase))
        {
            DetourageEtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            DetourageEtatText.Text = $"Détourage par le réseau, modèle « {demande} ».";
            return;
        }

        DetourageEtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
        DetourageEtatText.Text = $"« {demande} » n'est pas installé sur ce poste : " +
            (retenu is not null
                ? $"« {Path.GetFileName(retenu)} » sera utilisé à sa place."
                : "aucun modèle n'est installé, le détourage se fera par la méthode couleur.");
    }

    /// <summary>
    /// Envoie un vrai message à l'adresse d'expédition elle-même.
    ///
    /// C'est le seul contrôle qui vaille : « le serveur accepte » ne se devine pas d'un
    /// mot de passe bien tapé. Il part avec ce qui est À L'ÉCRAN, sans qu'on ait besoin
    /// d'enregistrer d'abord — sinon on écraserait une configuration qui marchait par une
    /// qu'on n'a pas encore vérifiée.
    /// </summary>
    private async void OnEssai(object sender, RoutedEventArgs e)
    {
        var reglages = Saisie();

        if (!reglages.EstUtilisable)
        {
            MessageBox.Show(
                "Il manque encore " + reglages.CeQuiManque() + ".",
                "Paramètres", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        EssaiButton.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        EtatText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
        EtatText.Text = "Envoi de l'essai…";

        // le fichier d'essai part dans le cache : il ne regarde aucun client, et n'a pas à
        // grossir le dossier des envois
        var dossier = Path.Combine(App.Services.DataRoot, "cache", "essai-courriel");

        try
        {
            await Task.Run(() => PhotoMailer.EnvoyerUnEssai(reglages, dossier));

            EtatText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            EtatText.Text = $"Essai accepté par le serveur. Vérifiez la boîte {reglages.Expediteur}.";
        }
        catch (Exception ex)
        {
            FileLog.Write("Essai d'envoi par courriel", ex);
            EtatText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            EtatText.Text = ex.Message;
        }
        finally
        {
            Mouse.OverrideCursor = null;
            EssaiButton.IsEnabled = true;
        }
    }
}
