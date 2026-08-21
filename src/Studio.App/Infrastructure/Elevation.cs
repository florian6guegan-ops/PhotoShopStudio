using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Windows;

namespace Studio.App.Infrastructure;

/// <summary>
/// Le logiciel tourne-t-il en ADMINISTRATEUR — et que faire quand non.
///
/// <b>Pourquoi ça compte.</b> Les SDK des machines écrivent sous
/// <c>HKLM\SOFTWARE\…\FUJIFILM\Frontier\…</c>, une clé en lecture seule pour les
/// utilisateurs ordinaires. Sans élévation, le SDK échoue en <c>RegCreateKeyEx error.
/// Code = 5</c> — accès refusé — et c'est exactement ce qui avait coûté une journée
/// d'enquête à Créteil le 10/08/2026, du côté de DiLand cette fois : lancé sans son
/// lanceur élevé, il chargeait à l'infini et bloquait le minilab pour tout le monde.
///
/// DiLand règle la question par un lanceur qui porte le drapeau <c>RUNASADMIN</c>
/// (<c>FitEng.Base.Starter.exe</c>). Nous n'avions rien : ni manifeste, ni contrôle, et un
/// raccourci de bureau qui pointe droit sur l'exécutable. Le poste identité tournait donc
/// sans droits dès que quelqu'un refaisait le raccourci ou déplaçait l'application, et le
/// défaut ne se voyait qu'AU MOMENT D'IMPRIMER : plusieurs boîtes d'erreur, et le tirage
/// qui ne part pas tant qu'on ne les a pas toutes fermées — devant le client.
///
/// <b>On demande, on ne force pas.</b> Un manifeste <c>requireAdministrator</c> aurait
/// l'air plus simple, mais il rendrait l'application INLANÇABLE sur un poste dont le compte
/// n'est pas administrateur — et il y a quatre boutiques, dont deux qu'on ne joignait pas
/// le jour où ceci a été écrit. Une question au démarrage se répond en un clic quand le
/// compte a les droits, et laisse travailler quand il ne les a pas.
///
/// <b>Au démarrage, et pas à l'impression.</b> C'est toute la différence : l'opérateur est
/// prévenu quand il ouvre sa journée, pas quand un client attend sa photo.
/// </summary>
public static class Elevation
{
    /// <summary>
    /// Vrai quand le processus tourne avec les droits d'administrateur.
    ///
    /// Mesuré UNE fois : les droits d'un processus ne changent pas en cours de route, et
    /// c'est une question qu'on pose à Windows.
    /// </summary>
    public static bool EstAdministrateur { get; } = Mesurer();

