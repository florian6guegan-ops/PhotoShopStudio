using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Printing.Devices.Fuji;

/// <summary>
/// Pilote du minilab Fuji Frontier DE100, au-dessus de <see cref="De100Interop"/>.
///
/// À n'instancier que dans un processus 32 bits (voir <see cref="De100Interop"/>).
/// Le suivi des tirages est délégué à <see cref="De100JobTracker"/>, qui traite les
/// neuf statuts et borne l'attente — c'est ce qui empêche le renvoi en boucle observé
/// avec DiLand.
/// </summary>
public sealed class De100Driver : IDisposable
{
    private const int OrderHandleCapacity = 256;
    private const int ValueCapacity = 256;
    private const int MaxMagazines = 8;

    // les délégués passés au SDK doivent survivre aussi longtemps que le natif les détient :
    // stockés en champ, sinon le GC les collecte et le premier callback fait sauter le processus
    private readonly De100Interop.OrderCallback _orderCallback;
    private readonly De100Interop.EventCallback _eventCallback;
    private readonly De100JobTracker _tracker;
    private readonly Timer _sweepTimer;
    private readonly object _sendSync = new();

    private int _orderIdCounter;
    private bool _disposed;

    /// <summary>Un tirage a reçu une issue définitive (réussite, échec, annulation ou délai dépassé).</summary>
    public event EventHandler<De100JobResult>? JobFinished;

    /// <summary>Le minilab a signalé un événement machine (bourrage, fin de papier, erreur…).</summary>
    public event EventHandler<De100MachineEvent>? MachineEvent;

    /// <param name="jobTimeout">Délai au-delà duquel un tirage sans réponse est abandonné.</param>
    /// <param name="sweepInterval">Période de vérification des échéances.</param>
    public De100Driver(TimeSpan? jobTimeout = null, TimeSpan? sweepInterval = null)
    {
        _tracker = new De100JobTracker(jobTimeout ?? TimeSpan.FromMinutes(30));

        _orderCallback = OnOrderCallback;
        _eventCallback = OnEventCallback;

        Check(De100Interop.PIF_Open(), nameof(De100Interop.PIF_Open));

        var interval = sweepInterval ?? TimeSpan.FromMinutes(1);
        _sweepTimer = new Timer(_ => Sweep(), null, interval, interval);
    }

    private const string SdkFileName = "PModuleIF.dll";
    private static string? _sdkDirectory;
    private static bool _resolverInstalled;

    /// <summary>
    /// Déclare où trouver le SDK Fuji. Il n'est pas installé à côté de Studio mais dans le
    /// dossier de DiLand : sans cette indication, le chargement échouerait alors que la
    /// bibliothèque est bien présente sur le poste.
    /// </summary>
    public static void UseSdkFrom(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _sdkDirectory = directory;

        if (_resolverInstalled) return;
        _resolverInstalled = true;

        // un seul résolveur par assembly : on l'installe une fois et il consulte le
        // dossier courant à chaque chargement
        NativeLibrary.SetDllImportResolver(typeof(De100Driver).Assembly, (name, _, _) =>
        {
            if (!name.Equals(SdkFileName, StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
            if (string.IsNullOrEmpty(_sdkDirectory)) return IntPtr.Zero;

            var candidate = Path.Combine(_sdkDirectory, SdkFileName);
            return File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle)
                ? handle
                : IntPtr.Zero;
        });
    }

    /// <summary>Emplacements où chercher le SDK Fuji, du plus explicite au plus probable.</summary>
    public static IEnumerable<string> ProbeSdkDirectories()
    {
        var declare = Environment.GetEnvironmentVariable("STUDIO_DE100_SDK");
        if (!string.IsNullOrWhiteSpace(declare)) yield return declare;

        yield return AppContext.BaseDirectory;

        foreach (var programFiles in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                 })
        {
            if (!string.IsNullOrEmpty(programFiles))
                yield return Path.Combine(programFiles, "DiLand Studio 2");
        }
    }

    /// <summary>
    /// Cherche le SDK et le déclare s'il est trouvé. Renvoie le dossier retenu, ou null.
    /// </summary>
    public static string? LocateSdk()
    {
        foreach (var directory in ProbeSdkDirectories())
        {
            if (!File.Exists(Path.Combine(directory, SdkFileName))) continue;
            UseSdkFrom(directory);
            return directory;
        }
        return null;
    }

    /// <summary>Vrai si le SDK du DE100 est chargeable depuis ce poste.</summary>
    public static bool IsSdkInstalled()
    {
        if (!string.IsNullOrEmpty(_sdkDirectory))
            return File.Exists(Path.Combine(_sdkDirectory, SdkFileName));

        var handle = NativeLibrary.TryLoad(SdkFileName, out var lib);
        if (handle) NativeLibrary.Free(lib);
        return handle;
    }

