namespace Studio.Core.Domain;

/// <summary>
/// Réglages de correction d'une photo, dans l'esprit de Lightroom.
///
/// DiLand ne propose que luminosité et contraste ; c'est trop grossier pour rattraper un
/// contre-jour ou un intérieur jaune. On garde les mêmes noms que Lightroom pour que les
/// réglages soient lus de la même façon par quelqu'un qui le connaît.
///
/// Toutes les valeurs sont neutres à 0, sauf l'exposition qui est en diaphragmes (IL).
/// </summary>
public sealed class ImageAdjustments
{
    public bool Grayscale { get; set; }

    /// <summary>
    /// Remplace le fond par du blanc franc — photos d'identité.
    ///
    /// Le fond de studio grisonne toujours un peu et porte l'ombre du sujet, là où les
    /// normes veulent un fond clair et uni. Voir <c>BackgroundRemoval</c> : seul est
    /// effacé ce qui ressemble au fond ET communique avec le bord de l'image, de sorte
    /// qu'une chemise claire ou une mèche ne partent jamais avec.
    /// </summary>
    public bool WhiteBackground { get; set; }

    /// <summary>
    /// Trois corrections automatiques, reprises telles quelles de DiLand — où ce sont
    /// aussi des bascules, et non des actions qu'on subit (<c>AutoLevels</c>,
    /// <c>AutoContrast</c>, <c>AutoColor</c>, chacune avec son « annuler »).
    ///
    /// Elles s'appliquent AVANT les réglages fins : l'opérateur redresse d'un clic, puis
    /// affine s'il le juge utile. Dans l'autre ordre, l'automatique effacerait son travail.
    /// </summary>
    /// <summary>Étire chaque canal sur toute la plage : le classique « niveaux ».</summary>
    public bool AutoLevels { get; set; }

    /// <summary>Étire le contraste sans toucher à l'équilibre des couleurs.</summary>
    public bool AutoContrast { get; set; }

    /// <summary>Neutralise une dominante de couleur (intérieur jaune, ombre bleue).</summary>
    public bool AutoColor { get; set; }

    /// <summary>Exposition en diaphragmes : +1 double la lumière, −1 la divise par deux.</summary>
    public double Exposure { get; set; }

    /// <summary>Luminosité globale, −100..100. Conservée pour les commandes déjà enregistrées.</summary>
    public double Brightness { get; set; }

    public double Contrast { get; set; }

    /// <summary>Hautes lumières, −100..100 : négatif récupère un ciel brûlé.</summary>
    public double Highlights { get; set; }

    /// <summary>Ombres, −100..100 : positif déniche un visage en contre-jour.</summary>
    public double Shadows { get; set; }

    /// <summary>Blancs, −100..100 : déplace le point blanc.</summary>
    public double Whites { get; set; }

    /// <summary>Noirs, −100..100 : déplace le point noir.</summary>
    public double Blacks { get; set; }

    /// <summary>Température, −100 (froid, bleu) .. 100 (chaud, orangé).</summary>
    public double Temperature { get; set; }

    /// <summary>Teinte, −100 (vert) .. 100 (magenta).</summary>
    public double Tint { get; set; }

    /// <summary>Saturation, −100..100 : agit sur toutes les couleurs.</summary>
    public double Saturation { get; set; }

    /// <summary>
    /// Vibrance, −100..100 : n'intensifie que les couleurs déjà ternes, et épargne donc
    /// les teints de peau — c'est ce qu'on veut sur un portrait.
    /// </summary>
    public double Vibrance { get; set; }

    /// <summary>Clarté, −100..100 : contraste local, donne du relief sans toucher aux couleurs.</summary>
    public double Clarity { get; set; }

    /// <summary>Netteté, 0..100.</summary>
    public double Sharpness { get; set; }

    public bool IsNeutral =>
        !Grayscale && !WhiteBackground && !AutoLevels && !AutoContrast && !AutoColor &&
        Exposure == 0 && Brightness == 0 && Contrast == 0 &&
        Highlights == 0 && Shadows == 0 && Whites == 0 && Blacks == 0 &&
        Temperature == 0 && Tint == 0 && Saturation == 0 && Vibrance == 0 &&
        Clarity == 0 && Sharpness == 0;

    public ImageAdjustments Clone() => (ImageAdjustments)MemberwiseClone();
}

/// <summary>Une photo dans une ligne de commande, avec son recadrage et ses réglages.</summary>
public sealed class OrderItem
{
    /// <summary>Nom du fichier dans le dossier photos/ de la commande (les originaux sont toujours copiés).</summary>
    public string FileName { get; set; } = "";
    public string OriginalName { get; set; } = "";
    public int Quantity { get; set; } = 1;
    /// <summary>Rotation utilisateur en quarts de tour horaires (0..3), après orientation EXIF.</summary>
    public int RotationQuarterTurns { get; set; }
    /// <summary>Redressement fin en degrés (« Tilt » de DiLand), appliqué après les quarts de tour.</summary>
    public double FineRotationDegrees { get; set; }
    public CropSpec Crop { get; set; } = CropSpec.Full;
    /// <summary>Si null, le mode par défaut du produit s'applique.</summary>
    public FitMode? FitOverride { get; set; }
    /// <summary>
    /// Contour noir sur le bord de la photo, à suivre aux ciseaux. Sans objet en « remplir le
    /// format », où la photo occupe tout le tirage et où il n'y a rien à recouper.
    /// </summary>
    public bool CutBorder { get; set; }
    /// <summary>Planches identité : nombre de photos sur la planche. Si null, celui du produit s'applique.</summary>
    public int? SheetCopiesOverride { get; set; }

