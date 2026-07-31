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

    public const string Submit = "submit";
    public const string Cancel = "cancel";
    public const string PendingJobs = "pending-jobs";
    public const string Shutdown = "shutdown";

    /// <summary>
    /// État des imprimantes DNP. Elles transitent par le même relais : leur SDK
    /// (<c>cspstat.dll</c>) est lui aussi en 32 bits.
    /// </summary>
    public const string DnpSnapshot = "dnp-snapshot";
}

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

/// <summary>Paramètres d'une demande de tirage transmise au relais.</summary>
public sealed record De100SubmitRequest(De100PrintJob Job, char MachineId);

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
