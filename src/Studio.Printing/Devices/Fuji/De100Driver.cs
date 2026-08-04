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

    /// <summary>Déclare où trouver le SDK Fuji.</summary>
    public static void UseSdkFrom(string directory) => NativeSdkResolver.Register(SdkFileName, directory);

    /// <summary>Emplacements où chercher le SDK Fuji, du plus explicite au plus probable.</summary>
    public static IEnumerable<string> ProbeSdkDirectories() =>
        NativeSdkResolver.ProbeDirectories("STUDIO_DE100_SDK");

    /// <summary>
    /// Cherche le SDK et le déclare s'il est trouvé. Renvoie le dossier retenu, ou null.
    /// </summary>
    public static string? LocateSdk() => NativeSdkResolver.Locate(SdkFileName, "STUDIO_DE100_SDK");

    /// <summary>Vrai si le SDK du DE100 est chargeable depuis ce poste.</summary>
    public static bool IsSdkInstalled()
    {
        if (NativeSdkResolver.DirectoryOf(SdkFileName) is not null)
            return NativeSdkResolver.Exists(SdkFileName);

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

    /// <summary>
    /// Instantané complet d'une machine : état, rouleau chargé, encres, bac de maintenance
    /// et formats encore tirables.
    ///
    /// Attention au piège du SDK : <c>LoadingNum</c> n'est pas un NOMBRE de magasins mais le
    /// NUMÉRO du magasin chargé. Toutes les propriétés indexées se lisent avec cette
    /// valeur — c'est ainsi que procède le pilote de DiLand.
    /// </summary>
    public De100PrinterInfo GetPrinterInfo(char machineId)
    {
        var handle = IntPtr.Zero;
        Check(De100Interop.PIF_DevGetPrinterInfo(machineId, ref handle), nameof(De100Interop.PIF_DevGetPrinterInfo));

        De100PrinterStatus status;
        string regNum, serial;
        long printCount;
        De100Media? media = null;
        De100Supplies? supplies = null;

        try
        {
            var loading = ReadValue(handle, "LoadingNum");
            var n = uint.TryParse(loading, out var parsed) ? parsed : 0u;

            status = (De100PrinterStatus)ReadIndexedInt(handle, "PrinterStatus", n);
            regNum = ReadValue(handle, "RegNum");
            serial = ReadValue(handle, "SerialNumber");
            printCount = long.TryParse(ReadValue(handle, "TotalPrintCountAS"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var count) ? count : 0;

            if (!string.IsNullOrEmpty(loading))
            {
                media = new De100Media(
                    LoadingNumber: (int)n,
                    MagazineType: ReadIndexedValue(handle, "MagazineType", n),
                    PaperWidthMm: (int)Math.Round(ReadIndexedDouble(handle, "PaperWidth", n)),
                    PaperHeightMm: (int)Math.Round(ReadIndexedDouble(handle, "PaperHeight", n)),
                    Surface: (De100Surface)ReadIndexedInt(handle, "Surface", n),
                    PaperRemainingMm: ReadIndexedDouble(handle, "PaperRest", n));

                supplies = new De100Supplies(
                    Yellow: new De100Supply("Jaune", ReadIndexedInt(handle, "InkRemain", 1)),
                    Magenta: new De100Supply("Magenta", ReadIndexedInt(handle, "InkRemain", 2)),
                    Cyan: new De100Supply("Cyan", ReadIndexedInt(handle, "InkRemain", 3)),
                    Black: new De100Supply("Noir", ReadIndexedInt(handle, "InkRemain", 4)),
                    MaintenanceTank: new De100Supply("Bac de maintenance", (int)ReadInt(handle, "MaintenanceTank")),
                    InkCount: (int)ReadInt(handle, "InkNum"));
            }
        }
        finally
        {
            De100Interop.PHIF_ReleaseHandle(handle);
        }

        // modèle, type et adresse réseau vivent dans un second jeu d'informations
        var (model, ip) = ReadSetupInfo(machineId);

        var formats = media is null
            ? []
            : De100Formats.Estimate(media.PaperWidthMm, media.PaperRemainingMm);

        return new De100PrinterInfo(machineId, status, regNum, model, serial, ip, printCount,
            media, supplies, formats);
    }

    private static (string Model, string IpAddress) ReadSetupInfo(char machineId)
    {
        var handle = IntPtr.Zero;
        if (De100Interop.PIF_DevGetSetupInfo(machineId, ref handle) != (int)PifResult.Ok)
            return ("", "");

        try
        {
            return (ReadValue(handle, "PrinterName"), ReadValue(handle, "IPAddress"));
        }
        finally
        {
            De100Interop.PHIF_ReleaseHandle(handle);
        }
    }

    /// <summary>Envoie un tirage seul — une commande minilab d'une photo.</summary>
    public string Submit(De100PrintJob job, char machineId)
    {
        ArgumentNullException.ThrowIfNull(job);
        return Submit([job], machineId);
    }

    /// <summary>
    /// Envoie TOUS les tirages d'une enveloppe en UNE SEULE commande minilab. Renvoie le
    /// handle si le minilab l'a acceptée ; lève <see cref="De100Exception"/> sinon. Chaque
    /// tirage est suivi jusqu'à son issue, remontée par <see cref="JobFinished"/>.
    ///
    /// <b>Une commande porte N images, et ce n'est pas un raccourci.</b> La signature du
    /// SDK le dit : <c>PIF_Print</c> prend le handle de commande EN PARAMÈTRE, et
    /// <c>PIF_GetPrintInfo(handle, index, …)</c> relit les tirages d'une commande PAR
    /// INDICE. C'est aussi ce que fait le pilote de DiLand, sur les 9 336 tirages de son
    /// journal.
    ///
    /// Studio ouvrait au contraire une commande PAR PHOTO — <c>PIF_StartOrder</c> →
    /// <c>PIF_Print</c> → <c>PIF_EndOrder</c>, quatre fois en une seconde sur la commande
    /// 04-007 du 04/08/2026. Les quatre handles sont revenus <c>Ok</c> ; deux tirages sur
    /// quatre ne sont jamais sortis, sans erreur ni trace. Rien ne garantit ce
    /// va-et-vient, et c'est le seul candidat sérieux à une perte silencieuse.
    ///
    /// Conséquence à connaître : <b>une commande part entière ou pas du tout</b>. Un refus
    /// sur la troisième photo annule les deux premières — c'est voulu, une demi-commande
    /// ouverte côté minilab est exactement le genre d'ordre fantôme qui bloque sa file.
    /// </summary>
    public string Submit(IReadOnlyList<De100PrintJob> jobs, char machineId)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
            throw new ArgumentException("Aucun tirage à envoyer.", nameof(jobs));

        foreach (var job in jobs)
            if (!File.Exists(job.ImagePath))
                throw new FileNotFoundException("Image à tirer introuvable.", job.ImagePath);

        // PIF_StartOrder → PIF_Print × N → PIF_EndOrder forment une transaction non réentrante
        lock (_sendSync)
        {
            var orderHandle = new StringBuilder(OrderHandleCapacity);
            var start = (PifResult)De100Interop.PIF_StartOrder(orderHandle);
            if (start != PifResult.Ok)
                throw new De100Exception($"Ouverture de commande refusée par le minilab ({start}).", start);

            // un seul identifiant de commande pour toutes les images : c'est LA commande
            // que le minilab suivra, et c'est sous lui qu'il rendra son verdict
            var orderId = NextOrderId();

            for (var i = 0; i < jobs.Count; i++)
            {
                var parameters = BuildParameters(jobs[i], machineId, orderId);
                var imageData = new ST_IMAGE_DATA
                {
                    srcRGB = IntPtr.Zero,
                    pxImageWidth = 0,
                    pxImageHeight = 0,
                    imagePath = jobs[i].ImagePath,
                };

                var print = (PifResult)De100Interop.PIF_Print(
                    orderHandle, ref imageData, parameters, (uint)parameters.Length);
                if (print == PifResult.Ok) continue;

                De100Interop.PIF_CancelOrder(orderHandle);
                throw new De100Exception(
                    $"Envoi du tirage {i + 1}/{jobs.Count} refusé par le minilab (PIF_Print={print}). " +
                    "La commande entière a été annulée.", print);
            }

            var end = (PifResult)De100Interop.PIF_EndOrder(orderHandle);
            if (end != PifResult.Ok)
            {
                // la commande est ouverte mais inexploitable : on l'annule pour ne pas
                // laisser d'ordre fantôme dans la file du minilab
                De100Interop.PIF_CancelOrder(orderHandle);
                throw new De100Exception(
                    $"Clôture de la commande refusée par le minilab (PIF_EndOrder={end}).", end);
            }

            var ohnd = orderHandle.ToString();
            _tracker.Track([.. jobs.Select(j => j.JobId)], ohnd, DateTimeOffset.Now);
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

        // un callback vaut pour toute la commande, donc pour chacune de ses photos
        foreach (var result in _tracker.Report(order.ohnd, (De100OrderStatus)order.status, DateTimeOffset.Now))
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
        (uint)Math.Max(0, ParseNumber(ReadValue(handle, name)));

    private static int ReadIndexedInt(IntPtr handle, string name, uint index) =>
        (int)ParseNumber(ReadIndexedValue(handle, name, index));

    private static double ReadIndexedDouble(IntPtr handle, string name, uint index) =>
        ParseNumber(ReadIndexedValue(handle, name, index));

    /// <summary>
    /// Analyse un nombre renvoyé par le SDK.
    ///
    /// Le SDK suit le séparateur décimal du système : sur un poste français il renvoie
    /// « 152,0 » et non « 152.0 ». Analyser en culture invariante rendait donc zéro sur
    /// toutes les valeurs décimales — largeur de papier et longueur restante en tête —
    /// alors que les entiers, eux, passaient. On accepte les deux écritures.
    /// </summary>
    internal static double ParseNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;

        var texte = value.Trim();
        if (double.TryParse(texte, NumberStyles.Float, CultureInfo.CurrentCulture, out var courant))
            return courant;
        if (double.TryParse(texte, NumberStyles.Float, CultureInfo.InvariantCulture, out var invariant))
            return invariant;

        // dernier recours : normaliser le séparateur quel qu'il soit
        return double.TryParse(texte.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var normalise)
            ? normalise
            : 0;
    }

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
