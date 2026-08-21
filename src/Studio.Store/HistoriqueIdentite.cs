using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Store;

/// <summary>
/// Une photo d'identité passée par l'écran de travail, gardée telle que l'opérateur l'avait
/// réglée.
///
/// <b>Une photo FAITE, c'est une photo imprimée ou envoyée</b> — tranché par l'exploitant le
/// 19/08/2026. Pas une photo simplement ouverte : la carte d'un client en porte quatre-vingts,
/// et l'historique se remplirait de ce qu'on a seulement regardé. Le client qui revient dans
/// trois jours revient pour une photo qu'il a prise.
///
/// <b>Ce n'est pas une commande pour autant.</b> Une commande dit ce qui a été facturé ;
/// celle-ci dit comment la photo était RÉGLÉE — pour la refaire sans rien remettre.
///
/// <b>Le travail est porté par un <see cref="TravailEnAttente"/></b>, celui-là même qui sert
/// aux planches mises de côté : il décrit déjà la norme du document, le cadrage, les repères
/// de crâne et de menton, l'axe du visage, le redressement, le noir et blanc, le fond blanc,
/// le fond gris, les corrections fines, les photos par planche et la quantité. Rien à
/// inventer, et surtout un seul format à faire évoluer — voir <see cref="PhotoIdentiteEnAttente"/>.
///
/// ⚠ Les commandes (<c>orders\</c>) ne tiennent pas ce rôle, et pour une raison qui reste
/// entière : <b>une photo ENVOYÉE PAR COURRIEL n'y est pas reconnaissable comme une
/// identité</b> — elle n'a pas de planche, donc pas de ligne d'identité. Une commande ne dit
/// que ce qui est passé en caisse.
///
/// Elles gardent en revanche les repères de crâne et de menton depuis le 21/08/2026 (voir
/// <see cref="ReperesIdentite"/>) : « Commandes du jour › Photos d'identité » rouvre donc
/// une planche IMPRIMÉE telle qu'elle est sortie, sans repasser par la détection de visage.
/// </summary>
public sealed class PhotoFaite
{
    /// <summary>
    /// Ce qui désigne LA MÊME photo d'un geste à l'autre : le fichier et la journée.
    ///
    /// Sans elle, la photo qu'on imprime PUIS qu'on envoie ferait deux tuiles au lieu d'une —
    /// et le client qui repart avec sa planche et son courriel n'a fait faire qu'une photo.
    /// Voir <see cref="CleDe"/>.
    /// </summary>
    public string Cle { get; set; } = "";

    /// <summary>Premier geste sur cette photo : la planche, ou le courriel.</summary>
    public DateTimeOffset FaiteLe { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Dernier geste. C'est LUI qui fait la rétention et l'ordre de la grille : une photo
    /// reprise et retirée trois jours plus tard reste en tête, et ne s'efface que trente
    /// jours après le dernier geste.
    /// </summary>
    public DateTimeOffset ModifieeLe { get; set; } = DateTimeOffset.Now;

    /// <summary>Le nom que l'opérateur reconnaît : celui du fichier du client.</summary>
    public string NomDuFichier { get; set; } = "";

    /// <summary>
    /// Où lire les pixels — la copie locale faite à l'ouverture par
    /// <c>IdPhotoView.MettreALAbriAsync</c>, sous <c>cache\travail\&lt;jour&gt;\</c>.
    ///
    /// Jamais le fichier du support : la carte du client est repartie avec lui le jour même,
    /// et c'est précisément ce que l'historique doit survivre.
    /// </summary>
    public string Chemin { get; set; } = "";

    /// <summary>La planche est sortie sur le papier.</summary>
    public bool Imprimee { get; set; }

    /// <summary>La photo est partie par courriel — et l'envoi a RÉUSSI.</summary>
    public bool Envoyee { get; set; }

    /// <summary>Numéro de la dernière commande née de cette photo (« 19-003 »), ou vide.</summary>
    public string? Commande { get; set; }

    /// <summary>Ce que la tuile affiche : « France · 35×45 · 6 photos ».</summary>
    public string Resume { get; set; } = "";

    /// <summary>
    /// Le travail, dans la forme que l'écran sait reprendre. Son <c>Identite</c> ne porte
    /// qu'UNE photo : l'historique se parcourt par photos, pas par planches.
    /// </summary>
    public TravailEnAttente Travail { get; set; } = new();

    /// <summary>Quand, en clair, pour la tuile.</summary>
    [JsonIgnore]
    public string Quand
    {
        get
        {
            var local = ModifieeLe.LocalDateTime;
            if (local.Date == DateTime.Today) return $"aujourd'hui à {local:HH:mm}";
            if (local.Date == DateTime.Today.AddDays(-1)) return $"hier à {local:HH:mm}";
            return $"le {local:dd/MM} à {local:HH:mm}";
        }
    }

    /// <summary>Ce qui a été fait, en une pastille : 🖨, ✉, les deux, ou rien.</summary>
    [JsonIgnore]
    public string Pastille => (Imprimee, Envoyee) switch
    {
        (true, true) => "🖨 ✉",
        (true, false) => "🖨",
        (false, true) => "✉",
        _ => "",
    };

    /// <summary>
    /// Ce qui désigne la même photo d'un dépôt à l'autre : le fichier et la journée.
    ///
    /// La journée en fait partie — le même client peut revenir la semaine suivante avec la
    /// même carte, et ce n'est pas la même photo faite. C'est aussi ce qui aligne l'entrée
    /// sur la copie locale, rangée elle aussi par journée.
    /// </summary>
    public static string CleDe(string chemin, DateTimeOffset quand) =>
        $"{quand.LocalDateTime:yyyyMMdd}|" +
        Path.GetFileName(chemin ?? "").ToLowerInvariant();
}

/// <summary>
/// L'historique des trente jours de Studio Photo Identité : un fichier par photo faite.
///
/// Jumeau d'<see cref="AttenteStore"/>, et pour les mêmes raisons — un fichier par entrée,
/// deux photos réglées la même minute n'ont aucune raison de se gêner, un fichier abîmé
/// n'emporte pas les autres, et l'effacement est trivial. La rétention est celle du reste de
/// la maison : trente jours, purgés à la lecture.
///
/// ⚠ <b>Il ne facture rien et ne solde rien.</b> Les commandes restent la vérité comptable ;
/// ceci n'est qu'un index de travail. Une entrée effacée ne retire pas un centime d'une
/// commande, et une commande annulée ne retire pas l'entrée.
/// </summary>
public sealed class HistoriqueIdentite
{
    /// <summary>Trente jours, comme les commandes mises de côté.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dossier;

