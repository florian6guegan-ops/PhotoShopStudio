using System.IO;
using System.Windows;
using System.Windows.Threading;
using Studio.App;
using Studio.App.Infrastructure;

namespace Studio.Identite;

/// <summary>
/// Démarrage de Studio Photo Identité.
///
/// Il compose EXACTEMENT les mêmes services que le Studio complet — même racine de données,
/// même catalogue, mêmes machines — puis les dépose dans <see cref="Studio.App.App"/>, que
/// les écrans réutilisés interrogent. Deux applications, une seule composition.
///
/// <b>La racine de données est partagée à dessein.</b> Sur un poste qui porte les deux
/// logiciels, les commandes, le catalogue et les réglages doivent être les mêmes : une
/// planche tirée ici doit se retrouver dans l'historique de là-bas. Ce n'est pas un
/// deuxième labo, c'est une autre porte sur le même.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;
        Studio.Printing.BitmapPrinter.Log = message => FileLog.Write(message);

        // UN SEUL des deux logiciels à la fois : ils se disputent le relais des machines, et
        // celui qui perd n'imprime plus — sans un mot. L'opérateur peut demander la bascule
        // plutôt que d'aller fermer l'autre à la main. Voir UnSeulLogiciel.
        if (!UnSeulLogiciel.LaVoieEstLibre("Studio.Identite", "Studio Photo Identité"))
        {
            Shutdown(2);
            return;
        }

        var racine = AppServices.RacineDonneesParDefaut();

        FileLog.LogsDir = Path.Combine(racine, "logs");
        Studio.Imaging.BiRefNetMatting.DossiersCherches =
        [
            Path.Combine(racine, "models"),
            Path.Combine(AppContext.BaseDirectory, "models"),
        ];

        try
        {
            var services = AppServices.Load(racine);

            // CE LOGICIEL EST LE POSTE IDENTITÉ, quoi que dise mode.json.
            //
            // Le mode vit dans un fichier que le Studio complet lit au démarrage pour
            // savoir s'il doit se verrouiller. Ici la question ne se pose pas : c'est
            // l'exécutable lui-même qui répond. Le poser explicitement fait que tout le
            // code partagé — retour à l'accueil, impression sans récapitulatif — se
            // comporte comme il doit, sans qu'on ait à poser un fichier sur la machine.
            services.Mode.Mode = "identite";

            Studio.App.App.AmorcerServices(services);

            // TOUT TIENT EN UNE PAGE, comme ID Maker : pas d'écran d'accueil, pas de
            // parcours document → support → photos → cadrage. L'application OUVRE sur sa
            // page de travail, et « Client suivant » en repose une neuve. Les deux seules
            // sorties sont le choix des photos et les réglages.
            // Et elle s'ouvre SUR LA CARTE quand elle est là : le client la tend, on
            // l'insère, les photos sont à l'écran. « Client suivant » repasse par ici, donc
            // il relit la carte lui aussi — celle du client suivant, justement.
            AccueilStudio.PageDAccueil = Studio.App.Views.IdPhotoView.Ouverture;

            AppliquerLaPalette(services.Identite.ModeSombre);

            // l'écran des réglages est celui du Studio : il ne connaît pas cette
            // application, il demande simplement à qui sait faire
            Habillage.Appliquer = AppliquerLaPalette;

            // Les cartes du poste se départagent ICI AUSSI, et c'est même ici que ça
            // compte le plus : ce logiciel-ci détoure toute la journée. Voir
            // Studio.App.App.MesurerLesCartes — en tâche de fond, une fois dans la vie du
            // poste.
            Studio.App.App.MesurerLesCartes();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible de démarrer :\n{ex.Message}",
                "Studio Photo Identité", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        new FenetreIdentite().Show();
    }

    /// <summary>
    /// Pose la palette claire ou sombre, sans redémarrer.
    ///
    /// Les gabarits du thème lisent leurs pinceaux en <c>DynamicResource</c> : remplacer le
    /// dictionnaire de palette suffit, tous les écrans ouverts suivent. C'est ce qui permet
    /// à l'écran des réglages de montrer le résultat pendant qu'on coche la case, au lieu
    /// de demander de relancer.
    ///
    /// ⚠ La palette est TOUJOURS le dernier dictionnaire fusionné : c'est ce qui lui donne
    /// le dernier mot sur les couleurs du thème du Studio.
    /// </summary>
    public static void AppliquerLaPalette(bool sombre)
    {
        var dictionnaires = Current.Resources.MergedDictionaries;

        var voulue = new Uri(sombre ? "ThemeSombre.xaml" : "ThemeClair.xaml", UriKind.Relative);

        for (var i = dictionnaires.Count - 1; i >= 0; i--)
        {
            var source = dictionnaires[i].Source?.OriginalString ?? "";
            if (source.EndsWith("ThemeClair.xaml", StringComparison.OrdinalIgnoreCase)
                || source.EndsWith("ThemeSombre.xaml", StringComparison.OrdinalIgnoreCase))
                dictionnaires.RemoveAt(i);
        }

        dictionnaires.Add(new ResourceDictionary { Source = voulue });
    }

    /// <summary>
    /// Une erreur non rattrapée ne doit pas fermer le poste devant un client : on la
    /// montre, on l'écrit, et l'application continue. Même règle que le Studio complet.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        FileLog.Write("Erreur non rattrapée (Studio Photo Identité)", e.Exception);

        MessageBox.Show(
            $"Une erreur est survenue :\n\n{e.Exception.Message}\n\n" +
            "L'application reste ouverte. Si cela se reproduit, notez ce que vous faisiez.",
            "Studio Photo Identité", MessageBoxButton.OK, MessageBoxImage.Warning);

        e.Handled = true;
    }
}
