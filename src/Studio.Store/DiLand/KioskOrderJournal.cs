using System.Text.Json;
using System.Text.Json.Serialization;

namespace Studio.Store.DiLand;

/// <summary>Où en est une commande de borne dans le magasin.</summary>
public enum KioskOrderStage
{
    /// <summary>Reçue de la borne, personne ne s'en est encore occupé.</summary>
    Waiting,

    /// <summary>Prise en charge : ouverte pour retouche, ou reprise et pas encore tirée.</summary>
    InProgress,

    /// <summary>Le tirage est sorti. Sort de la liste, entre dans l'historique.</summary>
    Printed,

    /// <summary>Retirée à la main sans être tirée chez nous (DiLand l'a faite, ou annulation).</summary>
    Dismissed,
}

/// <summary>
/// Ce que le journal retient d'une commande de borne.
///
/// Le libellé, le client et le total sont recopiés ici au moment de la prise en charge :
/// l'historique doit tenir un mois même quand DiLand a purgé la commande de sa base.
/// </summary>
public sealed class KioskOrderEntry
{
    /// <summary>Identifiant DiLand — la clé du journal.</summary>
    public long Oid { get; set; }

    public int Number { get; set; }
    public string DailyNumber { get; set; } = "";

    /// <summary>Heure du dépôt à la borne.</summary>
    public DateTime OrderedAt { get; set; }

    public string CustomerName { get; set; } = "";

    /// <summary>Le contenu en clair, ex. « 10x15 × 12 · 13x18 × 2 ».</summary>
    public string Summary { get; set; } = "";

    public decimal Total { get; set; }

    public KioskOrderStage Stage { get; set; } = KioskOrderStage.Waiting;

    /// <summary>La commande Studio née de cette commande de borne, quand elle existe.</summary>
    public Guid? StudioOrderId { get; set; }

    /// <summary>Moment de la prise en charge.</summary>
    public DateTimeOffset? StartedAt { get; set; }

    /// <summary>Moment où le tirage est sorti (ou du retrait à la main).</summary>
    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>Vrai tant que la commande doit rester dans la liste de l'opérateur.</summary>
    [JsonIgnore]
    public bool IsOpen => Stage is KioskOrderStage.Waiting or KioskOrderStage.InProgress;
}

/// <summary>
/// Le journal des commandes de bornes : ce qui reste à faire, et ce qui a été fait.
///
/// Il remplace le simple registre des reprises, qui ne disait que « déjà vue » — pas assez
/// pour ce que la boutique demande : une commande reste affichée tant que le tirage n'est
/// pas sorti, puis bascule dans un historique consultable pendant un mois.
///
/// Le journal ne s'appuie pas sur DiLand pour l'historique : DiLand finit par purger ses
/// commandes, et l'historique doit survivre à cette purge. Chaque entrée porte donc sa
/// propre copie de ce qu'il faut afficher.
///
/// Le fichier est écrit en entier à chaque changement (quelques dizaines d'entrées par
/// mois), de façon atomique : une coupure de courant ne laisse jamais un journal tronqué.
/// </summary>
public sealed class KioskOrderJournal
{
    /// <summary>Durée de conservation de l'historique, une fois la commande close.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private Dictionary<long, KioskOrderEntry>? _entries;

    public KioskOrderJournal(string path) => _path = path;

    /// <summary>Toutes les entrées connues, la plus récente d'abord.</summary>
    public IReadOnlyList<KioskOrderEntry> All() =>
        Entries().Values.OrderByDescending(e => e.OrderedAt).ToList();

    /// <summary>L'entrée d'une commande, ou null si le journal ne l'a jamais vue.</summary>
    public KioskOrderEntry? Find(long oid) =>
        Entries().TryGetValue(oid, out var entry) ? entry : null;

    /// <summary>Vrai si la commande a été close : tirée chez nous, ou retirée à la main.</summary>
    public bool IsClosed(long oid) => Find(oid) is { IsOpen: false };

    /// <summary>
    /// Les commandes closes, la plus récemment close d'abord. C'est l'historique montré
    /// dans l'onglet du même nom ; il ne remonte pas plus loin que <see cref="Retention"/>.
    /// </summary>
    public IReadOnlyList<KioskOrderEntry> History() =>
        Entries().Values
            .Where(e => !e.IsOpen)
            .OrderByDescending(e => e.ClosedAt ?? DateTimeOffset.MinValue)
            .ToList();

    /// <summary>
    /// Note ce qu'on sait d'une commande sans changer son état : son contenu et son prix
    /// peuvent encore bouger tant qu'elle est en attente, et l'historique doit en garder
    /// la dernière version connue.
    /// </summary>
    public void Describe(long oid, int number, string dailyNumber, DateTime orderedAt,
        string customerName, string summary, decimal total)
    {
        var entry = GetOrCreate(oid);
        entry.Number = number;
        entry.DailyNumber = dailyNumber;
        entry.OrderedAt = orderedAt;
        entry.CustomerName = customerName;
        entry.Summary = summary;
        entry.Total = total;
        Save();
    }

