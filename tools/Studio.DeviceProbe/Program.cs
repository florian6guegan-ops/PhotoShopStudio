using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;

// Outil de diagnostic à passer sur la borne, en présence des machines.
//
// Compilé en x86 (obligatoire : les deux SDK sont 32 bits). À lancer depuis un dossier
// où les DLL natives sont résolubles — le plus simple est de copier DeviceProbe.exe et
// ses dépendances dans « C:\Program Files (x86)\DiLand Studio 2 », qui contient déjà
// PModuleIF.dll et cspstat.dll.
//
//   DeviceProbe            → état des deux familles de machines
//   DeviceProbe dnp        → DNP seulement
//   DeviceProbe de100      → Fuji DE100 seulement
//   DeviceProbe de100 test <image> <format> → envoie un tirage d'essai et attend l'issue

var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "all";

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine($"Studio DeviceProbe — processus {(Environment.Is64BitProcess ? "64" : "32")} bits");
if (Environment.Is64BitProcess)
{
    Console.WriteLine("ERREUR : ce processus est en 64 bits, les SDK ne se chargeront pas.");
    Console.WriteLine("Recompiler avec <PlatformTarget>x86</PlatformTarget>.");
    return 2;
}
Console.WriteLine();

var failures = 0;

if (mode is "all" or "dnp")
    failures += ProbeDnp();

if (mode is "all" or "de100")
    failures += ProbeDe100(args);

return failures == 0 ? 0 : 1;

int ProbeDnp()
{
    Section("Imprimantes DNP (cspstat.dll)");

    if (!DnpDriver.IsSdkInstalled())
    {
        Console.WriteLine("  SDK DNP absent : cspstat.dll introuvable depuis ce dossier.");
        return 1;
    }

    try
    {
        var driver = new DnpDriver();
        var ports = driver.ListPorts();
        if (ports.Count == 0)
        {
            Console.WriteLine("  Aucune imprimante DNP détectée.");
            return 1;
        }

        foreach (var port in ports)
        {
            var info = driver.GetPrinterInfo(port);
            var reste = info.MediaRemainingPercent is { } pct
                ? $"{info.MediaRemaining} tirages ({pct:0}% du rouleau)"
                : $"{info.MediaRemaining} tirages";

            Console.WriteLine($"  Port {port} — série {info.SerialNumber}, micrologiciel {info.FirmwareVersion}");
            Console.WriteLine($"    État        : {info.Status.Message}");
            Console.WriteLine($"    Média       : {info.MediaSize} ({info.MediaClass}), reste {reste}");
            Console.WriteLine($"    File        : {info.QueuedPrints} tirage(s) en mémoire");
            Console.WriteLine($"    Compteur    : {info.LifetimePrints} tirages depuis la mise en service");
            var (h, v) = driver.GetResolution(port);
            Console.WriteLine($"    Résolution  : {h}×{v} ppp");
            Console.WriteLine();
        }
        return 0;
    }
    catch (DllNotFoundException)
    {
        Console.WriteLine("  cspstat.dll introuvable : lancer l'outil depuis le dossier de DiLand.");
        return 1;
    }
    catch (BadImageFormatException)
    {
        Console.WriteLine("  cspstat.dll est en 32 bits et ce processus ne l'est pas.");
        return 1;
    }
}

