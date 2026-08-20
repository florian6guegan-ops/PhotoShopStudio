using System.IO;
using Studio.Store;

namespace Studio.App.Infrastructure;

/// <summary>
/// Comment une planche d'identité mise de côté — ou une photo rouverte depuis l'historique
/// des trente jours — retrouve ses photos dans la bande.
///
/// <b>Tout tient à ceci : une photo a DEUX noms, et ils ne se confondent pas toujours.</b>
/// Le nom du client (<c>IMG_1234.jpg</c>) est celui que l'opérateur reconnaît, celui qui part
/// dans la commande et dans les messages. Le nom sur le disque est celui du fichier qu'on lit
/// vraiment — et, dès qu'on passe par l'historique, c'est celui de la copie de travail
/// (<c>IMG_1234-ab12cd34.jpg</c>), parce que la carte du client, elle, est repartie avec lui.
///
/// La bande se remplit depuis les CHEMINS : elle porte donc le nom sur le disque. La fiche,
/// elle, a gardé le nom du client. Chercher l'un avec l'autre ne trouve rien — et ne trouver
/// personne ne se voit pas : la boucle saute simplement les photos, la planche revient vide
/// de tout réglage, sans erreur à l'écran ni ligne au journal.
///
/// ⚠ <b>Sorti d'<c>IdPhotoView</c> pour être ESSAYABLE</b>, comme <see cref="CopieDeTravail"/>
/// et pour la même raison : la règle est trop silencieuse quand elle casse pour vivre dans un
/// code-behind de trois mille lignes que rien ne couvre.
/// </summary>
public static class RepriseDeLaPlanche
{
    /// <summary>
    /// Le nom à garder dans la fiche pour retrouver le fichier, ou null quand il n'apprend
    /// rien — c'est-à-dire quand le fichier lu est celui du client.
    ///
    /// Null plutôt que le même nom deux fois : une planche mise de côté sur le support du
    /// client n'a aucune raison de porter la trace d'un problème qui n'est pas le sien.
    /// </summary>
    /// <param name="nomDuClient">Le nom du fichier tel que le client l'a apporté.</param>
    /// <param name="chemin">Le fichier réellement lu — copie de travail comprise.</param>
    public static string? NomSurLeDisque(string? nomDuClient, string? chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return null;

        var surDisque = Path.GetFileName(chemin);

        return surDisque.Length > 0 &&
               !string.Equals(surDisque, nomDuClient, StringComparison.OrdinalIgnoreCase)
            ? surDisque
            : null;
    }

    /// <summary>
    /// L'index qui retrouve une photo par le nom que la bande lui donne, quel qu'il soit :
    /// celui du client, ou celui du fichier sur le disque.
    ///
    /// <b>Le nom du client passe d'abord.</b> Deux photos différentes peuvent porter le même
    /// nom sur des cartes différentes ; c'est le nom du client qui fait foi, le nom de copie
    /// ne comble que les trous. Les doublons sont ignorés plutôt que d'écraser : mieux vaut
    /// reprendre le réglage de la première que celui d'une inconnue.
    /// </summary>
    public static Dictionary<string, PhotoIdentiteEnAttente> ParNom(
        IReadOnlyList<PhotoIdentiteEnAttente>? photos)
    {
        var index = new Dictionary<string, PhotoIdentiteEnAttente>(StringComparer.OrdinalIgnoreCase);
        if (photos is null) return index;

        foreach (var photo in photos)
            if (!string.IsNullOrWhiteSpace(photo.FileName))
                index[photo.FileName] = photo;

        foreach (var photo in photos)
            if (photo.NomSurLeDisque is { Length: > 0 } surDisque)
                index.TryAdd(surDisque, photo);

        return index;
    }
}
