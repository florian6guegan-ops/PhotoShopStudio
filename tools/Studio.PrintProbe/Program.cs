using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Printing;
using Studio.Printing;

// Outil de diagnostic impression :
//   list                                   — imprimantes, formats papier, résolutions
//   devmode <imprimante> <fichier.bin>     — ouvre le dialogue du pilote et sauve le DEVMODE
//   test <imprimante> <Lmm> <Hmm> [pdf]    — imprime une page de test calibrée (règle en cm)

return args switch
{
    ["list"] => ListPrinters(),
    ["addforms"] => AddShopForms(),
    ["devmode", var printer, var file] => CaptureDevMode(printer, file),
    ["test", var printer, var w, var h] => PrintTestPage(printer, double.Parse(w), double.Parse(h), null),
    ["test", var printer, var w, var h, var pdf] => PrintTestPage(printer, double.Parse(w), double.Parse(h), pdf),
    _ => Usage(),
};

static int Usage()
{
    Console.WriteLine("""
        Studio.PrintProbe — diagnostic impression
          list                                  liste les imprimantes et leurs formats
          devmode <imprimante> <fichier.bin>    capture les réglages pilote (dialogue)
          test <imprimante> <Lmm> <Hmm> [pdf]   page de test calibrée (règle cm)
        """);
    return 1;
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
