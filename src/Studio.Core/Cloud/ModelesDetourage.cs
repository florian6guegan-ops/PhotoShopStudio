namespace Studio.Core.Cloud;

/// <summary>
/// Le téléchargement des modèles de détourage, publiés À PART des versions de Studio.
///
/// <b>Pourquoi ils ne sont pas dans l'archive.</b> Le modèle courant pèse 109 Mo, soit la
/// moitié de l'application. L'embarquer ferait retélécharger un demi-gigaoctet à chaque
/// correction d'un défaut d'impression, sur des postes de boutique qui partagent souvent
/// la connexion de la caisse. L'écran des réglages le disait déjà — « un demi-gigaoctet
/// n'a rien à faire dans le logiciel » — mais il demandait alors de poser le fichier à la
/// main, ce que personne n'a fait : le poste de Créteil a tourné sans détourage depuis son
/// installation, et le réglage était resté sur la méthode par couleur.
///
/// <b>Une publication qu'on ne remplace jamais.</b> Les modèles vivent sous une étiquette
/// fixe, séparée des versions : son adresse ne change pas, et toutes les versions de
/// Studio — passées comme futures — y puisent. Les rattacher à la dernière publication
/// obligerait à les renvoyer à chaque fois, 109 Mo à chaque parution, et un oubli les
/// rendrait introuvables pour tout le parc.
/// </summary>
public static class ModelesDetourage
{
    /// <summary>
    /// L'étiquette de la publication qui les porte. Fixe, et c'est tout l'intérêt :
    /// <c>tools\Publier.ps1</c> ne la touche pas.
    /// </summary>
    public const string Etiquette = "modeles-v1";

    /// <summary>Adresse d'un modèle, par son nom de fichier.</summary>
    public static string Adresse(string nomDeFichier) =>
        $"https://github.com/{MiseAJour.Depot}/releases/download/{Etiquette}/{nomDeFichier}";

    /// <summary>
    /// Télécharge un modèle dans <paramref name="dossierCible"/>.
    ///
    /// <b>Écrit à côté, puis renommé.</b> Un téléchargement de 109 Mo interrompu — réseau
    /// coupé, poste éteint, opérateur pressé — laisserait sinon un fichier tronqué portant
    /// le bon nom : le moteur le chargerait, échouerait, et retomberait sur la méthode par
    /// couleur sans que rien n'explique pourquoi. Un fichier partiel ne prend jamais le nom
    /// du modèle.
    /// </summary>
    /// <param name="client">Fourni par l'appelant, pour être vérifiable sans réseau.</param>
    /// <param name="nomDeFichier">Par exemple <c>birefnet-lite-fp16.onnx</c>.</param>
    /// <param name="dossierCible">Le <c>models\</c> du dossier de données.</param>
    /// <param name="avancement">Fraction téléchargée, de 0 à 1, quand la taille est connue.</param>
    /// <returns>Le chemin du modèle installé.</returns>
    public static async Task<string> TelechargerAsync(
        HttpClient client,
        string nomDeFichier,
        string dossierCible,
        IProgress<double>? avancement = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(nomDeFichier);
        ArgumentException.ThrowIfNullOrWhiteSpace(dossierCible);

        Directory.CreateDirectory(dossierCible);

        var cible = Path.Combine(dossierCible, nomDeFichier);
        var partiel = cible + ".part";

        using var reponse = await client.GetAsync(
            Adresse(nomDeFichier), HttpCompletionOption.ResponseHeadersRead, ct);

        reponse.EnsureSuccessStatusCode();

        var total = reponse.Content.Headers.ContentLength;

        await using (var source = await reponse.Content.ReadAsStreamAsync(ct))
        await using (var sortie = File.Create(partiel))
        {
            var tampon = new byte[81920];
            long recus = 0;
            int lus;

            while ((lus = await source.ReadAsync(tampon, ct)) > 0)
            {
                await sortie.WriteAsync(tampon.AsMemory(0, lus), ct);
                recus += lus;

                if (total is > 0) avancement?.Report((double)recus / total.Value);
            }
        }

        File.Move(partiel, cible, overwrite: true);
        return cible;
    }
}
