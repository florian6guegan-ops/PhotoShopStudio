using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using System.Globalization;
using Studio.Printing;

// Outil de diagnostic impression :
//   list                                   — imprimantes, formats papier, résolutions
//   devmode <imprimante> <fichier.bin>     — ouvre le dialogue du pilote et sauve le DEVMODE
//   test <imprimante> <Lmm> <Hmm> [pdf]    — imprime une page de test calibrée (règle en cm)

return args switch
{
    ["list"] => ListPrinters(),
    ["addforms"] => AddShopForms(),
    ["dnp"] => EtatDnp(),
    ["papier", var printer, var w, var h] => CheckPaper(printer, ParseMm(w), ParseMm(h)),
    ["devmode", var printer, var file] => CaptureDevMode(printer, file),
    ["test", var printer, var w, var h] => PrintTestPage(printer, ParseMm(w), ParseMm(h), null),
    ["test", var printer, var w, var h, var pdf] => PrintTestPage(printer, ParseMm(w), ParseMm(h), pdf),
    ["image", var printer, var file, var w, var h] => PrintImage(printer, file, ParseMm(w), ParseMm(h), null),
    ["image", var printer, var file, var w, var h, var dm] => PrintImage(printer, file, ParseMm(w), ParseMm(h), dm),
    _ => Usage(),
};

static double ParseMm(string value) =>
    double.Parse(value.Replace(',', '.'), CultureInfo.InvariantCulture);

static int Usage()
{
    Console.WriteLine("""
        Studio.PrintProbe — diagnostic impression
          list                                  liste les imprimantes et leurs formats
          dnp                                   état des DNP vu par le spouleur Windows
          papier <imprimante> <Lmm> <Hmm>       ce format passera-t-il ? (n'imprime rien)
          devmode <imprimante> <fichier.bin>    capture les réglages pilote (dialogue)
          test <imprimante> <Lmm> <Hmm> [pdf]   page de test calibrée (règle cm)
        """);
    return 1;
}

/// <summary>
/// Ce que le SPOULEUR dit des DNP — la seule source qui reste vraie quand DiLand tient le
/// port USB, c'est-à-dire presque toujours en boutique.
///
/// À lancer machine allumée puis pendant un tirage : c'est le contrôle qui a manqué quand
/// l'écran d'état annonçait « en veille » en continu (04/08/2026).
/// </summary>
static int EtatDnp()
{
    Studio.Printing.Devices.Dnp.DnpSpouleur.Log = Console.WriteLine;

    var vues = Studio.Printing.Devices.Dnp.DiLandPresence.VuesParWindows();
    if (vues.Count == 0)
    {
        Console.WriteLine("Aucune file DNP dans le spouleur Windows.");
        return 1;
    }

    Console.WriteLine($"DiLand tourne : {Studio.Printing.Devices.Dnp.DiLandPresence.IsRunning()}");
    Console.WriteLine();

    foreach (var dnp in vues)
    {
        var file = dnp.Spouleur!;
        Console.WriteLine($"  {file.Nom}");
        Console.WriteLine($"    état            {file.Etat}");
        Console.WriteLine($"    libellé         {Studio.Printing.Devices.Dnp.DnpSpouleur.Decrire(file)}");
        Console.WriteLine($"    photos restantes {file.PhotosRestantes}");
        Console.WriteLine($"    travaux en file  {file.TravauxEnAttente}");
        if (file.Message.Length > 0) Console.WriteLine($"    message         {file.Message}");
        Console.WriteLine();
    }

    return 0;
}

static int AddShopForms()
{
    var anyDenied = false;
    foreach (var (name, w, h) in PaperForms.ShopForms)
    {
        var ok = PaperForms.EnsureForm("Microsoft Print to PDF", name, w, h);
        Console.WriteLine($"  {name} ({w}×{h} mm) : {(ok ? "OK" : "REFUSÉ (lancer en administrateur)")}");
        anyDenied |= !ok;
    }
    return anyDenied ? 1 : 0;
}

static int ListPrinters()
{
    foreach (string name in PrinterSettings.InstalledPrinters)
    {
        var settings = new PrinterSettings { PrinterName = name };
        Console.WriteLine($"■ {name}{(settings.IsDefaultPrinter ? "  (par défaut)" : "")}");
        if (!settings.IsValid)
        {
            Console.WriteLine("   (invalide / hors ligne)");
            continue;
        }
        Console.WriteLine($"   Couleur: {settings.SupportsColor}, Recto-verso: {settings.CanDuplex}");
        foreach (PaperSize p in settings.PaperSizes)
        {
            var wMm = p.Width * 25.4 / 100;
            var hMm = p.Height * 25.4 / 100;
            Console.WriteLine($"   Papier: {p.PaperName,-30} {wMm,6:0.0} × {hMm,6:0.0} mm ({p.Kind})");
        }
        Console.WriteLine();
    }
    return 0;
}

