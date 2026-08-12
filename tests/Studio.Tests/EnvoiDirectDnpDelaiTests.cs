using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Dnp;
using Studio.Printing.Devices.Fuji;
using Studio.Printing.Devices.Fuji.Bridge;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// Le délai dépassé sur un envoi direct à la DNP, et la règle qu'il impose : <b>on ne se
/// replie pas</b>.
///
/// Ces tests portent la commande 12-012 du 12/08/2026, à Créteil. Trois planches
/// d'identité sur la DS620 ; les deux premières partent en direct ; la troisième arrive
/// pendant que la machine tire les deux autres. Le relais lui accordait dix secondes —
/// celles d'une simple interrogation —, a renoncé, et a répondu « machine muette » SANS
/// interrompre l'appel natif. L'application s'est alors rabattue sur le pilote Windows.
/// Une seconde plus tard, le SDK rendait la main : tirage accepté. La même planche est
/// partie deux fois.
///
/// Deux verrous en découlent, et ils sont indépendants : le délai (pour que le cas
/// n'arrive presque plus) et le non-repli (pour qu'il ne coûte plus une feuille quand il
/// arrive quand même).
/// </summary>
public class EnvoiDirectDnpDelaiTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "StudioDnpDelai-" + Guid.NewGuid().ToString("N"));

    public EnvoiDirectDnpDelaiTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ------------------------------------------------------------------
    // 1. Le délai : « dnp-print » engage la machine, il ne l'interroge pas
    // ------------------------------------------------------------------

    /// <summary>
    /// Le test qui aurait empêché la panne. Un envoi de tirage doit avoir le délai long,
    /// quelle que soit la machine visée.
    /// </summary>
    [Theory]
    [InlineData(De100Commands.DnpPrint)]
    [InlineData(De100Commands.Submit)]
    [InlineData(De100Commands.Cancel)]
    public void Un_envoi_de_tirage_engage_la_machine(string commande) =>
        Assert.True(De100Commands.EngageLaMachine(commande),
            $"« {commande} » envoie du papier : il ne peut pas partager le délai des interrogations.");

    /// <summary>
    /// Et l'inverse compte tout autant : une interrogation qui hériterait des trois
    /// minutes figerait le bandeau des machines pendant tout ce temps.
    /// </summary>
    [Theory]
    [InlineData(De100Commands.DnpSnapshot)]
    [InlineData(De100Commands.ListMachines)]
    [InlineData(De100Commands.IsReady)]
    [InlineData(De100Commands.PrinterInfo)]
    [InlineData(De100Commands.PixelCount)]
    [InlineData(De100Commands.OrderProgress)]
    [InlineData(De100Commands.PendingJobs)]
    [InlineData(De100Commands.Ping)]
    public void Une_interrogation_n_engage_pas_la_machine(string commande) =>
        Assert.False(De100Commands.EngageLaMachine(commande),
            $"« {commande} » ne fait que demander : l'écran attend sa réponse.");

    /// <summary>
    /// La règle doit se voir dans le délai RÉELLEMENT accordé, et pas seulement dans le
    /// prédicat : c'est le client qui a coupé à trente secondes pendant que le relais
    /// coupait à dix.
    /// </summary>
    [Fact]
    public void Le_client_accorde_le_delai_long_a_un_envoi_direct_dnp()
    {
        var court = TimeSpan.FromSeconds(30);
        var client = new De100BridgeClient(court);

        Assert.True(client.DelaiPour(De100Commands.DnpPrint) > court,
            "un envoi direct à la DNP ne doit pas hériter du délai des interrogations");
        Assert.Equal(client.DelaiPour(De100Commands.Submit), client.DelaiPour(De100Commands.DnpPrint));
        Assert.Equal(court, client.DelaiPour(De100Commands.DnpSnapshot));
    }

    // ------------------------------------------------------------------
    // 2. Le non-repli : un délai dépassé ne se rattrape pas par le pilote
    // ------------------------------------------------------------------

    /// <summary>
    /// LE test de la panne. Le relais expire ; l'orchestrateur ne doit pas rendre
    /// « faux » — ce qui ferait imprimer la page par le pilote Windows — mais refuser
    /// net, parce que le tirage est peut-être déjà parti.
    /// </summary>
    [Fact]
    public void Un_delai_depasse_sur_l_envoi_ne_se_replie_PAS_sur_le_pilote()
    {
        var minilab = new FauxMinilabDnp { EnvoiExpire = true };
        var orchestrateur = Orchestrateur(minilab);

        var ex = Assert.Throws<PrintUnconfirmedException>(
            () => orchestrateur.EnvoyerDirectementALaDnp(ProduitDnp(), Page()));

        Assert.Contains("10 s", ex.Message);
        Assert.Equal(1, minilab.EnvoisTentes);
    }

    /// <summary>
    /// La contrepartie, et elle est vitale : un échec FRANC doit toujours se replier. Un
    /// relais absent ou une image introuvable ne doit pas empêcher d'imprimer — le pilote,
    /// lui, répond toujours.
    /// </summary>
    [Fact]
    public void Un_echec_franc_de_l_envoi_se_replie_toujours_sur_le_pilote()
    {
        var minilab = new FauxMinilabDnp { EnvoiEchoue = true };
        var orchestrateur = Orchestrateur(minilab);

        Assert.False(orchestrateur.EnvoyerDirectementALaDnp(ProduitDnp(), Page()),
            "un refus net laisse la page au pilote : on sait que rien n'est parti");
    }

    /// <summary>
    /// Le délai dépassé sur l'INTERROGATION qui précède, lui, se replie : à ce stade
    /// aucune image n'a été remise à la machine. C'est la nuance que la première version du
    /// correctif avait manquée.
    /// </summary>
    [Fact]
    public void Un_delai_depasse_sur_l_interrogation_se_replie_lui()
    {
        var minilab = new FauxMinilabDnp { EtatExpire = true };
        var orchestrateur = Orchestrateur(minilab);

        Assert.False(orchestrateur.EnvoyerDirectementALaDnp(ProduitDnp(), Page()));
        Assert.Equal(0, minilab.EnvoisTentes);
    }

    /// <summary>Le chemin nominal reste le chemin nominal : la machine prend, on ne double pas.</summary>
    [Fact]
    public void Une_machine_qui_repond_prend_le_tirage_sans_passer_par_le_pilote()
    {
        var minilab = new FauxMinilabDnp();
        var orchestrateur = Orchestrateur(minilab);

        Assert.True(orchestrateur.EnvoyerDirectementALaDnp(ProduitDnp(), Page()));
        Assert.Equal(1, minilab.EnvoisTentes);
    }

    // ------------------------------------------------------------------
    // Échafaudage
    // ------------------------------------------------------------------

    /// <summary>DNP factice : une seule machine vue, et des pannes qu'on choisit.</summary>
    private sealed class FauxMinilabDnp : IMinilabPrinter
    {
        /// <summary>Le relais expire sur l'ENVOI — l'appel natif, lui, continue sa vie.</summary>
        public bool EnvoiExpire { get; init; }

        /// <summary>Le relais rejette l'envoi franchement : rien n'est parti.</summary>
        public bool EnvoiEchoue { get; init; }

        /// <summary>Le relais expire sur l'interrogation qui précède l'envoi.</summary>
        public bool EtatExpire { get; init; }

        public int EnvoisTentes { get; private set; }

        public Task<IReadOnlyList<DnpPrinterInfo>> DnpSnapshotAsync()
        {
            if (EtatExpire)
                throw new TimeoutException("Le relais DE100 n'a pas répondu à « dnp-snapshot » en 30 s.");

            return Task.FromResult<IReadOnlyList<DnpPrinterInfo>>(
            [
                new DnpPrinterInfo(
                    PortNumber: 0, SerialNumber: "DS6C98030384", FirmwareVersion: "1.00",
                    Status: new DnpStatus((uint)DnpStatusGroup.Usual), MediaRemaining: 275,
                    MediaInitialCount: 400,
                    MediaSize: DnpMediaSize.Size6x4, MediaClass: DnpMediaClass.Unknown,
                    QueuedPrints: 0, LifetimePrints: 1000),
            ]);
        }

        public Task<int> DnpPrintAsync(string imagePath, int portNumber, int overcoat, int copies)
        {
            EnvoisTentes++;

            if (EnvoiExpire)
                throw new TimeoutException("Le relais DE100 n'a pas répondu à « dnp-print » en 10 s.");

            if (EnvoiEchoue)
                throw new InvalidOperationException("L imprimante a refuse le tirage apres 0 exemplaire(s).");

            return Task.FromResult(copies);
        }

        // Le minilab Fuji ne joue aucun rôle ici.
        public IReadOnlyList<char> ReadyMachines() => [];
        public De100Surface? LoadedSurface(char machineId) => null;
        public int LoadedPaperWidthMm(char machineId) => 0;
        public (uint Width, uint Height) ExpectedPixels(
            char machineId, double widthMm, double heightMm, uint dpi) => (0, 0);
        public string Submit(IReadOnlyList<De100PrintJob> jobs, char machineId) => "";
        public void Cancel(string orderHandle) { }
        public Task<De100OrderProgress?> OrderProgressAsync(string orderHandle) =>
            Task.FromResult<De100OrderProgress?>(null);
    }

    /// <summary>La planche identité de la commande 12-012, telle que le catalogue la porte.</summary>
    private static Product ProduitDnp() => new()
    {
        Code = "ID-FR-6",
        Name = "Photos d'identité - planche 10x15",
        WidthMm = 152,
        HeightMm = 102,
        Dpi = 300,
        PrinterName = "DP-DS620",
        PrinterChannel = "DP-DS620",
        Output = ProductOutput.Printer,
        Price = 10.00m,
    };

    private PrintOrchestrator.RenderedPage Page() => new(
        Path: Path.Combine(_root, "env01-ID-FR-6-003.png"),
        Copies: 1, WidthMm: 152, HeightMm: 102, Product: ProduitDnp(), Finish: null);

    private PrintOrchestrator Orchestrateur(IMinilabPrinter minilab)
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var catalogDir = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogDir);
        return new PrintOrchestrator(new ProductCatalog([ProduitDnp()]), store, catalogDir, minilab);
    }
}