    /// <summary>
    /// Prise en charge : la commande a été ouverte pour retouche, ou reprise dans Studio.
    /// Elle reste dans la liste de l'opérateur — c'est le tirage qui l'en fera sortir.
    /// </summary>
    public void MarkInProgress(long oid, Guid? studioOrderId = null)
    {
        var entry = GetOrCreate(oid);

        // une commande déjà close ne redevient pas « en cours » : rouvrir ses photos pour
        // vérifier un tirage ne doit pas la faire réapparaître dans la liste du jour
        if (!entry.IsOpen) return;

        entry.Stage = KioskOrderStage.InProgress;
        entry.StartedAt ??= DateTimeOffset.Now;
        if (studioOrderId is not null) entry.StudioOrderId = studioOrderId;
        Save();
    }

    /// <summary>Rattache après coup la commande Studio née d'une commande de borne.</summary>
    public void AttachStudioOrder(long oid, Guid studioOrderId)
    {
        var entry = GetOrCreate(oid);
        entry.StudioOrderId = studioOrderId;
        Save();
    }

    /// <summary>Le tirage est sorti : la commande quitte la liste pour l'historique.</summary>
    public void MarkPrinted(long oid, Guid? studioOrderId = null)
    {
        var entry = GetOrCreate(oid);
        if (studioOrderId is not null) entry.StudioOrderId = studioOrderId;
        entry.Stage = KioskOrderStage.Printed;
        entry.StartedAt ??= DateTimeOffset.Now;
        entry.ClosedAt ??= DateTimeOffset.Now;
        Save();
    }

    /// <summary>
    /// Retrait à la main, sans tirage de notre côté — typiquement une commande que DiLand
    /// a imprimée. Sans cette porte de sortie, elle resterait affichée indéfiniment.
    /// </summary>
    public void Dismiss(long oid)
    {
        var entry = GetOrCreate(oid);
        entry.Stage = KioskOrderStage.Dismissed;
        entry.ClosedAt ??= DateTimeOffset.Now;
        Save();
    }

    /// <summary>Remet une commande close dans la liste, quand elle a été retirée par erreur.</summary>
    public void Reopen(long oid)
    {
        var entry = GetOrCreate(oid);
        entry.Stage = entry.StudioOrderId is null ? KioskOrderStage.Waiting : KioskOrderStage.InProgress;
        entry.ClosedAt = null;
        Save();
    }

    // — persistance —

    private KioskOrderEntry GetOrCreate(long oid)
    {
        var entries = Entries();
        if (entries.TryGetValue(oid, out var entry)) return entry;

        entry = new KioskOrderEntry { Oid = oid };
        entries[oid] = entry;
        return entry;
    }

    private Dictionary<long, KioskOrderEntry> Entries()
    {
        if (_entries is not null) return _entries;

        _entries = Read().ToDictionary(e => e.Oid);
        if (Purge()) Save();
        return _entries;
    }

    private List<KioskOrderEntry> Read()
    {
        var json = AtomicFile.ReadAllTextOrNull(_path);
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];

            var premier = document.RootElement.EnumerateArray().FirstOrDefault();

            // ancien registre : un simple tableau d'OID déjà repris. Ces commandes-là ont
            // été traitées avant que le journal existe ; on les archive plutôt que de les
            // faire remonter d'un coup dans la liste de l'opérateur.
            if (premier.ValueKind == JsonValueKind.Number)
                return document.RootElement.EnumerateArray()
                    .Select(e => new KioskOrderEntry
                    {
                        Oid = e.GetInt64(),
                        Stage = KioskOrderStage.Printed,
                        ClosedAt = DateTimeOffset.Now,
                        Summary = "reprise antérieure au journal",
                    })
                    .ToList();

            return JsonSerializer.Deserialize<List<KioskOrderEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException)
        {
            // journal illisible : on repart à vide plutôt que de bloquer la boutique. Le
            // pire risque est de revoir des commandes déjà faites — visible et corrigeable.
            return [];
        }
    }

    /// <summary>Oublie les commandes closes depuis plus d'un mois. Vrai si quelque chose a été retiré.</summary>
    private bool Purge()
    {
        if (_entries is null) return false;

        var cutoff = DateTimeOffset.Now - Retention;
        var perimes = _entries.Values
            .Where(e => !e.IsOpen && (e.ClosedAt ?? DateTimeOffset.Now) < cutoff)
            .Select(e => e.Oid)
            .ToList();

        foreach (var oid in perimes) _entries.Remove(oid);
        return perimes.Count > 0;
    }

    private void Save()
    {
        if (_entries is null) return;

        var dossier = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);

        AtomicFile.WriteAllText(_path,
            JsonSerializer.Serialize(_entries.Values.OrderBy(e => e.Oid).ToList(), JsonOptions));
    }
}
