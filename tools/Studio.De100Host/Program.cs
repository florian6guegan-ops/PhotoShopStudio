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

// UTF-8 sur la sortie d'erreur : c'est par elle que le journal du relais remonte à
// l'application, et la console d'un processus sans fenêtre est en CP850 par défaut — les
// accents arrivaient en « trouvâ€š » dans app-*.log.
Console.OutputEncoding = new UTF8Encoding(false);
var sortieErreur = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false))
{
    AutoFlush = true,
};

var log = new Action<string>(m =>
    sortieErreur.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}"));

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
    ? "SDK DNP introuvable : definissez STUDIO_DNP_SDK sur le dossier contenant CPPCtrl32.dll."
    : $"SDK DNP trouvé : {sdkDnp}");

De100Driver? driver = null;

// Blocage constate hors DiLand : on cesse d interroger la DNP jusqu a ce que DiLand
// passe par la (voir EtatDesDnp).
var dnpAbandonne = false;

// DiLand tenait-il le port au dernier passage ? Sert a detecter la bascule, pas l etat.
var dilandTenaitLePort = DiLandPresence.IsRunning();
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
    // sans cela, ce que le pilote a à dire d'un callback en défaut se perd : c'est le seul
    // fil qui traverse la frontière native, et rien d'autre ne l'observe
    driver.Log = message => log(message);
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

        await RepondreSansJamaisBloquer(request);
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

/// <summary>
/// Traite une commande et repond TOUJOURS, meme si le SDK ne rend jamais la main.
///
/// Le relais servait les commandes l une apres l autre, en attendant chaque reponse. Une
/// seule machine muette suffisait alors a figer tout le reste : le 03/08/2026, une
/// interrogation DNP restee suspendue a bloque la question posee au minilab pour une
/// commande de 41 photos, qui est restee douze minutes sans un mot ni une erreur.
///
/// Desormais l appel part sur un fil, et passe le delai on repond « muette » sans
/// l attendre. Le fil orphelin garde le SDK — on n y peut rien, il ne s interrompt pas —
/// mais le tube reste vivant et l application peut se rabattre proprement.
/// </summary>
async Task RepondreSansJamaisBloquer(De100Message request)
{
    // Envoyer un tirage prend legitimement du temps ; interroger une machine, non.
    var budget = request.Name is De100Commands.Submit or De100Commands.Cancel
        ? TimeSpan.FromMinutes(3)
        : TimeSpan.FromSeconds(10);

    var travail = Task.Run(() => Handle(request));

    if (await Task.WhenAny(travail, Task.Delay(budget)) == travail)
    {
        try
        {
            Send(await travail);
        }
        catch (Exception ex)
        {
            log($"Echec de « {request.Name} » : {ex.Message}");
            Send(De100Protocol.Failure(request, ex.Message));
        }
        return;
    }

    log($"« {request.Name} » sans reponse en {budget.TotalSeconds:0} s : la machine est " +
        "probablement en veille. On repond sans attendre pour ne pas bloquer le reste.");

    Send(De100Protocol.Failure(request,
        $"La machine n'a pas repondu en {budget.TotalSeconds:0} s. Elle est probablement " +
        "en veille ou eteinte."));

    // le fil continue sa vie ; son resultat, s il arrive, ne concerne plus personne
    _ = travail.ContinueWith(t => log(t.IsFaulted
            ? $"« {request.Name} » a fini par echouer : {t.Exception?.GetBaseException().Message}"
            : $"« {request.Name} » a fini par repondre, trop tard."),
        TaskScheduler.Default);
}