    /// <summary>
    /// Planches identité : taille d'une case, en millimètres. Null = celle du produit.
    ///
    /// C'est le DOCUMENT visé qui la fixe, et non le papier : un passeport espagnol fait
    /// 26 × 32 mm là où le français fait 35 × 45. Le produit ne porte qu'une seule cellule,
    /// et l'écran d'identité en propose 274 — sans cette valeur, toutes les planches
    /// sortaient au format français, quel que soit le document choisi à l'écran précédent.
    /// </summary>
    public double? SheetCellWidthMm { get; set; }

    /// <summary>Hauteur de la case ; voir <see cref="SheetCellWidthMm"/>.</summary>
    public double? SheetCellHeightMm { get; set; }
    /// <summary>Nom de la finition choisie (voir Product.Finishes) ; null = DEVMODE par défaut du produit.</summary>
    public string? Finish { get; set; }
    public ImageAdjustments Adjustments { get; set; } = new();
}

/// <summary>Un produit commandé (ex : 10×15 brillant) et ses photos.</summary>
public sealed class OrderLine
{
    public string ProductCode { get; set; } = "";
    /// <summary>Prix unitaire figé au moment de la commande.</summary>
    public decimal UnitPrice { get; set; }
    public List<OrderItem> Items { get; set; } = new();

    /// <summary>
    /// Taille demandée par l'opérateur pour le format « personnalisé », en millimètres.
    ///
    /// Non nulle = la ligne est une PLANCHE : ses <see cref="Items"/> ne sont plus des
    /// tirages pleine page mais des cases à caser côte à côte, et
    /// <see cref="ProductCode"/> désigne le PAPIER que le logiciel a retenu pour les
    /// contenir. Tout le circuit d'impression continue donc de voir un produit ordinaire du
    /// catalogue — c'est ce qui permet au minilab de fonctionner sans rien savoir de cette
    /// nouveauté.
    /// </summary>
    public double? CustomCellWidthMm { get; set; }

    /// <summary>Hauteur de la case ; voir <see cref="CustomCellWidthMm"/>.</summary>
    public double? CustomCellHeightMm { get; set; }

    /// <summary>Vrai quand la ligne est une planche à taille choisie.</summary>
    public bool IsCustomSheet => CustomCellWidthMm is > 0 && CustomCellHeightMm is > 0;

    /// <summary>Nombre de cases demandées, planches personnalisées comprises.</summary>
    public int TotalPrints => Items.Sum(i => i.Quantity);

    /// <summary>
    /// Ce qui est facturé.
    ///
    /// Une planche personnalisée est facturée AU PAPIER : une planche 13×18 coûte un tirage
    /// 13×18, quel que soit le nombre de photos qu'on y a casées. Le nombre de planches est
    /// posé dans <see cref="SheetCount"/> au moment où le logiciel choisit le papier — il ne
    /// se recalcule pas ici, sous peine de changer le prix d'une commande déjà encaissée si
    /// le catalogue bouge.
    /// </summary>
    public decimal Total => UnitPrice * (IsCustomSheet ? Math.Max(1, SheetCount) : TotalPrints);

    /// <summary>Nombre de planches à tirer ; n'a de sens que pour une ligne personnalisée.</summary>
    public int SheetCount { get; set; }
}

/// <summary>
/// Une enveloppe = tout ce qui part sur un même canal d'impression.
/// C'est l'unité d'impression et d'idempotence : une enveloppe passée à
/// Spooled n'est jamais resoumise automatiquement.
/// </summary>
public sealed class Envelope
{
    public int Number { get; set; } // 1..n dans la commande
    public string PrinterChannel { get; set; } = "";
    public EnvelopeStatus Status { get; set; } = EnvelopeStatus.Pending;
    public List<OrderLine> Lines { get; set; } = new();
    public string? Error { get; set; }

    public decimal Total => Lines.Sum(l => l.Total);
}

public sealed class Order
{
    /// <summary>Généré par le créateur (borne, téléphone, opérateur) : c'est la clé d'idempotence réseau.</summary>
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Numéro du jour (1..n), attribué par le poste opérateur à la soumission.</summary>
    public int DailyNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    /// <summary>Operateur | Borne1 | Borne2 | Telephone | Support</summary>
    public string Source { get; set; } = "Operateur";
    public OrderStatus Status { get; set; } = OrderStatus.Draft;
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }

    /// <summary>
    /// Adresse à prévenir dès que la commande est SORTIE, ou null.
    ///
    /// Saisie avant l'impression, à l'écran des tirages : c'est le moment où le client est
    /// encore là. Le message part tout seul quand la machine a fini — l'opérateur n'a rien
    /// à surveiller, et c'est tout l'intérêt.
    /// </summary>
    public string? CustomerEmail { get; set; }

    /// <summary>
    /// Vrai une fois le client prévenu. Empêche un second message si la commande est
    /// réimprimée : elle était déjà annoncée, et deux courriels pour la même commande font
    /// douter le client de ce qu'il doit venir chercher.
    /// </summary>
    public bool CustomerNotified { get; set; }
    public string? Note { get; set; }
    public List<Envelope> Envelopes { get; set; } = new();

    public decimal Total => Envelopes.Sum(e => e.Total);

    /// <summary>Numéro affiché au client, ex « 11-042 » (jour du mois + compteur).</summary>
    public string DisplayNumber => $"{CreatedAt:dd}-{DailyNumber:000}";

    public string EnvelopeIdempotencyKey(Envelope envelope) => $"{Id:N}-env{envelope.Number:00}";
}
