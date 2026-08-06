namespace Studio.Core.Domain;

/// <summary>Disposition d'une planche (photos d'identité) : N copies d'une même image sur un tirage.</summary>
public sealed class SheetSpec
{
    public const double DefaultGapMm = 2;

    /// <summary>
    /// Épaisseur du trait de découpe, en millimètres. C'est aussi l'écart entre deux cases
    /// d'une planche à fond perdu : le trait y tient tout entier, sans mordre sur les photos.
    /// Voir <c>ImagePipeline.DrawCutBorders</c>.
    /// </summary>
    public const double CutLineMm = 0.2;

    public int Copies { get; set; } = 6;
    public double CellWidthMm { get; set; } = 35;
    public double CellHeightMm { get; set; } = 45;
    /// <summary>Espace minimal entre cellules (les traits de coupe y sont dessinés).</summary>
    public double GapMm { get; set; } = DefaultGapMm;
    public bool CutMarks { get; set; } = true;

    /// <summary>
    /// Planche « à fond perdu » : photos JOINTIVES, séparées du seul trait de découpe, et
    /// pas de repères dans les marges — la bande basse prend leur place.
    ///
    /// C'est la planche que la boutique sort depuis le 04/08/2026, sur le modèle des tirages
    /// de borne. L'écart de 2 mm et les repères en marge dispersaient les photos au milieu
    /// du blanc ; jointives, elles se coupent d'un trait de massicot d'un bord à l'autre.
    ///
    /// L'écart réellement appliqué est <see cref="LayoutGapMm"/> : <see cref="GapMm"/> ne
    /// sert plus quand le fond perdu est actif.
    /// </summary>
    public bool FullBleed { get; set; } = true;

    /// <summary>
    /// L'écart réellement laissé entre deux cases, en millimètres.
    ///
    /// C'est LUI qu'il faut passer aux calculs de capacité, et pas <see cref="GapMm"/> :
    /// compter les places avec un écart de 2 mm puis les poser avec un écart de 0,2 mm
    /// annoncerait moins de photos que la planche n'en porte, et le client paierait un
    /// papier à moitié vide.
    /// </summary>
    public double LayoutGapMm => FullBleed ? CutLineMm : GapMm;

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

    /// <summary>
    /// Ce produit sort-il avec un LISERÉ blanc régulier — un « bord blanc » ?
    ///
    /// À ne pas confondre avec la « photo entière », qui partage le même
    /// <see cref="FitMode.Fit"/> : celle-ci ne coupe RIEN et laisse le blanc combler ce que
    /// le rapport de la photo ne remplit pas, donc des marges INÉGALES — un calage. Le bord
    /// blanc, lui, recadre la photo pour qu'elle remplisse la fenêtre, et le blanc fait la
    /// même largeur des quatre côtés.
    ///
    /// C'est <see cref="BorderMm"/> qui les distingue, et rien d'autre.
    /// </summary>
    public bool ABordBlanc => BorderMm > 0;

    /// <summary>
    /// La FENÊTRE : ce que la photo occupe réellement, liseré déduit, en millimètres.
    ///
    /// <b>C'est elle qui donne le rapport à cadrer</b>, et non le format du papier. Sur un
    /// « bord blanc 10×15 », le papier fait 102 × 152 mais la photo n'occupe que
    /// 92 × 142 : cadrer au rapport du papier fait forcément perdre une bande au tirage,
    /// puisque ce n'est pas le rectangle où la photo atterrit.
    /// </summary>
    public (double Width, double Height) FenetreMm => ABordBlanc
        ? (WidthMm - 2 * BorderMm, HeightMm - 2 * BorderMm)
        : (WidthMm, HeightMm);
    /// <summary>Fichier ICC dans catalog/icc, null = sRGB géré par le pilote.</summary>
    public string? IccProfile { get; set; }

    /// <summary>
    /// Correction d'exposition propre à ce produit, en diaphragmes (IL), appliquée au
    /// RENDU par-dessus les réglages de l'opérateur. 0 = rien.
    ///
    /// Elle existe parce qu'une machine ne rend pas ce que l'écran montre, et que l'écart
    /// est le sien : la DS620 sort plus sombre que le minilab, sur la même photo et le
    /// même fichier. Signalé par l'exploitant le 04/08/2026 sur les photos d'identité et
    /// les E-Photo — les deux produits de cette imprimante.
    ///
    /// Trois raisons d'en faire un réglage de PRODUIT plutôt qu'une constante dans le code :
    ///
    /// 1. l'écart dépend de la machine ET du papier, donc du produit ;
    /// 2. il se mesure sur un tirage réel, et se corrige au dixième de diaphragme après
    ///    l'avoir regardé — recompiler entre deux essais n'aurait pas de sens ;
    /// 3. il ne doit pas toucher l'aperçu à l'écran : l'opérateur cadre sur ce qu'il voit,
    ///    et une photo éclaircie à l'écran l'amènerait à la rassombrir à la main, ce qui
    ///    annulerait la correction.
    ///
    /// L'échelle est celle de l'exposition photographique : +1 double la lumière. Les
    /// valeurs utiles se comptent en dixièmes.
    /// </summary>
    public double PrintExposure { get; set; }
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
        PrintExposure = PrintExposure,
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
