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
    /// <summary>Profil ICC du média (catalog/icc) ; null = celui du produit. Le DE100 en a un par média.</summary>
    public string? IccProfile { get; set; }
}

/// <summary>
/// Un palier de tarif dégressif : à partir de <see cref="FromQuantity"/> exemplaires du
/// même produit dans la commande, le tirage est facturé <see cref="UnitPrice"/>.
/// </summary>
public sealed class PriceTier
{
    public int FromQuantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
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

    /// <summary>
    /// Paliers de tarif dégressif, du plus petit au plus grand. Vide = prix unique.
    /// Le palier à la quantité 1 doit valoir <see cref="Price"/> ; c'est ce que vérifie
    /// le catalogue à sa relecture.
    /// </summary>
    public List<PriceTier> PriceTiers { get; set; } = new();

    public string Channel => string.IsNullOrEmpty(PrinterChannel) ? PrinterName : PrinterChannel!;

    /// <summary>
    /// Prix unitaire applicable pour <paramref name="quantity"/> exemplaires : le palier
    /// le plus avantageux déjà atteint. Sans palier défini, c'est <see cref="Price"/>.
    /// </summary>
    public decimal UnitPriceFor(int quantity)
    {
        if (quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(quantity), "La quantité doit être au moins 1.");
        if (PriceTiers.Count == 0)
            return Price;

        var applicable = PriceTiers
            .Where(t => t.FromQuantity <= quantity)
            .OrderByDescending(t => t.FromQuantity)
            .FirstOrDefault();

        return applicable?.UnitPrice ?? Price;
    }
}
