using Studio.Printing.Devices.Dnp;

// Sonde de diagnostic : lit l'etat reel de la DS620 par le SDK DNP, sans imprimer.
// Jetable — voir la conversation du 05/08/2026 (la DS620 accepte les travaux et ne sort rien).

Console.OutputEncoding = System.Text.Encoding.UTF8;

Console.WriteLine($"DiLand ouvert : {DiLandPresence.IsRunning()}");

var sdk = DnpDriver.LocateSdk();
Console.WriteLine($"SDK DNP       : {sdk ?? "INTROUVABLE"}");
if (sdk is not null) DnpDriver.UseSdkFrom(sdk);
Console.WriteLine($"SDK chargeable: {DnpDriver.IsSdkInstalled()}");

if (!DnpDriver.IsSdkInstalled())
{
    Console.WriteLine("Sans SDK, rien a lire.");
    return;
}

var pilote = new DnpDriver();
var ports = pilote.ListPorts();
Console.WriteLine($"Ports trouves : {ports.Count} [{string.Join(", ", ports)}]");

foreach (var port in ports)
{
    Console.WriteLine($"\n===== Port {port} =====");
    try
    {
        var info = pilote.GetPrinterInfo(port);
        Console.WriteLine($"  Serie         : {info.SerialNumber}");
        Console.WriteLine($"  Micrologiciel : {info.FirmwareVersion}");
        Console.WriteLine($"  Etat brut     : 0x{info.Status.Raw:X8}");
        Console.WriteLine($"  Etat          : {info.Status.Message}");
        Console.WriteLine($"  Famille       : {info.Status.Group}");
        Console.WriteLine($"  Prete         : {info.Status.IsReady}");
        Console.WriteLine($"  Occupee       : {info.Status.IsBusy}");
        Console.WriteLine($"  Operateur ?   : {info.Status.NeedsOperator}");
        Console.WriteLine($"  Panne ?       : {info.Status.IsFault}");
        Console.WriteLine($"  Media         : {info.MediaSize} / {info.MediaClass}");
        Console.WriteLine($"  Restant       : {info.MediaRemaining} sur {info.MediaInitialCount}");
        Console.WriteLine($"  En file (SDK) : {info.QueuedPrints}");
        Console.WriteLine($"  Total machine : {info.LifetimePrints}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ILLISIBLE : {ex.GetType().Name} — {ex.Message}");
    }
}

// Interrogation DIRECTE des premiers rangs, meme si la decouverte n'a rien rendu :
// c'est ce qui distingue « aucune imprimante » de « imprimante qui ne repond pas ».
Console.WriteLine("\n===== Interrogation directe des rangs 0 a 2 =====");
foreach (var rang in new[] { 0, 1, 2 })
{
    try
    {
        var etatBrut = pilote.GetStatus(rang);
        Console.WriteLine($"  rang {rang} : 0x{etatBrut.Raw:X8}  " +
                          $"(comm KO={etatBrut.IsCommunicationFailure}, delai={etatBrut.IsTimeout}) " +
                          $"— {etatBrut.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  rang {rang} : EXCEPTION {ex.GetType().Name} — {ex.Message}");
    }
}

Console.WriteLine("\n===== File Windows =====");
var etat = DnpSpouleur.Lire("DP-DS620");
Console.WriteLine($"  Etat    : {etat.Etat}");
Console.WriteLine($"  Message : {(string.IsNullOrEmpty(etat.Message) ? "(aucun)" : etat.Message)}");
Console.WriteLine($"  Restantes : {etat.PhotosRestantes}");
Console.WriteLine($"  Travaux   : {etat.TravauxEnAttente}");
