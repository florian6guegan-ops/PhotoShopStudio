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
    /// Les imprimantes DNP telles que leur SDK les voit. Elles passent par ce relais parce
    /// que leur bibliothèque est en 32 bits, comme celle du minilab.
    /// </summary>
    Task<IReadOnlyList<Dnp.DnpPrinterInfo>> DnpSnapshotAsync();

    /// <summary>
    /// Envoie un tirage à une DNP <b>sans passer par le pilote Windows</b>, et rend le
    /// nombre d'exemplaires acceptés par la machine.
    ///
    /// C'est le chemin de DiLand, et le seul qui ne fabrique pas le fantôme coloré : le
    /// pilote de DNP date de 2017, n'a pas de successeur, et le défaut n'apparaît que par
    /// lui. Voir <c>DnpEnvoiDirect</c>.
    /// </summary>
    /// <param name="imagePath">Le rendu, DÉJÀ à la taille de la trame de la machine.</param>
    /// <param name="portNumber">Rang de la machine dans la découverte du SDK.</param>
    /// <param name="overcoat">Finition de surface (voir <c>DnpOvercoat</c>).</param>
    /// <param name="copies">Nombre d'exemplaires.</param>
    Task<int> DnpPrintAsync(string imagePath, int portNumber, int overcoat, int copies);

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
    /// La définition, en pixels, que la MACHINE attend pour un format donné.
    ///
    /// <b>Elle n'est pas celle qu'on calcule.</b> Le DE100 ajoute son débord : pour un
    /// 210 × 297 à 300 ppp il réclame 2515 × 3543 px, soit 213 × 300 mm. Les canaux à
    /// format FIXE — <c>A4</c> en est un — refusent tout ce qui n'est pas exactement cette
    /// taille, sans donner le moindre motif. C'est ce qui a fait échouer le 21×29,7 six
    /// fois de suite le 04/08/2026, pendant que le 18×24 sortait : lui passe par un canal
    /// VARIABLE, qui tolère l'à-peu-près.
    /// </summary>
    /// <returns><c>(0, 0)</c> si la machine n'en dit rien : l'appelant garde son calcul.</returns>
    (uint Width, uint Height) ExpectedPixels(char machineId, double widthMm, double heightMm, uint dpi);

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

    /// <summary>
    /// Panne, avertissement ou fin de consommable signalés par la machine.
    ///
    /// Le relais les transmettait depuis toujours, et personne ne les écoutait : un tirage
    /// refusé ne laissait que « erreur signalée par le minilab », sans jamais le motif que
    /// la machine venait pourtant de donner. C'est ce qui a rendu l'échec des commandes
    /// 04-015 et 04-020 du 04/08/2026 inexplicable depuis le journal.
    /// </summary>
    public event EventHandler<De100MachineEvent>? MachineEvent
    {
        add => _client.MachineEvent += value;
        remove => _client.MachineEvent -= value;
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

    public (uint Width, uint Height) ExpectedPixels(
        char machineId, double widthMm, double heightMm, uint dpi)
    {
        EnsureConnected();

        try
        {
            return _client.PixelCountAsync(machineId, widthMm, heightMm, dpi)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // On ne perd JAMAIS un tirage parce que cette lecture a échoué : sans réponse,
            // l'orchestrateur garde son propre calcul, celui qui sort depuis toujours sur
            // les canaux variables.
            Log?.Invoke($"Minilab : définition attendue illisible pour " +
                        $"{widthMm:0}×{heightMm:0} mm — {ex.Message}");
            return (0, 0);
        }
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

    /// <summary>
    /// Tire sur une DNP sans passer par le pilote Windows, et rend le nombre d'exemplaires
    /// acceptés par la machine.
    ///
    /// Passe par le relais parce que le SDK des DNP est en 32 bits, comme celui du minilab.
    /// </summary>
    public async Task<int> DnpPrintAsync(string imagePath, int portNumber, int overcoat, int copies)
    {
        if (!_client.IsConnected) await _client.ConnectAsync();
        return await _client.DnpPrintAsync(imagePath, portNumber, overcoat, copies);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
