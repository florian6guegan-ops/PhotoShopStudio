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

    /// <summary>
    /// Handles dont le SDK a déjà dit qu'il ne les connaissait pas.
    ///
    /// Le relevé d'avancement passe toutes les dix secondes : sans cette mémoire, une
    /// commande d'un quart d'heure écrirait cent fois la même ligne et noierait le journal —
    /// or c'est ce journal qui sert à diagnostiquer les postes qu'on ne voit pas.
    /// </summary>
    private readonly HashSet<string> _handlesMuets = new(StringComparer.Ordinal);
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
    /// Où en est UNE commande : combien de ses tirages sont sortis, sur combien.
    ///
    /// <b>C'est le compte que la machine tient elle-même</b>, commande par commande
    /// (<c>ST_ORDER_INFO.printedNum</c>). Studio suivait jusqu'ici le compteur GLOBAL de la
    /// machine — celui des tirages depuis sa mise en service — et lui retranchait sa valeur
    /// de départ : tout ce que la machine sortait par ailleurs venait donc gonfler
    /// l'avancement de la commande en cours, et deux commandes lancées à la suite se
    /// comptaient l'une l'autre. C'est ce champ-ci que lit le pilote de DiLand, dont
    /// l'affichage ne décale pas.
    ///
    /// Null quand le SDK ne sait rien de ce handle : commande déjà purgée de sa file, ou
    /// relais qui vient de redémarrer. L'appelant retombe alors sur les verdicts.
    /// </summary>
    /// <param name="orderHandle">Le handle rendu par <c>Submit</c>.</param>
    public De100OrderProgress? OrderProgress(string orderHandle)
    {
        ArgumentException.ThrowIfNullOrEmpty(orderHandle);

        var info = new ST_ORDER_INFO();
        var handle = new StringBuilder(orderHandle, OrderHandleCapacity);

        // Jamais fatal : ce relevé ne sert qu'à faire avancer une barre. Une commande que
        // le SDK ne reconnaît plus ne doit pas faire échouer un tirage qui, lui, se passe
        // bien.
        //
        // Mais le CODE de refus se dit, une fois par handle. Il ne se disait pas, et c'est
        // ce silence qui a laissé passer un défaut d'encodage pendant toute une journée :
        // « la machine ne dit rien » aurait été « FileIoError » ou « InvalidParameter », et
        // l'on aurait cherché du bon côté tout de suite.
        var code = De100Interop.PIF_GetOrderInfo(handle, ref info);
        if (code != (int)PifResult.Ok)
        {
            if (_handlesMuets.Add(orderHandle))
                Log?.Invoke($"DE100 : la commande « {orderHandle} » est inconnue du SDK " +
                            $"({(PifResult)code}). L'avancement retombera sur les verdicts.");

            return null;
        }

        _handlesMuets.Remove(orderHandle);

        return new De100OrderProgress(
            info.printedNum, info.orderNum, (De100OrderStatus)info.status);
    }

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

    /// <summary>
    /// La machine sait-elle produire ce format ? Réponse SANS rien imprimer.
    ///
    /// <c>PIF_DevGetPixelCount</c> demande à la machine la définition attendue pour un
    /// format donné. Un format qu'elle ne sait pas produire ne rend pas de pixels : c'est
    /// donc un contrôle, et le seul qui interroge la MACHINE plutôt que notre table.
    ///
    /// Elle existe pour le 21×29,7 des commandes 04-015 à 04-029 du 04/08/2026 : accepté à
    /// l'envoi, refusé dix secondes plus tard, sans message ni événement machine — le
    /// profil exact d'un format que la configuration du minilab ne connaît pas.
    /// </summary>
    /// <param name="machineId">Machine visée.</param>
    /// <param name="largeurMm">Largeur du tirage, en millimètres.</param>
    /// <param name="hauteurMm">Hauteur du tirage, en millimètres.</param>
    /// <param name="ppp">Résolution demandée.</param>
    /// <returns>Le verdict du SDK et, s'il accepte, la définition attendue.</returns>
    public (PifResult Result, uint Width, uint Height) FormatAccepte(
        char machineId, double largeurMm, double hauteurMm, uint ppp = 300)
    {
        var taille = new ST_PRINT_SIZE
        {
            mmPaperWidth = largeurMm,
            mmPaperHeight = hauteurMm,
            resolution = ppp,
        };

        uint w = 0, h = 0;
        var resultat = (PifResult)De100Interop.PIF_DevGetPixelCount(machineId, ref taille, ref w, ref h);
        return (resultat, w, h);
    }

    /// <summary>
    /// Lit des propriétés de la machine par leur nom, y compris indexées.
    ///
    /// Outil de DIAGNOSTIC : le SDK n'expose aucune liste des propriétés disponibles, et
    /// celles que le pilote de DiLand utilise ne sont sûrement pas les seules. On tâtonne
    /// donc par noms, ce qui ne coûte rien — une propriété inconnue rend une chaîne vide.
    /// </summary>
    /// <param name="machineId">Machine visée.</param>
    /// <param name="noms">Noms de propriétés à tenter.</param>
    /// <param name="indices">Indices à essayer pour chacune ; 0 = lecture directe.</param>
    /// <returns>Les seules propriétés qui ont rendu quelque chose.</returns>
    public IReadOnlyList<(string Nom, uint Indice, string Valeur)> LireProprietes(
        char machineId, IEnumerable<string> noms, IEnumerable<uint> indices)
    {
        ArgumentNullException.ThrowIfNull(noms);
        ArgumentNullException.ThrowIfNull(indices);

        var handle = IntPtr.Zero;
        Check(De100Interop.PIF_DevGetPrinterInfo(machineId, ref handle), nameof(De100Interop.PIF_DevGetPrinterInfo));

        var trouvees = new List<(string, uint, string)>();
        try
        {
            foreach (var nom in noms)
                foreach (var indice in indices)
                {
                    var valeur = indice == 0 ? ReadValue(handle, nom) : ReadIndexedValue(handle, nom, indice);
                    if (!string.IsNullOrWhiteSpace(valeur)) trouvees.Add((nom, indice, valeur));
                }
        }
        finally
        {
            De100Interop.PHIF_ReleaseHandle(handle);
        }

        return trouvees;
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

    /// <summary>
    /// Tirages qu'on relit au plus dans une commande en échec. Une enveloppe de la boutique
    /// en compte quelques dizaines ; la borne est là pour qu'une valeur aberrante rendue
    /// par le SDK ne fasse pas tourner la boucle indéfiniment.
    /// </summary>
    private const int MaxTiragesRelus = 200;

    /// <summary>
    /// <b>Tout ce callback est protégé.</b> Il est appelé par le natif : une exception qui
    /// remonte jusqu'à lui ne se rattrape nulle part et emporte le processus — donc le
    /// relais, donc le suivi de TOUS les tirages en cours.
    /// </summary>
    private void OnOrderCallback(IntPtr orderInfoPtr)
    {
        if (orderInfoPtr == IntPtr.Zero) return;

        try
        {
            var order = Marshal.PtrToStructure<ST_ORDER_INFO>(orderInfoPtr);
            var statut = (De100OrderStatus)order.status;

            // Le MOTIF du refus, lu à la source. Sans lui, un tirage refusé ne disait que
            // « erreur signalée par le minilab » — et le 21×29,7 des commandes 04-015,
            // 04-020 et 04-027 du 04/08/2026 est resté inexplicable trois fois de suite.
            var motif = statut == De100OrderStatus.Error ? LireLesMotifs(order) : "";

            // un callback vaut pour toute la commande, donc pour chacune de ses photos
            foreach (var result in _tracker.Report(order.ohnd, statut, DateTimeOffset.Now, motif))
                JobFinished?.Invoke(this, result);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"DE100 : callback de commande en défaut — {ex.Message}");
        }
    }

    /// <summary>
    /// Ce que la machine dit d'une commande en échec, tirage par tirage.
    ///
    /// <c>ST_PRINT_INFO.errmsg</c> porte 512 caractères de message, et <c>PIF_GetPrintInfo</c>
    /// les relit par indice. La fonction était déclarée depuis le début et n'était appelée
    /// nulle part : le motif existait, personne n'allait le chercher.
    ///
    /// Les doublons sont écartés : sur une enveloppe de trente tirages refusés pour la même
    /// raison, la répéter trente fois ne dit rien de plus.
    /// </summary>
    private string LireLesMotifs(ST_ORDER_INFO order)
    {
        // Les cotes de ST_ORDER_INFO sont en DIXIÈMES de millimètre : 2100 × 2970 pour un
        // 210 × 297. Les afficher brutes laissait croire à un format de deux mètres.
        Log?.Invoke($"DE100 : commande {order.orderNo} en erreur sur la machine {order.machineID} " +
                    $"— format « {order.printSizeName} », " +
                    $"{order.mmPaperWidth / 10:0.#}×{order.mmPaperHeight / 10:0.#} mm, " +
                    $"{order.printedNum}/{order.orderNum} tirage(s) sortis.");

        // ⚠ On n'appelle PLUS le SDK depuis ce callback.
        //
        // `PIF_GetPrintInfo` y était lu pour récupérer `errmsg`. Deux constats du
        // 04/08/2026 : l'indice 0 rend `BadParam` — le SDK compte à partir de 1, comme
        // pour toutes ses propriétés indexées — et surtout le RELAIS MOURAIT quelques
        // secondes après l'appel, « Pipe is broken », commande 04-041. Rentrer dans le SDK
        // depuis une callback qu'il vient d'émettre ne lui convient pas.
        //
        // Ce qu'on perd : le message de la machine, qui était vide dans tous les cas
        // observés. Ce qu'on garde : le format, les cotes et le compte des sorties, qui
        // ont suffi à trouver la cause — l'image en niveaux de gris.
        return $"la machine n'a pas donné de motif (format demandé : « {order.printSizeName} », " +
               $"{order.printedNum}/{order.orderNum} sortis)";
    }

    /// <summary>Journal optionnel du pilote.</summary>
    public Action<string>? Log { get; set; }

    /// <inheritdoc cref="OnOrderCallback"/>
    private void OnEventCallback(IntPtr eventInfoPtr, uint onOff)
    {
        if (eventInfoPtr == IntPtr.Zero) return;

        try
        {
            var evt = Marshal.PtrToStructure<ST_EVENT_INFO>(eventInfoPtr);

            MachineEvent?.Invoke(this, new De100MachineEvent(
                evt.machineID,
                (De100ErrorLevel)evt.errorLevel,
                evt.errorNo,
                string.Join(' ', new[] { evt.errorString1, evt.errorString2, evt.errorString3 }
                    .Where(s => !string.IsNullOrWhiteSpace(s))),
                IsActive: onOff != 0));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"DE100 : callback d'événement en défaut — {ex.Message}");
        }
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