int ProbeDe100(string[] argv)
{
    Section("Minilab Fuji Frontier DE100 (PModuleIF.dll)");

    // le SDK vit dans le dossier de DiLand, pas à côté de cet outil
    var sdk = De100Driver.LocateSdk();
    Console.WriteLine(sdk is null ? "  SDK Fuji : introuvable" : $"  SDK Fuji : {sdk}");

    if (!De100Driver.IsSdkInstalled())
    {
        Console.WriteLine("  SDK Fuji absent : PModuleIF.dll introuvable depuis ce dossier.");
        return 1;
    }

    try
    {
        using var driver = new De100Driver(jobTimeout: TimeSpan.FromMinutes(10));

        var machines = driver.ListMachines();
        if (machines.Count == 0)
        {
            Console.WriteLine("  Aucune machine déclarée dans la configuration du DE100.");
            return 1;
        }

        foreach (var machineId in machines)
        {
            var info = driver.GetPrinterInfo(machineId);
            Console.WriteLine($"  Machine '{machineId}' — {info.Model} (série {info.SerialNumber})");
            Console.WriteLine($"    État       : {info.Status}   Prête : {(driver.IsReady(machineId) ? "oui" : "non")}");
            Console.WriteLine($"    Réseau     : {info.IpAddress}   Compteur : {info.TotalPrintCount} tirages");

            if (info.Media is { } media)
            {
                Console.WriteLine($"    Rouleau    : magasin {media.LoadingNumber}, type {media.MagazineType}, " +
                                  $"{media.PaperWidthMm} mm, {media.Surface}");
                Console.WriteLine($"    Papier     : {media.PaperRemainingMm:0} (unité brute du SDK) restant");
            }

            if (info.Supplies is { } supplies)
            {
                var encres = string.Join("   ", supplies.Inks.Select(i => $"{i.Name} {i.Level}"));
                Console.WriteLine($"    Encres     : {encres}");
                Console.WriteLine($"    {supplies.MaintenanceTank.Name} : {supplies.MaintenanceTank.Level}");
            }

            if (info.Formats.Count > 0)
            {
                Console.WriteLine("    Tirages restants par format :");
                foreach (var f in info.Formats.Where(f => !f.Format.IsVariable))
                    Console.WriteLine($"      {f.Format.Name,-8} {f.RemainingPrints,6}");
            }
            Console.WriteLine();
        }

        if (argv.Length >= 4 && argv[1].Equals("test", StringComparison.OrdinalIgnoreCase))
            return SubmitTestPrint(driver, machines[0], imagePath: argv[2], printSizeName: argv[3]);

        return 0;
    }
    catch (DllNotFoundException)
    {
        Console.WriteLine("  PModuleIF.dll introuvable : lancer l'outil depuis le dossier de DiLand.");
        return 1;
    }
    catch (BadImageFormatException)
    {
        Console.WriteLine("  PModuleIF.dll est en 32 bits et ce processus ne l'est pas.");
        return 1;
    }
    catch (De100Exception ex)
    {
        Console.WriteLine($"  Le SDK a refusé la demande : {ex.Message}");
        return 1;
    }
}

int SubmitTestPrint(De100Driver driver, char machineId, string imagePath, string printSizeName)
{
    Section("Tirage d'essai");

    if (!File.Exists(imagePath))
    {
        Console.WriteLine($"  Image introuvable : {imagePath}");
        return 1;
    }

    using var finished = new ManualResetEventSlim(false);
    De100JobResult? outcome = null;

    driver.JobFinished += (_, result) =>
    {
        outcome = result;
        finished.Set();
    };
    driver.MachineEvent += (_, evt) =>
        Console.WriteLine($"  [machine] {evt.Level} {evt.ErrorNumber} : {evt.Message}");

    driver.Subscribe(machineId);

    var job = new De100PrintJob(
        JobId: "essai-" + DateTime.Now.ToString("HHmmss"),
        ImagePath: Path.GetFullPath(imagePath),
        WidthMm: 152,
        HeightMm: 102,
        PrintSizeName: printSizeName);

    Console.WriteLine($"  Envoi de « {job.ImagePath} » au format {printSizeName} sur la machine '{machineId}'…");
    var handle = driver.Submit(job, machineId);
    Console.WriteLine($"  Accepté, handle de commande : {handle}");
    Console.WriteLine("  Attente de l'issue (10 min max)…");

    // le suivi borne l'attente : même un minilab totalement muet finit par trancher,
    // c'est exactement ce qui manquait à DiLand
    finished.Wait(TimeSpan.FromMinutes(11));

    if (outcome is null)
    {
        Console.WriteLine("  Aucune issue reçue — le pilote n'a même pas rendu son verdict d'expiration.");
        return 1;
    }

    Console.WriteLine($"  Issue : {outcome.Outcome} — {outcome.Reason}");
    return outcome.Outcome == De100JobOutcome.Printed ? 0 : 1;
}

static void Section(string title)
{
    Console.WriteLine(title);
    Console.WriteLine(new string('─', title.Length));
}
