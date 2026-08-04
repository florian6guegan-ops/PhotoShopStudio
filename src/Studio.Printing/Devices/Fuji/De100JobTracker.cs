namespace Studio.Printing.Devices.Fuji;

/// <summary>Issue d'un tirage suivi par <see cref="De100JobTracker"/>.</summary>
public enum De100JobOutcome
{
    /// <summary>Le minilab a confirmé le tirage.</summary>
    Printed,
    /// <summary>Le minilab a signalé une erreur sur la commande.</summary>
    Failed,
    /// <summary>La commande a été annulée (à la machine ou par nous).</summary>
    Canceled,
    /// <summary>Aucune issue définitive dans le délai imparti — la commande est abandonnée côté Studio.</summary>
    TimedOut,
}

/// <summary>Un tirage terminé, avec de quoi expliquer à l'opérateur ce qui s'est passé.</summary>
public sealed record De100JobResult(string JobId, string OrderHandle, De100JobOutcome Outcome, string Reason);

/// <summary>
/// Suit les tirages envoyés au DE100 jusqu'à une issue définitive.
///
/// C'est ici que se trouve la correction du défaut du driver de DiLand : celui-ci ne
/// réagissait qu'aux statuts <see cref="De100OrderStatus.Complete"/> et
/// <see cref="De100OrderStatus.Canceled"/>. Une commande finissant en
/// <see cref="De100OrderStatus.Error"/>, <see cref="De100OrderStatus.Hold"/> ou
/// <see cref="De100OrderStatus.Busy"/> n'était jamais retirée de sa liste et n'était
/// jamais marquée imprimée : la couche supérieure la considérait indéfiniment en attente
/// et la renvoyait sans fin (le « replay storm » observé en juin 2026 — plus de 300
/// callbacks Busy/Error dans les journaux de la borne).
///
/// Ici, les neuf statuts sont classés, et une échéance absolue prise à la soumission
/// garantit qu'aucun tirage ne reste suivi pour toujours. Un tirage qui échoue n'est
/// jamais resoumis automatiquement : c'est l'opérateur qui tranche, conformément au
/// principe anti-doublon déjà appliqué par <see cref="PrintOrchestrator"/>.
///
/// La classe est sûre vis-à-vis des accès concurrents : les callbacks du SDK arrivent
/// sur un fil à lui.
/// </summary>
public sealed class De100JobTracker
{
    /// <summary>
    /// Une commande DE100 et les tirages qu'elle porte.
    ///
    /// <b>Plusieurs tirages sous un seul handle</b> depuis le 04/08/2026 : une enveloppe
    /// forme désormais UNE commande minilab (<c>PIF_StartOrder</c> une fois,
    /// <c>PIF_Print</c> par photo), comme le fait le pilote de DiLand. Le minilab notifie
    /// par COMMANDE : un seul callback vaut donc pour toutes ses photos.
    /// </summary>
    private sealed record Entry(IReadOnlyList<string> JobIds, string OrderHandle, DateTimeOffset Deadline)
    {
        public De100OrderStatus LastStatus { get; set; } = De100OrderStatus.PrintWaiting;
    }

    private readonly Dictionary<string, Entry> _byHandle = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly TimeSpan _timeout;

    /// <param name="timeout">
    /// Délai au-delà duquel un tirage sans issue est abandonné. Compté depuis la
    /// soumission, volontairement : un minilab qui répète « Busy » ne doit pas pouvoir
    /// repousser l'échéance indéfiniment.
    /// </param>
    public De100JobTracker(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Le délai doit être strictement positif.");
        _timeout = timeout;
    }

    /// <summary>
    /// Nombre de TIRAGES encore en attente d'une issue — pas de commandes. C'est ce que
    /// l'opérateur compte : une commande de six photos qui patiente, ce sont six photos
    /// qui ne sont pas sorties.
    /// </summary>
    public int PendingCount
    {
        get { lock (_sync) return _byHandle.Values.Sum(e => e.JobIds.Count); }
    }

    /// <summary>Identifiants des tirages encore suivis.</summary>
    public IReadOnlyList<string> PendingJobIds
    {
        get { lock (_sync) return _byHandle.Values.SelectMany(e => e.JobIds).ToList(); }
    }

    /// <summary>Enregistre un tirage accepté par le minilab.</summary>
    public void Track(string jobId, string orderHandle, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        Track([jobId], orderHandle, now);
    }

    /// <summary>
    /// Enregistre une commande minilab et tous les tirages qu'elle porte.
    ///
    /// L'échéance est prise à la SOUMISSION et vaut pour la commande entière : un minilab
    /// qui répète « Busy » ne doit pas pouvoir la repousser photo par photo.
    /// </summary>
    public void Track(IReadOnlyList<string> jobIds, string orderHandle, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(jobIds);
        ArgumentException.ThrowIfNullOrEmpty(orderHandle);
        if (jobIds.Count == 0)
            throw new ArgumentException("Une commande minilab porte au moins un tirage.", nameof(jobIds));

        lock (_sync)
        {
            _byHandle[orderHandle] = new Entry([.. jobIds], orderHandle, now + _timeout);
        }
    }

