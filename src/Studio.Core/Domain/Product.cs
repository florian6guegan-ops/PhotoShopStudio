namespace Studio.Core.Domain;

/// <summary>Disposition d'une planche (photos d'identité) : N copies d'une même image sur un tirage.</summary>
public sealed class SheetSpec
{
    public const double DefaultGapMm = 2;

    public int Copies { get; set; } = 6;
    public double CellWidthMm { get; set; } = 35;
    public double CellHeightMm { get; set; } = 45;
    /// <summary>Espace minimal entre cellules (les traits de coupe y sont dessinés).</summary>
    public double GapMm { get; set; } = DefaultGapMm;
    public bool CutMarks { get; set; } = true;
}

/// <summary>
/// Une finition proposée à l'opérateur (« Brillant », « Mat », « Lustré »…). Elle n'est
/// qu'un DEVMODE nommé : les réglages sont capturés dans le dialogue du pilote, où la
/// finition se choisit réellement (surlaminage DNP, type de média…). Rien n'est codé en
/// dur, ce qui marche pour la DS620 comme pour n'importe quel autre pilote.
/// </summary>
public sealed class FinishOption
{
    public string Name { get; set; } = "";
    /// <summary>Fichier DEVMODE dans catalog/.</summary>
    public string DevmodeFile { get; set; } = "";
}

public sealed class Product
{
    public string Code { get; set; } = "";
    /// <summary>Nom affiché (français), ex « Tirage 10×15 brillant ».</summary>
    public string Name { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    /// <summary>Nom exact de la file d'impression Windows.</summary>
    public string PrinterName { get; set; } = "";
    /// <summary>Canal logique (regroupe les enveloppes) ; par défaut le nom de l'imprimante.</summary>
    public string? PrinterChannel { get; set; }
    public int Dpi { get; set; } = 300;
    public decimal Price { get; set; }
    public FitMode DefaultFit { get; set; } = FitMode.Fill;
    /// <summary>Marge blanche imposée (mode Fit), en mm.</summary>
    public double BorderMm { get; set; }
    /// <summary>Fichier ICC dans catalog/icc, null = sRGB géré par le pilote.</summary>
    public string? IccProfile { get; set; }
    /// <summary>Fichier DEVMODE capturé dans catalog/, null = réglages par défaut du pilote.</summary>
    public string? DevmodeFile { get; set; }
    /// <summary>Finitions proposées à l'impression ; vide = pas de choix, on prend DevmodeFile.</summary>
    public List<FinishOption> Finishes { get; set; } = new();
    /// <summary>Non null pour les produits « planche » (identité).</summary>
    public SheetSpec? Sheet { get; set; }
    public bool Enabled { get; set; } = true;

    public string Channel => string.IsNullOrEmpty(PrinterChannel) ? PrinterName : PrinterChannel!;
}
