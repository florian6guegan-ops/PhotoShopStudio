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
    ? "SDK DNP introuvable : definissez STUDIO_DNP_SDK sur le dossier contenant cspstat.dll."
    : $"SDK DNP trouvé : {sdkDnp}");

De100Driver? driver = null;

// Blocage constate trop de fois : on cesse d interroger la DNP jusqu au redemarrage du
// relais (voir EtatDesDnp).
var dnpAbandonne = false;

// Une machine qui imprime repond lentement. On la laisse tranquille un moment plutot que
// de la declarer disparue.
var dnpMuetJusqua = DateTime.MinValue;
var dnpQuarantaines = 0;

// Trois minutes : plus long qu une planche d identite, assez court pour que le bandeau
// se retablisse pendant que l operateur regarde encore l ecran.
var DnpQuarantaine = TimeSpan.FromMinutes(3);

// Au-dela, ce n est plus une machine occupee : chaque delai depasse laisse un fil bloque
// dans l appel natif, et l on ne peut pas en accumuler indefiniment.
const int DnpQuarantainesAvantAbandon = 5;
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

/// <summary>
/// Ouvre le pilote, en DISANT combien de temps chaque étape a pris.
///
/// <b>Pourquoi ces mesures.</b> Le 10/08/2026, à Créteil, l'ouverture prenait
/// <b>119 secondes</b> — deux fois le même chiffre — là où elle en prend une à
/// Maisons-Alfort. Le client, lui, renonce au bout de dix secondes et annonce une machine
/// « en veille », alors qu'elle répond : <c>PIF_DevIsReady</c> rend 0 et
/// <c>PIF_GetPrinterList</c> les deux machines en 22 ms. Le même exécutable lancé à la main
/// ouvre en 163 ms ; lancé par l'application, en 119 s.
///
/// Tout ce qu'on pouvait observer du dehors a été écarté (SDK, matériel, réseau, DiLand,
/// PUD, antivirus, session Windows). Faute de savoir OÙ passent ces deux minutes, on le
/// demande au relais lui-même : le chargement de la bibliothèque native et
/// <c>PIF_Open</c> sont désormais chronométrés séparément.
/// </summary>
De100Driver Driver()
{
    if (driver is not null) return driver;

    var chrono = System.Diagnostics.Stopwatch.StartNew();
    var chargeable = De100Driver.IsSdkInstalled();
    log($"SDK Fuji chargé ({(chargeable ? "présent" : "ABSENT")}) en {chrono.ElapsedMilliseconds} ms.");

    chrono.Restart();
    driver = new De100Driver();
    log($"PIF_Open rendu en {chrono.ElapsedMilliseconds} ms.");

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

    var chrono = System.Diagnostics.Stopwatch.StartNew();
    var travail = Task.Run(() => Handle(request));

    if (await Task.WhenAny(travail, Task.Delay(budget)) == travail)
    {
        try
        {
            Send(await travail);

            // Une interrogation lente mais qui ABOUTIT ne laisse aucune trace, et c'est
            // elle qu'on cherche : à Créteil le bandeau tombe sur le délai sans qu'on
            // sache si le temps part dans le SDK ou ailleurs. Seuil haut : en marche
            // normale ces commandes rendent en quelques millisecondes.
            if (chrono.ElapsedMilliseconds >= 1000)
                log($"« {request.Name} » a repondu en {chrono.ElapsedMilliseconds} ms.");
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

    // le fil continue sa vie ; son resultat, s il arrive, ne concerne plus personne — mais
    // le TEMPS qu il aura mis nous intéresse, lui : c est la seule mesure de ce que le SDK
    // fait vraiment quand le client a déjà renoncé
    _ = travail.ContinueWith(t => log(t.IsFaulted
            ? $"« {request.Name} » a fini par echouer apres {chrono.ElapsedMilliseconds} ms : " +
              $"{t.Exception?.GetBaseException().Message}"
            : $"« {request.Name} » a fini par repondre apres {chrono.ElapsedMilliseconds} ms, trop tard."),
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

    De100Commands.DnpPrint => TirerSurDnp(request),

    _ => De100Protocol.Failure(request, $"Commande inconnue : « {request.Name} »"),
};

/// <summary>
/// Etat des imprimantes DNP branchees.
///
/// ON N ATTEND PLUS QUE DILAND SE FERME. Jusqu au 06/08/2026, ce relais sautait purement
/// l appel des que DiLand tournait — c est-a-dire presque toujours en boutique — au motif
/// qu il tenait le port USB en exclusif. Ce motif etait faux : le SDK ne voyait rien
/// parce que le mauvais fichier etait appele (CPPCtrl32.dll au lieu de cspstat.dll, voir
/// CspStatInterop). Avec le bon, la DS620 rend son numero de serie et son etat DILAND
/// OUVERT. Le masquage n avait donc plus d objet, et il coutait le bandeau d etat.
///
/// UNE LENTEUR N EST PAS UNE PANNE. Le renoncement etait DEFINITIF pour la session : six
/// secondes sans reponse et la DNP disparaissait de Studio jusqu au redemarrage du relais.
/// Or la machine repond lentement PENDANT QU ELLE IMPRIME — deux planches d identite
/// lancees a la suite ont suffi (06/08/2026 a 17:31), la machine allant parfaitement bien :
/// « Prete », 35 tirages restants. Studio la donnait pour disparue, DiLand non.
///
/// On met donc la DNP en QUARANTAINE quelques minutes au lieu de l abandonner, et l on ne
/// renonce pour de bon qu apres plusieurs quarantaines de suite. Le compromis est celui-ci :
/// chaque delai depasse laisse derriere lui un fil bloque dans l appel natif, et cette
/// boucle sert aussi le minilab — on ne peut donc pas reessayer indefiniment.
/// </summary>
List<DnpPrinterInfo> EtatDesDnp()
{
    if (dnpAbandonne) return [];

    if (DateTime.Now < dnpMuetJusqua) return [];

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

    // Le relais ne rend QUE ce que le SDK a vu. Si la machine dort, il rend une liste
    // vide et c est l application qui completera d apres le spouleur Windows : cette
    // enumeration-la a sa place cote application, pas ici.
    //
    // Elle etait ici, et c est ce qui a fige une commande de 41 photos le 03/08/2026 :
    // enumerer les imprimantes peut rester suspendu quand une file ne repond pas, et le
    // relais servant les commandes une par une, tout le reste attendait derriere.
    if (lecture.Wait(TimeSpan.FromSeconds(6)))
    {
        dnpQuarantaines = 0;   // elle a repondu : l ardoise est effacee
        return lecture.Result;
    }

    dnpQuarantaines++;

    if (dnpQuarantaines >= DnpQuarantainesAvantAbandon)
    {
        dnpAbandonne = true;
        log($"Imprimantes DNP sans reponse {dnpQuarantaines} fois de suite : port tenu par un " +
            "autre programme, ou machine muette. On cesse de les interroger jusqu au " +
            "redemarrage du relais.");
        return [];
    }

    dnpMuetJusqua = DateTime.Now + DnpQuarantaine;
    log($"Imprimantes DNP sans reponse en 6 s (essai {dnpQuarantaines} sur " +
        $"{DnpQuarantainesAvantAbandon}) : elles impriment peut-etre. On les laisse " +
        $"tranquilles {DnpQuarantaine.TotalMinutes:0} min et l on reessaie.");
    return [];
}

/// <summary>
/// Envoie un tirage a une DNP SANS PASSER PAR LE PILOTE WINDOWS.
///
/// C est le chemin de DiLand, et c est celui qui ne fabrique pas le fantome colore :
/// mesure du 06/08/2026, le pilote est hors de cause des qu on ne l emprunte plus. Voir
/// DnpEnvoiDirect pour les trois conventions de la trame.
///
/// La decouverte doit avoir eu lieu DANS CE PROCESSUS avant tout envoi : c est
/// ListPorts qui construit la table de ports du SDK, et sans elle SendImageData ne sait
/// pas a qui parler.
///
/// L application garde le pilote en secours : si cet envoi echoue, elle imprime comme
/// avant plutot que de ne rien sortir.
/// </summary>
De100Message TirerSurDnp(De100Message request)
{
    var demande = De100Protocol.Payload<De100DnpPrintRequest>(request)
                  ?? throw new InvalidOperationException("Demande de tirage DNP vide.");

    if (!File.Exists(demande.ImagePath))
        return De100Protocol.Failure(request, $"Image introuvable : {demande.ImagePath}");

    var pilote = new DnpDriver();

    var ports = pilote.ListPorts();
    if (!ports.Contains(demande.PortNumber))
        return De100Protocol.Failure(request,
            $"Aucune DNP au rang {demande.PortNumber} (decouverte : {ports.Count}).");

    using var image = new System.Drawing.Bitmap(demande.ImagePath);

    // Un 10x15 sur un rouleau 15x20 doit etre COUPE, sinon la machine sort une feuille
    // entiere dont la moitie part a la poubelle. On lui reclame donc le format qui convient.
    var etat = pilote.GetPrinterInfo(demande.PortNumber);
    var (pppH, pppV) = pilote.GetResolution(demande.PortNumber);
    var taille = pppH > 0 && pppV > 0
        ? DnpDriver.TailleDeTirage(etat.MediaSize, image.Width / (double)pppH, image.Height / (double)pppV)
        : etat.MediaSize;

    if (taille != etat.MediaSize)
        log($"Rouleau {etat.MediaSize} et tirage plus petit : on reclame {taille} " +
            "(la machine coupe, deux tirages par feuille).");

    var faits = 0;
    for (var i = 0; i < Math.Max(1, demande.Copies); i++)
    {
        if (!DnpEnvoiDirect.Envoyer(demande.PortNumber, image, (DnpOvercoat)demande.Overcoat, taille))
        {
            log($"Envoi direct DNP refuse a la copie {i + 1} : {faits} exemplaire(s) accepte(s).");
            return De100Protocol.Failure(request,
                $"L imprimante a refuse le tirage apres {faits} exemplaire(s).");
        }

        faits++;
    }

    log($"Envoi direct DNP : {faits} exemplaire(s) de {Path.GetFileName(demande.ImagePath)} " +
        $"({image.Width}x{image.Height}, rouleau {etat.MediaSize}, format demande {taille}) " +
        "acceptes, sans passer par le spouleur.");

    return De100Protocol.Success(request, faits);
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