De100Message Handle(De100Message request) => request.Name switch
{
    De100Commands.Ping => De100Protocol.Success(request, De100Driver.IsSdkInstalled()),

    De100Commands.ListMachines => De100Protocol.Success(request, Driver().ListMachines()),

    De100Commands.IsReady => De100Protocol.Success(request,
        Driver().IsReady(MachineId(request))),

    De100Commands.PrinterInfo => De100Protocol.Success(request,
        Driver().GetPrinterInfo(MachineId(request))),

    De100Commands.PixelCount => PixelCount(request),

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
/// PIEGE VERIFIE LE 31/07/2026 : CPPCtrl32.dll se bloque indefiniment quand DiLand tient
/// le port USB de la DS620. Un appel direct figerait la boucle de lecture du relais, qui
/// ne repondrait plus pour le minilab non plus. On borne donc l attente.
///
/// Depuis le 03/08/2026 on ne renonce plus pour toute la session : on regarde d abord si
/// DiLand tourne. S il tourne, on ne tente meme pas l appel (port tenu, la DNP disparait
/// du bandeau) ; des qu il se ferme, la machine redevient interrogeable sans redemarrer
/// Studio Photo. Un blocage constate HORS DiLand reste, lui, definitif : c est alors un
/// vrai probleme materiel, et reessayer toutes les deux minutes accumulerait des fils
/// bloques.
/// </summary>
List<DnpPrinterInfo> EtatDesDnp()
{
    var diland = DiLandPresence.IsRunning();

    if (diland != dilandTenaitLePort)
    {
        log(diland
            ? "DiLand vient de s ouvrir : il tient le port USB, la DNP est masquee."
            : "DiLand vient de se fermer : la DNP redevient interrogeable.");

        // le renoncement constate pendant que DiLand tenait le port ne vaut plus rien
        // maintenant qu il l a lache
        if (!diland) dnpAbandonne = false;
        dilandTenaitLePort = diland;
    }

    if (diland) return [];

    if (dnpAbandonne) return [];

    if (!DnpDriver.IsSdkInstalled())
    {
        log("SDK DNP introuvable (CPPCtrl32.dll) : aucune imprimante DNP remontee.");
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

    // Le relais ne rend QUE ce que le SDK a vu. Si la machine dort, il rend une liste
    // vide et c est l application qui completera d apres le spouleur Windows : cette
    // enumeration-la a sa place cote application, pas ici.
    //
    // Elle etait ici, et c est ce qui a fige une commande de 41 photos le 03/08/2026 :
    // enumerer les imprimantes peut rester suspendu quand une file ne repond pas, et le
    // relais servant les commandes une par une, tout le reste attendait derriere.
    if (lecture.Wait(TimeSpan.FromSeconds(6))) return lecture.Result;

    dnpAbandonne = true;
    log("Imprimantes DNP sans reponse en 6 s alors que DiLand ne tourne pas : port tenu " +
        "par un autre programme, ou machine muette. On cesse de les interroger jusqu au " +
        "prochain passage de DiLand.");
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

    var handle = Driver().Submit(demande.Jobs, demande.MachineId);
    log($"Commande de {demande.Jobs.Count} tirage(s) acceptée par le minilab " +
        $"(handle {Tronque(handle)}) : {string.Join(", ", demande.Jobs.Select(j => j.JobId))}.");
    return De100Protocol.Success(request, handle);
}

/// <summary>
/// La définition que la MACHINE attend pour un format donné.
///
/// Elle ajoute son débord — 2515 × 3543 px pour un 210 × 297 à 300 ppp, soit 213 × 300 mm —
/// et les canaux à format FIXE refusent tout ce qui n'est pas exactement cette taille. Voir
/// <c>PrintOrchestrator.FitPageToRoll</c>.
/// </summary>
De100Message PixelCount(De100Message request)
{
    var demande = De100Protocol.Payload<De100PixelCountRequest>(request)
                  ?? throw new InvalidOperationException("Demande de définition vide.");

    var (resultat, largeur, hauteur) = Driver().FormatAccepte(
        demande.MachineId, demande.WidthMm, demande.HeightMm, demande.Dpi);

    // un refus n'est pas une erreur du relais : on rend 0 × 0, et l'appelant garde son
    // propre calcul plutôt que de perdre le tirage
    if (resultat != PifResult.Ok)
    {
        log($"Définition refusée pour {demande.WidthMm:0}×{demande.HeightMm:0} mm " +
            $"sur « {demande.MachineId} » ({resultat}).");
        return De100Protocol.Success(request, new De100PixelCountResponse(0, 0));
    }

    return De100Protocol.Success(request, new De100PixelCountResponse(largeur, hauteur));
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
