using System.Globalization;
using Studio.Core.Cloud;

namespace Studio.Web.Dropbox;

/// <summary>
/// Retire du Dropbox du studio les dossiers d'envoi devenus vieux.
///
/// <b>Pourquoi c'est nous qui le faisons.</b> Dropbox ne sait pas supprimer tout seul au
/// bout de N jours : sa seule notion de péremption est la date d'expiration du LIEN, qui
/// ferme l'accès sans rien effacer — et qui demande en plus un compte payant. Sur un compte
/// gratuit de 2 Go, trois mariages suffisent à le remplir, et l'envoi suivant échoue au
/// comptoir. Le ménage est donc la seule chose qui tienne le compte en état, et c'est la
/// seule qui marche sur toutes les offres.
///
/// <b>Ce qui le rend sûr.</b> Trois verrous, et il faut les trois :
///
/// 1. on ne regarde QUE sous la racine réglée, jamais ailleurs dans le Dropbox ;
/// 2. on ne supprime QUE les dossiers dont le nom est celui que nous écrivons —
///    « AAAA-MM-JJ hhmm » éventuellement suivi du nom du lot. Un dossier posé à la main par
///    la boutique ne porte pas ce nom, et il est donc épargné ;
/// 3. la date vient du NOM et non des métadonnées : Dropbox ne donne pas de date de
///    création pour un dossier, et sa date de modification remonterait au moindre passage.
///
/// Ce que Dropbox supprime part dans SA corbeille, récupérable trente jours depuis le site :
/// une erreur de réglage se rattrape.
/// </summary>
public static class DropboxMenage
{
    /// <summary>Journal optionnel, branché par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Format exact des dossiers que <see cref="DropboxTransfer"/> crée.</summary>
    private const string FormatDeDate = "yyyy-MM-dd HHmm";

    /// <summary>Ce qu'a fait un passage de ménage.</summary>
    /// <param name="Supprimes">Dossiers retirés.</param>
    /// <param name="Gardes">Dossiers encore dans leur délai.</param>
    /// <param name="Ignores">Dossiers dont le nom n'est pas le nôtre : jamais touchés.</param>
    public sealed record Bilan(int Supprimes, int Gardes, int Ignores);

    /// <summary>
    /// Supprime les dossiers d'envoi plus vieux que <see cref="DropboxSettings.RetentionJours"/>.
    ///
    /// Ne lève pas : c'est une tâche de fond, et une panne de réseau ne doit pas remonter
    /// jusqu'à un opérateur qui est en train de servir quelqu'un. Ce qui rate part au
    /// journal et sera retenté au passage suivant.
    /// </summary>
    /// <param name="maintenant">L'heure de référence. Paramétrable pour être vérifiable.</param>
    public static async Task<Bilan> FaireLeMenageAsync(
        DropboxSettings reglages, DateTime? maintenant = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reglages);

        if (!reglages.EstUtilisable || reglages.RetentionJours <= 0)
            return new Bilan(0, 0, 0);

        try
        {
            var jeton = await DropboxAuth.JetonDAccesAsync(reglages.AppKey, reglages.RefreshToken, ct);
            using var client = new DropboxClient(jeton);

            var racine = reglages.RacineNormalisee();
            var dossiers = await client.ListerLesDossiersAsync(racine, ct);

            var limite = (maintenant ?? DateTime.Now).AddDays(-reglages.RetentionJours);
            var supprimes = 0;
            var gardes = 0;
            var ignores = 0;

            foreach (var dossier in dossiers)
            {
                ct.ThrowIfCancellationRequested();

                if (DateDuDossier(dossier.Nom) is not { } date)
                {
                    ignores++;
                    continue;
                }

                if (date > limite)
                {
                    gardes++;
                    continue;
                }

                try
                {
                    await client.SupprimerAsync(dossier.Chemin, ct);
                    supprimes++;
                    Log?.Invoke($"Dropbox : dossier « {dossier.Nom} » supprimé ({date:dd/MM/yyyy}).");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // un dossier qui résiste ne doit pas arrêter les autres : il repassera
                    Log?.Invoke($"Dropbox : « {dossier.Nom} » n'a pas pu être supprimé — {ex.Message}");
                }
            }

            if (supprimes > 0 || ignores > 0)
                Log?.Invoke(
                    $"Dropbox : ménage fait — {supprimes} dossier(s) supprimé(s), {gardes} gardé(s), " +
                    $"{ignores} laissé(s) de côté (nom qui n'est pas le nôtre).");

            return new Bilan(supprimes, gardes, ignores);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Dropbox : ménage impossible — {ex.Message}");
            return new Bilan(0, 0, 0);
        }
    }

    /// <summary>
    /// La date portée par le nom d'un dossier d'envoi, ou null si ce n'est pas un des nôtres.
    ///
    /// C'est LE garde-fou : tout ce qui ne s'analyse pas est laissé en place. Un dossier que
    /// la boutique aurait rangé là à la main — « Archives », « Mariage Durand » — ne porte
    /// pas cette date en tête et ne sera jamais supprimé.
    /// </summary>
    public static DateTime? DateDuDossier(string nom)
    {
        if (string.IsNullOrWhiteSpace(nom) || nom.Length < FormatDeDate.Length) return null;

        return DateTime.TryParseExact(
            nom[..FormatDeDate.Length], FormatDeDate,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }
}
