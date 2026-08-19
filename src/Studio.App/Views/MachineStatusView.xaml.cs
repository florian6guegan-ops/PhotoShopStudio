using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Studio.App.Infrastructure;
using Studio.Printing;
using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;

namespace Studio.App.Views;

/// <summary>
/// Suivi des consommables du minilab : papier restant, encres, bac de maintenance, et
/// nombre de tirages encore possibles pour chaque format.
///
/// Les valeurs viennent de la machine elle-même, via le relais 32 bits. Le nombre de
/// tirages par format est calculé ici : le minilab ne le donne pas, et DiLand ne le
/// calcule pas non plus.
/// </summary>
public partial class MachineStatusView : UserControl
{
    /// <summary>En dessous de ce niveau, on prévient l'opérateur avant qu'un tirage sorte faux.</summary>
    private const int SeuilAlerte = 25;

    /// <summary>
    /// En dessous de ce nombre de tirages restants, on prévient pour la DNP : son rouleau
    /// ne se recharge pas en trente secondes, et une commande de vingt photos passe vite.
    /// </summary>
    private const int SeuilTiragesBas = 25;

    /// <summary>
    /// Cadence de relecture.
    ///
    /// L'écran ne se lisait qu'à l'ouverture : le nombre de photos restantes y serait resté
    /// figé pendant toute une commande, ce qui est exactement le moment où on le regarde.
    /// Dix secondes suffisent — une DS620 met une quinzaine de secondes par tirage.
    /// </summary>
    private static readonly TimeSpan Periode = TimeSpan.FromSeconds(10);

    private readonly System.Windows.Threading.DispatcherTimer _minuteur;

