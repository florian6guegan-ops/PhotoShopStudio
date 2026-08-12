using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace Studio.Printing.Devices.Fuji.Bridge;

/// <summary>
/// Accès au minilab DE100 depuis l'application 64 bits, à travers le relais 32 bits.
///
/// Le SDK Fuji est en 32 bits et ne peut donc pas être appelé directement depuis
/// Studio.App. Cette classe démarre <c>Studio.De100Host.exe</c> au besoin, dialogue avec
/// lui par un tube nommé, et présente la même surface que <see cref="De100Driver"/>.
///
/// Le relais s'arrête quand on se déconnecte, et Windows le tue si nous disparaissons
/// sans prévenir — voir <see cref="ProcessusLie"/>. Cette dernière garantie manquait :
/// l'application a planté deux fois le 07/08/2026, et le poste de Créteil s'est retrouvé
/// avec deux relais de versions différentes en concurrence sur le même SDK.
/// </summary>
public sealed class De100BridgeClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Délai accordé à un ENVOI de tirage, bien plus long que pour le reste.
    ///
    /// Un même délai de 30 s valait pour toutes les commandes, y compris <c>submit</c>.
    /// Or le SDK Fuji retient <c>PIF_Print</c> tant que la machine n'a pas pris l'ordre en
    /// charge, et un DE100 en train de sortir la commande précédente met couramment plus
    /// de trente secondes. On déclarait donc l'impression en échec alors qu'elle partait :
    /// la commande 01-017 du 01/08/2026 a échoué ainsi pendant que la machine tirait les
    /// deux précédentes.
    ///
    /// La conséquence n'est pas anodine : l'enveloppe reste « partie sans confirmation »,
    /// et l'opérateur se voit proposer de réimprimer ce qui est peut-être déjà passé. Un
    /// délai trop court coûte donc des tirages en double — exactement ce qu'on cherche à
    /// ne jamais provoquer.
    ///
    /// <b>On n'en profite pas pour réessayer.</b> Si l'envoi finit malgré tout par expirer,
    /// la règle est inchangée : rien n'est renvoyé automatiquement, l'opérateur tranche.
    /// </summary>
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Délai par commande. Court pour tout ce qui interroge — l'écran attend la réponse,
    /// et un relais muet ne doit pas figer le bandeau des machines pendant des minutes —
    /// long pour ce qui ENGAGE la machine.
    ///
    /// Le partage se lit dans <see cref="De100Commands.EngageLaMachine"/>, que le relais
    /// applique aussi : c'est le plus court des deux délais qui décide, les tenir séparés
    /// ne servait qu'à les laisser diverger.
    /// </summary>
    internal TimeSpan DelaiPour(string command) =>
        De100Commands.EngageLaMachine(command) ? SubmitTimeout : _timeout;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<De100Message>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TimeSpan _timeout;

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _host;
    private CancellationTokenSource? _readLoop;
    private bool _disposed;

    /// <summary>Journal optionnel : branché sur FileLog par l'application.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Un tirage a reçu une issue définitive.</summary>
    public event EventHandler<De100JobResult>? JobFinished;

    /// <summary>Le minilab signale un événement machine.</summary>
    public event EventHandler<De100MachineEvent>? MachineEvent;

    /// <param name="timeout">Délai d'attente d'une réponse du relais.</param>
    public De100BridgeClient(TimeSpan? timeout = null) => _timeout = timeout ?? DefaultTimeout;

    /// <summary>Vrai si le relais est démarré et connecté.</summary>
    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Emplacements où chercher le relais : à côté de l'application, dans un sous-dossier
    /// dédié, puis dans la sortie de compilation quand on travaille depuis les sources.
    /// </summary>
    public static IEnumerable<string> ProbeHostPaths()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Studio.De100Host.exe");
        yield return Path.Combine(baseDir, "de100", "Studio.De100Host.exe");

        // exécution depuis les sources : remonter jusqu'à la racine du dépôt
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "Studio.De100Host",
                "bin", "Debug", "net8.0-windows", "Studio.De100Host.exe");
            if (File.Exists(candidate)) yield return candidate;
            dir = dir.Parent;
        }
    }

    /// <summary>
    /// Chemin d'un relais réellement exécutable, ou null.
    ///
    /// La présence du seul .exe ne suffit pas : une référence de compilation peut en
    /// déposer une copie sans sa bibliothèque, et cet exécutable-là s'arrête aussitôt
    /// lancé. On exige donc le .dll à côté.
    /// </summary>
    public static string? FindHost() => ProbeHostPaths().FirstOrDefault(IsRunnableHost);

    private static bool IsRunnableHost(string exePath) =>
        File.Exists(exePath) && File.Exists(Path.ChangeExtension(exePath, ".dll"));

    /// <summary>
    /// Démarre le relais s'il ne tourne pas, puis s'y connecte.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellation = default)
    {
        if (IsConnected) return;

        var hostPath = FindHost()
            ?? throw new FileNotFoundException(
                "Relais DE100 introuvable (Studio.De100Host.exe). Sans lui, le minilab ne peut pas " +
                "être piloté : le SDK Fuji est en 32 bits et l'application en 64 bits.");

        Log?.Invoke($"Démarrage du relais DE100 : {hostPath}");

        var demarrage = new ProcessStartInfo(hostPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,

            // Le relais écrit son journal en UTF-8 ; sans cette ligne, .NET le relit dans
            // la page de codes ANSI du poste et « trouvé » arrive en « trouvÃ© ».
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        PointerVersLeRuntime32Bits(demarrage);

        _host = Process.Start(demarrage);

        // Lié à nous AVANT toute autre chose : à partir d'ici, le relais ne peut plus nous
        // survivre, même si nous plantons. Le Kill de Deconnecter reste — il ferme
        // proprement le cas normal — mais il demande que quelqu'un soit encore là pour
        // l'appeler, ce qui n'est justement pas le cas quand l'application meurt d'un coup.
        if (_host is not null && !ProcessusLie.Attacher(_host))
            Log?.Invoke("Relais DE100 : le système a refusé de le lier à l'application. " +
                        "Il sera fermé normalement, mais pourrait survivre à un plantage.");

        DrainerLaSortieDErreur();

        _pipe = new NamedPipeClientStream(".", De100Protocol.PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await _pipe.ConnectAsync((int)_timeout.TotalMilliseconds, cancellation);
        }
        catch (TimeoutException)
        {
            // le plus souvent le relais est mort au démarrage : sa sortie d'erreur dit
            // pourquoi, et c'est bien plus utile qu'un « pas de réponse »
            AbandonnerLaConnexion();
            throw new TimeoutException(DescribeStartupFailure(hostPath));
        }
        catch
        {
            AbandonnerLaConnexion();
            throw;
        }

        _reader = new StreamReader(_pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };

        _readLoop = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readLoop.Token), CancellationToken.None);

        Log?.Invoke("Relais DE100 connecté.");
    }

    /// <summary>
    /// Défait ce qu'une connexion ratée laisse derrière elle : le tube, et surtout le RELAIS.
    ///
    /// <b>Sans cela, chaque échec laissait un processus 32 bits en vie.</b> Le relais ne
    /// s'arrête tout seul qu'à la DÉCONNEXION d'un client — s'il n'y en a jamais eu, il
    /// attend indéfiniment sur son tube. Or l'appelant réessaie : le bandeau des machines
    /// interroge le minilab toutes les quelques secondes, et chaque tentative en démarrait
    /// un de plus. Un minilab en veille suffisait donc à empiler des dizaines de
    /// <c>Studio.De100Host.exe</c>, chacun tenant un tube du même nom — après quoi plus
    /// aucune connexion ne tombait sur le bon.
    /// </summary>
    private void AbandonnerLaConnexion()
    {
        Ferme(_pipe);
        _pipe = null;

        try
        {
            if (_host is { HasExited: false }) _host.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
            // processus déjà parti, ou hors de portée : rien de plus à faire
        }

        _host?.Dispose();
        _host = null;
    }

    /// <summary>Vrai si le SDK Fuji est présent sur le poste (question posée au relais).</summary>
    public async Task<bool> IsSdkInstalledAsync() =>
        await SendAsync<bool>(De100Commands.Ping);

    /// <summary>Identifiants des machines déclarées dans la configuration du DE100.</summary>
    public async Task<IReadOnlyList<char>> ListMachinesAsync() =>
        await SendAsync<List<char>>(De100Commands.ListMachines) ?? [];

    public async Task<bool> IsReadyAsync(char machineId) =>
        await SendAsync<bool>(De100Commands.IsReady, machineId.ToString());

    public async Task<De100PrinterInfo?> GetPrinterInfoAsync(char machineId) =>
        await SendAsync<De100PrinterInfo>(De100Commands.PrinterInfo, machineId.ToString());

    /// <summary>
    /// La définition que la machine attend pour un format, en pixels. <c>(0, 0)</c> = elle
    /// n'a rien voulu en dire.
    /// </summary>
    public async Task<(uint Width, uint Height)> PixelCountAsync(
        char machineId, double widthMm, double heightMm, uint dpi)
    {
        var reponse = await SendAsync<De100PixelCountResponse>(
            De100Commands.PixelCount,
            new De100PixelCountRequest(machineId, widthMm, heightMm, dpi));

        return reponse is null ? (0, 0) : (reponse.Width, reponse.Height);
    }

    /// <summary>Abonne le relais aux notifications : sans cela, aucun tirage n'aura d'issue.</summary>
    public async Task SubscribeAsync(char machineId) =>
        await SendAsync<object>(De100Commands.Subscribe, machineId.ToString());

    /// <summary>Envoie un tirage ; renvoie le handle de commande attribué par le minilab.</summary>
    public async Task<string> SubmitAsync(De100PrintJob job, char machineId) =>
        await SubmitAsync([job], machineId);

    /// <summary>
    /// Envoie toutes les photos d'une enveloppe : elles forment UNE commande côté minilab.
    /// </summary>
    public async Task<string> SubmitAsync(IReadOnlyList<De100PrintJob> jobs, char machineId) =>
        await SendAsync<string>(De100Commands.Submit, new De100SubmitRequest(jobs, machineId))
        ?? throw new InvalidOperationException("Le relais n'a pas renvoyé de handle de commande.");

    public async Task CancelAsync(string orderHandle) =>
        await SendAsync<object>(De100Commands.Cancel, orderHandle);

    /// <summary>
    /// Où en est une commande, comptée par la machine elle-même. Null si le SDK ne
    /// reconnaît plus ce handle.
    /// </summary>
    public Task<De100OrderProgress?> OrderProgressAsync(string orderHandle) =>
        SendAsync<De100OrderProgress>(De100Commands.OrderProgress, orderHandle);

    /// <summary>Tirages encore suivis par le relais.</summary>
    public async Task<IReadOnlyList<string>> PendingJobsAsync() =>
        await SendAsync<List<string>>(De100Commands.PendingJobs) ?? [];

    /// <summary>
    /// État des imprimantes DNP. Elles passent par le même relais : leur SDK est
    /// également en 32 bits.
    /// </summary>
    public async Task<IReadOnlyList<Dnp.DnpPrinterInfo>> DnpSnapshotAsync() =>
        await SendAsync<List<Dnp.DnpPrinterInfo>>(De100Commands.DnpSnapshot) ?? [];

    /// <summary>
    /// Envoie un tirage à une DNP sans passer par le pilote Windows, et rend le nombre
    /// d'exemplaires que la machine a acceptés.
    ///
    /// Seul le CHEMIN traverse le tube : le relais ouvre le fichier de son côté. Une
    /// planche 10×15 à 300 ppp fait près de sept méga-octets une fois décompressée, et ce
    /// tube sert aussi le minilab, une commande à la fois.
    /// </summary>
    public async Task<int> DnpPrintAsync(string imagePath, int portNumber, int overcoat, int copies) =>
        await SendAsync<int>(De100Commands.DnpPrint,
            new De100DnpPrintRequest(imagePath, portNumber, overcoat, copies));

    /// <summary>
    /// Un processus 64 bits transmet souvent DOTNET_ROOT pointant vers son propre runtime :
    /// le relais 32 bits y chercherait alors un hostfxr qui n'y est pas, et s'arrêterait
    /// aussitôt. On efface la variable et on désigne explicitement le runtime 32 bits.
    /// </summary>
    private static void PointerVersLeRuntime32Bits(ProcessStartInfo demarrage)
    {
        demarrage.Environment.Remove("DOTNET_ROOT");

        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (string.IsNullOrEmpty(programFilesX86)) return;

        var racine32 = Path.Combine(programFilesX86, "dotnet");
        if (Directory.Exists(Path.Combine(racine32, "host", "fxr")))
            demarrage.Environment["DOTNET_ROOT(x86)"] = racine32;
    }

    /// <summary>
    /// Les dernières lignes que le relais a écrites sur sa sortie d'erreur.
    ///
    /// Bornée : le relais journalise chaque commande, et on ne garde que de quoi
    /// comprendre une panne — pas la séance entière.
    /// </summary>
    private readonly Queue<string> _dernieresLignes = new();

    private const int LignesGardees = 40;

    /// <summary>
    /// Vide la sortie d'erreur du relais EN CONTINU, et la déverse dans le journal.
    ///
    /// <b>Sans cela, le relais se fige.</b> Il écrit tous ses journaux sur
    /// <c>Console.Error</c> — une ligne par commande traitée — et cette sortie est
    /// redirigée. Le tampon d'un tube anonyme fait quelques kilo-octets : une fois plein,
    /// <c>WriteLine</c> BLOQUE le processus enfant, qui cesse de répondre. L'application
    /// voyait alors « Pipe is broken », relançait le relais, et le cycle recommençait —
    /// vingt-sept redémarrages dans la journée du 04/08/2026, des verdicts de tirage
    /// perdus, et un bandeau des machines qui se vidait.
    ///
    /// La lecture ne se faisait qu'en cas d'échec au DÉMARRAGE (<see cref="DescribeStartupFailure"/>) :
    /// autant dire jamais, puisque le démarrage réussit presque toujours.
    ///
    /// Effet de bord recherché : ce que le relais a à dire arrive enfin au journal, avec le
    /// préfixe « relais ». C'est le seul endroit d'où l'on voit ce qui se passe côté SDK.
    /// </summary>
    private void DrainerLaSortieDErreur()
    {
        if (_host is null) return;

        _host.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;

            lock (_dernieresLignes)
            {
                _dernieresLignes.Enqueue(e.Data);
                while (_dernieresLignes.Count > LignesGardees) _dernieresLignes.Dequeue();
            }

            Log?.Invoke($"relais · {e.Data}");
        };

        _host.BeginErrorReadLine();
    }

    /// <summary>
    /// Explique pourquoi le relais n'a pas répondu, en reprenant sa sortie d'erreur quand
    /// il s'est arrêté tout seul. Le cas le plus fréquent au premier déploiement est
    /// l'absence du runtime .NET 32 bits, que rien d'autre ne signale.
    /// </summary>
    private string DescribeStartupFailure(string hostPath)
    {
        if (_host is null || !_host.HasExited)
            return "Le relais DE100 a démarré mais n'a pas ouvert la liaison dans le délai imparti.";

        // ce que le drainage a recueilli : ReadToEnd n'est plus possible une fois
        // BeginErrorReadLine engagé, et ce serait de toute façon la même chose
        string stderr;
        lock (_dernieresLignes) stderr = string.Join("\n", _dernieresLignes).Trim();

        var message = $"Le relais DE100 s'est arrêté aussitôt lancé (code {_host.ExitCode}).\n{hostPath}";

        if (stderr.Contains("must install .NET", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("hostfxr", StringComparison.OrdinalIgnoreCase))
        {
            return message + "\n\nCause : le runtime .NET 8 en 32 bits n'est pas installé sur ce poste. " +
                   "Le relais doit être en 32 bits pour parler au SDK Fuji, et il lui faut donc le runtime " +
                   "correspondant.\n\nÀ installer : winget install Microsoft.DotNet.Runtime.8 --architecture x86";
        }

        return string.IsNullOrEmpty(stderr) ? message : message + "\n\n" + stderr;
    }

    /// <summary>
    /// Le message d'expiration. Sur un envoi il doit dire l'essentiel : on ne SAIT PAS si
    /// le tirage est parti. L'opérateur qui lit « échec » sans cette réserve réimprime, et
    /// sort la commande en double.
    /// </summary>
    private static string Expire(string command, TimeSpan attente)
    {
        var duree = attente.TotalSeconds >= 90
            ? $"{attente.TotalMinutes:0} min"
            : $"{attente.TotalSeconds:0} s";

        var message = $"Le relais DE100 n'a pas répondu à « {command} » en {duree}.";

        if (!De100Commands.EngageLaMachine(command)) return message;

        var ou = command is De100Commands.DnpPrint ? "CE QUI SORT DE LA DNP" : "SUR LE MINILAB";

        return message + "\n\nLe tirage a peut-être malgré tout été pris par la machine : " +
               $"VÉRIFIEZ {ou} avant de réimprimer. Rien n'a été renvoyé automatiquement.";
    }

    private async Task<T?> SendAsync<T>(string command, object? payload = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Relais DE100 non connecté : appelez ConnectAsync d'abord.");

        var request = De100Protocol.Request(command, payload);
        var waiter = new TaskCompletionSource<De100Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = waiter;

        try
        {
            await _writeLock.WaitAsync();
            try
            {
                await _writer!.WriteLineAsync(De100Protocol.Encode(request));
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // La commande n'est jamais partie : son attente n'a plus lieu d'être.
            //
            // Elle restait sinon dans le registre — un tube rompu lève ICI, pas plus loin —
            // et n'en sortait qu'à l'arrêt de la boucle de lecture. Le bandeau des machines
            // interroge le relais toutes les quelques secondes : une liaison coupée y
            // empilait une entrée morte par interrogation, chacune avec sa promesse jamais
            // tenue.
            _pending.TryRemove(request.Id, out _);
            throw;
        }

        var attente = DelaiPour(command);
        using var delai = new CancellationTokenSource(attente);
        await using var abandon = delai.Token.Register(() => waiter.TrySetException(
            new TimeoutException(Expire(command, attente))));

        De100Message response;
        try
        {
            response = await waiter.Task;
        }
        finally
        {
            _pending.TryRemove(request.Id, out _);
        }

        if (!response.Ok)
            throw new InvalidOperationException(response.Error ?? $"Le relais a rejeté « {command} ».");

        return De100Protocol.Payload<T>(response);
    }

    private async Task ReadLoopAsync(CancellationToken cancellation)
    {
        try
        {
            while (!cancellation.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(cancellation);
                if (line is null) break;
                if (!De100Protocol.TryDecode(line, out var message)) continue;

                switch (message.Kind)
                {
                    case De100MessageKind.Response when _pending.TryGetValue(message.Id, out var waiter):
                        waiter.TrySetResult(message);
                        break;

                    case De100MessageKind.Event:
                        RaiseEvent(message);
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // arrêt demandé
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // ObjectDisposedException est le cas de l'ARRÊT : DisposeAsync referme le tube
            // pendant que cette boucle attend une ligne dessus. Non capturée, elle sortait
            // d'une tâche que personne n'observe — le finally passait quand même, mais la
            // fermeture s'achevait sur une exception perdue.
            Log?.Invoke($"Relais DE100 déconnecté : {ex.Message}");
        }
        finally
        {
            // ne jamais laisser un appelant attendre une réponse qui ne viendra plus
            foreach (var waiter in _pending.Values)
                waiter.TrySetException(new InvalidOperationException("Le relais DE100 s'est arrêté."));
            _pending.Clear();
        }
    }

    private void RaiseEvent(De100Message message)
    {
        switch (message.Name)
        {
            case De100Events.JobFinished:
                if (De100Protocol.Payload<De100JobResult>(message) is { } result)
                    JobFinished?.Invoke(this, result);
                break;

            case De100Events.MachineEvent:
                if (De100Protocol.Payload<De100MachineEvent>(message) is { } evt)
                    MachineEvent?.Invoke(this, evt);
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (IsConnected)
                await SendAsync<object>(De100Commands.Shutdown);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Arrêt propre du relais impossible : {ex.Message}");
        }

        _readLoop?.Cancel();
        _readLoop?.Dispose();

        // le relais ferme le tube dès qu'il a reçu l'ordre d'arrêt : refermer nos lecteurs
        // provoque alors une tentative d'écriture sur un tube mort. On ferme le tube en
        // premier, et on ignore ce que la fermeture des habillages peut encore lever.
        Ferme(_pipe);
        Ferme(_writer);
        Ferme(_reader);

        try
        {
            // le relais s'arrête de lui-même à la déconnexion ; on ne le tue qu'en dernier recours
            if (_host is { HasExited: false } && !_host.WaitForExit(3000))
                _host.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // processus déjà parti
        }
        _host?.Dispose();

        _writeLock.Dispose();
    }

    /// <summary>Fermeture sans bruit : un tube déjà mort ne doit pas faire échouer l'arrêt.</summary>
    private static void Ferme(IDisposable? ressource)
    {
        try
        {
            ressource?.Dispose();
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }
}
