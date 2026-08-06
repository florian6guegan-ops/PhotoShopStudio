using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Printing.Devices.Fuji.Bridge;

/// <summary>Nature d'un message échangé sur le tube.</summary>
public static class De100MessageKind
{
    public const string Request = "request";
    public const string Response = "response";
    public const string Event = "event";
}

/// <summary>Commandes que l'application 64 bits adresse au relais 32 bits.</summary>
public static class De100Commands
{
    /// <summary>Vérifie que le relais répond et que le SDK est chargeable.</summary>
    public const string Ping = "ping";

    public const string ListMachines = "list-machines";
    public const string IsReady = "is-ready";
    public const string PrinterInfo = "printer-info";

    /// <summary>Abonne le relais aux notifications d'une machine.</summary>
    public const string Subscribe = "subscribe";

    /// <summary>
    /// Définition que la MACHINE attend pour un format donné (<c>PIF_DevGetPixelCount</c>).
    /// Charge utile : <see cref="De100PixelCountRequest"/>.
    /// </summary>
    public const string PixelCount = "pixel-count";

    public const string Submit = "submit";
    public const string Cancel = "cancel";
    public const string PendingJobs = "pending-jobs";
    public const string Shutdown = "shutdown";

    /// <summary>
    /// État des imprimantes DNP. Elles transitent par le même relais : leur SDK
    /// (<c>cspstat.dll</c>) est lui aussi en 32 bits.
    /// </summary>
    public const string DnpSnapshot = "dnp-snapshot";

    /// <summary>
    /// Envoie une image à une DNP <b>sans passer par le pilote Windows</b>.
    /// Charge utile : <see cref="De100DnpPrintRequest"/>.
    ///
    /// C'est le CHEMIN du fichier qui traverse le tube, jamais l'image : une planche 10×15
    /// à 300 ppp pèse près de sept méga-octets une fois décompressée, et le tube sert aussi
    /// le minilab, une commande à la fois.
    /// </summary>
    public const string DnpPrint = "dnp-print";
}

/// <summary>
/// Un tirage à envoyer directement à une DNP, sans le pilote Windows.
/// </summary>
/// <param name="ImagePath">Le rendu à tirer, DÉJÀ à la taille de la trame de la machine.</param>
/// <param name="PortNumber">Rang de la machine dans la découverte du SDK.</param>
/// <param name="Overcoat">Finition de surface, telle que le produit la demande.</param>
/// <param name="Copies">Nombre d'exemplaires ; chacun est un envoi distinct.</param>
public sealed record De100DnpPrintRequest(
    string ImagePath, int PortNumber, int Overcoat, int Copies);

/// <summary>Événements poussés spontanément par le relais.</summary>
public static class De100Events
{
    /// <summary>Un tirage a reçu une issue définitive ; charge utile : <see cref="De100JobResult"/>.</summary>
    public const string JobFinished = "job-finished";

    /// <summary>Le minilab signale une erreur ou un avertissement ; charge utile : <see cref="De100MachineEvent"/>.</summary>
    public const string MachineEvent = "machine-event";
}

/// <summary>
/// Un message du protocole. Volontairement plat : une ligne de JSON, sans hiérarchie,
/// pour qu'un relais planté ou une ligne tronquée ne puissent pas bloquer l'analyse.
/// </summary>
/// <param name="Kind">Voir <see cref="De100MessageKind"/>.</param>
/// <param name="Id">Corrélation requête/réponse ; vide pour un événement.</param>
/// <param name="Name">Nom de commande ou d'événement.</param>
/// <param name="Payload">Charge utile en JSON, ou null.</param>
/// <param name="Ok">Faux si la commande a échoué côté relais.</param>
/// <param name="Error">Message d'erreur destiné à l'opérateur.</param>
public sealed record De100Message(
    string Kind,
    string Id,
    string Name,
    string? Payload = null,
    bool Ok = true,
    string? Error = null);

