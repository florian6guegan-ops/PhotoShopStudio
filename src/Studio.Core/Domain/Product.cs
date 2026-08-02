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

    /// <summary>
    /// Contour noir tracé autour de chaque photo. C'est le repère sur lequel on coupe :
    /// les traits dans les marges obligent à aligner une règle d'un bord à l'autre, un
    /// contour se suit aux ciseaux directement.
    /// </summary>
    public bool CutBorder { get; set; } = true;

    /// <summary>
    /// Date et heure du tirage, portées dans la marge de la planche. L'administration
    /// l'exige pour les photos d'identité, qui doivent être récentes.
    /// </summary>
    public bool DateStamp { get; set; } = true;
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

    /// <summary>
    /// Prix unitaire applicable : le palier le plus avantageux déjà atteint, ou
    /// <paramref name="basePrice"/> s'il n'y a pas de palier.
    ///
    /// Posé ici plutôt que dans <see cref="Product"/> parce que le choix du papier d'une
    /// planche personnalisée doit appliquer la MÊME règle sans avoir de produit sous la
    /// main — deux règles de tarif finiraient par diverger, et l'écart se verrait en caisse.
    /// </summary>
    public static decimal UnitPriceFor(IReadOnlyList<PriceTier> tiers, decimal basePrice, int quantity)
    {
        if (quantity < 1)
            throw new ArgumentOutOfRangeException(nameof(quantity), "La quantité doit être au moins 1.");
        if (tiers is null || tiers.Count == 0) return basePrice;

        var applicable = tiers
            .Where(t => t.FromQuantity <= quantity)
            .OrderByDescending(t => t.FromQuantity)
            .FirstOrDefault();

        return applicable?.UnitPrice ?? basePrice;
    }
}

public sealed class Product
{
    public string Code { get; set; } = "";
    /// <summary>Nom affiché (français), ex « Tirage 10×15 brillant ».</summary>
    public string Name { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    /// <summary>Nom exact de la file d'impression Windows. Vide si <see cref="Output"/> vaut ManualFile.</summary>
    public string PrinterName { get; set; } = "";

    /// <summary>Par où sort le produit : file Windows, fichier repris dans Photoshop, ou minilab Fuji.</summary>
    public ProductOutput Output { get; set; } = ProductOutput.Printer;

    /// <summary>
    /// Machine visée sur le minilab (« A », « B »…), quand <see cref="Output"/> vaut
    /// FujiMinilab. Vide = la première machine prête, ce qui évite de rester bloqué
    /// quand l'une des deux est hors ligne.
    /// </summary>
    public string? MinilabMachineId { get; set; }

    /// <summary>
    /// Nom du format tel que le minilab le connaît (paramètre <c>PrintSizeName</c>).
    /// Vide = on reprend <see cref="Name"/>, qui correspond déjà aux noms Fuji.
    /// </summary>
    public string? MinilabPrintSizeName { get; set; }
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
    /// Copie complète et indépendante.
    ///
    /// Le Catalogue s'en sert pour deux gestes : éditer sans salir le catalogue en mémoire
    /// si l'opérateur annule, et dupliquer. Dans les deux cas, <b>un champ oublié ici est un
    /// champ perdu</b>.
    ///
    /// Il en manquait huit quand cette copie vivait dans l'écran : <see cref="Output"/>,
    /// <see cref="MinilabMachineId"/>, <see cref="MinilabPrintSizeName"/>,
    /// <see cref="PriceTiers"/>, et quatre de <see cref="SheetSpec"/>. Modifier un tirage du
    /// minilab le transformait donc en produit imprimante — <see cref="Output"/> retombait
    /// sur son défaut — et effaçait ses paliers de tarif.
    ///
    /// Elle est posée ICI, et non dans l'écran, pour être vérifiable : un essai compare la
    /// liste des propriétés de la classe à ce que la copie restitue, et échoue dès qu'une
    /// propriété est ajoutée sans être recopiée.
    /// </summary>
    public Product Copy() => new()
    {
        Code = Code,
        Name = Name,
        WidthMm = WidthMm,
        HeightMm = HeightMm,
        PrinterName = PrinterName,
        Output = Output,
        MinilabMachineId = MinilabMachineId,
        MinilabPrintSizeName = MinilabPrintSizeName,
        PrinterChannel = PrinterChannel,
        Dpi = Dpi,
        Price = Price,
        DefaultFit = DefaultFit,
        BorderMm = BorderMm,
        IccProfile = IccProfile,
        DevmodeFile = DevmodeFile,
        Finishes = Finishes
            .Select(f => new FinishOption
            {
                Name = f.Name,
                DevmodeFile = f.DevmodeFile,
                IccProfile = f.IccProfile,
            })
            .ToList(),
        PriceTiers = PriceTiers
            .Select(t => new PriceTier { FromQuantity = t.FromQuantity, UnitPrice = t.UnitPrice })
            .ToList(),
        Sheet = Sheet is null ? null : new SheetSpec
        {
            Copies = Sheet.Copies,
            CellWidthMm = Sheet.CellWidthMm,
            CellHeightMm = Sheet.CellHeightMm,
            GapMm = Sheet.GapMm,
            CutMarks = Sheet.CutMarks,
            CutBorder = Sheet.CutBorder,
            DateStamp = Sheet.DateStamp,
        },
        Enabled = Enabled,
    };

    /// <summary>
    /// Prix unitaire applicable pour <paramref name="quantity"/> exemplaires : le palier
    /// le plus avantageux déjà atteint. Sans palier défini, c'est <see cref="Price"/>.
    /// </summary>
    public decimal UnitPriceFor(int quantity) => PriceTier.UnitPriceFor(PriceTiers, Price, quantity);
}
