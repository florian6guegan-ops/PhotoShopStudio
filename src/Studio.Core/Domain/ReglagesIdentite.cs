using System.Text.Json;

namespace Studio.Core.Domain;

/// <summary>
/// Les réglages du poste identité. Ils vivent dans le dossier de DONNÉES
/// (<c>config\identite.json</c>), comme les autres réglages du poste.
/// </summary>
/// <param name="DossierPhotos">
/// Où « Ouvrir des photos » commence.
///
/// <b>Vide = la carte mémoire, trouvée toute seule</b>, et c'est le cas courant du
/// comptoir : le client tend sa carte, on l'insère, on ouvre. Un chemin fixe ne sert que
/// sur un poste où les photos arrivent toujours au même endroit — un dossier réseau, un
/// dossier de dépôt du téléphone.
/// </param>
/// <param name="ModeSombre">
/// L'habillage sombre, au choix du poste.
///
/// Demandé depuis Arcueil : un comptoir en contre-jour, ou une fin de journée, et l'écran
/// clair fatigue. Faux par défaut — c'est la maquette validée qui est claire.
/// </param>
/// <param name="CadrageAutomatique">
/// Placer le cadre tout seul à l'ouverture d'une photo, d'après le visage détecté.
///
/// <b>Vrai par défaut</b>, parce que c'est ce qui rend le comptoir rapide : la photo
/// s'ouvre déjà cadrée à la norme, et l'opérateur n'a plus qu'à corriger. Mais la détection
/// se trompe — un fond chargé, une frange, un enfant porté — et l'opérateur qui recadre à la
/// main les cinquante photos de sa journée préfère alors partir d'un cadre neutre plutôt que
/// de défaire à chaque fois une proposition fausse. Demandé le 18/08/2026.
///
/// Faux, la photo s'ouvre sur un cadre CENTRÉ au rapport du document — jamais sur la photo
/// entière, qui n'aurait aucun sens sur une planche d'identité. La détection de visage
/// continue de tourner : elle pose les repères, donc le contrôle de conformité à l'écran.
/// Seul le PLACEMENT du cadre est laissé à l'opérateur, et le bouton « Cadrage automatique »
/// reste là pour le demander expressément.
/// </param>
public sealed record ReglagesIdentite(
    string DossierPhotos = "", bool ModeSombre = false, bool CadrageAutomatique = true)
{
    /// <summary>Nom du fichier, dans le dossier de configuration.</summary>
    public const string FileName = "identite.json";

    /// <summary>Vrai quand un dossier fixe a été choisi, et qu'il existe encore.</summary>
    public bool DossierFixeUtilisable =>
        !string.IsNullOrWhiteSpace(DossierPhotos) && Directory.Exists(DossierPhotos);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge les réglages. Un fichier absent ou abîmé rend les réglages PAR DÉFAUT plutôt
    /// que de lever : le poste doit démarrer même si personne n'a rien réglé — et le défaut,
    /// la carte mémoire, est justement ce qu'on veut.
    /// </summary>
    public static ReglagesIdentite Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return new ReglagesIdentite();

        try
        {
            using var flux = File.OpenRead(chemin);
            return JsonSerializer.Deserialize<ReglagesIdentite>(flux, Options) ?? new ReglagesIdentite();
        }
        catch (Exception)
        {
            return new ReglagesIdentite();
        }
    }

    /// <summary>Enregistre les réglages, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, ReglagesIdentite reglages)
    {
        ArgumentNullException.ThrowIfNull(reglages);

        Directory.CreateDirectory(configDir);
        var chemin = Path.Combine(configDir, FileName);
        var json = JsonSerializer.Serialize(reglages, Options);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