/// <summary>
/// Paramètres d'une demande de tirage transmise au relais.
///
/// <see cref="Jobs"/> porte TOUTES les photos d'une enveloppe : elles forment une seule
/// commande côté minilab (voir <c>De100Driver.Submit</c>). Le champ était au singulier, et
/// Studio ouvrait donc une commande par photo — c'est ce qui a fait perdre deux tirages
/// sur quatre le 04/08/2026.
///
/// <b>Un seul constructeur, et c'est délibéré.</b> Un second — le confort « un tirage
/// seul » — laissait <c>System.Text.Json</c> sans règle pour choisir, et la
/// désérialisation échouait net : « Deserialization of types without a parameterless
/// constructor… is not supported ». Le relais aurait refusé toutes les demandes de tirage.
/// Pour un tirage seul, on passe une liste d'un élément.
/// </summary>
public sealed record De100SubmitRequest(IReadOnlyList<De100PrintJob> Jobs, char MachineId);

/// <summary>
/// Demande la définition que la machine attend pour un format, en millimètres.
/// </summary>
/// <param name="MachineId">Machine visée.</param>
/// <param name="WidthMm">Largeur du tirage.</param>
/// <param name="HeightMm">Hauteur du tirage.</param>
/// <param name="Dpi">Résolution.</param>
public sealed record De100PixelCountRequest(char MachineId, double WidthMm, double HeightMm, uint Dpi);

/// <summary>
/// Ce que la machine attend, en pixels. <c>0 × 0</c> = elle n'a rien voulu en dire, et
/// l'appelant garde alors son propre calcul.
/// </summary>
public sealed record De100PixelCountResponse(uint Width, uint Height);

/// <summary>
/// Encodage et décodage des messages du relais DE100 : une ligne de JSON par message.
///
/// Le protocole est isolé ici pour pouvoir être vérifié sans tube, sans processus et
/// sans minilab — c'est la seule partie du relais qu'on puisse tester sur cette machine.
/// </summary>
public static class De100Protocol
{
    /// <summary>Nom du tube nommé, propre à la session Windows courante.</summary>
    public const string PipeName = "studio-photo-de100";