    /// <summary>
    /// Traite une notification du minilab. Renvoie une issue PAR TIRAGE de la commande
    /// lorsque le statut est définitif, une liste vide tant qu'elle progresse encore.
    ///
    /// Le minilab notifie par COMMANDE, et une commande porte maintenant toutes les photos
    /// d'une enveloppe : un callback vaut donc pour toutes. Rendre une seule issue en
    /// laisserait cinq sur six sans verdict, et le compte des photos restantes ne
    /// descendrait jamais.
    /// </summary>
    /// <param name="motif">
    /// Ce que la MACHINE dit du refus (<c>ST_PRINT_INFO.errmsg</c>), quand elle le dit.
    /// Vide le reste du temps. Il complète le libellé du statut au lieu de le remplacer :
    /// « erreur signalée par le minilab » situe le moment, le motif dit la cause.
    /// </param>
    public IReadOnlyList<De100JobResult> Report(string orderHandle, De100OrderStatus status,
        DateTimeOffset now, string motif = "")
    {
        lock (_sync)
        {
            // un handle inconnu = notification tardive après une issue déjà rendue :
            // on l'ignore, surtout on ne recrée pas de suivi
            if (!_byHandle.TryGetValue(orderHandle, out var entry))
                return [];

            entry.LastStatus = status;

            var outcome = Classify(status);
            if (outcome is null)
            {
                // statut de progression : on vérifie seulement que l'échéance tient toujours
                return now >= entry.Deadline ? Expire(entry, status) : [];
            }

            _byHandle.Remove(orderHandle);
            var raison = Raison(status, motif);
            return [.. entry.JobIds.Select(jobId =>
                new De100JobResult(jobId, orderHandle, outcome.Value, raison))];
        }
    }

    /// <summary>Le statut, et ce que la machine en dit quand elle en dit quelque chose.</summary>
    internal static string Raison(De100OrderStatus status, string motif) =>
        string.IsNullOrWhiteSpace(motif)
            ? Describe(status)
            : $"{Describe(status)} — {motif.Trim()}";

    /// <summary>
    /// Rend leur issue aux tirages dont l'échéance est dépassée. À appeler périodiquement :
    /// un minilab muet ne produit aucun callback, et c'est précisément le cas qu'il faut couvrir.
    /// </summary>
    public IReadOnlyList<De100JobResult> SweepTimeouts(DateTimeOffset now)
    {
        lock (_sync)
        {
            var expired = _byHandle.Values.Where(e => now >= e.Deadline).ToList();
            var results = new List<De100JobResult>();
            foreach (var entry in expired)
            {
                _byHandle.Remove(entry.OrderHandle);
                results.AddRange(entry.JobIds.Select(jobId =>
                    new De100JobResult(jobId, entry.OrderHandle, De100JobOutcome.TimedOut,
                        $"Aucune réponse définitive du minilab après {_timeout.TotalMinutes:0} min " +
                        $"(dernier état connu : {Describe(entry.LastStatus)}).")));
            }
            return results;
        }
    }

    /// <summary>Cesse de suivre un tirage sans produire de résultat (annulation déclenchée par nous).</summary>
    public bool Forget(string orderHandle)
    {
        lock (_sync) return _byHandle.Remove(orderHandle);
    }

    private IReadOnlyList<De100JobResult> Expire(Entry entry, De100OrderStatus status)
    {
        _byHandle.Remove(entry.OrderHandle);
        return [.. entry.JobIds.Select(jobId =>
            new De100JobResult(jobId, entry.OrderHandle, De100JobOutcome.TimedOut,
                $"Délai dépassé alors que la commande était encore « {Describe(status)} »."))];
    }

    /// <summary>
    /// Classe un statut. <c>null</c> = le tirage progresse encore.
    /// <see cref="De100OrderStatus.Busy"/> et <see cref="De100OrderStatus.Hold"/> ne sont
    /// pas définitifs (la commande peut repartir), mais l'échéance finira par les trancher.
    /// </summary>
    private static De100JobOutcome? Classify(De100OrderStatus status) => status switch
    {
        De100OrderStatus.Complete => De100JobOutcome.Printed,
        De100OrderStatus.Error => De100JobOutcome.Failed,
        De100OrderStatus.Canceled => De100JobOutcome.Canceled,
        De100OrderStatus.PrintWaiting or De100OrderStatus.Printing
            or De100OrderStatus.ImageProcessWaiting or De100OrderStatus.ImageProcessing
            or De100OrderStatus.Hold or De100OrderStatus.Busy => null,
        _ => De100JobOutcome.Failed, // statut inconnu : on tranche plutôt que de boucler
    };

    /// <summary>Libellé destiné à l'opérateur.</summary>
    public static string Describe(De100OrderStatus status) => status switch
    {
        De100OrderStatus.PrintWaiting => "en attente de tirage",
        De100OrderStatus.Printing => "en cours de tirage",
        De100OrderStatus.Complete => "tirage terminé",
        De100OrderStatus.Error => "erreur signalée par le minilab",
        De100OrderStatus.ImageProcessWaiting => "en attente de traitement d'image",
        De100OrderStatus.ImageProcessing => "traitement d'image en cours",
        De100OrderStatus.Hold => "suspendue à la machine",
        De100OrderStatus.Canceled => "annulée",
        De100OrderStatus.Busy => "refusée, minilab occupé",
        _ => $"état inconnu ({(int)status})",
    };
}
