using System.Diagnostics;
using System.Windows;

namespace Studio.App.Infrastructure;

/// <summary>
/// UN SEUL des deux logiciels à la fois sur un poste.
///
/// <b>Ce que ça évite, et ça a bloqué une DNP en boutique.</b> Studio Photo et Studio Photo
/// Identité pilotent les machines par le même relais 32 bits, et chacun DÉMARRE LE SIEN
/// (<c>De100BridgeClient.ConnectAsync</c>). Or le relais sert un <b>tube nommé à instance
/// unique</b> : le second à s'ouvrir ne crée pas le sien, il se branche sur le relais du
/// premier. Les deux applications se retrouvent donc à parler à un relais qui appartient à
/// l'une d'elles — et le jour où celle-là se ferme, elle l'emporte avec elle (le relais lui
/// est lié pour ne jamais lui survivre). L'autre garde une connexion morte et <b>plus rien
/// ne part à l'imprimante</b>, sans un mot.
///
/// C'est arrivé à Arcueil le 14/08/2026, le soir où Identité a été installé sur un poste qui
/// portait déjà le Studio.
///
/// La règle est donc simple, et elle se dit à l'ouverture plutôt que de se découvrir devant
/// un client : sur un poste, on ouvre l'un OU l'autre.
///
/// <b>Et depuis la 1.5.21, on n'est plus renvoyé à la main :</b> l'opérateur peut demander la
/// BASCULE — fermer l'autre et continuer, d'un clic. Voir <see cref="LaVoieEstLibre"/>.
/// </summary>
public static class UnSeulLogiciel
{
    /// <summary>Le nom d'exécutable du Studio complet, sans extension.</summary>
    private const string Studio = "Studio.App";

    /// <summary>Le nom d'exécutable du poste identité, sans extension.</summary>
    private const string Identite = "Studio.Identite";

    /// <summary>Le nom d'exécutable du relais 32 bits des machines, sans extension.</summary>
    private const string Relais = "Studio.De100Host";

    /// <summary>Ce qu'on laisse à l'autre application pour se fermer d'elle-même.</summary>
    private static readonly TimeSpan DelaiDeFermeture = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Ce qu'on laisse au relais pour disparaître APRÈS le départ de son application.
    ///
    /// Il est lié à elle par un Job Object, donc le système l'emporte — mais pas
    /// instantanément, et rouvrir pendant ce battement nous ferait tomber sur le tube du
    /// mourant. C'est exactement la panne qu'on cherche à éviter.
    /// </summary>
    private static readonly TimeSpan DelaiDuRelais = TimeSpan.FromSeconds(10);

    /// <summary>
    /// L'AUTRE logiciel, s'il tourne déjà sur ce poste. Null quand la voie est libre.
    /// </summary>
    /// <param name="moi">
    /// Le nom d'exécutable de l'application qui démarre, sans extension.
    /// </param>
    /// <returns>Son nom lisible, à montrer à l'opérateur.</returns>
    public static string? LAutreQuiTourne(string moi)
    {
        var autre = LAutre(moi);

        try
        {
            // On ne compte que les AUTRES processus : deux fenêtres du même logiciel ne se
            // disputent pas le relais, c'est le même client qui le tient.
            var vivants = Process.GetProcessesByName(autre);
            try
            {
                if (vivants.Length == 0) return null;
            }
            finally
            {
                foreach (var p in vivants) p.Dispose();
            }
        }
        catch (Exception)
        {
            // Pas de droit de lecture sur la liste des processus : on ne bloque rien. Se
            // tromper en refusant l'ouverture serait pire que le défaut qu'on prévient.
            return null;
        }

        return NomLisible(autre);
    }

    /// <summary>
    /// Le nom d'exécutable de l'AUTRE logiciel, sans extension.
    ///
    /// Volontairement symétrique : <c>LAutre(LAutre(x)) == x</c>. C'est ce qui permet aux deux
    /// applications d'appeler la même séquence en ne donnant que leur propre nom.
    /// </summary>
    public static string LAutre(string moi) =>
        moi.Equals(Identite, StringComparison.OrdinalIgnoreCase) ? Studio : Identite;

    /// <summary>Le nom sous lequel l'opérateur connaît ce logiciel.</summary>
    public static string NomLisible(string exe) =>
        exe.Equals(Studio, StringComparison.OrdinalIgnoreCase)
            ? "Studio Photo"
            : "Studio Photo Identité";

    /// <summary>Ce qu'a donné une demande de bascule.</summary>
    public enum Bascule
    {
        /// <summary>La voie était déjà libre : il n'y avait rien à fermer.</summary>
        RienAFermer,

        /// <summary>L'autre est parti, relais compris. On peut ouvrir.</summary>
        Ferme,

        /// <summary>Il n'a pas rendu la main dans le délai. Rien n'a été forcé.</summary>
        Echec,
    }