    public MachineStatusView()
    {
        InitializeComponent();

        _minuteur = new System.Windows.Threading.DispatcherTimer { Interval = Periode };
        _minuteur.Tick += async (_, _) => await RefreshAsync(discret: true);

        Loaded += async (_, _) =>
        {
            _minuteur.Start();
            await RefreshAsync();
        };

        Unloaded += (_, _) => _minuteur.Stop();
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <param name="discret">
    /// Relecture automatique : ni sablier ni liste vidée. Les faire à chaque tic ferait
    /// clignoter l'écran toutes les dix secondes et volerait le curseur à l'opérateur.
    /// </param>
    private async Task RefreshAsync(bool discret = false)
    {
        if (!discret)
        {
            MessageText.Text = "Interrogation des machines…";
            MachinesList.ItemsSource = null;
            Mouse.OverrideCursor = CurseurStudio.Attente;
        }

        var lignes = new List<MachineRow>();
        var minilabMuet = (string?)null;

        try
        {
            foreach (var etat in await App.Services.Minilab.SnapshotAsync())
                lignes.Add(new MachineRow(etat));
        }
        catch (Exception ex)
        {
            FileLog.Write("Lecture des consommables impossible", ex);
            minilabMuet =
                "Impossible d'interroger le minilab :\n\n" + ex.Message +
                "\n\nLes tirages ne sont pas affectés tant que la machine répond à l'impression.";
        }

        // Le minilab n'a rien dit : on le montre quand même, d'après les files Windows et
        // hors ligne. Même filet que la DNP juste en dessous — un écran qui s'intitule
        // « état des machines » et n'en affiche aucune ne renseigne sur rien.
        if (lignes.Count == 0)
            foreach (var connu in await Task.Run(() => MinilabPresence.VusParWindows()))
                lignes.Add(new MachineRow(connu));

        // La DNP à part, et sans faire échouer l'écran : DiLand tient son port en exclusif
        // tant qu'il tourne, donc son absence est une situation normale et non une panne.
        try
        {
            var dnps = await App.Services.Minilab.DnpSnapshotAsync();

            // Le SDK ne la voit pas — DiLand tient le port, ou elle dort. On la montre
            // quand même, avec ce que le SPOULEUR en sait : c'est par lui que Studio
            // imprime, et il dit l'essentiel, à savoir si la machine sort du papier et
            // combien il lui en reste. L'écran se contentait jusqu'ici de conseiller de
            // fermer DiLand, ce qu'on ne peut pas faire en pleine journée.
            if (dnps.Count == 0)
                dnps = await Task.Run(() => DiLandPresence.VuesParWindows());

            foreach (var dnp in dnps)
                lignes.Add(new MachineRow(dnp));
        }
        catch (Exception ex)
        {
            FileLog.Write("Consommables : imprimante DNP indisponible", ex);
        }

        try
        {
            MachinesList.ItemsSource = lignes;

            MessageText.Text =
                minilabMuet
                ?? (lignes.Count > 0 ? ""
                    : "Aucune machine détectée. Vérifiez que le minilab est allumé.");

            if (minilabMuet is null && !lignes.Any(l => l.EstDnp) && DiLandPresence.IsRunning())
            {
                MessageText.Text =
                    "La DS620 (DNP) n'apparaît pas : DiLand est ouvert et garde son port USB " +
                    "pour lui. Fermez DiLand — elle réapparaîtra toute seule en quelques secondes.";
            }
            else if (minilabMuet is null && lignes.Any(l => l.EstDnp) && DiLandPresence.IsRunning())
            {
                MessageText.Text =
                    "DiLand est ouvert et garde le port USB de la DS620 : son état vient du " +
                    "spouleur Windows, qui dit ce qu'elle imprime mais pas ce qu'il reste de " +
                    "rouleau. Fermez DiLand pour lire ses consommables.";
            }
        }
        finally
        {
            if (!discret) Mouse.OverrideCursor = null;
        }
    }

    private sealed record SupplyRow(string Nom, int Niveau, Brush Couleur)
    {
        /// <summary>Largeur de la jauge, sur 220 points de large.</summary>
        public double Largeur => Math.Clamp(Niveau, 0, 100) / 100.0 * 220;

        public string Texte => $"{Niveau} %";
    }

    private sealed record FormatRow(string Nom, string Restants);

    private sealed record MachineRow
    {
        public MachineRow(De100PrinterInfo info)
        {
            var horsLigne = info.Status == De100PrinterStatus.Offline;

            Titre = $"Machine {info.MachineId} — {info.Model}";
            SousTitre = horsLigne
                ? "Hors ligne : aucune information disponible."
                : $"{Etat(info.Status)} · série {info.SerialNumber}";

            EtatCouleur = info.Status switch
            {
                De100PrinterStatus.Ready => Pinceau(0x2E, 0x6B, 0x33),
                De100PrinterStatus.Printing or De100PrinterStatus.Busy => Pinceau(0x1B, 0x5E, 0x8A),
                De100PrinterStatus.Sleep => Pinceau(0x37, 0x47, 0x4F),
                De100PrinterStatus.Offline => Pinceau(0x4A, 0x4A, 0x4A),
                De100PrinterStatus.ErrorProcessingCanBeContinued => Pinceau(0x8A, 0x62, 0x0E),
                _ => Pinceau(0xB3, 0x26, 0x1E),
            };

            DetailVisibility = horsLigne ? Visibility.Collapsed : Visibility.Visible;

            if (horsLigne)
            {
                Consommables = [];
                Formats = [];
                Papier = Compteur = Alerte = "";
                AlerteVisibility = Visibility.Collapsed;
                return;
            }

            Compteur = $"{info.TotalPrintCount:N0} tirages depuis la mise en service";

            Papier = info.Media switch
            {
                // Voir De100Media.LongueurNonDeclaree : la machine décompte une longueur
                // qu'on lui donne, elle ne mesure rien. Non déclarée, elle annonce zéro —
                // et l'écran annonçait « 0 tirage restant » sur un rouleau neuf.
                { LongueurNonDeclaree: true } m =>
                    $"rouleau {m.PaperWidthMm} mm, {m.Surface} — longueur restante non déclarée sur la machine",
                { } m => $"{m.PaperRemainingMm / 1000:0.00} m restants — rouleau {m.PaperWidthMm} mm, {m.Surface}",
                _ => "aucun rouleau déclaré",
            };

            Consommables = info.Supplies is { } s
                ?
                [
                    new SupplyRow(s.Cyan.Name, s.Cyan.Level, Brushes.DarkCyan),
                    new SupplyRow(s.Magenta.Name, s.Magenta.Level, Brushes.MediumVioletRed),
                    new SupplyRow(s.Yellow.Name, s.Yellow.Level, Brushes.Goldenrod),
                    new SupplyRow(s.Black.Name, s.Black.Level, Brushes.Black),
                    new SupplyRow(s.MaintenanceTank.Name, s.MaintenanceTank.Level, Brushes.SlateGray),
                ]
                : [];

            // LES FORMATS DE LA BOUTIQUE, pas ceux du pilote. « 15xS », « 15xL », « 15x23 »
            // sont une nomenclature de canal que personne ne vend, et le 13×18 — qui sort
            // très bien d'un rouleau de 152 avec ses bandes blanches — n'y paraissait même
            // pas. Voir FormatsDuCatalogue.
            //
            // Le compte se déduit de la longueur restante : non déclarée, ces zéros n'ont
            // aucun sens et il vaut mieux ne rien avancer.
            var longueurInconnue = info.Media?.LongueurNonDeclaree == true;
            Formats = FormatsDuCatalogue
                .SurLeMinilab(
                    App.Services.Catalog.Enabled,
                    info.MachineId,
                    info.Media?.PaperWidthMm ?? 0,
                    info.Media?.PaperRemainingMm ?? 0)
                .Select(f => new FormatRow(f.Nom, longueurInconnue ? "?" : $"{f.Restants:N0}"))
                .ToList();

            var basses = info.Supplies?.InksBelow(SeuilAlerte).Select(i => $"{i.Name} ({i.Level} %)").ToList() ?? [];
            Alerte = basses.Count > 0
                ? "Encre bientôt épuisée : " + string.Join(", ", basses) +
                  ". Les couleurs peuvent sortir fausses avant la panne."
                : "";
            AlerteVisibility = basses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Une imprimante DNP (DS620).
        ///
        /// Elle n'a ni encres séparées ni bac de maintenance : le ruban et le papier
        /// s'épuisent ensemble, et c'est le nombre de tirages restants qui compte. Les
        /// jauges restent donc vides, et le rouleau se lit en tirages, pas en mètres.
        /// </summary>
        public MachineRow(DnpPrinterInfo info)
        {
            EstDnp = true;

            var horsLigne = info.Status.IsCommunicationFailure;

            Titre = info.EndormieOuInjoignable
                ? info.WindowsQueueName!
                : "DS620 (DNP)" +
                  (string.IsNullOrWhiteSpace(info.SerialNumber) ? "" : $" — série {info.SerialNumber}");

            // Muette au SDK ne veut PAS dire endormie : DiLand tient le port USB presque en
            // permanence, et c'est ce qui affichait « en veille » machine allumée, prête, et
            // même en train de tirer. Le spouleur, lui, répond toujours.
            if (info.EndormieOuInjoignable && info.VueParLeSpouleur)
            {
                var file = info.Spouleur!;

                EtatCouleur = CouleurSpouleur(file.Etat);
                SousTitre = DnpSpouleur.Decrire(file) +
                            " · consommables lisibles seulement DiLand fermé";

                DetailVisibility = Visibility.Visible;
                Consommables = [];
                Formats = [];
                Compteur = "";

                Papier = file.PhotosRestantes == 0
                    ? "Rien dans la file d'impression."
                    : file.PhotosRestantes == 1
                        ? "1 photo reste à sortir."
                        : $"{file.PhotosRestantes} photos restent à sortir.";

                Alerte = file.Etat switch
                {
                    EtatFileDnp.Erreur => file.Message + " Le tirage reprendra tout seul une " +
                                          "fois la machine remise en état.",
                    EtatFileDnp.EnPause => "La file d'impression est EN PAUSE : rien ne sortira " +
                                           "tant qu'elle n'est pas relancée depuis Windows.",
                    EtatFileDnp.HorsLigne => "Windows la déclare hors ligne : vérifiez le câble " +
                                             "et l'alimentation.",
                    _ => "",
                };
                AlerteVisibility = Alerte.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            SousTitre = info.EndormieOuInjoignable
                ? "En veille — elle se réveillera au premier tirage. Ses consommables ne " +
                  "sont lisibles que machine réveillée."
                : horsLigne
                    ? "Hors ligne : aucune information disponible."
                    : $"{EtatDnp(info.Status)} · micrologiciel {info.FirmwareVersion}";

            EtatCouleur = info.EndormieOuInjoignable
                ? Pinceau(0x37, 0x47, 0x4F)
                : horsLigne ? Pinceau(0x4A, 0x4A, 0x4A)
                : info.Status.IsFault ? Pinceau(0xB3, 0x26, 0x1E)
                : info.Status.NeedsOperator ? Pinceau(0x8A, 0x62, 0x0E)
                : info.Status.IsBusy ? Pinceau(0x1B, 0x5E, 0x8A)
                : info.Status.IsReady ? Pinceau(0x2E, 0x6B, 0x33)
                : Pinceau(0x37, 0x47, 0x4F);

            DetailVisibility = horsLigne ? Visibility.Collapsed : Visibility.Visible;
            Consommables = [];

            if (horsLigne)
            {
                Formats = [];
                Papier = Compteur = Alerte = "";
                AlerteVisibility = Visibility.Collapsed;
                return;
            }

            Compteur = $"{info.LifetimePrints:N0} tirages depuis la mise en service";

            var pourcent = info.MediaRemainingPercent is { } pc ? $" ({pc:0} %)" : "";
            Papier = $"{info.MediaRemaining:N0} tirages restants{pourcent} — " +
                     $"média {DecrireMedia(info.MediaSize)}, {info.MediaClass}";

            // LES FORMATS DE LA BOUTIQUE, là encore — et pas seulement le média chargé.
            //
            // Le compteur de la DS620 parle en FEUILLES : sur un rouleau 15×20, une planche
            // d'identité 10×15 est coupée en deux, et « 138 restants » veut dire 276
            // planches. L'écran annonçait la moitié de ce qui restait vraiment.
            Formats = FormatsDuCatalogue
                .SurLaDnp(
                    App.Services.Catalog.Enabled,
                    info.WindowsQueueName ?? "",
                    info.MediaSize,
                    info.MediaRemaining)
                .Select(f => new FormatRow(f.Nom, $"{f.Restants:N0}"))
                .ToList();

            // Aucun produit du catalogue ne correspond à ce rouleau : plutôt que de laisser
            // un vide, on dit au moins ce que la machine annonce.
            if (Formats.Count == 0)
                Formats = [new FormatRow(DecrireMedia(info.MediaSize), $"{info.MediaRemaining:N0}")];

            Alerte = info.Status.NeedsOperator
                ? "Intervention nécessaire sur la machine : " + info.Status.Message + "."
                : info.Status.IsFault
                    ? "Panne : " + info.Status.Message + ". La machine relève du SAV."
                    : info.MediaRemaining <= SeuilTiragesBas
                        ? $"Bientôt à court : {info.MediaRemaining} tirages restants. " +
                          "Prévoyez un rouleau avant la prochaine grosse commande."
                        : "";
            AlerteVisibility = Alerte.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>Nom lisible d'un format DNP : « Size6x4 » ne parle à personne.</summary>
        private static string DecrireMedia(DnpMediaSize media) =>
            media.ToString().Replace("Size", "").Replace("p", ",").Replace("x", "×");

        private static string EtatDnp(DnpStatus status) =>
            status.IsCommunicationFailure ? "Hors ligne"
            : status.IsFault ? "Erreur — arrêtée"
            : status.NeedsOperator ? "Intervention nécessaire"
            : status.IsBusy ? "Impression en cours"
            : status.IsReady ? "Prête"
            : status.Message;

        /// <summary>Vrai pour une DNP : l'écran s'en sert pour expliquer une absence.</summary>
        public bool EstDnp { get; }

        /// <summary>
        /// La pastille d'état, MÊMES COULEURS pour toutes les machines : vert prête, bleu
        /// en train de tirer, orangé un geste à faire, rouge en panne, gris hors ligne,
        /// ardoise en veille.
        ///
        /// L'écran n'en portait aucune : tout y était du texte, et il fallait lire chaque
        /// ligne pour savoir laquelle des machines demandait quelque chose.
        /// </summary>
        public Brush EtatCouleur { get; } = Brushes.Transparent;

        private static Brush Pinceau(byte r, byte v, byte b)
        {
            var brosse = new SolidColorBrush(Color.FromRgb(r, v, b));
            brosse.Freeze();
            return brosse;
        }

        private static Brush CouleurSpouleur(EtatFileDnp etat) => etat switch
        {
            EtatFileDnp.Prete => Pinceau(0x2E, 0x6B, 0x33),
            EtatFileDnp.Impression => Pinceau(0x1B, 0x5E, 0x8A),
            EtatFileDnp.EnPause => Pinceau(0x8A, 0x62, 0x0E),
            EtatFileDnp.Erreur => Pinceau(0xB3, 0x26, 0x1E),
            EtatFileDnp.HorsLigne => Pinceau(0x4A, 0x4A, 0x4A),
            _ => Pinceau(0x37, 0x47, 0x4F),
        };

        public string Titre { get; }
        public string SousTitre { get; }
        public string Papier { get; }
        public string Compteur { get; }
        public string Alerte { get; }
        public Visibility AlerteVisibility { get; }
        public Visibility DetailVisibility { get; }
        public IReadOnlyList<SupplyRow> Consommables { get; }
        public IReadOnlyList<FormatRow> Formats { get; }

        private static string Etat(De100PrinterStatus status) => status switch
        {
            De100PrinterStatus.Ready => "Prête",
            De100PrinterStatus.Printing => "Impression en cours",
            De100PrinterStatus.Busy => "Occupée",
            De100PrinterStatus.Sleep => "En veille",
            De100PrinterStatus.ErrorProcessingCanBeContinued => "Erreur — le travail peut continuer",
            De100PrinterStatus.ErrorProcessingCannotBeContinued => "Erreur — travail interrompu",
            De100PrinterStatus.Offline => "Hors ligne",
            _ => status.ToString(),
        };
    }
}
