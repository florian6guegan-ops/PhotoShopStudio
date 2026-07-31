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
/// Le relais s'arrête tout seul quand on se déconnecte : pas de processus fantôme.
/// </summary>
public sealed class De100BridgeClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

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
        };
        PointerVersLeRuntime32Bits(demarrage);

        _host = Process.Start(demarrage);

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
            throw new TimeoutException(DescribeStartupFailure(hostPath));
        }

        _reader = new StreamReader(_pipe, new UTF8Encoding(false));
        _writer = new StreamWriter(_pipe, new UTF8Encoding(false)) { AutoFlush = true };

        _readLoop = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readLoop.Token), CancellationToken.None);

        Log?.Invoke("Relais DE100 connecté.");
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

    /// <summary>Abonne le relais aux notifications : sans cela, aucun tirage n'aura d'issue.</summary>
    public async Task SubscribeAsync(char machineId) =>
        await SendAsync<object>(De100Commands.Subscribe, machineId.ToString());

    /// <summary>Envoie un tirage ; renvoie le handle de commande attribué par le minilab.</summary>
    public async Task<string> SubmitAsync(De100PrintJob job, char machineId) =>
        await SendAsync<string>(De100Commands.Submit, new De100SubmitRequest(job, machineId))
        ?? throw new InvalidOperationException("Le relais n'a pas renvoyé de handle de commande.");

    public async Task CancelAsync(string orderHandle) =>
        await SendAsync<object>(De100Commands.Cancel, orderHandle);

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
    /// Explique pourquoi le relais n'a pas répondu, en reprenant sa sortie d'erreur quand
    /// il s'est arrêté tout seul. Le cas le plus fréquent au premier déploiement est
    /// l'absence du runtime .NET 32 bits, que rien d'autre ne signale.
    /// </summary>
    private string DescribeStartupFailure(string hostPath)
    {
        if (_host is null || !_host.HasExited)
            return "Le relais DE100 a démarré mais n'a pas ouvert la liaison dans le délai imparti.";

        var stderr = "";
        try
        {
            stderr = _host.StandardError.ReadToEnd().Trim();
        }
        catch (InvalidOperationException)
        {
            // sortie déjà consommée ou non redirigée
        }

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

    private async Task<T?> SendAsync<T>(string command, object? payload = null)
    {
        if (!IsConnected)
            throw new InvalidOperationException("Relais DE100 non connecté : appelez ConnectAsync d'abord.");

        var request = De100Protocol.Request(command, payload);
        var waiter = new TaskCompletionSource<De100Message>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[request.Id] = waiter;

        await _writeLock.WaitAsync();
        try
        {
            await _writer!.WriteLineAsync(De100Protocol.Encode(request));
        }
        finally
        {
            _writeLock.Release();
        }

        using var delai = new CancellationTokenSource(_timeout);
        await using var abandon = delai.Token.Register(() => waiter.TrySetException(
            new TimeoutException($"Le relais DE100 n'a pas répondu à « {command} » en {_timeout.TotalSeconds:0} s.")));

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
        catch (IOException ex)
        {
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
