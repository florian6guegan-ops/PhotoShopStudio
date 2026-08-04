using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;

// Outil de diagnostic à passer sur la borne, en présence des machines.
//
// Compilé en x86 (obligatoire : les deux SDK sont 32 bits). À lancer depuis un dossier
// où les DLL natives sont résolubles — le plus simple est de copier DeviceProbe.exe et
// ses dépendances dans « C:\Program Files (x86)\DiLand Studio 2 », qui contient déjà
// PModuleIF.dll et CPPCtrl32.dll.
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
    Section("Imprimantes DNP (CPPCtrl32.dll)");

    // le SDK vit dans le dossier de DiLand, pas à côté de cet outil
    var sdkDnp = DnpDriver.LocateSdk();
    Console.WriteLine(sdkDnp is null ? "  SDK DNP : introuvable" : $"  SDK DNP : {sdkDnp}");

    if (!DnpDriver.IsSdkInstalled())
    {
        Console.WriteLine("  SDK DNP absent : CPPCtrl32.dll introuvable depuis ce dossier.");
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
        Console.WriteLine("  CPPCtrl32.dll introuvable : lancer l'outil depuis le dossier de DiLand.");
        return 1;
    }
    catch (BadImageFormatException)
    {
        Console.WriteLine("  CPPCtrl32.dll est en 32 bits et ce processus ne l'est pas.");
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
                Console.WriteLine($"    Papier     : {media.PaperRemainingMm / 1000:0.00} m restants " +
                                  $"({media.PaperRemainingMm:0} mm)");
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

        if (argv.Length >= 2 && argv[1].Equals("formats", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var machineId in machines) SonderLesFormats(driver, machineId);
            return 0;
        }

        // « de100 essais <machine> <image> » : fait varier un paramètre d'envoi à la fois
        if (argv.Length >= 4 && argv[1].Equals("essais", StringComparison.OrdinalIgnoreCase))
            return SonderLesEnvois(driver, argv[2][0], argv[3]);

        // « de100 definitions <machine> <image> » : fait varier la DÉFINITION de l'image
        if (argv.Length >= 4 && argv[1].Equals("definitions", StringComparison.OrdinalIgnoreCase))
            return SonderLesDefinitions(driver, argv[2][0], argv[3]);

        if (argv.Length >= 3 && argv[1].Equals("test", StringComparison.OrdinalIgnoreCase))
        {
            // jamais la première machine venue : la A de la boutique est hors ligne
            var prete = machines.FirstOrDefault(m => driver.GetPrinterInfo(m).Status != De100PrinterStatus.Offline);
            if (prete == default)
            {
                Console.WriteLine("  Aucune machine en ligne : rien n'a été envoyé.");
                return 1;
            }
            return SubmitTestPrint(driver, prete, imagePath: argv[2]);
        }

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

/// Demande à la MACHINE quels formats elle sait produire, sans rien imprimer.
///
/// Écrit pour le 21×29,7 des commandes 04-015 à 04-029 du 04/08/2026 : accepté à l'envoi,
/// refusé dix secondes plus tard, sans message ni événement machine. Notre table des
/// formats vient du pilote de DiLand ; celle-ci vient du minilab lui-même.
void SonderLesFormats(De100Driver driver, char machineId)
{
    Section($"Machine '{machineId}' — formats acceptés par la machine");

    var info = driver.GetPrinterInfo(machineId);
    var rouleau = info.Media?.PaperWidthMm ?? 0;
    Console.WriteLine($"  Rouleau chargé : {rouleau} mm\n");

    // les formats de la boutique, plus le 21×29,7 en cause
    (string Nom, double L, double H)[] candidats =
    [
        ("10x15", 152, 102),
        ("13x18", 152, 127),
        ("15x20", 152, 203),
        ("20x30", 203, 305),
        ("21x29,7 (210x297)", 210, 297),
        ("21x29,7 en travers", 297, 210),
        ("A4 exact (210x297)", 210, 297),
        ("21x21", 210, 210),
        ("21x15", 210, 152),
    ];

    Console.WriteLine($"  {"Format",-22} {"demandé",-14} {"verdict SDK",-12} définition attendue");
    foreach (var (nom, l, h) in candidats)
    {
        var (resultat, w, px) = driver.FormatAccepte(machineId, l, h);
        var pixels = resultat == PifResult.Ok && w > 0 ? $"{w} × {px} px" : "—";
        Console.WriteLine($"  {nom,-22} {$"{l:0}×{h:0} mm",-14} {resultat,-12} {pixels}");
    }

    // Les noms viennent de DevPrintInfoParam.ini, le fichier de correspondance du SDK
    // livré avec DiLand : c'est la liste EXHAUSTIVE de ce que PHIF_GetValue sait rendre.
    // PaperLengthMin/Max sont les plus attendues ici — un rouleau qui plafonne sous
    // 297 mm expliquerait à lui seul le refus du 21×29,7.
    Console.WriteLine("\n  Propriétés lisibles de la machine :");
    string[] noms =
    [
        "PaperLengthMin", "PaperLengthMax", "PaperName", "PaperType", "PaperAuxiliaryInfo",
        "MagazineCaption", "NumResolution", "Resolution", "LongScale",
        "PrinterDetailStatus", "CntDrvWaiting", "CntPrnWaiting", "CntPrnPrinting", "CntWaiting",
        "FirmwareVersion", "QualitySupport", "ExpressSupport", "TwoSidedPrint", "DuplexPrint",
        "CutWaste", "SorterUnit", "MaintenanceOpe", "PrintedSheets",
    ];

    var trouvees = driver.LireProprietes(machineId, noms, [0, 1, 2, 3]);
    if (trouvees.Count == 0)
    {
        Console.WriteLine("    (aucune de ces propriétés n'a rendu de valeur)");
    }
    else
    {
        foreach (var (nom, indice, valeur) in trouvees)
            Console.WriteLine($"    {nom}{(indice == 0 ? "" : $"[{indice}]"),-6} = {valeur}");
    }

    Console.WriteLine();
}

/// Enchaîne des envois d'essai en faisant varier UN paramètre à la fois, et s'arrête au
/// premier qui sort.
///
/// Écrit pour le 21×29,7 refusé par la machine B sans le moindre motif, quand « 210x297 »
/// comme « A4 » ont échoué. Rien ne sort tant que la machine refuse : les essais ne
/// coûtent donc pas de papier, et le premier qui réussit donne la réponse.
///
/// L'ordre compte. Le premier essai est un TÉMOIN : un format dont on SAIT qu'il sort
/// (210 × 240, celui du 18×24). S'il échoue lui aussi, ce n'est pas le format qui est en
/// cause mais le protocole d'essai, et il faut s'arrêter là.
int SonderLesEnvois(De100Driver driver, char machineId, string imagePath)
{
    Section($"Essais d'envoi sur la machine '{machineId}'");

    if (!File.Exists(imagePath))
    {
        Console.WriteLine($"  Image introuvable : {imagePath}");
        return 1;
    }

    var info = driver.GetPrinterInfo(machineId);
    var surface = info.Media?.Surface ?? De100Surface.Glossy;
    Console.WriteLine($"  Rouleau {info.Media?.PaperWidthMm} mm, finition {surface}");
    Console.WriteLine($"  Image : {Path.GetFullPath(imagePath)}\n");

    (string Quoi, double W, double H, string Nom)[] essais =
    [
        ("TÉMOIN — le format du 18×24, qui sort",        210, 240, "210x240"),
        ("le format actuel du 21×29,7",                  210, 297, "210x297"),
        ("le nom du format chez DiLand",                 210, 297, "A4"),
        ("le nom du CANAL chez DiLand",                  210, 297, "21xL"),
        ("cotes inversées, nom du canal",                297, 210, "21xL"),
        ("cotes inversées, nom du format",               297, 210, "A4"),
        ("cotes inversées, nom déduit",                  297, 210, "297x210"),
        ("canal variable, longueur juste sous 297",      210, 296, "21xL"),
        ("sans nom de format du tout",                   210, 297, ""),
    ];

    De100JobResult? issue = null;
    using var fini = new ManualResetEventSlim(false);

    driver.JobFinished += (_, resultat) => { issue = resultat; fini.Set(); };
    driver.MachineEvent += (_, evt) =>
        Console.WriteLine($"    [machine] {evt.Level} {evt.ErrorNumber} : {evt.Message}");
    driver.Subscribe(machineId);

    for (var i = 0; i < essais.Length; i++)
    {
        var (quoi, w, h, nom) = essais[i];
        Console.WriteLine($"  [{i + 1}/{essais.Length}] {quoi}");
        Console.WriteLine($"        Width={w:0} Height={h:0} PrintSizeName=" +
                          (nom.Length > 0 ? $"« {nom} »" : "(vide)"));

        issue = null;
        fini.Reset();

        try
        {
            var handle = driver.Submit(new De100PrintJob(
                JobId: $"essai-{i + 1}-" + DateTime.Now.ToString("HHmmss"),
                ImagePath: Path.GetFullPath(imagePath),
                WidthMm: w,
                HeightMm: h,
                PrintSizeName: nom,
                Surface: surface,
                Copies: 1), machineId);

            Console.WriteLine($"        accepté à l'envoi (handle {handle}), attente du verdict…");
        }
        catch (De100Exception ex)
        {
            // refus À L'ENVOI : la machine n'a même pas pris la commande, c'est déjà une
            // réponse et elle ne coûte rien
            Console.WriteLine($"        REFUSÉ À L'ENVOI : {ex.Message}\n");
            continue;
        }

        if (!fini.Wait(TimeSpan.FromMinutes(2)))
        {
            Console.WriteLine("        pas de verdict en 2 min — on passe au suivant.\n");
            continue;
        }

        var verdict = issue!;
        Console.WriteLine($"        → {verdict.Outcome} · {verdict.Reason}\n");

        if (verdict.Outcome == De100JobOutcome.Printed)
        {
            Console.WriteLine("  ═══════════════════════════════════════════════════════");
            Console.WriteLine($"  CELUI-CI SORT : Width={w:0} Height={h:0} " +
                              $"PrintSizeName=" + (nom.Length > 0 ? $"« {nom} »" : "(vide)"));
            Console.WriteLine("  ═══════════════════════════════════════════════════════");
            Console.WriteLine("  On s'arrête ici : une feuille est en train de sortir.");
            return 0;
        }
    }

    Console.WriteLine("  Aucun essai n'est sorti. Le nom du format n'est pas en cause :");
    Console.WriteLine("  il faut regarder ailleurs (configuration de la machine, canal");
    Console.WriteLine("  absent de SA table, ou limitation du magasin).");
    return 1;
}

/// Deuxième série : on fait varier la DÉFINITION de l'image, pas son nom de format.
///
/// La première série a montré que même le format du 18×24 est refusé quand on lui envoie
/// une image au mauvais rapport — et que la machine ne consomme ni papier ni compteur sur
/// un refus. `PIF_DevGetPixelCount` dit ce qu'elle ATTEND : 2515 × 3543 px pour un
/// 210 × 297, là où Studio envoie 2480 × 3508. L'écart de 35 px est le débord de 3 mm que
/// la machine ajoute.
///
/// L'hypothèse : les canaux VARIABLES tolèrent l'à-peu-près (le 18×24 sort en 2480 × 2835),
/// les canaux FIXES comme A4 exigent la définition exacte.
int SonderLesDefinitions(De100Driver driver, char machineId, string imagePath)
{
    Section($"Essais de DÉFINITION sur la machine '{machineId}'");

    if (!File.Exists(imagePath))
    {
        Console.WriteLine($"  Image introuvable : {imagePath}");
        return 1;
    }

    var info = driver.GetPrinterInfo(machineId);
    var surface = info.Media?.Surface ?? De100Surface.Glossy;

    var (verdict, attendueW, attendueH) = driver.FormatAccepte(machineId, 210, 297);
    Console.WriteLine($"  La machine attend {attendueW} × {attendueH} px pour un 210 × 297 " +
                      $"({verdict})");
    Console.WriteLine($"  Studio envoie aujourd'hui 2480 × 3508 px\n");

    var dossier = Path.Combine(Path.GetTempPath(), "studio-essais-de100");
    Directory.CreateDirectory(dossier);

    // chaque essai : cotes annoncées, nom, et définition de l'image à fabriquer
    (string Quoi, double W, double H, string Nom, uint PxW, uint PxH)[] essais =
    [
        ("définition EXACTE attendue, nom déduit", 210, 297, "210x297", attendueW, attendueH),
        ("définition EXACTE attendue, nom A4",     210, 297, "A4",      attendueW, attendueH),
        ("définition EXACTE attendue, nom 21xL",   210, 297, "21xL",    attendueW, attendueH),
        ("TÉMOIN 18×24 à sa définition juste",     210, 240, "210x240", 2480, 2835),
    ];

    De100JobResult? issue = null;
    using var fini = new ManualResetEventSlim(false);

    driver.JobFinished += (_, resultat) => { issue = resultat; fini.Set(); };
    driver.MachineEvent += (_, evt) =>
        Console.WriteLine($"    [machine] {evt.Level} {evt.ErrorNumber} : {evt.Message}");
    driver.Subscribe(machineId);

    for (var i = 0; i < essais.Length; i++)
    {
        var (quoi, w, h, nom, pxW, pxH) = essais[i];

        // L'image est REDIMENSIONNÉE sans conserver le rapport : on veut exactement la
        // définition demandée, c'est tout l'objet de l'essai.
        //
        // GDI+ et non Magick.NET : cette sonde est en 32 bits — le SDK Fuji l'impose — et
        // le Magick.NET du projet est en x64.
        var fichier = Path.Combine(dossier, $"essai-{i + 1}-{pxW}x{pxH}.png");
        using (var source = new System.Drawing.Bitmap(Path.GetFullPath(imagePath)))
        using (var cible = new System.Drawing.Bitmap((int)pxW, (int)pxH))
        {
            cible.SetResolution(300, 300);
            using (var g = System.Drawing.Graphics.FromImage(cible))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(source, 0, 0, (int)pxW, (int)pxH);
            }
            cible.Save(fichier, System.Drawing.Imaging.ImageFormat.Png);
        }

        Console.WriteLine($"  [{i + 1}/{essais.Length}] {quoi}");
        Console.WriteLine($"        Width={w:0} Height={h:0} PrintSizeName=« {nom} » " +
                          $"image {pxW} × {pxH} px");

        issue = null;
        fini.Reset();

        try
        {
            driver.Submit(new De100PrintJob(
                JobId: $"def-{i + 1}-" + DateTime.Now.ToString("HHmmss"),
                ImagePath: fichier,
                WidthMm: w,
                HeightMm: h,
                PrintSizeName: nom,
                Surface: surface,
                Copies: 1), machineId);
        }
        catch (De100Exception ex)
        {
            Console.WriteLine($"        REFUSÉ À L'ENVOI : {ex.Message}\n");
            continue;
        }

        if (!fini.Wait(TimeSpan.FromMinutes(2)))
        {
            Console.WriteLine("        pas de verdict en 2 min — on passe au suivant.\n");
            continue;
        }

        Console.WriteLine($"        → {issue!.Outcome} · {issue.Reason}\n");

        if (issue.Outcome == De100JobOutcome.Printed)
        {
            Console.WriteLine("  ═══════════════════════════════════════════════════════");
            Console.WriteLine($"  CELUI-CI SORT : {pxW} × {pxH} px, nom « {nom} »");
            Console.WriteLine("  ═══════════════════════════════════════════════════════");
            return 0;
        }
    }

    Console.WriteLine("  Aucun essai n'est sorti.");
    return 1;
}

int SubmitTestPrint(De100Driver driver, char machineId, string imagePath)
{
    Section("Tirage d'essai");

    if (!File.Exists(imagePath))
    {
        Console.WriteLine($"  Image introuvable : {imagePath}");
        return 1;
    }

    // on reprend le papier réellement chargé : format ET finition
    var info = driver.GetPrinterInfo(machineId);
    var media = info.Media;
    var largeurRouleau = media?.PaperWidthMm ?? 152;
    var surface = media?.Surface ?? De100Surface.Glossy;

    // un 10×15 sur ce rouleau : le grand côté d'abord, comme l'attend le minilab
    double grandCote = 152, petitCote = 102;
    var printSizeName = $"{grandCote:0}x{petitCote:0}";

    Console.WriteLine($"  Machine {machineId} — rouleau {largeurRouleau} mm, finition {surface}");
    Console.WriteLine($"  Format demandé : {printSizeName} mm");

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
        WidthMm: grandCote,
        HeightMm: petitCote,
        PrintSizeName: printSizeName,
        Surface: surface,
        Copies: 1);

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
