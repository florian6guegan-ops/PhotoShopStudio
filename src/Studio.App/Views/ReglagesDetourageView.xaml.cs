using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Core.Cloud;
using Studio.Core.Imaging;
using Studio.Imaging;

namespace Studio.App.Views;

/// <summary>
/// Comment le fond blanc des photos d'identité est détouré sur CE poste : par couleur, ou
/// par le réseau de neurones — et avec quel modèle.
///
/// <b>Pourquoi c'est un écran à part.</b> Ce réglage vivait dans les Paramètres du Studio
/// complet, et NULLE PART ailleurs. Or les postes qui en ont le plus besoin sont justement
/// ceux qui ne font que de l'identité : ils tournent sous Studio Photo Identité, qui n'a pas
/// de Paramètres. Résultat à Arcueil (kodakidpc) le 17/08/2026 — <c>detourage.json</c>
/// n'existait pas, le défaut est « réseau éteint », et l'exploitant se plaignait que « le
/// détourage du fond blanc ne marche pas ». Il ne marchait pas parce qu'il n'avait jamais
/// été allumé, et qu'aucun écran de ce logiciel ne permettait de l'allumer.
///
/// Sorti des Paramètres plutôt que recopié : c'est la règle du dépôt — <b>les BOUTONS se
/// doublent, ce qu'ils font, non.</b> Les deux logiciels ouvrent le même écran, qui écrit le
/// même <c>detourage.json</c>.
/// </summary>
public partial class ReglagesDetourageView : UserControl
{
    /// <summary>
    /// Vrai le temps de poser les cases à l'ouverture : les gestionnaires doivent alors se
    /// taire, sinon cocher une case à l'ouverture ferait surgir l'avertissement du modèle
    /// puissant sur un réglage qu'on vient juste de relire.
    /// </summary>
    private bool _chargement;

    public ReglagesDetourageView()
    {
        InitializeComponent();

        Loaded += (_, _) => Montrer(App.Services.Detourage);
    }

    private void Montrer(DetourageSettings reglages)
    {
        _chargement = true;
        try
        {
            CouleurRadio.IsChecked = !reglages.Actif;
            ReseauLegerRadio.IsChecked = reglages.Actif && !reglages.ModelePuissant;
            ReseauPuissantRadio.IsChecked = reglages.Actif && reglages.ModelePuissant;
        }
        finally
        {
            _chargement = false;
        }

        DecrireLeMateriel();
        DecrireLesModeles();
        DireOuEnEst(Saisie());
    }

    /// <summary>Les réglages tels qu'ils sont à l'écran, sans les enregistrer.</summary>
    private DetourageSettings Saisie() => new(
        Actif: CouleurRadio.IsChecked != true,
        ModelePuissant: ReseauPuissantRadio.IsChecked == true);

