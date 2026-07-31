using System.IO.Pipes;
using System.Text;
using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;
using Studio.Printing.Devices.Fuji.Bridge;

// Relais 32 bits vers le minilab Fuji Frontier DE100.
//
// Ce processus existe uniquement parce que le SDK Fuji est en 32 bits alors que
// Studio.App tourne en 64 bits. Il héberge le pilote, écoute un tube nommé, et pousse
// les notifications du minilab vers l'application.
//
// Il se lance tout seul (l'application le démarre au besoin) et s'arrête dès que
// l'application se déconnecte : aucun processus fantôme sur la borne.

var log = new Action<string>(m =>
    Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}"));

if (Environment.Is64BitProcess)
{
    log("ERREUR : ce relais doit être compilé en x86, le SDK Fuji étant en 32 bits.");
    return 2;
}

// le SDK Fuji vit dans le dossier de DiLand, pas a cote du relais : on le localise
// avant tout, sinon le premier appel echouerait alors que la bibliotheque est presente
var sdkDirectory = De100Driver.LocateSdk();
log(sdkDirectory is null
    ? "SDK Fuji introuvable : definissez STUDIO_DE100_SDK sur le dossier contenant PModuleIF.dll."
    : $"SDK Fuji trouvé : {sdkDirectory}");

var sdkDnp = DnpDriver.LocateSdk();
log(sdkDnp is null
    ? "SDK DNP introuvable : definissez STUDIO_DNP_SDK sur le dossier contenant cspstat.dll."
    : $"SDK DNP trouvé : {sdkDnp}");

De100Driver? driver = null;
var dnpAbandonne = false;
var writeLock = new object();
StreamWriter? writer = null;

void Send(De100Message message)
{
    lock (writeLock)
    {
        if (writer is null) return;
        try
        {
            writer.WriteLine(De100Protocol.Encode(message));
        }
        catch (IOException)
        {
            // l'application s'est déconnectée : la boucle de lecture s'en apercevra
        }
    }
}

De100Driver Driver()
{
    if (driver is not null) return driver;

    driver = new De100Driver();
    driver.JobFinished += (_, result) => Send(De100Protocol.Event(De100Events.JobFinished, result));
    driver.MachineEvent += (_, evt) => Send(De100Protocol.Event(De100Events.MachineEvent, evt));
    log("Pilote DE100 ouvert.");
    return driver;
}

try
{
    using var server = new NamedPipeServerStream(
        De100Protocol.PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    log($"Relais DE100 en attente sur le tube « {De100Protocol.PipeName} »…");
    await server.WaitForConnectionAsync();
    log("Application connectée.");

    using var reader = new StreamReader(server, new UTF8Encoding(false));
    writer = new StreamWriter(server, new UTF8Encoding(false)) { AutoFlush = true };

    while (server.IsConnected)
    {
        var line = await reader.ReadLineAsync();
        if (line is null) break; // tube fermé côté application

        if (!De100Protocol.TryDecode(line, out var request))
        {
            log($"Ligne illisible ignorée : {Tronque(line)}");
            continue;
        }
        if (request.Kind != De100MessageKind.Request) continue;

        if (request.Name == De100Commands.Shutdown)
        {
            Send(De100Protocol.Success(request));
            log("Arrêt demandé par l'application.");
            break;
        }

        try
        {
            Send(Handle(request));
        }
        catch (Exception ex)
        {
            log($"Échec de « {request.Name} » : {ex.Message}");
            Send(De100Protocol.Failure(request, ex.Message));
        }
    }

    log("Application déconnectée, arrêt du relais.");
    return 0;
}
catch (Exception ex)
{
    log($"Erreur fatale du relais : {ex}");
    return 1;
}
finally
{
    driver?.Dispose();
}

De100Message Handle(De100Message request) => request.Name switch
{
    De100Commands.Ping => De100Protocol.Success(request, De100Driver.IsSdkInstalled()),

    De100Commands.ListMachines => De100Protocol.Success(request, Driver().ListMachines()),

    De100Commands.IsReady => De100Protocol.Success(request,
        Driver().IsReady(MachineId(request))),

    De100Commands.PrinterInfo => De100Protocol.Success(request,
        Driver().GetPrinterInfo(MachineId(request))),

    De100Commands.Subscribe => Subscribe(request),

    De100Commands.Submit => Submit(request),

    De100Commands.Cancel => Cancel(request),

    De100Commands.PendingJobs => De100Protocol.Success(request, Driver().PendingJobIds),

    De100Commands.DnpSnapshot => De100Protocol.Success(request, EtatDesDnp()),

    _ => De100Protocol.Failure(request, $"Commande inconnue : « {request.Name} »"),
};

/// <summary>
/// Etat des imprimantes DNP branchees.
///
/// PIEGE VERIFIE LE 31/07/2026 : cspstat.dll se bloque indefiniment quand DiLand tient
/// le port USB de la DS620 - et DiLand le tient en permanence. Un appel direct figerait
/// la boucle de lecture du relais, qui ne repondrait plus pour le minilab non plus.
/// On borne donc l attente, et on renonce definitivement apres un premier blocage :
/// reessayer toutes les deux minutes ne ferait qu accumuler des fils bloques.
/// </summary>
List<DnpPrinterInfo> EtatDesDnp()
{
    if (dnpAbandonne) return [];

    if (!DnpDriver.IsSdkInstalled())
    {
        log("SDK DNP introuvable (cspstat.dll) : aucune imprimante DNP remontee.");
        dnpAbandonne = true;
        return [];
    }

    var lecture = Task.Run(() =>
    {
        var pilote = new DnpDriver();
        var etats = new List<DnpPrinterInfo>();
        foreach (var port in pilote.ListPorts())
        {
            try { etats.Add(pilote.GetPrinterInfo(port)); }
            catch (Exception ex) { log($"Imprimante DNP du port {port} illisible : {ex.Message}"); }
        }
        return etats;
    });

    if (lecture.Wait(TimeSpan.FromSeconds(6))) return lecture.Result;

    dnpAbandonne = true;
    log("Imprimantes DNP sans reponse en 6 s : port USB probablement tenu par DiLand. " +
        "On cesse de les interroger pour cette session.");
    return [];
}

De100Message Subscribe(De100Message request)
{
    Driver().Subscribe(MachineId(request));
    log($"Abonné aux notifications de la machine « {MachineId(request)} ».");
    return De100Protocol.Success(request);
}

De100Message Submit(De100Message request)
{
    var demande = De100Protocol.Payload<De100SubmitRequest>(request)
                  ?? throw new InvalidOperationException("Demande de tirage vide.");

    var handle = Driver().Submit(demande.Job, demande.MachineId);
    log($"Tirage « {demande.Job.JobId} » accepté par le minilab (handle {Tronque(handle)}).");
    return De100Protocol.Success(request, handle);
}

De100Message Cancel(De100Message request)
{
    var handle = De100Protocol.Payload<string>(request)
                 ?? throw new InvalidOperationException("Handle de commande manquant.");
    Driver().Cancel(handle);
    return De100Protocol.Success(request);
}

char MachineId(De100Message request)
{
    var value = De100Protocol.Payload<string>(request);
    if (string.IsNullOrEmpty(value))
        throw new InvalidOperationException("Identifiant machine manquant.");
    return value[0];
}

static string Tronque(string texte) => texte.Length <= 60 ? texte : texte[..60] + "…";
