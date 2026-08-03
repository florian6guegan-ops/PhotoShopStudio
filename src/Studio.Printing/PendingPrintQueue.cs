using Studio.Core.Domain;

namespace Studio.Printing;

/// <summary>Ce que la file d'attente vient de faire, pour l'affichage.</summary>
/// <param name="Numero">Numéro de commande, tel qu'il s'affiche.</param>
/// <param name="Enveloppe">Rang de l'enveloppe dans la commande.</param>
/// <param name="Reprise">Vrai si le tirage a repris en cours de route plutôt que de démarrer.</param>
/// <param name="Message">Ce qu'on dit à l'opérateur.</param>
public sealed record PendingPrintOutcome(string Numero, int Enveloppe, bool Reprise, string Message);

/// <summary>
/// Reprend toute seule les commandes mises en attente faute d'imprimante.
///
/// Une commande lancée pendant qu'on change le rouleau, ou coupée par un bourrage, ne doit
/// être ni perdue ni refaite en double. Elle est rangée en attente avec le rang de la
/// dernière page sortie (voir <see cref="PrintResumePoint"/>) ; cette file la relance dès
/// que la machine répond, à la page suivante.
///
/// On ne relance QUE si l'imprimante est prête : réessayer sur une machine en panne
/// remplirait la file Windows de travaux qui sortiraient tous d'un coup à la réparation,
/// exactement le « replay storm » qui fait tomber DiLand.
/// </summary>
public sealed class PendingPrintQueue(PrintOrchestrator orchestrator, Func<IEnumerable<Order>> lireCommandes)
{
    private readonly PrintOrchestrator _orchestrator =
        orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    private readonly Func<IEnumerable<Order>> _lireCommandes =
        lireCommandes ?? throw new ArgumentNullException(nameof(lireCommandes));

    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Délai minimal entre deux tentatives sur une même enveloppe.
    ///
    /// La file passe toutes les vingt secondes, mais une reprise REFAIT le rendu — vingt
    /// et une planches pour la commande du 03/08/2026. Réessayer si souvent mettrait le
    /// poste à genoux pour rien, alors qu'on attend qu'un opérateur réveille une machine.
    /// </summary>
    public TimeSpan DelaiEntreTentatives { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Une seule reprise à la fois : la boucle tourne sur un minuteur, et deux passages
    /// simultanés enverraient la même enveloppe deux fois.
    /// </summary>
    private readonly SemaphoreSlim _verrou = new(1, 1);

    /// <summary>Nombre d'enveloppes actuellement en attente.</summary>
    public int Count => _orchestrator.FindWaitingEnvelopes(_lireCommandes()).Count;

    /// <summary>
    /// Tente de reprendre les enveloppes en attente. Rend ce qui a effectivement bougé.
    ///
    /// Ne lève jamais : elle est appelée par un minuteur, et une commande qui refuse de
    /// repartir ne doit pas emporter le reste de la file.
    /// </summary>
    public async Task<IReadOnlyList<PendingPrintOutcome>> TryResumeAsync(CancellationToken ct = default)
    {
        if (!await _verrou.WaitAsync(0, ct).ConfigureAwait(false))
            return [];

        try
        {
            return await Task.Run(() => Reprendre(ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        finally
        {
            _verrou.Release();
        }
    }

    private List<PendingPrintOutcome> Reprendre(CancellationToken ct)
    {
        var faits = new List<PendingPrintOutcome>();

        foreach (var (order, envelope) in _orchestrator.FindWaitingEnvelopes(_lireCommandes()))
        {
            ct.ThrowIfCancellationRequested();

            var reprise = _orchestrator.ReadResumePoint(order, envelope);
            var deja = reprise?.PagesRemises ?? 0;

            // trop tôt pour réessayer : on laisse à l'opérateur le temps de réveiller la
            // machine, plutôt que de refaire le rendu toutes les vingt secondes
            if (reprise is not null &&
                DateTimeOffset.Now - reprise.At < DelaiEntreTentatives)
                continue;

            try
            {
                _orchestrator.PrintEnvelope(order, envelope, ct: ct);

                var message = deja > 0
                    ? $"Commande {order.DisplayNumber} reprise à la page {deja + 1} et terminée."
                    : $"Commande {order.DisplayNumber} imprimée : l'imprimante était redevenue prête.";

                Log?.Invoke(message);
                faits.Add(new PendingPrintOutcome(order.DisplayNumber, envelope.Number, deja > 0, message));
            }
            catch (PrinterNotReadyException)
            {
                // toujours pas prête : elle reste en attente, sans bruit. C'est le cas
                // NORMAL de cette boucle, pas un incident à signaler à chaque passage.
            }
            catch (PrintCanceledException)
            {
                // l'opérateur a arrêté cette commande entre-temps : elle n'est plus à nous
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Reprise de {order.DisplayNumber}/{envelope.Number} impossible : {ex.Message}");
            }
        }

        return faits;
    }
}
