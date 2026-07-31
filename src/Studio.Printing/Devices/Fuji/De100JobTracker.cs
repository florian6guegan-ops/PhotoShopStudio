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
    private sealed record Entry(string JobId, string OrderHandle, DateTimeOffset Deadline)
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

    /// <summary>Nombre de tirages encore en attente d'une issue.</summary>
    public int PendingCount
    {
        get { lock (_sync) return _byHandle.Count; }
    }

    /// <summary>Identifiants des tirages encore suivis.</summary>
    public IReadOnlyList<string> PendingJobIds
    {
        get { lock (_sync) return _byHandle.Values.Select(e => e.JobId).ToList(); }
    }

    /// <summary>Enregistre un tirage accepté par le minilab.</summary>
    public void Track(string jobId, string orderHandle, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobId);
        ArgumentException.ThrowIfNullOrEmpty(orderHandle);

        lock (_sync)
        {
            _byHandle[orderHandle] = new Entry(jobId, orderHandle, now + _timeout);
        }
    }

    /// <summary>
    /// Traite une notification du minilab. Renvoie un résultat lorsque le statut est
    /// définitif, <c>null</c> tant que le tirage progresse encore.
    /// </summary>
    public De100JobResult? Report(string orderHandle, De100OrderStatus status, DateTimeOffset now)
    {
        lock (_sync)
        {
            // un handle inconnu = notification tardive après une issue déjà rendue :
            // on l'ignore, surtout on ne recrée pas de suivi
            if (!_byHandle.TryGetValue(orderHandle, out var entry))
                return null;

            entry.LastStatus = status;

            var outcome = Classify(status);
            if (outcome is null)
            {
                // statut de progression : on vérifie seulement que l'échéance tient toujours
                return now >= entry.Deadline ? Expire(entry, status) : null;
            }

            _byHandle.Remove(orderHandle);
            return new De100JobResult(entry.JobId, orderHandle, outcome.Value, Describe(status));
        }
    }

    /// <summary>
    /// Rend leur issue aux tirages dont l'échéance est dépassée. À appeler périodiquement :
    /// un minilab muet ne produit aucun callback, et c'est précisément le cas qu'il faut couvrir.
    /// </summary>
    public IReadOnlyList<De100JobResult> SweepTimeouts(DateTimeOffset now)
    {
        lock (_sync)
        {
            var expired = _byHandle.Values.Where(e => now >= e.Deadline).ToList();
            var results = new List<De100JobResult>(expired.Count);
            foreach (var entry in expired)
            {
                _byHandle.Remove(entry.OrderHandle);
                results.Add(new De100JobResult(entry.JobId, entry.OrderHandle, De100JobOutcome.TimedOut,
                    $"Aucune réponse définitive du minilab après {_timeout.TotalMinutes:0} min " +
                    $"(dernier état connu : {Describe(entry.LastStatus)})."));
            }
            return results;
        }
    }

    /// <summary>Cesse de suivre un tirage sans produire de résultat (annulation déclenchée par nous).</summary>
    public bool Forget(string orderHandle)
    {
        lock (_sync) return _byHandle.Remove(orderHandle);
    }

    private De100JobResult Expire(Entry entry, De100OrderStatus status)
    {
        _byHandle.Remove(entry.OrderHandle);
        return new De100JobResult(entry.JobId, entry.OrderHandle, De100JobOutcome.TimedOut,
            $"Délai dépassé alors que la commande était encore « {Describe(status)} ».");
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
