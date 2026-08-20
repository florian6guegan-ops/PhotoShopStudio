using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Studio.App.Infrastructure;

/// <summary>
/// La photo du client, mise à l'abri sous <c>cache\travail\&lt;jour&gt;\</c> — comment on la
/// nomme, et comment on la désigne auprès du cache des masques de détourage.
///
/// <b>Les deux vont ensemble, et c'est tout l'objet de cette classe.</b> Le nom de la copie
/// entre dans la clé du masque : une photo qui change de nom en changeant de dossier repaie
/// son détourage en entier — plusieurs secondes, et un second passage du réseau, celui qui
/// manque de mémoire vidéo sur les cartes des boutiques. Or la même photo change de dossier
/// plusieurs fois dans sa vie : le support du client, la copie du jour, puis la copie d'un
/// jour suivant quand on la rouvre depuis l'historique des trente jours.
///
/// La règle tient en une phrase : <b>on nomme la PHOTO, jamais l'endroit où elle est.</b>
/// </summary>
public static class CopieDeTravail
{
    /// <summary>
    /// Le nom de la copie : celui du fichier du client, plus une empreinte courte contre les
    /// collisions — deux cartes portent souvent un <c>IMG_1234.jpg</c>.
    ///
    /// L'empreinte est faite du NOM, de la TAILLE et de la DATE de dernière écriture : trois
    /// choses qu'une copie conserve. Elle venait du CHEMIN, et par
    /// <c>string.GetHashCode</c> — que .NET tire au sort à chaque démarrage du processus :
    /// la copie changeait donc de nom au moindre déplacement, et même d'un lancement à
    /// l'autre.
    /// </summary>
    /// <param name="nomDuClient">Le nom du fichier tel que le client l'a apporté.</param>
    /// <param name="source">Le fichier à copier, pour sa taille et sa date.</param>
    public static string Nom(string nomDuClient, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nomDuClient);

        var infos = new FileInfo(source);

        var empreinte = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(
                    $"{nomDuClient}|{infos.Length}|{infos.LastWriteTimeUtc.Ticks}")))
            [..8].ToLowerInvariant();

        return $"{Path.GetFileNameWithoutExtension(nomDuClient)}-{empreinte}" +
               Path.GetExtension(source);
    }

    /// <summary>
    /// Un nom stable pour les pixels d'un fichier, qui sert de clé au cache des masques de
    /// détourage (voir <c>MasqueSujet.Nu</c>).
    ///
    /// <b>Le nom seul ne suffit pas.</b> Une photo reprise à la borne, ou un fichier réécrit
    /// sous le même nom, rendrait un masque qui n'est plus le sien — et le sujet sortirait
    /// découpé sur la silhouette de quelqu'un d'autre. La taille et la date de dernière
    /// écriture referment ce trou pour le prix d'un appel système.
    ///
    /// ⚠ <b>Le DOSSIER n'en fait pas partie, et c'est voulu</b> — voir l'en-tête de cette
    /// classe. Deux photos qui partageraient à la fois le nom, la taille à l'octet et la date
    /// à la fraction de seconde sont la même photo.
    ///
    /// Null si le fichier est illisible : on retombe alors sur l'empreinte des pixels, plus
    /// lente mais toujours juste.
    /// </summary>
    public static string? Cle(string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return null;

        try
        {
            var fichier = new FileInfo(chemin);
            if (!fichier.Exists) return null;

            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{fichier.Name}|{fichier.Length}|{fichier.LastWriteTimeUtc.Ticks}");
        }
        catch (Exception)
        {
            // chemin trop long, disque retiré, droits : l'empreinte des pixels prend le relais
            return null;
        }
    }
}
