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

// Appels au SDK partis et jamais revenus. Voir FilsOrphelins.
var filsOrphelins = new FilsOrphelins();
var plafondSignale = false;

// UN SEUL APPEL A LA FOIS DANS LE SDK DNP, et c est ce qui tuait le relais.
//
// cspstat.dll est une bibliotheque native de 2008 (elle tourne sur MSVCR90) et elle n est
// PAS reentrante. Or deux chemins y entrent : EtatDesDnp pour le bandeau, toutes les
// quelques secondes, et TirerSurDnp pour un envoi. RepondreSansJamaisBloquer donnant un fil
// a chaque commande, les deux pouvaient s y trouver ENSEMBLE.
//
// Le 12/08/2026 a Creteil : CINQ plantages du relais, zero les six jours precedents. Tous
// avec la meme signature — module MSVCR90.dll, code 0xc0000417 (parametre invalide passe au
// CRT), meme decalage 0x0003523b. Le premier arrive dix secondes apres un envoi direct DNP.
// Ce n est pas une fuite de memoire : au moment ou j ecris, le relais tient en 59 Mo et
// douze fils, tres loin des deux gigaoctets.
//
// ⚠ ET C EST MA CORRECTION DE LA 1.4.1 QUI A OUVERT LA FENETRE : dnp-print y est passe de
// dix secondes a trois minutes de budget. Le SDK reste donc occupe bien plus longtemps,
// pendant que le bandeau continue de l interroger. Le correctif de la 1.4.1 reste juste —
// c est la concurrence qu il fallait borner, pas le delai.
var verrouDnp = new object();
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
    // Envoyer un tirage prend legitimement du temps ; interroger une machine, non. La
    // regle est celle de De100Commands.EngageLaMachine, que le client applique aussi :
    // elle etait ecrite ici en dur, et DnpPrint n y figurait pas.
    //
    // Ce qu il en a coute, le 12/08/2026 : la troisieme planche de la commande 12-012 est
    // arrivee pendant que la machine tirait les deux premieres, le relais a renonce a
    // 10 s SANS INTERROMPRE l appel natif, l application s est rabattue sur le pilote
    // Windows — et 1 s plus tard le SDK rendait la main, tirage accepte. La meme planche
    // est partie deux fois. Une DNP qui imprime met couramment plus de dix secondes a
    // prendre l ordre suivant : le commentaire de EtatDesDnp le disait deja.
    var budget = De100Commands.EngageLaMachine(request.Name)
        ? TimeSpan.FromMinutes(3)
        : TimeSpan.FromSeconds(10);

    // ON NE LANCE PLUS DE TRAVAIL QUAND TROP DE FILS SONT DEJA PERDUS.
    //
    // Chaque delai depasse laisse un fil bloque dans un appel natif qui ne rendra jamais la
    // main — c est dit plus haut, et c est irreductible : le SDK ne s interrompt pas. Ce qui
    // ne l etait pas, c est que RIEN ne bornait leur nombre.
    //
    // Or ce relais est en 32 BITS : deux gigaoctets d espace d adressage, et un mega-octet
    // de pile par fil. Sur un poste ou le SDK se coince — Creteil, ou list-machines et
    // dnp-snapshot expirent en boucle des que DiLand tient les machines — ils s accumulent
    // au rythme du bandeau, et le processus finit par mourir. Constate le 12/08/2026 : le
    // relais s est arrete DEUX FOIS en pleine commande (16:11 et 17:36), laissant la
    // commande 12-024 « non confirmee » sans qu aucun verdict n arrive.
    //
    // Passe ce plafond, on repond tout de suite sans rien lancer : le SDK est manifestement
    // coince, et lui envoyer du travail supplementaire ne fait qu avancer l heure du crash.
    if (filsOrphelins.Sature)
    {
        if (!plafondSignale)
        {
            plafondSignale = true;
            log($"{filsOrphelins.Perdus} appels au SDK sont restes sans retour : il est " +
                "coince. On cesse d en lancer de nouveaux — le relais tiendrait sinon jusqu a " +
                "epuiser sa memoire, et il mourrait en pleine commande.");
        }

        Send(De100Protocol.Failure(request,
            "Le SDK du minilab ne rend plus la main. Redemarrez l application pour repartir " +
            "sur une session neuve."));
        return;
    }

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

    // Le fil continue sa vie ; son resultat, s il arrive, ne concerne plus personne — mais
    // le TEMPS qu il aura mis nous interesse, lui : c est la seule mesure de ce que le SDK
    // fait vraiment quand le client a deja renonce.
    //
    // Il est COMPTE a partir d ici, et decompte s il finit par revenir : c est ce compte qui
    // dit si le SDK est simplement lent ou definitivement coince.
    filsOrphelins.Abandonne();

    _ = travail.ContinueWith(t =>
        {
            filsOrphelins.Revenu();
            if (!filsOrphelins.Sature) plafondSignale = false;

            log(t.IsFaulted
                ? $"« {request.Name} » a fini par echouer apres {chrono.ElapsedMilliseconds} ms : " +
                  $"{t.Exception?.GetBaseException().Message}"
                : $"« {request.Name} » a fini par repondre apres {chrono.ElapsedMilliseconds} ms, trop tard.");
        },
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

    De100Commands.OrderProgress => AvancementDeLaCommande(request),

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
        // SI LE SDK EST DEJA OCCUPE, ON NE FAIT PAS LA QUEUE : on rend la main tout de
        // suite. Un tirage a la priorite sur un bandeau, et attendre ici ne ferait
        // qu empiler des fils derriere un envoi qui peut durer trois minutes.
        //
        // Rendre une liste vide est exactement ce que fait deja une machine endormie :
        // l application complete d apres le spouleur Windows, et le bandeau ne ment pas.
        if (!Monitor.TryEnter(verrouDnp))
        {
            log("SDK DNP occupe par un tirage : on ne l interroge pas maintenant.");
            return new List<DnpPrinterInfo>();
        }

        try
        {
            var pilote = new DnpDriver();
            var etats = new List<DnpPrinterInfo>();
            foreach (var port in pilote.ListPorts())
            {
                try { etats.Add(pilote.GetPrinterInfo(port)); }
                catch (Exception ex) { log($"Imprimante DNP du port {port} illisible : {ex.Message}"); }
            }
            return etats;
        }
        finally
        {
            Monitor.Exit(verrouDnp);
        }
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

    // TOUT L ENVOI SOUS LE MEME VERROU, decouverte comprise : c est ListPorts qui construit
    // la table de ports du SDK, et un bandeau qui la reconstruirait au milieu de l envoi
    // ferait exactement ce qui a tue le relais cinq fois le 12/08/2026. Voir verrouDnp.
    lock (verrouDnp)
        return TirerSurDnpSousVerrou(request, demande);
}

