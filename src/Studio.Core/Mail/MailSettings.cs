using System.Text.Json;

namespace Studio.Core.Mail;

/// <summary>
/// Réglages d'envoi des photos par courriel.
///
/// Ils vivent dans le dossier de DONNÉES (<c>D:\PhotoStudioData\config\</c>), jamais dans
/// le dépôt : celui-ci est public sur GitHub, et un mot de passe poussé par mégarde ne se
/// rattrape pas — il reste dans l'historique.
/// </summary>
/// <param name="Serveur">Serveur SMTP. Gmail : <c>smtp.gmail.com</c>.</param>
/// <param name="Port">Port SMTP. Gmail : 587 (STARTTLS).</param>
/// <param name="Expediteur">Adresse qui envoie, et qui sert aussi d'identifiant.</param>
/// <param name="NomExpediteur">Nom affiché chez le client.</param>
/// <param name="MotDePasseApplication">
/// Mot de passe D'APPLICATION, pas celui du compte : depuis 2022 Google refuse le mot de
/// passe ordinaire pour l'envoi SMTP. Il se crée sur le compte, validation en deux étapes
/// activée.
/// </param>
/// <param name="Actif">
/// Faux tant que le mot de passe n'a pas été posé : l'écran doit pouvoir dire « l'envoi
/// n'est pas configuré » au lieu d'échouer devant le client.
/// </param>
public sealed record MailSettings(
    string Serveur = "smtp.gmail.com",
    int Port = 587,
    string Expediteur = "",
    string NomExpediteur = "Photoconcept Maisons-Alfort",
    string MotDePasseApplication = "",
    bool Actif = false)
{
    /// <summary>Nom du fichier, dans le dossier de configuration.</summary>
    public const string FileName = "mail.json";

    /// <summary>
    /// Vrai si l'envoi est réellement utilisable. On ne se fie pas au seul drapeau
    /// <see cref="Actif"/> : un fichier à moitié rempli ne doit pas laisser croire que
    /// ça marchera.
    /// </summary>
    public bool EstUtilisable =>
        Actif
        && !string.IsNullOrWhiteSpace(Serveur)
        && Port > 0
        && !string.IsNullOrWhiteSpace(Expediteur)
        && !string.IsNullOrWhiteSpace(MotDePasseApplication);

    /// <summary>Ce qui manque pour envoyer, en clair. Vide si tout est là.</summary>
    public string CeQuiManque()
    {
        if (EstUtilisable) return "";

        var manques = new List<string>();
        if (string.IsNullOrWhiteSpace(Expediteur)) manques.Add("l'adresse d'expédition");
        if (string.IsNullOrWhiteSpace(MotDePasseApplication)) manques.Add("le mot de passe d'application");
        if (string.IsNullOrWhiteSpace(Serveur)) manques.Add("le serveur");
        if (Port <= 0) manques.Add("le port");

        if (manques.Count == 0 && !Actif) return "l'envoi par courriel est désactivé";

        return string.Join(", ", manques);
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge les réglages. Un fichier absent ou abîmé rend des réglages VIDES plutôt que
    /// de lever : l'application doit démarrer même si personne n'a encore configuré
    /// l'envoi.
    /// </summary>
    public static MailSettings Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return new MailSettings();

        try
        {
            using var flux = File.OpenRead(chemin);
            return JsonSerializer.Deserialize<MailSettings>(flux, Options) ?? new MailSettings();
        }
        catch (Exception)
        {
            return new MailSettings();
        }
    }

    /// <summary>Enregistre les réglages, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, MailSettings settings)
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