    /// <summary>
    /// La voie est-elle libre pour ouvrir ? Pose la question à l'opérateur quand l'autre
    /// logiciel tourne, et bascule s'il le demande.
    ///
    /// C'est ici que vit toute la séquence — les deux applications n'ont qu'à l'appeler, avec
    /// leur propre nom d'exécutable et le titre de leurs boîtes de dialogue.
    /// </summary>
    /// <param name="moi">Le nom d'exécutable de l'application qui démarre, sans extension.</param>
    /// <param name="titre">Le titre des boîtes de dialogue de cette application.</param>
    /// <returns>Vrai quand le démarrage peut se poursuivre.</returns>
    public static bool LaVoieEstLibre(string moi, string titre)
    {
        if (LAutreQuiTourne(moi) is not { } autre) return true;

        var reponse = MessageBox.Show(
            $"{autre} est déjà ouvert sur ce poste.\n\n" +
            "Les deux logiciels pilotent les imprimantes par le même relais, et ouverts en " +
            "même temps ils se le disputent : les tirages cessent de partir.\n\n" +
            $"Fermer {autre} et continuer ?\n\n" +
            "Vérifiez qu'aucun tirage n'est en cours avant de confirmer.",
            titre, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

        if (reponse != MessageBoxResult.Yes)
        {
            FileLog.Write($"Ouverture abandonnée : {autre} tourne déjà, la bascule a été refusée.");
            return false;
        }

        if (FermerLAutre(moi) is Bascule.Ferme or Bascule.RienAFermer)
        {
            FileLog.Write($"Bascule : {autre} fermé et son relais parti, l'ouverture se poursuit.");
            return true;
        }

        MessageBox.Show(
            $"{autre} ne s'est pas fermé.\n\n" +
            "Fermez-le à la main, puis rouvrez celui-ci. S'il ne répond pas, c'est peut-être " +
            "qu'un tirage est en cours.\n\n" +
            "Rien n'a été forcé : aucune impression n'a été interrompue.",
            titre, MessageBoxButton.OK, MessageBoxImage.Warning);

        FileLog.Write($"Bascule impossible : {autre} n'a pas rendu la main dans le délai imparti.");
        return false;
    }

    /// <summary>
    /// Demande à l'autre logiciel de se fermer, et attend que son relais ait disparu.
    ///
    /// <b>On DEMANDE, on ne tue jamais.</b> Un <c>Kill</c> sur une application en train
    /// d'envoyer un tirage laisserait la commande à moitié partie — et c'est bien pire que de
    /// renoncer, parce que personne ne saurait ce qui est sorti. L'opérateur, lui, sait s'il
    /// peut fermer : c'est pour ça qu'on le lui demande d'abord.
    ///
    /// ⚠ Cet appel BLOQUE, jusqu'à une trentaine de secondes dans le pire des cas. C'est
    /// assumé : on est au démarrage, avant la moindre fenêtre, et l'attente ordinaire est de
    /// deux ou trois secondes — le temps que l'autre ferme et que le système emporte son relais.
    /// </summary>
    public static Bascule FermerLAutre(string moi)
    {
        Process[] vivants;
        try
        {
            vivants = Process.GetProcessesByName(LAutre(moi));
        }
        catch (Exception)
        {
            return Bascule.Echec;
        }

        if (vivants.Length == 0) return Bascule.RienAFermer;

        try
        {
            foreach (var p in vivants)
            {
                try
                {
                    p.CloseMainWindow();
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                              or System.ComponentModel.Win32Exception)
                {
                    // déjà parti, ou hors de portée : l'attente ci-dessous tranchera
                }
            }

            // Un seul délai pour TOUT le monde, pas un par processus : deux fenêtres du même
            // logiciel se ferment ensemble, et l'opérateur n'a pas à attendre deux fois.
            var limite = DateTime.UtcNow + DelaiDeFermeture;

            foreach (var p in vivants)
            {
                var reste = limite - DateTime.UtcNow;
                if (reste < TimeSpan.Zero) reste = TimeSpan.Zero;

                try
                {
                    if (!p.WaitForExit((int)reste.TotalMilliseconds)) return Bascule.Echec;
                }
                catch (Exception ex) when (ex is InvalidOperationException
                                              or SystemException)
                {
                    // hors de portée : on ne peut pas l'observer, donc on ne conclut pas à
                    // l'échec — le contrôle du relais qui suit est le vrai juge.
                }
            }
        }
        finally
        {
            foreach (var p in vivants) p.Dispose();
        }

        return AttendreQueLeRelaisSoitParti() ? Bascule.Ferme : Bascule.Echec;
    }

    /// <summary>
    /// Attend qu'aucun relais ne tourne plus.
    ///
    /// <b>C'est l'étape qui fait toute la valeur de la bascule.</b> Le relais est lié à son
    /// application par un Job Object, donc il s'en va — mais après elle. Ouvrir pendant ce
    /// battement nous brancherait sur le tube d'un relais en train de mourir : précisément la
    /// panne muette d'Arcueil, et elle serait d'autant plus vicieuse qu'on l'aurait provoquée
    /// en croyant bien faire.
    /// </summary>
    private static bool AttendreQueLeRelaisSoitParti()
    {
        var limite = DateTime.UtcNow + DelaiDuRelais;

        while (true)
        {
            Process[] relais;
            try
            {
                relais = Process.GetProcessesByName(Relais);
            }
            catch (Exception)
            {
                // Pas de droit de regard sur les processus : même raison que plus haut, on ne
                // bloque pas une ouverture sur ce qu'on n'a pas pu vérifier.
                return true;
            }

            try
            {
                if (relais.Length == 0) return true;
            }
            finally
            {
                foreach (var p in relais) p.Dispose();
            }

            if (DateTime.UtcNow >= limite) return false;
            Thread.Sleep(250);
        }
    }
}