/// <summary>
/// Combien de points par pouce pour juger de la TAILLE PHYSIQUE du tirage.
///
/// La REGLE vit dans DnpDriver.DefinitionRetenue, ou elle est eprouvee par des essais ;
/// ici on ne fait que lui apporter ses trois chiffres et dire au journal ce qu on a retenu.
/// Voir cette methode pour la demi-feuille perdue de kodakidpc le 17/08/2026.
/// </summary>
(double H, double V) ResolutionUtile(DnpDriver pilote, int port, System.Drawing.Bitmap image)
{
    double machineH = 0, machineV = 0;
    try
    {
        (machineH, machineV) = pilote.GetResolution(port);
    }
    catch (Exception ex)
    {
        log($"Definition de la DNP illisible ({ex.Message}).");
    }

    var retenue = DnpDriver.DefinitionRetenue(
        machineH, machineV, image.HorizontalResolution, image.VerticalResolution);

    // On ne le dit QUE si la machine s est tue : en fonctionnement normal cette ligne
    // paraitrait a chaque tirage sans rien apprendre a personne.
    if (retenue.H != machineH || retenue.V != machineV)
        log($"La DNP n annonce pas sa definition ({machineH}x{machineV} ppp) : on retient " +
            $"{retenue.H:0}x{retenue.V:0} ppp plutot que de renoncer a la decoupe.");

    return retenue;
}

De100Message TirerSurDnpSousVerrou(De100Message request, De100DnpPrintRequest demande)
{
    var pilote = new DnpDriver();

    var ports = pilote.ListPorts();
    if (!ports.Contains(demande.PortNumber))
        return De100Protocol.Failure(request,
            $"Aucune DNP au rang {demande.PortNumber} (decouverte : {ports.Count}).");

    using var image = new System.Drawing.Bitmap(demande.ImagePath);

    // Un 10x15 sur un rouleau 15x20 doit etre COUPE, sinon la machine sort une feuille
    // entiere dont la moitie part a la poubelle. On lui reclame donc le format qui convient.
    var etat = pilote.GetPrinterInfo(demande.PortNumber);
    var (pppH, pppV) = ResolutionUtile(pilote, demande.PortNumber, image);

    var taille = DnpDriver.TailleDeTirage(
        etat.MediaSize, image.Width / pppH, image.Height / pppV);

    if (taille != etat.MediaSize)
        log($"Rouleau {etat.MediaSize} et tirage plus petit : on reclame {taille} " +
            "(la machine coupe, deux tirages par feuille).");

    // ⚠ LA TRAME SUIT LE ROULEAU, PAS LE RENDU.
    //
    // La machine n'oriente rien : elle attend une trame dont la LARGEUR est celle du
    // rouleau — pour un 6x4, du 1844 x 1240. On lui remettait l'image telle que le rendu
    // l'avait faite ; pour un produit PORTRAIT c'est l'inverse, et elle lit alors la trame
    // en travers et coupe ce qui depasse.
    //
    // Commande 18-006 du 18/08/2026 : une E-Photo portrait sortie coupee en paysage. Le
    // rendu etait pourtant juste — 1240 x 1844 pour un produit de 105 x 156,1 mm, photo
    // entiere, aucun recadrage. Les planches d'identite, elles, sont deja dans le sens de la
    // trame (1844 x 1240) et sortent juste depuis des semaines : c'est pour ca que personne
    // ne l'avait vu.
    if (DnpDriver.DoitPivoter(taille, image.Width, image.Height))
    {
        image.RotateFlip(System.Drawing.RotateFlipType.Rotate90FlipNone);
        log($"Trame pivotee d'un quart de tour pour {taille} : " +
            $"{image.Width}x{image.Height} (la largeur suit le rouleau).");
    }

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

/// <summary>
/// Ou en est une commande, comptee par la machine elle meme.
///
/// Une commande que le SDK ne reconnait plus rend NULL et non une erreur : ce releve ne
/// sert qu a faire avancer une barre, et l appelant retombe sur les verdicts.
/// </summary>
De100Message AvancementDeLaCommande(De100Message request)
{
    var handle = De100Protocol.Payload<string>(request)
                 ?? throw new InvalidOperationException("Handle de commande manquant.");

    return De100Protocol.Success(request, Driver().OrderProgress(handle));
}

char MachineId(De100Message request)
{
    var value = De100Protocol.Payload<string>(request);
    if (string.IsNullOrEmpty(value))
        throw new InvalidOperationException("Identifiant machine manquant.");
    return value[0];
}

static string Tronque(string texte) => texte.Length <= 60 ? texte : texte[..60] + "…";
