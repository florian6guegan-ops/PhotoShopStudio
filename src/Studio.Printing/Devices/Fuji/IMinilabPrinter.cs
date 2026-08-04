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

    /// <summary>
    /// Finition du papier réellement chargé dans la machine. Le tirage doit la déclarer :
    /// annoncer « brillant » sur du lustré donne un rendu faux.
    /// </summary>
    De100Surface LoadedSurface(char machineId);

    /// <summary>
    /// Largeur du rouleau réellement chargé, en millimètres. C'est elle qui décide des
    /// formats tirables : demander un 15×20 sur un rouleau de 10 cm ne sort pas un 15×20,
    /// la machine avertit et gâche du papier. 0 = largeur inconnue, on ne bloque alors rien.
    /// </summary>
    int LoadedPaperWidthMm(char machineId);

    /// <summary>
    /// Envoie TOUS les tirages d'une enveloppe et renvoie le handle de la commande
    /// attribué par le minilab.
    ///
    /// Une enveloppe = UNE commande DE100, parce que c'est ce qu'attend le SDK
    /// (<c>PIF_Print</c> prend le handle en paramètre, <c>PIF_GetPrintInfo</c> relit par
    /// indice) et ce que fait le pilote de DiLand. Envoyer photo par photo faisait perdre
    /// des tirages sans un mot — voir <c>De100Driver.Submit</c>.
    ///
    /// La commande part entière ou pas du tout : un refus en cours de route l'annule.
    /// </summary>
    string Submit(IReadOnlyList<De100PrintJob> jobs, char machineId);

    /// <summary>
    /// Rappelle une commande déjà transmise, tant que la machine ne l'a pas tirée.
    ///
    /// DiLand n'a pas cet appel : chez lui, une commande partie ne se reprend qu'en
    /// allant vider la file SUR le minilab. Le SDK sait pourtant le faire.
    /// </summary>
    void Cancel(string orderHandle);
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

            // Le relais est neuf : ce qu'on lui avait demandé ne tient plus. Sans cette
            // remise à zéro, `_subscribed` gardait la machine d'AVANT la coupure, on ne se
            // réabonnait donc jamais, et plus aucun tirage ne recevait son verdict — pour
            // toute la vie de l'application, en silence. Le relais redémarre : c'est arrivé
            // deux fois le 04/08/2026.
            _subscribed.Clear();

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

    public De100Surface LoadedSurface(char machineId)
    {
        EnsureConnected();

        var info = _client.GetPrinterInfoAsync(machineId).GetAwaiter().GetResult();
        return info?.Media?.Surface ?? De100Surface.Glossy;
    }

    public int LoadedPaperWidthMm(char machineId)
    {
        EnsureConnected();

        var info = _client.GetPrinterInfoAsync(machineId).GetAwaiter().GetResult();
        return info?.Media?.PaperWidthMm ?? 0;
    }

    /// <summary>
    /// État complet de chaque machine : papier, encres, bac de maintenance, formats
    /// encore tirables. Sert à l'écran de suivi des consommables.
    /// </summary>
    public async Task<IReadOnlyList<De100PrinterInfo>> SnapshotAsync()
    {
        if (!_client.IsConnected) await _client.ConnectAsync();

        var machines = await _client.ListMachinesAsync();
        var etats = new List<De100PrinterInfo>();
        foreach (var machine in machines)
        {
            var info = await _client.GetPrinterInfoAsync(machine);
            if (info is not null) etats.Add(info);
        }
        return etats;
    }

    public string Submit(IReadOnlyList<De100PrintJob> jobs, char machineId)
    {
        EnsureConnected();

        // sans abonnement, aucun tirage ne recevrait jamais son issue
        lock (_sync)
        {
            if (_subscribed.Add(machineId))
                _client.SubscribeAsync(machineId).GetAwaiter().GetResult();
        }

        return _client.SubmitAsync(jobs, machineId).GetAwaiter().GetResult();
    }

    public void Cancel(string orderHandle)
    {
        EnsureConnected();
        _client.CancelAsync(orderHandle).GetAwaiter().GetResult();
    }

    /// <summary>État des imprimantes DNP, vues par le même relais.</summary>
    public async Task<IReadOnlyList<Dnp.DnpPrinterInfo>> DnpSnapshotAsync()
    {
        if (!_client.IsConnected) await _client.ConnectAsync();
        return await _client.DnpSnapshotAsync();
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
