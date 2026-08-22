using System.Runtime.ExceptionServices;
using System.Text;
using Studio.Core.Cloud;

namespace Studio.Web.Dropbox;

/// <summary>Avancement d'un envoi, pour la barre de l'écran.</summary>
/// <param name="Faits">Fichiers déjà envoyés.</param>
/// <param name="Total">Fichiers à envoyer.</param>
/// <param name="Fichier">Nom du dernier fichier parti.</param>
/// <param name="Octets">Volume déjà envoyé.</param>
/// <param name="OctetsTotal">Volume à envoyer.</param>
public sealed record AvancementEnvoi(
    int Faits, int Total, string Fichier, long Octets = 0, long OctetsTotal = 0)
{
    /// <summary>
    /// La part faite, en OCTETS quand on connaît le volume et en fichiers sinon.
    ///
    /// En octets parce qu'un lot de photos n'est pas régulier : trente vignettes suivies de
    /// deux fichiers de reflex feraient une barre qui saute à 90 % puis n'avance plus
    /// pendant deux minutes. Le volume, lui, avance à la vitesse de la ligne.
    /// </summary>
    public double Part => OctetsTotal > 0
        ? Math.Clamp((double)Octets / OctetsTotal, 0, 1)
        : Total > 0 ? (double)Faits / Total : 0;
}

/// <summary>Ce qu'un envoi laisse au comptoir.</summary>
/// <param name="Url">Le lien à donner au client.</param>
/// <param name="Dossier">Le dossier créé dans le Dropbox du studio.</param>
/// <param name="Fichiers">Nombre de photos envoyées.</param>
/// <param name="Octets">Volume envoyé.</param>
/// <param name="Expire">Vrai si le lien porte bien une date d'expiration.</param>
/// <param name="Protege">Vrai si le lien porte bien un mot de passe.</param>
public sealed record ResultatEnvoi(
    string Url, string Dossier, int Fichiers, long Octets, bool Expire, bool Protege);

/// <summary>
/// Envoi d'un lot de photos au client par Dropbox : un dossier daté, les photos dedans, un
/// lien de partage.
///
/// C'est ce que « Dropbox Transfer » ferait s'il avait une API — voir
/// <see cref="DropboxSettings"/> pour l'état des lieux. Le résultat est le même du point de
/// vue du client : un lien, un dossier à télécharger, aucun compte à créer.
/// </summary>
public static class DropboxTransfer
{
    /// <summary>Journal optionnel, branché par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Envoie <paramref name="fichiers"/> et rend le lien de partage.
    /// </summary>
    /// <param name="nomDuLot">
    /// Nom donné au dossier créé, en plus de la date — le nom du client ou du dossier
    /// d'origine. C'est ce que le client verra en tête de son téléchargement.
    /// </param>
    /// <param name="avancement">Averti à chaque fichier ; peut être null.</param>
    public static async Task<ResultatEnvoi> EnvoyerAsync(
        DropboxSettings reglages,
        IReadOnlyList<string> fichiers,
        string nomDuLot,
        IProgress<AvancementEnvoi>? avancement = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reglages);
        ArgumentNullException.ThrowIfNull(fichiers);

        if (!reglages.EstUtilisable)
            throw new InvalidOperationException(
                $"L'envoi par Dropbox n'est pas configuré : il manque {reglages.CeQuiManque()}.");

        if (fichiers.Count == 0)
            throw new InvalidOperationException("Aucune photo à envoyer.");

        var jeton = await DropboxAuth.JetonDAccesAsync(reglages.AppKey, reglages.RefreshToken, ct);
        using var client = new DropboxClient(jeton);

        var racine = reglages.RacineNormalisee();
        var dossier = $"{racine}/{NomDeDossier(nomDuLot)}";

        // la racine d'abord : Dropbox crée bien les dossiers manquants au téléversement,
        // mais pas au PARTAGE — et c'est le dossier du lot qu'on partage
        if (racine.Length > 0) await client.CreerLeDossierAsync(racine, ct);
        await client.CreerLeDossierAsync(dossier, ct);

        // Les tailles d'abord, en un seul passage : elles servent à la barre d'avancement,
        // et les relire APRÈS chaque envoi obligerait à retoucher le disque au moment où
        // l'on veut justement lui laisser la place.
        var tailles = new long[fichiers.Count];
        long octetsTotal = 0;
        for (var i = 0; i < fichiers.Count; i++)
        {
            try { tailles[i] = new FileInfo(fichiers[i]).Length; }
            catch (IOException) { tailles[i] = 0; }
            octetsTotal += tailles[i];
        }

        long octets = 0;
        var faits = 0;