/// <summary>
/// Dit si un format sortira de cette imprimante, sans gâcher une feuille.
///
/// C'est le contrôle qui manquait : une DS620 à qui on demande un format qu'elle ne
/// déclare pas accepte le travail et ne sort rien. On peut désormais le vérifier avant
/// d'enregistrer un produit au catalogue.
/// </summary>
static int CheckPaper(string printer, double widthMm, double heightMm)
{
    Console.WriteLine($"Format demandé : {widthMm:0.#} × {heightMm:0.#} mm sur « {printer} »");
    try
    {
        BitmapPrinter.EnsurePageSizeAvailable(printer, widthMm, heightMm);
        Console.WriteLine("  ✓ le pilote a une forme pour ce format — le tirage sortira.");
        return 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.WriteLine("  ✗ " + ex.Message);
        return 1;
    }
}

static int CaptureDevMode(string printer, string file)
{
    Console.WriteLine($"Ouverture du dialogue du pilote pour « {printer} »…");
    var bytes = DevMode.ShowDriverDialog(printer);
    if (bytes is null)
    {
        Console.WriteLine("Annulé — rien n'a été sauvegardé.");
        return 1;
    }
    File.WriteAllBytes(file, bytes);
    Console.WriteLine($"DEVMODE sauvegardé : {file} ({bytes.Length} octets)");
    return 0;
}

/// <summary>
/// Imprime une image déjà rendue, à ses dimensions exactes. Sert à contrôler sur la
/// machine ce qu'on a d'abord contrôlé à l'écran — une planche identité, par exemple —
/// sans avoir à créer une commande.
/// </summary>
static int PrintImage(string printer, string file, double widthMm, double heightMm, string? devModeFile)
{
    if (!File.Exists(file))
    {
        Console.WriteLine($"Fichier introuvable : {file}");
        return 1;
    }

    using var bitmap = new Bitmap(file);

    var devMode = devModeFile is not null && File.Exists(devModeFile)
        ? File.ReadAllBytes(devModeFile)
        : null;

    Console.WriteLine($"Image  : {file} ({bitmap.Width}×{bitmap.Height} px)");
    Console.WriteLine($"Tirage : {widthMm}×{heightMm} mm sur « {printer} »"
        + (devMode is null ? " (réglages par défaut du pilote)" : $" (DEVMODE {devMode.Length} octets)"));

    BitmapPrinter.Print(printer, bitmap, widthMm, heightMm, devModeBytes: devMode,
        documentName: "Studio Photo — controle planche");

    Console.WriteLine("Envoyé au spouleur.");
    return 0;
}

static int PrintTestPage(string printer, double widthMm, double heightMm, string? pdfPath)
{
    const int dpi = 300;
    var wPx = (int)Math.Round(widthMm / 25.4 * dpi);
    var hPx = (int)Math.Round(heightMm / 25.4 * dpi);

    using var bitmap = new Bitmap(wPx, hPx);
    bitmap.SetResolution(dpi, dpi);
    using (var g = Graphics.FromImage(bitmap))
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.White);

        using var thin = new Pen(Color.Black, 2);
        using var font = new Font("Arial", 24, GraphicsUnit.Pixel);

        // cadre au bord exact de la page : permet de vérifier le sans-marges
        g.DrawRectangle(thin, 0, 0, wPx - 1, hPx - 1);
        // diagonales : détecte les étirements
        g.DrawLine(thin, 0, 0, wPx - 1, hPx - 1);
        g.DrawLine(thin, wPx - 1, 0, 0, hPx - 1);

        // règle en centimètres sur les bords haut et gauche : à vérifier à la règle physique
        var pxPerCm = dpi / 2.54;
        for (var cm = 1; cm * pxPerCm < wPx; cm++)
        {
            var x = (int)Math.Round(cm * pxPerCm);
            var len = cm % 5 == 0 ? 60 : 35;
            g.DrawLine(thin, x, 0, x, len);
            if (cm % 5 == 0) g.DrawString(cm.ToString(), font, Brushes.Black, x + 4, len - 28);
        }
        for (var cm = 1; cm * pxPerCm < hPx; cm++)
        {
            var y = (int)Math.Round(cm * pxPerCm);
            var len = cm % 5 == 0 ? 60 : 35;
            g.DrawLine(thin, 0, y, len, y);
            if (cm % 5 == 0) g.DrawString(cm.ToString(), font, Brushes.Black, len + 4, y - 12);
        }

        // bandes de couleur : détecte une double correction couleur (teintes fausses)
        var swatches = new[] { Color.Red, Color.Green, Color.Blue, Color.Cyan, Color.Magenta, Color.Yellow, Color.Gray };
        var swatchW = wPx / 2 / swatches.Length;
        for (var i = 0; i < swatches.Length; i++)
        {
            using var brush = new SolidBrush(swatches[i]);
            g.FillRectangle(brush, wPx / 4 + i * swatchW, hPx / 2 - 100, swatchW, 200);
        }

        g.DrawString($"{printer} — {widthMm}×{heightMm} mm @ {dpi} dpi — {DateTime.Now:dd/MM/yyyy HH:mm}",
            font, Brushes.Black, 80, hPx - 120);
    }

    Console.WriteLine($"Impression de la page de test {widthMm}×{heightMm} mm sur « {printer} »…");
    BitmapPrinter.Print(printer, bitmap, widthMm, heightMm, printToFilePath: pdfPath,
        documentName: "PrintProbe page de test");
    Console.WriteLine(pdfPath is null ? "Envoyé au spouleur." : $"PDF écrit : {pdfPath}");
    return 0;
}