    internal static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Résout TOUS les convertisseurs du protocole avant que le tube ne serve, puis fige
    /// les options.
    ///
    /// <b>C'est la correction du « Pipe is broken » d'une impression sur deux</b>
    /// (05/08/2026). Le relais répond à chaque commande dans son propre
    /// <c>Task.Run</c> : deux commandes simultanées — l'état des machines et celui des DNP,
    /// que le bandeau demande coup sur coup — faisaient construire le MÊME convertisseur
    /// d'énumération par réflexion en même temps. En 32 bits, le moteur d'exécution n'y
    /// survit pas :
    ///
    /// <code>
    /// Fatal error. Internal CLR error. (0x80131506)
    ///    at System.Text.Json...EnumConverter`1[[...DnpStatusGroup]]..ctor(...)
    ///    at System.Activator.CreateInstance(Type, Object[])
    /// </code>
    ///
    /// Le relais mourait donc en pleine séance, le tube se rompait, et l'impression
    /// échouait — il fallait rouvrir l'application pour repartir. Ce n'était pas
    /// l'impression qui cassait le relais : c'est le relais qui était déjà mort quand elle
    /// arrivait.
    ///
    /// Résolus ICI, au chargement de la classe, donc une seule fois et sur un seul fil : le
    /// cache est déjà chaud quand le trafic commence, et plus personne ne construit de
    /// convertisseur en pleine course. <c>MakeReadOnly</c> ferme la porte derrière — toute
    /// mutation ultérieure des options lèverait au lieu de rouvrir la course en silence.
    ///
    /// Un type qui échapperait à cette liste ne casse rien : il retomberait sur la
    /// résolution paresseuse d'avant. C'est pourquoi l'échec est avalé — un protocole ne
    /// doit pas refuser de démarrer parce qu'un préchauffage a échoué.
    /// </summary>
    static De100Protocol()
    {
        Type[] transportes =
        [
            typeof(De100Message),
            typeof(De100SubmitRequest), typeof(De100PrintJob), typeof(List<De100PrintJob>),
            typeof(De100PixelCountRequest), typeof(De100PixelCountResponse),
            typeof(De100PrinterInfo), typeof(List<De100PrinterInfo>),
            typeof(Dnp.DnpPrinterInfo), typeof(List<Dnp.DnpPrinterInfo>),
            typeof(Dnp.DnpStatus),
            typeof(De100JobResult), typeof(De100MachineEvent),
            typeof(Dnp.EtatSpouleurDnp), typeof(Dnp.EtatFileDnp),
            typeof(List<string>), typeof(string), typeof(bool),

            // Les primitives que portent les enregistrements ci-dessus. Elles sont
            // configurées EN CASCADE depuis leurs porteurs, mais les nommer coûte
            // trois lignes et ferme le cas où un type y échapperait.
            typeof(uint), typeof(int), typeof(double), typeof(char),
        ];

        foreach (var type in transportes)
        {
            try
            {
                // <b>GetTypeInfo ne suffisait PAS, et c'est ce qui manquait au correctif du
                // 05/08/2026.</b> Il fabrique la description du type, mais laisse sa
                // CONFIGURATION pour le premier usage — or c'est elle qui descend dans
                // chaque propriété, en construit le convertisseur, et fait le travail par
                // réflexion que le moteur 32 bits ne supporte pas quand deux fils s'y
                // mettent ensemble. Le relais est mort exactement là le 06/08/2026 :
                //
                //   Fatal error. Internal CLR error. (0x80131506)
                //      at JsonTypeInfo`1[[System.UInt32]].CreatePropertyInfoForTypeInfo()
                //      at ...JsonPropertyInfo.Configure() ← et la même chaîne, en boucle
                //
                // MakeReadOnly() force cette configuration ICI, une fois, sur un seul fil.
                Json.GetTypeInfo(type).MakeReadOnly();
            }
            catch (Exception) { /* ce type retombera sur la résolution paresseuse */ }
        }

        try { Json.MakeReadOnly(); }
        catch (Exception) { /* rien à figer : les options restent utilisables */ }
    }

    /// <summary>Sérialise un message en une ligne, sans retour à la ligne interne.</summary>
    public static string Encode(De100Message message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, Json);
    }

    /// <summary>
    /// Analyse une ligne reçue. Renvoie faux plutôt que de lever : sur un tube, une ligne
    /// illisible ne doit pas faire tomber la boucle de lecture.
    /// </summary>
    public static bool TryDecode(string? line, out De100Message message)
    {
        message = default!;
        if (string.IsNullOrWhiteSpace(line)) return false;

        try
        {
            var decoded = JsonSerializer.Deserialize<De100Message>(line, Json);
            if (decoded is null || string.IsNullOrEmpty(decoded.Kind)) return false;
            message = decoded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Construit une requête avec un identifiant de corrélation neuf.</summary>
    public static De100Message Request(string command, object? payload = null) =>
        new(De100MessageKind.Request, Guid.NewGuid().ToString("N"), command, Serialize(payload));

    /// <summary>Réponse positive à une requête.</summary>
    public static De100Message Success(De100Message request, object? payload = null) =>
        new(De100MessageKind.Response, request.Id, request.Name, Serialize(payload));

    /// <summary>Réponse négative : le relais explique ce qui a échoué.</summary>
    public static De100Message Failure(De100Message request, string error) =>
        new(De100MessageKind.Response, request.Id, request.Name, null, Ok: false, Error: error);

    /// <summary>Événement poussé sans requête associée.</summary>
    public static De100Message Event(string name, object? payload = null) =>
        new(De100MessageKind.Event, "", name, Serialize(payload));

    /// <summary>Relit une charge utile typée.</summary>
    public static T? Payload<T>(De100Message message) =>
        string.IsNullOrEmpty(message.Payload) ? default : JsonSerializer.Deserialize<T>(message.Payload, Json);

    private static string? Serialize(object? payload) =>
        payload is null ? null : JsonSerializer.Serialize(payload, payload.GetType(), Json);
}