        var simultanes = reglages.EnvoisSimultanesReels;
        Log?.Invoke($"Dropbox : {fichiers.Count} fichier(s) à envoyer, {simultanes} de front.");

        // ⚠ PLUSIEURS FICHIERS DE FRONT, et c'est tout le gain de temps : un envoi seul
        // passe l'essentiel de sa vie à ATTENDRE — l'aller-retour TLS, l'accusé de Dropbox,
        // la fenêtre TCP qui remonte à chaque nouvelle connexion. Sur deux cents photos, ces
        // temps morts pèsent plus que les octets eux-mêmes, et la ligne montante du magasin
        // n'est jamais remplie. Quatre de front la remplissent.
        //
        // Le même DropboxClient sert à tous : HttpClient est fait pour être partagé, et
        // c'est lui qui garde les connexions ouvertes d'un fichier au suivant.
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, fichiers.Count),
                new ParallelOptions { MaxDegreeOfParallelism = simultanes, CancellationToken = ct },
                async (i, jeton) =>
                {
                    var fichier = fichiers[i];
                    var nom = Path.GetFileName(fichier);

                    await client.TeleverserAsync(fichier, $"{dossier}/{NomDeFichier(nom)}", jeton);

                    // compteurs partagés par plusieurs fils : rien ici ne peut être un « += »
                    var volume = Interlocked.Add(ref octets, tailles[i]);
                    var compte = Interlocked.Increment(ref faits);

                    avancement?.Report(
                        new AvancementEnvoi(compte, fichiers.Count, nom, volume, octetsTotal));
                });
        }
        catch (AggregateException ex) when (ex.InnerException is not null)
        {
            // Parallel.ForEachAsync empaquette les pannes ; l'écran, lui, affiche le message
            // tel quel — « Une ou plusieurs erreurs se sont produites » ne dit rien au
            // comptoir, alors que « le Dropbox du studio est plein » dit quoi faire.
            ExceptionDispatchInfo.Capture(ex.InnerExceptions[0]).Throw();
        }

        avancement?.Report(
            new AvancementEnvoi(fichiers.Count, fichiers.Count, "lien de partage…",
                octetsTotal, octetsTotal));

        var lien = await client.PartagerAsync(dossier, reglages.ExpirationJours, reglages.MotDePasse, ct);

        Log?.Invoke(
            $"Dropbox : {fichiers.Count} photo(s), {octets / 1024 / 1024} Mo, dossier « {dossier} », " +
            $"lien {lien.Url}" +
            (lien.Expire ? $", expire dans {reglages.ExpirationJours} j" : ", sans expiration") +
            (lien.Protege ? ", protégé par mot de passe" : ""));

        return new ResultatEnvoi(lien.Url, dossier, fichiers.Count, octets, lien.Expire, lien.Protege);
    }

    /// <summary>
    /// Le nom du dossier créé : la date d'abord, puis le nom du lot.
    ///
    /// La date en tête parce que le Dropbox du studio s'empile avec les mois, et qu'un tri
    /// alphabétique doit alors donner l'ordre chronologique — c'est ainsi qu'on retrouve
    /// « l'envoi de mardi dernier » sans chercher le nom du client.
    /// </summary>
    internal static string NomDeDossier(string nomDuLot)
    {
        var date = DateTime.Now.ToString("yyyy-MM-dd HHmm");
        var nom = Assainir(nomDuLot);

        return nom.Length > 0 ? $"{date} — {nom}" : date;
    }

    /// <summary>Nom de fichier accepté par Dropbox, en gardant le nom d'origine autant que possible.</summary>
    internal static string NomDeFichier(string nom)
    {
        var assaini = Assainir(Path.GetFileNameWithoutExtension(nom));
        var extension = Path.GetExtension(nom);

        return assaini.Length > 0 ? assaini + extension : "photo" + extension;
    }

    /// <summary>
    /// Retire ce que Dropbox n'accepte pas dans un nom.
    ///
    /// Les accents RESTENT — un studio français nomme ses dossiers « Séance Dupont », et les
    /// remplacer donnerait des noms illisibles au client. Ils voyagent d'ailleurs sans
    /// problème une fois l'en-tête échappé (voir <c>DropboxClient.EnTeteAscii</c>).
    /// </summary>
    private static string Assainir(string texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return "";

        var sortie = new StringBuilder(texte.Length);
        foreach (var c in texte.Trim())
        {
            // Dropbox refuse ces caractères, et le point final fait échouer la création
            if (c is '/' or '\\' or ':' or '?' or '*' or '"' or '<' or '>' or '|')
                sortie.Append(' ');
            else if (!char.IsControl(c))
                sortie.Append(c);
        }

        return sortie.ToString().Trim().TrimEnd('.');
    }
}
