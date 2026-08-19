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
    ///
    /// <b><c>null</c> = surface inconnue</b>, et l'on ne bloque alors rien — exactement
    /// comme le 0 de <see cref="LoadedPaperWidthMm"/>. La distinction n'est pas
    /// théorique : elle valait auparavant « brillant », si bien qu'une machine avare en
    /// informations se déclarait chargée de brillant et faisait refuser toutes les
    /// commandes lustrées d'une boutique. Une machine qui ne sait pas ne doit jamais
    /// empêcher de tirer.
    /// </summary>
    De100Surface? LoadedSurface(char machineId);

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

    /// <summary>
    /// Où en est une commande : combien de SES tirages sont sortis, sur combien.
    ///
    /// <b>C'est le compte de la machine, commande par commande.</b> Le bandeau suivait le
    /// compteur global du minilab — celui des tirages depuis la mise en service — dont il
    /// retranchait la valeur relevée au départ : tout ce que la machine sortait par
    /// ailleurs venait gonfler l'avancement, et deux commandes lancées à la suite se
    /// comptaient l'une l'autre. Le pilote de DiLand lit ce champ-ci, et son affichage ne
    /// décale pas.
    ///
    /// Null quand le relais ne répond pas ou que le SDK ne connaît plus ce handle :
    /// l'appelant retombe alors sur les verdicts, comme avant.
    /// </summary>
    Task<De100OrderProgress?> OrderProgressAsync(string orderHandle);
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

    /// <summary>
    /// LA LISTE DES MACHINES, MÉMORISÉE — sans quoi un poste SANS minilab paie une minute
    /// à chaque fois qu'on la lui demande.
    ///
    /// Mesuré sur kodakidpc (Arcueil), qui n'a qu'une DNP : « list-machines » répond en
    /// <b>61 secondes</b>, trois fois de suite au journal du 14/08/2026. Ce n'est pas un
    /// délai que Studio impose — le relais RÉPOND, au bout d'une minute : c'est le SDK Fuji
    /// qui cherche un minilab qui n'existe pas, et il cherche longtemps.
    ///
    /// Deux durées de mémoire, et l'écart est voulu : une réponse VIDE est celle qui coûte
    /// cher, et c'est aussi celle qui ne changera pas — un poste sans minilab n'en gagne pas
    /// un en cours de journée. Une réponse pleine se rafraîchit vite, parce qu'une machine
    /// éteinte ou rallumée, ça arrive.
    /// </summary>
    private IReadOnlyList<char>? _machinesConnues;
    private DateTime _machinesLues = DateTime.MinValue;

    private static readonly TimeSpan MemoireMachinesPresentes = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MemoireAucuneMachine = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Les machines du relais, sans les redemander tant que la réponse tient encore.
    ///
    /// ⚠ La mémoire est vidée par <see cref="OublierLesMachines"/> dès qu'un tirage part ou
    /// qu'on se reconnecte : c'est là qu'un branchement a pu changer, et c'est le seul
    /// moment où se tromper coûterait quelque chose.
    /// </summary>
    private async Task<IReadOnlyList<char>> MachinesAsync()
    {
        var memoire = _machinesConnues is { Count: 0 }
            ? MemoireAucuneMachine
            : MemoireMachinesPresentes;

        if (_machinesConnues is { } connues && DateTime.UtcNow - _machinesLues < memoire)
            return connues;

        IReadOnlyList<char> lues;
        try
        {
            lues = await _client.ListMachinesAsync();
        }
        catch (Exception ex) when (_machinesConnues is not null)
        {
            // ⚠ UN DÉLAI DÉPASSÉ N'EST PAS UN DÉBRANCHEMENT — la même règle que pour les
            // tirages (voir De100Commands.EngageLaMachine). Le relais accorde dix secondes
            // à « list-machines » ; une machine qui SORT DE VEILLE ou qui imprime les
            // dépasse sans être en panne. L'exception remontait alors jusqu'à l'écran, qui
            // n'avait plus aucune machine à proposer : le 18/08/2026 à 15:24, l'opérateur
            // s'est retrouvé sans choix de rouleau, les deux DE100 allumées à côté de lui.
            //
            // On garde donc ce qu'on savait, en le disant une fois. Rien n'est engagé sur
            // cette liste : c'est le TIRAGE qui vérifie la machine, et lui n'a pas changé.
            Log?.Invoke($"Minilab : liste des machines illisible ({ex.Message}) — on garde " +
                        $"les {_machinesConnues.Count} connue(s).");
            _machinesLues = DateTime.UtcNow;
            return _machinesConnues;
        }

        _machinesConnues = lues;
        _machinesLues = DateTime.UtcNow;

        if (lues.Count == 0)
            Log?.Invoke("Minilab : aucune machine sur ce poste — on ne redemandera pas " +
                        $"avant {MemoireAucuneMachine.TotalMinutes:0} minutes.");

        return lues;
    }

    /// <summary>Vide la mémoire des machines : un branchement a pu changer.</summary>
    private void OublierLesMachines()
    {
        _machinesConnues = null;
        _machinesLues = DateTime.MinValue;
    }
    private bool _connected;

    /// <summary>La cause d'un avancement illisible n'est dite qu'une fois — voir OrderProgressAsync.</summary>
    private bool _progressionMuette;

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

    /// <summary>
    /// Garantit une liaison vivante avec le relais, et REMET LA LIAISON DANS L'ÉTAT OÙ ELLE
    /// ÉTAIT — c'est cette seconde moitié qui compte.
    ///
    /// <b>Toute méthode qui parle au relais doit passer par ici</b>, DNP comprises. Une
    /// méthode qui se contente de « si pas connecté, connecte » rétablit le tube mais laisse
    /// <see cref="_subscribed"/> croire que l'abonnement tient encore : le relais est neuf,
    /// il n'a plus aucun abonnement, et plus un seul tirage ne reçoit son verdict — en
    /// silence, jusqu'à la fin de la session. C'est le défaut du 04/08/2026, qu'on peut
    /// rouvrir sans y penser en ajoutant une méthode qui court-circuite ce garde.
    ///
    /// <b>Le réabonnement est immédiat, plus paresseux.</b> Il attendait le prochain envoi :
    /// entre la mort du relais et le tirage suivant, les travaux DÉJÀ EN MACHINE perdaient
    /// leur issue sans que rien ne le dise. C'est ce qui a laissé la commande 06-021 de
    /// 61 tirages « non confirmée » le 06/08/2026 à 16:09. On se réabonne donc aux machines
    /// qu'on suivait, dans la seconde qui suit la reconnexion.
    /// </summary>
    private void EnsureConnected()
    {
        char[] aReabonner;

        lock (_sync)
        {
            if (_connected && _client.IsConnected) return;

            // Le relais est neuf : ce qu'on lui avait demandé ne tient plus. La liste des
            // machines non plus — c'est justement au rebranchement qu'elle peut changer.
            aReabonner = [.. _subscribed];
            _subscribed.Clear();
            OublierLesMachines();

            _client.ConnectAsync().GetAwaiter().GetResult();
            _connected = true;
        }

        // Hors du verrou : un appel au relais sous verrou bloquerait tout le reste de
        // l'application si la machine tardait à répondre.
        foreach (var machine in aReabonner) Reabonner(machine);
    }

    /// <summary>
    /// Réabonne une machine après une coupure, sans jamais faire échouer l'appel en cours.
    ///
    /// Un réabonnement qui échoue est une mauvaise nouvelle, pas une raison de refuser le
    /// tirage qu'on était en train de préparer : il sera retenté au prochain envoi, et le
    /// journal garde la trace.
    /// </summary>
    private void Reabonner(char machineId)
    {
        try
        {
            _client.SubscribeAsync(machineId).GetAwaiter().GetResult();
            lock (_sync) _subscribed.Add(machineId);
            Log?.Invoke($"Relais retrouvé : réabonné aux notifications de la machine « {machineId} ».");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Réabonnement de la machine « {machineId} » impossible : {ex.Message}");
        }
    }

    public IReadOnlyList<char> ReadyMachines()
    {
        EnsureConnected();

        var ready = new List<char>();
        foreach (var machine in MachinesAsync().GetAwaiter().GetResult())
        {
            // une machine hors ligne se déclare parfois « prête » : on vérifie son état
            var info = _client.GetPrinterInfoAsync(machine).GetAwaiter().GetResult();
            if (info is null || info.Status is De100PrinterStatus.Offline) continue;
            ready.Add(machine);
        }
        return ready;
    }

    public De100Surface? LoadedSurface(char machineId)
    {
        EnsureConnected();

        var info = _client.GetPrinterInfoAsync(machineId).GetAwaiter().GetResult();
        return info?.Media?.Surface;
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

        var machines = await MachinesAsync();
        var etats = new List<De100PrinterInfo>();
        foreach (var machine in machines)
        {
            var info = await _client.GetPrinterInfoAsync(machine);
            if (info is not null) etats.Add(info);
        }

        if (etats.Count > 0) DernierInstantane = (etats, DateTime.Now);

        return etats;
    }

    /// <summary>
    /// Le dernier instantané qui a ABOUTI, et quand.
    ///
    /// <b>À n'employer qu'en repli, et en le disant à l'écran.</b> Ce n'est pas l'état de la
    /// machine : c'est ce qu'elle disait la dernière fois. Le papier a pu être changé depuis.
    ///
    /// Il existe parce qu'un écran privé de réponse n'a pas à devenir inutilisable :
    /// l'opérateur qui choisit sur quelle machine tirer doit pouvoir le faire même quand le
    /// SDK met dix secondes de trop à répondre (18/08/2026, voir <see cref="MachinesAsync"/>).
    /// Rien ne s'engage sur ces valeurs — le tirage, lui, revérifie tout.
    /// </summary>
    public (IReadOnlyList<De100PrinterInfo> Etats, DateTime Quand)? DernierInstantane
    {
        get;
        private set;
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

    /// <inheritdoc/>
    public async Task<De100OrderProgress?> OrderProgressAsync(string orderHandle)
    {
        if (string.IsNullOrEmpty(orderHandle)) return null;

        try
        {
            if (!_client.IsConnected) await _client.ConnectAsync();
            return await _client.OrderProgressAsync(orderHandle);
        }
        catch (Exception ex)
        {
            // Jamais fatal : l'appelant voit le null et retombe sur les verdicts.
            //
            // Mais PLUS JAMAIS muet non plus. Ce relevé revient toutes les dix secondes, et
            // pour ne pas noyer le journal on avalait tout — y compris la raison pour
            // laquelle il ne marchait pas du tout. La première cause est dite, une fois,
            // puis on se tait : c'est ce qui manquait pour comprendre pourquoi la commande
            // 11-029 du 11/08/2026 n'a jamais fait bouger le compteur.
            if (!_progressionMuette)
            {
                _progressionMuette = true;
                Log?.Invoke($"DE100 : avancement de « {orderHandle} » illisible — {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>
    /// État des imprimantes DNP, vues par le même relais.
    ///
    /// Passe par <see cref="EnsureConnected"/> comme tout le reste : c'est l'appel LE PLUS
    /// FRÉQUENT de l'application — le bandeau d'état le lance en boucle — donc celui qui
    /// ressuscite le relais neuf fois sur dix. S'il rétablissait le tube sans rétablir les
    /// abonnements, il ferait taire le minilab à chaque coupure.
    /// </summary>
    public Task<IReadOnlyList<Dnp.DnpPrinterInfo>> DnpSnapshotAsync()
    {
        EnsureConnected();
        return _client.DnpSnapshotAsync();
    }

    /// <summary>
    /// Tire sur une DNP sans passer par le pilote Windows, et rend le nombre d'exemplaires
    /// acceptés par la machine.
    ///
    /// Passe par le relais parce que le SDK des DNP est en 32 bits, comme celui du minilab.
    /// </summary>
    public Task<int> DnpPrintAsync(string imagePath, int portNumber, int overcoat, int copies)
    {
        EnsureConnected();
        return _client.DnpPrintAsync(imagePath, portNumber, overcoat, copies);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