    private void OnDetourageChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _chargement) return;

        DireOuEnEst(Saisie());
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
        if (!IsLoaded || _chargement) return;

        OnDetourageChanged(sender, e);

        var avertissements = new List<string>();

        if (BiRefNetMatting.CheminDuModele(DetourageSettings.ModelePuissantFichier) is null)
            avertissements.Add(
                $"Le fichier « {DetourageSettings.ModelePuissantFichier} » n'est pas installé " +
                $"sur ce poste.\n\nIl doit être posé dans :\n{DossierDesModeles()}\n\n" +
                "En son absence, c'est le modèle « lite » qui sera utilisé.");

        if (CarteGraphique.Principale() is { MemoireGo: { } go } carte &&
            !DetourageSettings.AssezDeMemoirePourLeModelePuissant(go))
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

    /// <summary>
    /// La carte de ce poste peut-elle porter le modèle puissant ?
    ///
    /// La règle elle-même est dans <see cref="DetourageSettings"/> : elle était recopiée
    /// ici, dans l'avertissement ci-dessus et dans le choix du modèle au démarrage — trois
    /// copies d'un même seuil, et c'est ainsi qu'un seuil finit par diverger.
    /// </summary>
    private static bool LaCarteTientLeModelePuissant() =>
        DetourageSettings.AssezDeMemoirePourLeModelePuissant(CarteGraphique.Principale()?.MemoireGo);

    private void DecrireLeMateriel()
    {
        var carte = CarteGraphique.Principale();

        if (carte is null)
        {
            MaterielText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            MaterielText.Text = "Carte graphique : non identifiée sur ce poste.";
            return;
        }

        var assez = LaCarteTientLeModelePuissant();

        // Le modèle puissant n'est PAS offert à une carte qui ne le portera pas. Ce qui
        // avait piégé dans la version d'avant, c'était une case grise que rien n'expliquait :
        // elle l'est ici par la phrase qui suit, à l'endroit exact où le regard tombe.
        ReseauPuissantRadio.IsEnabled = assez;

        MaterielText.Foreground = (Brush)Application.Current.Resources[assez ? "OkBrush" : "TitleBrush"];
        MaterielText.Text = $"Carte de ce poste : {carte}." +
                            (assez
                                ? ""
                                : $" Il lui faut {DetourageSettings.MemoireVideoRecommandeeGo:0} Go de " +
                                  "mémoire vidéo pour le modèle « portrait » : le choix est grisé " +
                                  "ci-dessus, et le modèle « lite » reste le bon sur ce poste.");
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
                           "\nIls ne sont pas dans le logiciel — un demi-gigaoctet qu'on ne veut pas " +
                           $"retélécharger à chaque version — et se posent dans {DossierDesModeles()}.";

        // Le bouton ne propose que ce qui manque, et le « lite » d'abord : c'est celui de
        // la boutique, et le seul qui tourne sur les cartes qu'on y trouve.
        InstallerModeleButton.IsEnabled = leger is null || puissant is null;
        InstallerModeleButton.Content = leger is null
            ? "Installer le modèle (109 Mo)"
            : "Installer le modèle puissant (467 Mo)";

        if (leger is not null && puissant is not null)
        {
            InstallerModeleButton.Content = "Modèles installés";
            TelechargementText.Text = "";
        }

        // Sur une carte trop juste, on ne fait pas non plus télécharger 467 Mo pour un
        // modèle qu'on vient de griser au-dessus. Le « lite » manquant, lui, reste offert :
        // c'est celui qui tourne partout.
        else if (leger is not null && !LaCarteTientLeModelePuissant())
        {
            InstallerModeleButton.IsEnabled = false;
            InstallerModeleButton.Content = "Modèle installé";
            TelechargementText.Foreground = (Brush)Application.Current.Resources["MutedBrush"];
            TelechargementText.Text =
                "Le modèle « portrait » n'est pas proposé ici : cette carte n'a pas la mémoire " +
                "vidéo qu'il demande.";
        }
    }

    /// <summary>
    /// Télécharge le modèle manquant depuis la publication qui les porte.
    ///
    /// <b>Il s'installait à la main.</b> L'écran se contentait d'indiquer un dossier et un
    /// nom de fichier ; personne n'y a jamais rien posé, et le poste de Créteil a tourné
    /// sans détourage depuis son installation.
    /// </summary>
    private async void OnInstallerLeModele(object sender, RoutedEventArgs e)
    {
        var manquant = BiRefNetMatting.CheminDuModele(DetourageSettings.ModeleLeger) is null
            ? DetourageSettings.ModeleLeger
            : DetourageSettings.ModelePuissantFichier;

        InstallerModeleButton.IsEnabled = false;
        TelechargementText.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        TelechargementText.Text = $"Téléchargement de « {manquant} »…";

        try
        {
            using var client = new System.Net.Http.HttpClient
            {
                // 109 Mo sur la connexion partagée d'une boutique : le défaut de 100 s
                // couperait l'installation en plein milieu.
                Timeout = TimeSpan.FromMinutes(30),
            };

            var avancement = new Progress<double>(fraction =>
                TelechargementText.Text = $"Téléchargement de « {manquant} »… {fraction:P0}");

            var chemin = await ModelesDetourage.TelechargerAsync(
                client, manquant, DossierDesModeles(), avancement);

            FileLog.Write($"Modèle de détourage installé : {chemin}");

            TelechargementText.Foreground = (Brush)Application.Current.Resources["OkBrush"];
            TelechargementText.Text = "Modèle installé.";

            DecrireLesModeles();
            DireOuEnEst(Saisie());
        }
        catch (Exception ex)
        {
            FileLog.Write($"Modèle de détourage « {manquant} » : téléchargement impossible", ex);

            TelechargementText.Foreground = (Brush)Application.Current.Resources["DangerBrush"];
            TelechargementText.Text = PourquoiLeModeleNArrivePas(manquant, ex);

            InstallerModeleButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Ce qu'on dit à l'opérateur quand l'installation échoue.
    ///
    /// <b>« Response status code does not indicate success: 404 (Not Found) »</b> était ce
    /// qu'il lisait — un message d'outil de développement, sous un bouton de boutique. Et
    /// il désigne le cas le plus fréquent : le modèle n'a jamais été envoyé dans la
    /// publication qui les porte. Ce n'est ni la faute du poste, ni celle du réseau, et
    /// aucun nombre de tentatives n'y changera quoi que ce soit — il faut le dire.
    /// </summary>
    private static string PourquoiLeModeleNArrivePas(string manquant, Exception ex)
    {
        if (ex is System.Net.Http.HttpRequestException
            { StatusCode: System.Net.HttpStatusCode.NotFound })
            return $"« {manquant} » n'est pas encore publié : rien à télécharger pour l'instant, " +
                   "et réessayer n'y changera rien. Le détourage marche avec le modèle déjà " +
                   "installé, ou par la méthode couleur.";

        // Le réseau coupé, le disque plein, le pare-feu de la boutique : ceux-là valent la
        // peine d'être retentés, et le message d'origine dit lequel c'est.
        return $"Téléchargement impossible : {ex.Message}. Le détourage par couleur reste disponible.";
    }

    /// <summary>Ce que va faire le détourage, tel que l'écran est réglé — en une phrase.</summary>
    private void DireOuEnEst(DetourageSettings reglages)
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
    /// Le réglage s'applique SANS redémarrage : <c>SaveDetourage</c> réinitialise la session
    /// du réseau, de sorte que la photo suivante part déjà sur la nouvelle méthode.
    /// </summary>
    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        var reglages = Saisie();
        App.Services.SaveDetourage(reglages);

        FileLog.Write($"Détourage : réseau {(reglages.Actif ? "actif" : "éteint")}, " +
                      $"modèle « {reglages.ModeleDemande} ».");

        MessageBox.Show(
            "Réglage enregistré. Il s'applique à la photo suivante, sans redémarrer.",
            "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);

        Navigator.Back();
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();
}
