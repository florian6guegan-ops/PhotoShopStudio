using Studio.Printing.Devices.Fuji.Bridge;

namespace Studio.Printing.Devices.Fuji;

/// <summary>
/// Envoi de tirages au minilab Fuji, vu depuis l'orchestrateur d'impression.
///
/// L'interface existe pour que le routage des enveloppes soit vérifiable sans minilab :
/// les tests fournissent une implémentation factice, la production branche
/// <see cref="De100BridgePrinter"/>.
/// </summary>
public interface IMinilabPrinter
{
    /// <summary>Machines prêtes à recevoir un tirage. Vide = aucune, l'envoi doit être refusé.</summary>
    IReadOnlyList<char> ReadyMachines();

    /// <summary>Envoie un tirage et renvoie le handle de commande attribué par le minilab.</summary>
    string Submit(De100PrintJob job, char machineId);
}

/// <summary>
/// Implémentation réelle : passe par le relais 32 bits.
///
/// Les appels sont exposés en synchrone parce que l'orchestrateur d'impression l'est,
/// et qu'il tourne déjà sur un fil de fond côté application.
/// </summary>
public sealed class De100BridgePrinter : IMinilabPrinter, IAsyncDisposable
{
    private readonly De100BridgeClient _client;
    private readonly HashSet<char> _subscribed = [];
    private readonly object _sync = new();
    private bool _connected;

    public De100BridgePrinter(De100BridgeClient? client = null) => _client = client ?? new De100BridgeClient();

    /// <summary>Journal optionnel.</summary>
    public Action<string>? Log
    {
        get => _client.Log;
        set => _client.Log = value;
    }

    /// <summary>Issue d'un tirage remontée par le minilab.</summary>
    public event EventHandler<De100JobResult>? JobFinished
    {
        add => _client.JobFinished += value;
        remove => _client.JobFinished -= value;
    }

    private void EnsureConnected()
    {
        lock (_sync)
        {
            if (_connected && _client.IsConnected) return;
            _client.ConnectAsync().GetAwaiter().GetResult();
            _connected = true;
        }
    }

    public IReadOnlyList<char> ReadyMachines()
    {
        EnsureConnected();

        var ready = new List<char>();
        foreach (var machine in _client.ListMachinesAsync().GetAwaiter().GetResult())
        {
            // une machine hors ligne se déclare parfois « prête » : on vérifie son état
            var info = _client.GetPrinterInfoAsync(machine).GetAwaiter().GetResult();
            if (info is null || info.Status is De100PrinterStatus.Offline) continue;
            ready.Add(machine);
        }
        return ready;
    }

    public string Submit(De100PrintJob job, char machineId)
    {
        EnsureConnected();

        // sans abonnement, aucun tirage ne recevrait jamais son issue
        lock (_sync)
        {
            if (_subscribed.Add(machineId))
                _client.SubscribeAsync(machineId).GetAwaiter().GetResult();
        }

        return _client.SubmitAsync(job, machineId).GetAwaiter().GetResult();
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
