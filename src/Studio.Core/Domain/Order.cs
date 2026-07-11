namespace Studio.Core.Domain;

public sealed class ImageAdjustments
{
    public bool Grayscale { get; set; }
    public double Brightness { get; set; } // -100..100, 0 = neutre
    public double Contrast { get; set; }   // -100..100, 0 = neutre

    public bool IsNeutral => !Grayscale && Brightness == 0 && Contrast == 0;
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
    public CropSpec Crop { get; set; } = CropSpec.Full;
    /// <summary>Si null, le mode par défaut du produit s'applique.</summary>
    public FitMode? FitOverride { get; set; }
    public ImageAdjustments Adjustments { get; set; } = new();
}

/// <summary>Un produit commandé (ex : 10×15 brillant) et ses photos.</summary>
public sealed class OrderLine
{
    public string ProductCode { get; set; } = "";
    /// <summary>Prix unitaire figé au moment de la commande.</summary>
    public decimal UnitPrice { get; set; }
    public List<OrderItem> Items { get; set; } = new();

    public int TotalPrints => Items.Sum(i => i.Quantity);
    public decimal Total => UnitPrice * TotalPrints;
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
    public string? Note { get; set; }
    public List<Envelope> Envelopes { get; set; } = new();

    public decimal Total => Envelopes.Sum(e => e.Total);

    /// <summary>Numéro affiché au client, ex « 11-042 » (jour du mois + compteur).</summary>
    public string DisplayNumber => $"{CreatedAt:dd}-{DailyNumber:000}";

    public string EnvelopeIdempotencyKey(Envelope envelope) => $"{Id:N}-env{envelope.Number:00}";
}
