using System.Windows;
using System.Windows.Threading;

namespace Studio.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    /// <summary>
    /// Pose les services quand ce n'est PAS cette application-ci qui démarre.
    ///
    /// Studio Photo Identité est une autre application, dans le même dépôt et sur le même
    /// moteur : elle réutilise les écrans d'ici, et tous appellent <see cref="Services"/>.
    /// Son propre démarrage compose les services puis les dépose ici. Il n'y a qu'un seul
    /// <c>Application</c> par processus — celui d'Identité — mais un champ statique se
    /// partage très bien.
    /// </summary>
    public static void AmorcerServices(AppServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = services;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;
        Studio.Printing.BitmapPrinter.Log = message => Infrastructure.FileLog.Write(message);

        // UN SEUL des deux logiciels à la fois : ils se disputent le relais des machines, et
        // celui qui perd n'imprime plus — sans un mot. L'opérateur peut demander la bascule
        // plutôt que d'aller fermer l'autre à la main. Voir Infrastructure.UnSeulLogiciel.
        if (!Infrastructure.UnSeulLogiciel.LaVoieEstLibre("Studio.App", "Studio Photo"))
        {
            Shutdown(2);
            return;
        }

        // Le journal et les modèles de détourage visaient chacun « D:\PhotoStudioData »
        // de leur côté. Sur un poste sans disque D:, le premier écrivait dans le vide et
        // le second ne trouvait jamais rien — sans que rien ne le dise. Ils suivent
        // maintenant la racine RÉELLE, celle que l'application vient de choisir.
        var racine = AppServices.RacineDonneesParDefaut();

        Infrastructure.FileLog.LogsDir = System.IO.Path.Combine(racine, "logs");
        Studio.Imaging.BiRefNetMatting.DossiersCherches =
        [
            System.IO.Path.Combine(racine, "models"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "models"),
        ];

        try
        {
            Services = AppServices.Load(racine);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible de démarrer :\n{ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        FaireLeMenageDropbox();
    }

    /// <summary>
    /// Retire du Dropbox du studio les envois périmés, une fois par démarrage.
    ///
    /// <b>En tâche de fond et sans rien attendre</b> : c'est du réseau, et l'application ne
    /// doit pas ouvrir plus lentement parce que Dropbox met du temps à répondre. Tout ce
    /// qui rate part au journal et sera retenté au lancement suivant — un dossier de trop
    /// pendant une journée ne coûte rien, une application qui met dix secondes à s'ouvrir
    /// devant un client, si.
    ///
    /// Un ménage suit aussi chaque envoi (voir <c>DropboxSendView</c>) : sur un poste qui
    /// reste allumé la semaine, le démarrage seul ne passerait presque jamais.
    /// </summary>
    private static void FaireLeMenageDropbox()
    {
        if (!Services.Dropbox.EstUtilisable || Services.Dropbox.RetentionJours <= 0) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Studio.Web.Dropbox.DropboxMenage.FaireLeMenageAsync(Services.Dropbox);
            }
            catch (Exception ex)
            {
                // FaireLeMenageAsync avale déjà ce qu'il peut : ceci est le filet du filet,
                // pour qu'une tâche de fond ne fasse jamais tomber l'application
                Infrastructure.FileLog.Write("Ménage Dropbox impossible", ex);
            }
        });
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // dernière ligne de défense : on informe, on journalise, on ne meurt pas en silence
        Infrastructure.FileLog.Write("Exception non gérée", e.Exception);
        MessageBox.Show(
            $"Une erreur inattendue s'est produite :\n{e.Exception.Message}\n\nL'application continue.",
            "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
