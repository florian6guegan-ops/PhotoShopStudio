using System.Globalization;
using System.IO.Compression;
using System.Net.Mail;
using System.Text;
using Studio.Core.Mail;

namespace Studio.Printing;

/// <summary>
/// Le rapport qu'un poste envoie quand quelque chose ne va pas.
///
/// <b>Pourquoi.</b> Les journaux sont ce qui a permis de trouver le liseré blanc et le
/// « Pipe is broken » — mais ils étaient sur le poste de la boutique, sous la main. Sur le
/// poste d'un collègue, personne ne va lire <c>D:\PhotoStudioData\logs</c> : le défaut est
/// signalé au téléphone, en mots, et rien ne permet de le diagnostiquer.
///
/// <b>Rien ne part tout seul.</b> C'est un geste de l'opérateur — un bouton, quand ça ne va
/// pas — et non un envoi automatique : le poste travaille sur les photos de clients, et
/// l'on n'expédie pas leurs données sans que personne l'ait demandé. Le rapport dit
/// d'ailleurs exactement ce qu'il contient avant de partir.
///
/// <b>Aucune photo n'est jointe</b>, jamais. Les journaux nomment des fichiers ; ils n'en
/// transportent aucun.
/// </summary>
public static class RapportDiagnostic
{
    /// <summary>Jours de journal repris dans le rapport.</summary>
    /// <remarks>
    /// Sept, comme la liste des commandes récentes : un défaut signalé le lundi s'est
    /// souvent produit le samedi. Au-delà, le rapport grossit sans rien apprendre.
    /// </remarks>
    public const int JoursDeJournal = 7;

    /// <summary>
    /// Taille au-delà de laquelle un journal est TRONQUÉ, en octets.
    ///
    /// Un serveur de courriel refuse couramment au-delà de 25 Mo, et une journée chargée
    /// produit plusieurs mégaoctets. On garde la FIN du fichier : c'est là que se trouve
    /// ce qui vient de se passer.
    /// </summary>
    private const long MaxParJournal = 2 * 1024 * 1024;

    /// <summary>Ce que le rapport emporte, pour pouvoir le dire à l'opérateur AVANT l'envoi.</summary>
    /// <param name="Fichiers">Noms des journaux repris.</param>
    /// <param name="Octets">Taille de l'archive.</param>
    public sealed record Contenu(IReadOnlyList<string> Fichiers, long Octets)
    {
        public string TailleLisible => Octets < 1024 * 1024
            ? $"{Octets / 1024.0:0} Ko"
            : $"{Octets / (1024.0 * 1024):0.0} Mo";
    }

    /// <summary>
    /// Fabrique l'archive du rapport et rend ce qu'elle contient.
    /// </summary>
    /// <param name="dossierJournaux">Dossier des journaux du poste.</param>
    /// <param name="dossierConfig">
    /// Dossier de configuration. Les réglages y sont repris <b>sauf ceux qui portent un
    /// secret</b> — voir <see cref="EstSensible"/>.
    /// </param>
    /// <param name="destination">Chemin de l'archive à écrire.</param>
    /// <param name="note">Ce que l'opérateur a saisi pour décrire le problème.</param>
    public static Contenu Fabriquer(
        string dossierJournaux, string dossierConfig, string destination, string note = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var dossier = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);
        if (File.Exists(destination)) File.Delete(destination);

        var repris = new List<string>();

        using (var archive = ZipFile.Open(destination, ZipArchiveMode.Create))
        {
            // ce que l'opérateur a écrit, en premier : c'est ce qu'on lit d'abord
            Ecrire(archive, "rapport.txt", EnTete(note));

            foreach (var journal in JournauxRecents(dossierJournaux))
            {
                try
                {
                    Ecrire(archive, $"logs/{Path.GetFileName(journal)}", Queue(journal));
                    repris.Add(Path.GetFileName(journal));
                }
                catch (Exception)
                {
                    // fichier verrouillé par l'écriture en cours : on prend les autres
                }
            }

            foreach (var reglage in ReglagesRepris(dossierConfig))
            {
                try
                {
                    Ecrire(archive, $"config/{Path.GetFileName(reglage)}",
                        File.ReadAllText(reglage));
                    repris.Add(Path.GetFileName(reglage));
                }
                catch (Exception)
                {
                    // idem : un réglage illisible ne doit pas empêcher le rapport
                }
            }
        }