    /// <summary>Identifiants des machines déclarées dans la configuration du DE100.</summary>
    public IReadOnlyList<char> ListMachines()
    {
        var buffer = new StringBuilder(ValueCapacity);
        uint count = 0;
        Check(De100Interop.PIF_GetPrinterList(buffer, ref count), nameof(De100Interop.PIF_GetPrinterList));
        return buffer.ToString().Take((int)count).ToList();
    }

    /// <summary>Vrai si la machine est prête à recevoir des commandes.</summary>
    public bool IsReady(char machineId) => De100Interop.PIF_DevIsReady(machineId) == (int)PifResult.Ok;

    /// <summary>
    /// Abonne le pilote aux notifications d'une machine. Sans cet appel, aucun tirage
    /// ne recevra jamais d'issue autrement que par expiration du délai.
    /// </summary>
    public void Subscribe(char machineId) =>
        Check(De100Interop.PIF_DevSetCallbackAddress(machineId, _eventCallback, _orderCallback),
            nameof(De100Interop.PIF_DevSetCallbackAddress));

    /// <summary>Lit l'état d'une machine et le contenu de ses magasins.</summary>
    public De100PrinterInfo GetPrinterInfo(char machineId)
    {
        var handle = IntPtr.Zero;
        Check(De100Interop.PIF_DevGetPrinterInfo(machineId, ref handle), nameof(De100Interop.PIF_DevGetPrinterInfo));
        try
        {
            var loadingNum = ReadInt(handle, "LoadingNum");
            // les proprietes indexees du SDK Fuji sont numerotees a partir de 1
            var status = (De100PrinterStatus)ReadIndexedInt(handle, "PrinterStatus", 1);
            var regNum = ReadValue(handle, "RegNum");

            var magazines = new List<De100Magazine>();
            for (uint i = 1; i <= Math.Min(loadingNum, MaxMagazines); i++)
            {
                magazines.Add(new De100Magazine(
                    (int)i,
                    ReadIndexedValue(handle, "MagazineType", i),
                    ReadIndexedDouble(handle, "PaperWidth", i),
                    ReadIndexedDouble(handle, "PaperHeight", i)));
            }

            return new De100PrinterInfo(machineId, status, regNum, magazines);
        }
        finally
        {
            De100Interop.PHIF_ReleaseHandle(handle);
        }
    }