    private static bool Mesurer()
    {
        try
        {
            using var identite = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identite).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            // ⚠ ON SUPPOSE QUE OUI quand on ne sait pas.
            //
            // Poser la question à l'opérateur sur la foi d'une mesure qui vient d'échouer,
            // c'est risquer de lui faire relancer l'application tous les matins pour rien.
            // Le défaut qu'on cherche à éviter se rattrape ; une question quotidienne sans
            // fondement, non.
            FileLog.Write("Droits du processus illisibles : on suppose l'élévation", ex);
            return true;
        }
    }

    /// <summary>
    /// S'assure que le logiciel a les droits qu'il lui faut, en proposant de le relancer.
    ///
    /// À appeler TÔT — avant de composer les services et d'ouvrir la moindre fenêtre : ce
    /// qui a été monté ici serait à refaire dans l'instance élevée, et le relais des
    /// machines n'a qu'une place.
    /// </summary>
    /// <param name="titre">Le titre des boîtes de dialogue de cette application-ci.</param>
    /// <returns>
    /// <b>Faux quand l'appelant doit s'arrêter</b> : une instance élevée vient d'être
    /// lancée et prend la suite. Vrai dans tous les autres cas — déjà élevé, relance
    /// refusée, ou impossible : on continue alors sans les droits, ce qui vaut mieux que de
    /// laisser le comptoir sans logiciel.
    /// </returns>
    public static bool AssurerLesDroits(string titre)
    {
        if (EstAdministrateur) return true;

        FileLog.Write("Démarrage SANS les droits d'administrateur : les machines risquent " +
                      "de refuser les tirages.");

        var reponse = MessageBox.Show(
            "Ce logiciel n'a pas été lancé en tant qu'administrateur.\n\n" +
            "Les imprimantes photo en ont besoin : sans ces droits, le tirage s'arrête sur " +
            "des messages d'erreur au moment d'imprimer, devant le client.\n\n" +
            "Relancer en administrateur maintenant ?",
            titre, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes);

        if (reponse != MessageBoxResult.Yes)
        {
            FileLog.Write("Relance en administrateur refusée : on continue sans les droits.");
            return true;
        }

        return !Relancer(titre);
    }

    /// <summary>
    /// Ce que la nouvelle instance reçoit pour savoir qui elle remplace : le numéro du
    /// processus qui vient de la lancer.
    /// </summary>
    private const string DrapeauRelance = "--relance-de";

    /// <summary>
    /// Attend que l'instance qu'on remplace ait fini de se retirer.
    ///
    /// <b>Sans cela, la relance échange une boîte de dialogue contre une autre.</b>
    /// <see cref="UnSeulLogiciel.LaVoieEstLibre"/> ne cherche pas seulement l'AUTRE
    /// logiciel : il compte aussi les autres instances du sien, et annonce « Un autre
    /// Studio Photo Identité est déjà ouvert ». Or il y en a forcément une pendant la
    /// seconde où l'ancienne se retire — c'est elle qui vient de nous lancer. L'opérateur
    /// aurait donc vu une question de plus, et sur son propre logiciel.
    ///
    /// On attend le processus NOMMÉ, et pas un délai en l'air : il est parti quand il est
    /// parti. La borne des dix secondes n'est là que pour le cas où il resterait bloqué —
    /// mieux vaut alors poser la question d'<c>UnSeulLogiciel</c>, qui sait la traiter, que
    /// d'attendre indéfiniment devant une fenêtre vide.
    ///
    /// Sans le drapeau — le cas ordinaire, un lancement depuis le bureau — ne fait rien.
    /// </summary>
    public static void AttendreLInstanceRemplacee(string[]? args)
    {
        if (PidARemplacer(args) is not { } pid) return;

        try
        {
            using var precedente = Process.GetProcessById(pid);
            precedente.WaitForExit(10_000);
            FileLog.Write($"Instance remplacée ({pid}) retirée : la relance élevée prend la suite.");
        }
        catch (ArgumentException)
        {
            // déjà parti : c'est exactement ce qu'on attendait
        }
        catch (Exception ex)
        {
            FileLog.Write($"Attente de l'instance remplacée ({pid}) impossible", ex);
        }
    }

    /// <summary>
    /// Le numéro de processus que la ligne de commande désigne, ou null quand elle n'en
    /// désigne aucun.
    ///
    /// <b>Séparé du reste pour être ESSAYABLE</b> : c'est un contrat entre deux
    /// lancements du même logiciel, à une seconde d'intervalle, et qui ne se voit nulle
    /// part quand il casse — l'application démarrerait simplement en posant une question
    /// de trop. Le genre de règle qu'on ne veut pas laisser vivre dans un démarrage que
    /// rien ne couvre.
    ///
    /// Un numéro absent, illisible ou négatif ne vaut pas mieux qu'aucun drapeau : on
    /// n'attend personne plutôt que d'attendre n'importe qui.
    /// </summary>
    public static int? PidARemplacer(string[]? args)
    {
        if (args is null) return null;

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], DrapeauRelance, StringComparison.Ordinal)) continue;

            return int.TryParse(args[i + 1], out var pid) && pid > 0 ? pid : null;
        }

        return null;
    }

    /// <summary>
    /// Relance CE MÊME exécutable en demandant l'élévation.
    /// </summary>
    /// <returns>Vrai quand la nouvelle instance est partie et que celle-ci doit se retirer.</returns>
    private static bool Relancer(string titre)
    {
        // Le vrai fichier .exe, et non l'assembly : c'est lui que Windows sait relancer, et
        // c'est le seul chemin correct pour une application publiée en autonome.
        if (Environment.ProcessPath is not { Length: > 0 } exe)
        {
            FileLog.Write("Relance impossible : le chemin de l'exécutable est inconnu.");
            return false;
        }

        try
        {
            // UseShellExecute est OBLIGATOIRE pour « runas » : c'est le shell qui porte
            // l'élévation, pas le créateur de processus.
            var demarrage = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };

            // Elle saura QUI elle remplace, et l'attendra au lieu de demander à l'opérateur
            // de fermer un logiciel qui est déjà en train de partir. Voir
            // AttendreLInstanceRemplacee.
            demarrage.ArgumentList.Add(DrapeauRelance);
            demarrage.ArgumentList.Add(Environment.ProcessId.ToString());

            Process.Start(demarrage);

            FileLog.Write("Relancé en administrateur ; cette instance-ci se retire.");
            return true;
        }
        catch (Win32Exception ex)
        {
            // 1223 = l'opérateur a fermé la fenêtre de Windows, ou n'a pas le mot de passe.
            // Ce n'est pas une panne : c'est une réponse, et elle se respecte en silence.
            if (ex.NativeErrorCode == 1223)
            {
                FileLog.Write("Élévation refusée à l'invite de Windows : on continue sans.");
                return false;
            }

            FileLog.Write("Relance en administrateur impossible", ex);

            MessageBox.Show(
                $"La relance en administrateur a échoué : {ex.Message}\n\n" +
                "Le logiciel reste ouvert. Pour le lancer avec les droits : clic droit sur " +
                "son raccourci, « Exécuter en tant qu'administrateur ».",
                titre, MessageBoxButton.OK, MessageBoxImage.Warning);

            return false;
        }
        catch (Exception ex)
        {
            FileLog.Write("Relance en administrateur impossible", ex);
            return false;
        }
    }
}