    public HistoriqueIdentite(string dossier) => _dossier = dossier;

    /// <summary>
    /// Le fichier d'une entrée, DÉDUIT de sa clé.
    ///
    /// <b>Un identifiant tiré au sort obligerait à relire tout le dossier</b> pour retrouver
    /// une photo — et l'écran cherche à chaque dépôt de travail, c'est-à-dire à chaque
    /// changement de photo. Le nom vient donc de la clé : retrouver une entrée, c'est lire un
    /// fichier, et la mettre à jour, c'est le réécrire.
    ///
    /// L'empreinte plutôt que la clé elle-même : elle porte le nom du fichier du client,
    /// avec ses accents, ses espaces et parfois des caractères qu'un nom de fichier n'accepte
    /// pas.
    /// </summary>
    private string Chemin(string cle)
    {
        var octets = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(cle ?? ""));

        return Path.Combine(_dossier, $"{Convert.ToHexString(octets)[..24].ToLowerInvariant()}.json");
    }

    /// <summary>
    /// Les photos des trente derniers jours, la plus récemment touchée d'abord — c'est celle
    /// qu'on rouvre le plus souvent.
    ///
    /// Les périmées partent au passage : la liste est relue à chaque ouverture de
    /// l'historique, c'est le seul endroit où la purge ne coûte rien.
    /// </summary>
    public IReadOnlyList<PhotoFaite> Lister()
    {
        if (!Directory.Exists(_dossier)) return [];

        var vivantes = new List<PhotoFaite>();
        var plancher = DateTimeOffset.Now - Retention;

        foreach (var chemin in Directory.EnumerateFiles(_dossier, "*.json"))
        {
            var photo = LireLeFichier(chemin);

            // fichier illisible, ou trop vieux : il part, et il ne bloque personne
            if (photo is null || photo.ModifieeLe < plancher)
            {
                Supprimer(chemin);
                continue;
            }

            vivantes.Add(photo);
        }

        return vivantes.OrderByDescending(p => p.ModifieeLe).ToList();
    }

    /// <summary>
    /// L'entrée de cette photo pour cette journée, ou null.
    ///
    /// ⚠ Elle ne purge pas : elle est appelée à chaque dépôt de l'écran, et effacer pendant
    /// qu'on dépose ferait disparaître sous l'opérateur une photo qu'il regarde.
    /// </summary>
    public PhotoFaite? Trouver(string cle) =>
        string.IsNullOrWhiteSpace(cle) ? null : LireLeFichier(Chemin(cle));

    /// <summary>
    /// Note une photo faite : elle vient d'être imprimée, ou envoyée par courriel.
    ///
    /// <b>Elle FUSIONNE avec l'entrée du jour, si elle existe.</b> Le client qui repart avec
    /// sa planche ET son courriel n'a fait faire qu'une photo : c'est une tuile, avec les
    /// deux pastilles. Ce qui est repris de l'entrée précédente, ce sont les DRAPEAUX et
    /// l'heure du premier geste ; le travail, lui, est celui qu'on vient de faire — l'écran
    /// a pu recadrer entre les deux, et c'est le dernier réglage qui doit ressortir.
    ///
    /// L'écriture se fait à côté puis remplace, comme partout ailleurs.
    /// </summary>
    public void Noter(PhotoFaite photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ArgumentException.ThrowIfNullOrWhiteSpace(photo.Cle);

        if (Trouver(photo.Cle) is { } deja)
        {
            photo.FaiteLe = deja.FaiteLe;
            photo.Imprimee |= deja.Imprimee;
            photo.Envoyee |= deja.Envoyee;
            photo.Commande ??= deja.Commande;
        }

        photo.ModifieeLe = DateTimeOffset.Now;

        Directory.CreateDirectory(_dossier);
        AtomicFile.WriteAllText(Chemin(photo.Cle),
            JsonSerializer.Serialize(photo, JsonOptions));
    }

    /// <summary>
    /// Efface une entrée. Ne lève jamais : appelé depuis la purge, où l'échec ne doit rien
    /// arrêter.
    /// </summary>
    public void Effacer(string cle) => Supprimer(Chemin(cle));

    private static PhotoFaite? LireLeFichier(string chemin)
    {
        if (!File.Exists(chemin)) return null;

        try
        {
            return JsonSerializer.Deserialize<PhotoFaite>(File.ReadAllText(chemin), JsonOptions);
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException)
        {
            // fichier abîmé : l'historique doit s'ouvrir quand même. Perdre une tuile est
            // ennuyeux ; ne plus pouvoir en rouvrir aucune le serait bien davantage.
            return null;
        }
    }

    private static void Supprimer(string chemin)
    {
        try
        {
            if (File.Exists(chemin)) File.Delete(chemin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // fichier verrouillé : il repartira au prochain passage
        }
    }
}
