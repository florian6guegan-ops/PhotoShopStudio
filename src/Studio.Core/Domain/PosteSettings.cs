using System.Text.Json;

namespace Studio.Core.Domain;

/// <summary>
/// Ce qui dépend du POSTE et non de la boutique : où sont les bornes, quelles imprimantes
/// jouent quel rôle.
///
/// <b>Pourquoi ce fichier existe.</b> Ces valeurs étaient écrites en dur dans le code —
/// le dossier de DiLand, le nom « SC-P800 » de l'imprimante des agrandissements. Elles sont
/// justes sur le poste de la boutique et fausses partout ailleurs : c'est le premier
/// obstacle à donner l'application à un collègue, dont l'installation ne sera jamais
/// exactement la même.
///
/// <b>Tout est facultatif, et c'est le principe.</b> Vide, l'application se débrouille
/// seule (<c>DiLandLocator</c>, <c>DetectionImprimantes</c>) ; renseigné, l'opérateur a le
/// dernier mot. On ne demande donc rien à l'installation, et l'on garde de quoi rattraper
/// un poste que la détection ne saurait pas lire.
///
/// Vit dans les DONNÉES du poste (<c>config\poste.json</c>), jamais dans le dépôt.
/// </summary>
/// <param name="DiLandRacine">
/// Dossier d'installation de DiLand, ou son dépôt directement — les deux sont acceptés,
/// on ne peut pas demander à quelqu'un de retenir
/// « Data\AllUsersData\Repositories\Default ». Vide = détection automatique.
/// </param>
/// <param name="ImprimanteGrandFormat">
/// File Windows des agrandissements. Vide = on reconnaît la machine à son modèle.
/// </param>
/// <param name="ImprimanteSublimation">
/// File Windows de l'imprimante à sublimation (DNP DS620 et apparentées). Vide = détection.
/// </param>
/// <param name="AdresseRapport">
/// À qui envoyer les journaux quand quelque chose ne va pas.
///
/// Retenue pour ne se saisir qu'UNE fois par poste : le jour où l'on en a besoin est
/// justement celui où l'on ne veut pas la chercher.
/// </param>
/// <param name="CadrageAutoVisage">
/// À l'ouverture de « Modifier », poser le cadre sur le VISAGE au lieu du centre de la photo.
///
/// Faux par défaut, et c'est délibéré : le cadrage automatique déplace le cadre de photos
/// que l'opérateur n'a pas ouvertes, et une boutique qui ne s'y attend pas doit retrouver le
/// comportement qu'elle connaît. Il ne touche jamais un cadrage déjà posé — celui d'une
/// borne, ou celui qu'on vient de régler à la main.
/// </param>
/// <param name="SupportsMasques">
/// Supports à NE PAS proposer comme source de photos, désignés par leur nom de volume ou
/// leur lettre (« DILAND », « E: »). La comparaison ignore la casse.
///
/// La clef de DiLand est branchée en permanence sur le poste de la boutique : elle porte
/// sa licence, jamais de photos, et elle s'affichait pourtant à côté des cartes clients.
/// C'est un choix de POSTE et non du dépôt — chez un collègue, ce sera un autre volume, ou
/// aucun.
/// </param>
/// <param name="SeparerLesCommandes">
/// Intercaler une feuille blanche entre deux commandes qui s'enchaînent sur la même
/// machine du minilab.
///
/// Deux commandes tirées coup sur coup tombent dans le même bac, et rien ne dit où finit
/// l'une et où commence l'autre : on trie trente photos à la main en espérant reconnaître
/// les visages. La feuille sort au format le plus court du rouleau — 50 mm sur du 152 —
/// juste avant les premiers tirages de la seconde.
///
/// <b>Vrai par défaut</b>, contrairement aux autres bascules de ce fichier : la file
/// d'attente qui l'accompagne n'existait pas non plus, et une boutique qui enchaîne deux
/// commandes veut les distinguer — c'est la demande qui a fait écrire tout ceci. Une
/// boutique qui préfère économiser le papier décoche.
///
/// Ne vaut que pour le minilab : sur la DS620 une feuille blanche coûterait un panneau de
/// sublimation entier, ruban compris, pour séparer deux paquets qu'on relève à la main.
/// </param>
/// <param name="LogicielDeRetouche">
/// Chemin forcé du logiciel de retouche, quand celui du poste n'est pas là où on le
/// cherche. Vide = trouvé tout seul : Photoshop par le registre, GIMP à défaut.
///
/// Se règle à la main dans le fichier, et pas à l'écran : c'est un dépannage
/// d'installation, pas un choix de comptoir.
/// </param>
public sealed record PosteSettings(
    string DiLandRacine = "",
    string ImprimanteGrandFormat = "",
    string ImprimanteSublimation = "",
    string AdresseRapport = "",
    bool CadrageAutoVisage = false,
    IReadOnlyList<string>? SupportsMasques = null,
    bool SeparerLesCommandes = true,
    string LogicielDeRetouche = "")
{
    /// <summary>Les supports masqués, jamais null — un fichier ancien n'en porte pas.</summary>
    public IReadOnlyList<string> Masques => SupportsMasques ?? [];

    /// <summary>
    /// Ce support doit-il rester caché ?
    ///
    /// On accepte le nom de volume comme la lettre parce que l'opérateur lit les deux sur
    /// la tuile (« DILAND (E:) ») et qu'on ne va pas lui demander lequel compte. Une clef
    /// qui change de lettre reste reconnue par son nom, et inversement.
    /// </summary>
    public bool EstMasque(string libelle, string racine)
    {
        foreach (var masque in Masques)
        {
            if (string.IsNullOrWhiteSpace(masque)) continue;

            var propre = masque.Trim().TrimEnd('\\', ':');
            if (propre.Length == 0) continue;

            if (libelle.Contains(propre, StringComparison.OrdinalIgnoreCase)) return true;
            if (racine.TrimEnd('\\', ':').Equals(propre, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public const string FileName = "poste.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge les réglages. Un fichier absent ou abîmé rend les valeurs par défaut plutôt
    /// que de lever : sans lui, l'application doit démarrer et se débrouiller.
    /// </summary>
    public static PosteSettings Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return new PosteSettings();

        try
        {
            using var flux = File.OpenRead(chemin);
            return JsonSerializer.Deserialize<PosteSettings>(flux, Options) ?? new PosteSettings();
        }
        catch (Exception)
        {
            return new PosteSettings();
        }
    }

    /// <summary>Enregistre les réglages, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, PosteSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(configDir);
        var chemin = Path.Combine(configDir, FileName);
        var json = JsonSerializer.Serialize(settings, Options);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