        return new Contenu(repris, new FileInfo(destination).Length);
    }

    /// <summary>
    /// Envoie le rapport par courriel, en pièce jointe.
    ///
    /// Emprunte la voie SMTP déjà configurée pour les clients : un poste qui sait envoyer
    /// leurs photos sait envoyer son rapport, et il n'y a rien de plus à régler.
    /// </summary>
    /// <param name="destinataire">L'adresse de celui qui suit le logiciel.</param>
    public static void Envoyer(
        MailSettings reglages, string destinataire, string archive, string note = "")
    {
        ArgumentNullException.ThrowIfNull(reglages);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinataire);

        if (!reglages.EstUtilisable)
            throw new InvalidOperationException(
                "L'envoi par courriel n'est pas configuré : " + reglages.CeQuiManque() +
                ".\n\nOuvrez Paramètres → Envoi par courriel pour le renseigner.");

        if (!File.Exists(archive))
            throw new FileNotFoundException("Rapport introuvable.", archive);

        using var message = new MailMessage
        {
            From = new MailAddress(reglages.Expediteur, reglages.NomExpediteur),
            // le poste et le jour dans le SUJET : c'est ce qui permet de trier les
            // rapports de plusieurs boutiques sans les ouvrir
            Subject = $"Studio Photo — rapport de {Environment.MachineName} " +
                      $"du {DateTime.Now:dd/MM/yyyy HH:mm}",
            Body = EnTete(note),
            IsBodyHtml = false,
        };

        message.To.Add(destinataire.Trim());
        message.Attachments.Add(new Attachment(archive));

        PhotoMailer.Expedier(reglages, message, destinataire, 1);
    }

    /// <summary>Nom d'archive daté, pour que deux rapports ne s'écrasent pas.</summary>
    public static string NomPropose() =>
        $"studio-rapport-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.zip";

    /// <summary>
    /// Ce que le rapport dit de lui-même : le poste, la version, et le mot de l'opérateur.
    ///
    /// La VERSION est le renseignement le plus utile de tous — la première question devant
    /// un défaut est toujours « sur quelle version ? ».
    /// </summary>
    private static string EnTete(string note)
    {
        var texte = new StringBuilder();

        texte.AppendLine("Studio Photo — rapport de diagnostic");
        texte.AppendLine(CultureInfo.InvariantCulture, $"Poste       : {Environment.MachineName}");
        texte.AppendLine(CultureInfo.InvariantCulture, $"Utilisateur : {Environment.UserName}");
        texte.AppendLine(CultureInfo.InvariantCulture, $"Windows     : {Environment.OSVersion.VersionString}");
        texte.AppendLine(CultureInfo.InvariantCulture, $"Version     : {Version()}");
        texte.AppendLine(CultureInfo.InvariantCulture, $"Date        : {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        texte.AppendLine();

        texte.AppendLine("Imprimantes vues par Windows :");
        foreach (var imprimante in DetectionImprimantes.Detecter())
        {
            var reconnue = imprimante.Motif.Length == 0 ? "" : $" — {imprimante.Motif}";
            texte.AppendLine($"  [{imprimante.Role}] {imprimante.Nom}{reconnue}");
        }
        texte.AppendLine();

        if (!string.IsNullOrWhiteSpace(note))
        {
            texte.AppendLine("Ce que dit l'opérateur :");
            texte.AppendLine(note.Trim());
        }

        return texte.ToString();
    }

    private static string Version() =>
        typeof(RapportDiagnostic).Assembly.GetName().Version?.ToString() ?? "inconnue";

    private static IEnumerable<string> JournauxRecents(string dossier)
    {
        if (!Directory.Exists(dossier)) return [];

        var depuis = DateTime.Now.AddDays(-JoursDeJournal);

        try
        {
            return Directory.EnumerateFiles(dossier, "*.log")
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTime >= depuis)
                .OrderByDescending(f => f.LastWriteTime)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Les réglages du poste, SAUF ceux qui portent un secret.
    ///
    /// <b>Ce filtre est le point à ne pas défaire.</b> <c>mail.json</c> contient le mot de
    /// passe d'application de la boîte du magasin et <c>dropbox.json</c> le jeton d'accès :
    /// les joindre les enverrait en clair, par courriel, à chaque rapport.
    /// </summary>
    private static IEnumerable<string> ReglagesRepris(string dossierConfig)
    {
        if (!Directory.Exists(dossierConfig)) return [];

        try
        {
            return Directory.EnumerateFiles(dossierConfig, "*.json")
                .Where(f => !EstSensible(Path.GetFileName(f)))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Un réglage porte-t-il un secret ? <b>En cas de doute, OUI.</b>
    ///
    /// Publique à dessein : c'est la garantie que donne le rapport, et elle doit pouvoir
    /// être vérifiée du dehors — la sonde <c>RapportProbe</c> s'en sert pour contrôler une
    /// archive fabriquée sur les vraies données du poste.
    /// </summary>
    public static bool EstSensible(string nomFichier)
    {
        var minuscule = nomFichier.ToLowerInvariant();

        return minuscule.Contains("mail")
            || minuscule.Contains("dropbox")
            || minuscule.Contains("wifi")      // la clé du réseau du magasin
            || minuscule.Contains("secret")
            || minuscule.Contains("token")
            || minuscule.Contains("password");
    }

    /// <summary>
    /// La FIN d'un fichier, quand il dépasse la taille admise : c'est là que se trouve ce
    /// qui vient de se passer.
    /// </summary>
    private static string Queue(string chemin)
    {
        var info = new FileInfo(chemin);

        // partagé en lecture ET en écriture : le journal du jour est ouvert par
        // l'application elle-même, et sans cela le rapport échouerait sur le seul fichier
        // qui compte
        using var flux = new FileStream(
            chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (info.Length > MaxParJournal)
            flux.Seek(-MaxParJournal, SeekOrigin.End);

        using var lecteur = new StreamReader(flux);
        var contenu = lecteur.ReadToEnd();

        return info.Length > MaxParJournal
            ? $"[… début tronqué, {info.Length / 1024} Ko au total …]\n{contenu}"
            : contenu;
    }

    private static void Ecrire(ZipArchive archive, string nom, string contenu)
    {
        var entree = archive.CreateEntry(nom, CompressionLevel.Optimal);
        using var flux = entree.Open();
        using var ecrivain = new StreamWriter(flux, new UTF8Encoding(false));
        ecrivain.Write(contenu);
    }
}
