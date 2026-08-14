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
            AccueilStudio.PageDAccueil = () => new Studio.App.Views.IdPhotoView([]);
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