    /// <summary>
    /// Envoie un tirage. Renvoie le handle de commande si le minilab l'a accepté ;
    /// lève <see cref="De100Exception"/> sinon. Un tirage accepté est suivi jusqu'à
    /// son issue, remontée par <see cref="JobFinished"/>.
    /// </summary>
    public string Submit(De100PrintJob job, char machineId)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!File.Exists(job.ImagePath))
            throw new FileNotFoundException("Image à tirer introuvable.", job.ImagePath);

        // PIF_StartOrder → PIF_Print → PIF_EndOrder forment une transaction non réentrante
        lock (_sendSync)
        {
            var orderHandle = new StringBuilder(OrderHandleCapacity);
            var start = (PifResult)De100Interop.PIF_StartOrder(orderHandle);
            if (start != PifResult.Ok)
                throw new De100Exception($"Ouverture de commande refusée par le minilab ({start}).", start);

            var parameters = BuildParameters(job, machineId, NextOrderId());
            var imageData = new ST_IMAGE_DATA
            {
                srcRGB = IntPtr.Zero,
                pxImageWidth = 0,
                pxImageHeight = 0,
                imagePath = job.ImagePath,
            };

            var print = (PifResult)De100Interop.PIF_Print(orderHandle, ref imageData, parameters, (uint)parameters.Length);
            var end = (PifResult)De100Interop.PIF_EndOrder(orderHandle);

            if (print != PifResult.Ok || end != PifResult.Ok)
            {
                // la commande est ouverte mais inexploitable : on l'annule pour ne pas
                // laisser d'ordre fantôme dans la file du minilab
                De100Interop.PIF_CancelOrder(orderHandle);
                var failed = print != PifResult.Ok ? print : end;
                throw new De100Exception(
                    $"Envoi du tirage refusé par le minilab (PIF_Print={print}, PIF_EndOrder={end}).", failed);
            }

            var ohnd = orderHandle.ToString();
            _tracker.Track(job.JobId, ohnd, DateTimeOffset.Now);
            return ohnd;
        }
    }

    /// <summary>Demande l'annulation d'une commande au minilab et cesse de la suivre.</summary>
    public void Cancel(string orderHandle)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderHandle);
        De100Interop.PIF_CancelOrder(new StringBuilder(orderHandle, OrderHandleCapacity));
        _tracker.Forget(orderHandle);
    }

    /// <summary>Tirages encore en attente d'une issue.</summary>
    public IReadOnlyList<string> PendingJobIds => _tracker.PendingJobIds;

    /// <summary>Vérifie les échéances immédiatement, sans attendre le prochain passage du minuteur.</summary>
    public void Sweep()
    {
        foreach (var result in _tracker.SweepTimeouts(DateTimeOffset.Now))
            JobFinished?.Invoke(this, result);
    }

    private static ST_PARAM[] BuildParameters(De100PrintJob job, char machineId, int orderId) =>
    [
        Param(De100ParamNames.OrderId, orderId.ToString(CultureInfo.InvariantCulture)),
        Param(De100ParamNames.Width, job.WidthMm.ToString(CultureInfo.InvariantCulture)),
        Param(De100ParamNames.Height, job.HeightMm.ToString(CultureInfo.InvariantCulture)),
        Param(De100ParamNames.ColorSpace, "1"),
        Param(De100ParamNames.Rotation, "0"),
        Param(De100ParamNames.Surface, ((int)job.Surface).ToString(CultureInfo.InvariantCulture)),
        Param(De100ParamNames.PrintNum, job.Copies.ToString(CultureInfo.InvariantCulture)),
        Param(De100ParamNames.OutputPrinter, machineId.ToString()),
        Param(De100ParamNames.PrintSizeName, job.PrintSizeName),
        Param(De100ParamNames.Quality, job.HighQuality ? "1" : "0"),
        Param(De100ParamNames.CommonColorMode, job.ColorMode),
    ];

    private static ST_PARAM Param(string name, string value) => new() { name = name, value = value };

    private int NextOrderId()
    {
        // le SDK n'accepte que 1..65535
        var next = Interlocked.Increment(ref _orderIdCounter);
        return (next % 65535) + 1;
    }

    private void OnOrderCallback(IntPtr orderInfoPtr)
    {
        if (orderInfoPtr == IntPtr.Zero) return;

        var order = Marshal.PtrToStructure<ST_ORDER_INFO>(orderInfoPtr);

        var result = _tracker.Report(order.ohnd, (De100OrderStatus)order.status, DateTimeOffset.Now);
        if (result is not null)
            JobFinished?.Invoke(this, result);
    }

    private void OnEventCallback(IntPtr eventInfoPtr, uint onOff)
    {
        if (eventInfoPtr == IntPtr.Zero) return;

        var evt = Marshal.PtrToStructure<ST_EVENT_INFO>(eventInfoPtr);

        MachineEvent?.Invoke(this, new De100MachineEvent(
            evt.machineID,
            (De100ErrorLevel)evt.errorLevel,
            evt.errorNo,
            string.Join(' ', new[] { evt.errorString1, evt.errorString2, evt.errorString3 }
                .Where(s => !string.IsNullOrWhiteSpace(s))),
            IsActive: onOff != 0));
    }

    private static string ReadValue(IntPtr handle, string name)
    {
        var value = new StringBuilder(ValueCapacity);
        De100Interop.PHIF_GetValue(handle, new StringBuilder(name), value);
        return value.ToString();
    }

    private static string ReadIndexedValue(IntPtr handle, string name, uint index)
    {
        var value = new StringBuilder(ValueCapacity);
        De100Interop.PHIF_GetNValue(handle, new StringBuilder(name), index, value);
        return value.ToString();
    }

    private static uint ReadInt(IntPtr handle, string name) =>
        uint.TryParse(ReadValue(handle, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static int ReadIndexedInt(IntPtr handle, string name, uint index) =>
        int.TryParse(ReadIndexedValue(handle, name, index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ReadIndexedDouble(IntPtr handle, string name, uint index) =>
        double.TryParse(ReadIndexedValue(handle, name, index), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static void Check(int code, string call)
    {
        var result = (PifResult)code;
        if (result != PifResult.Ok)
            throw new De100Exception($"{call} a échoué ({result}).", result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sweepTimer.Dispose();
        try { De100Interop.PIF_Close(); }
        catch (DllNotFoundException) { /* SDK absent : rien à fermer */ }
    }
}

/// <summary>Événement machine remonté par le minilab.</summary>
public sealed record De100MachineEvent(
    char MachineId,
    De100ErrorLevel Level,
    string ErrorNumber,
    string Message,
    bool IsActive);

/// <summary>Erreur remontée par le SDK du DE100.</summary>
public sealed class De100Exception(string message, PifResult result) : Exception(message)
{
    public PifResult Result { get; } = result;
}
