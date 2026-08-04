using ImageMagick;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// L'arrêt d'une commande en cours d'impression.
///
/// C'est le geste que DiLand ne sait pas faire : chez lui, une commande partie ne se
/// reprend qu'en allant vider la file SUR le minilab. Le SDK sait pourtant annuler, et
/// c'est ce qui est vérifié ici — avec la contrainte qui compte : une commande arrêtée ne
/// doit JAMAIS ressortir « à réimprimer » au prochain démarrage, sans quoi on retombe
/// dans les tempêtes de renvois.
/// </summary>
public class PrintCancelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioCancel-" + Guid.NewGuid().ToString("N"));

    public PrintCancelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    /// <summary>Minilab factice : enregistre les envois, et ce qu'on lui rappelle.</summary>
    private sealed class FauxMinilab : IMinilabPrinter
    {
        /// <summary>Handles des commandes envoyées — UNE par enveloppe depuis le 04/08/2026.</summary>
        public List<string> Envoyes { get; } = [];

        /// <summary>Photos reçues, toutes commandes confondues.</summary>
        public List<De100PrintJob> Tirages { get; } = [];

        public List<string> Rappeles { get; } = [];

        /// <summary>Handles que la machine refuse de rappeler — un tirage déjà sorti.</summary>
        public HashSet<string> Refuse { get; } = [];

        /// <summary>Appelé pendant l'envoi, avec le nombre de commandes envoyées.</summary>
        public Action<int>? ApresEnvoi { get; set; }

        public IReadOnlyList<char> ReadyMachines() => ['A'];
        public De100Surface LoadedSurface(char machineId) => De100Surface.Lustre;
        public int LoadedPaperWidthMm(char machineId) => 152;

        public string Submit(IReadOnlyList<De100PrintJob> jobs, char machineId)
        {
            var handle = $"OH-{Envoyes.Count + 1}";
            Envoyes.Add(handle);
            Tirages.AddRange(jobs);
            ApresEnvoi?.Invoke(Envoyes.Count);
            return handle;
        }

        public void Cancel(string orderHandle)
        {
            if (Refuse.Contains(orderHandle))
                throw new InvalidOperationException($"Le minilab refuse d'annuler {orderHandle}.");
            Rappeles.Add(orderHandle);
        }
    }

    /// <summary>Recueille l'avancement sur le fil courant, dans l'ordre où il arrive.</summary>
    private sealed class JournalProgression : IProgress<PrintProgress>
    {
        public List<PrintProgress> Etapes { get; } = [];
        public void Report(PrintProgress value) => Etapes.Add(value);
    }

    private static Product Produit() => new()
    {
        Code = "10x15",
        Name = "10x15",
        WidthMm = 102,
        HeightMm = 152,
        Dpi = 300,
        PrinterName = "",
        PrinterChannel = "Minilab DE100",
        Output = ProductOutput.FujiMinilab,
        Price = 0.60m,
    };

    private OrderFolderStore _store = null!;

    private PrintOrchestrator Orchestrateur(IMinilabPrinter minilab)
    {
        _store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var catalogDir = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogDir);
        return new PrintOrchestrator(new ProductCatalog([Produit()]), _store, catalogDir, minilab);
    }

    /// <summary>Une commande de <paramref name="photos"/> tirages, avec de vrais fichiers à rendre.</summary>
    private Order Commande(int photos)
    {
        var envelope = new Envelope { Number = 1, PrinterChannel = "Minilab DE100" };
        var ligne = new OrderLine { ProductCode = "10x15", UnitPrice = 0.60m };

        var order = new Order { DailyNumber = 1, Source = "Test", Envelopes = { envelope } };
        envelope.Lines.Add(ligne);

        var photosDir = _store.GetPhotosFolder(order);
        Directory.CreateDirectory(photosDir);

        for (var i = 1; i <= photos; i++)
        {
            var nom = $"{i:000}.jpg";
            using (var image = new MagickImage(MagickColors.CadetBlue, 900, 600))
                image.Write(Path.Combine(photosDir, nom));

            ligne.Items.Add(new OrderItem { FileName = nom, OriginalName = nom, Quantity = 1 });
        }

        return order;
    }

    [Fact]
    public void Un_arret_pendant_la_preparation_n_envoie_rien_du_tout()
    {
        var minilab = new FauxMinilab();
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(3);

        using var arret = new CancellationTokenSource();
        arret.Cancel(); // arrêt demandé avant même le premier rendu

        var ex = Assert.Throws<PrintCanceledException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], ct: arret.Token));

        Assert.Empty(minilab.Envoyes);
        Assert.Empty(minilab.Rappeles);
        Assert.Equal(EnvelopeStatus.Canceled, commande.Envelopes[0].Status);
        Assert.Contains("aucun tirage", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Toutes les photos d'une enveloppe forment UNE commande minilab.
    ///
    /// C'est la correction du 04/08/2026 : Studio en ouvrait une par photo — quatre
    /// <c>PIF_StartOrder</c>/<c>PIF_EndOrder</c> en 1,2 s sur la commande 04-007 — et deux
    /// tirages sur quatre ne sont jamais sortis. Le SDK attend l'inverse : <c>PIF_Print</c>
    /// prend le handle en paramètre, et une commande porte N images.
    /// </summary>
    [Fact]
    public void Toute_l_enveloppe_part_en_une_seule_commande_minilab()
    {
        var minilab = new FauxMinilab();
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(4);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.Single(minilab.Envoyes);
        Assert.Equal(4, minilab.Tirages.Count);

        // chaque photo garde son identifiant : c'est par lui que la machine rendra son
        // verdict, photo par photo, sous le handle de la commande
        Assert.Equal(4, minilab.Tirages.Select(t => t.JobId).Distinct().Count());
    }

    /// <summary>
    /// L'arrêt demandé PENDANT l'envoi rappelle la commande — le geste que DiLand ne sait
    /// pas faire. Il n'y a plus qu'un handle à reprendre, mais le pouvoir est le même.
    /// </summary>
    [Fact]
    public void Un_arret_pendant_l_envoi_rappelle_la_commande()
    {
        var minilab = new FauxMinilab();
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(4);

        using var arret = new CancellationTokenSource();
        minilab.ApresEnvoi = _ => arret.Cancel();

        var ex = Assert.Throws<PrintCanceledException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], ct: arret.Token));

        Assert.Equal(minilab.Envoyes, minilab.Rappeles);
        Assert.Contains("rappel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Une commande que la machine refuse de rappeler — déjà tirée — ne doit pas faire
    /// échouer l'arrêt : l'enveloppe est close quand même, et le journal dit ce qui a
    /// résisté. Sans quoi une commande à demi tirée resterait « Spooled » et repartirait
    /// au prochain démarrage.
    /// </summary>
    [Fact]
    public void Une_commande_qui_resiste_au_rappel_ne_fait_pas_echouer_l_arret()
    {
        var minilab = new FauxMinilab();
        minilab.Refuse.Add("OH-1");

        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(4);

        using var arret = new CancellationTokenSource();
        minilab.ApresEnvoi = _ => arret.Cancel();

        Assert.Throws<PrintCanceledException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], ct: arret.Token));

        Assert.Empty(minilab.Rappeles);
        Assert.Equal(EnvelopeStatus.Canceled, commande.Envelopes[0].Status);
        Assert.Empty(orchestrateur.FindEnvelopesNeedingConfirmation([commande]));
    }

    /// <summary>
    /// Le point qui compte le plus : une enveloppe arrêtée ne doit PAS être proposée à la
    /// réimpression au prochain démarrage. Laissée à « Spooled », elle le serait — et
    /// c'est exactement la tempête de renvois qu'on refuse de reproduire.
    /// </summary>
    [Fact]
    public void Une_enveloppe_arretee_n_est_pas_a_confirmer_au_redemarrage()
    {
        var minilab = new FauxMinilab();
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(3);

        using var arret = new CancellationTokenSource();
        minilab.ApresEnvoi = faits => { if (faits == 1) arret.Cancel(); };

        Assert.Throws<PrintCanceledException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], ct: arret.Token));

        Assert.Empty(orchestrateur.FindEnvelopesNeedingConfirmation([commande]));
        Assert.Equal(EnvelopeStatus.Canceled, commande.Envelopes[0].Status);
    }

    [Fact]
    public void L_avancement_est_rapporte_du_rendu_jusqu_a_l_envoi()
    {
        var minilab = new FauxMinilab();
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(3);

        // PAS Progress<T> : sans contexte de synchronisation il rejoue sur le pool, et
        // les rapports arriveraient après la fin du test. Ici on veut l'ordre exact.
        var journal = new JournalProgression();

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], progression: journal);

        var rendu = journal.Etapes.Where(e => e.Etape == PrintProgress.Rendu).ToList();
        var envoi = journal.Etapes.Where(e => e.Etape == PrintProgress.Envoi).ToList();

        Assert.Equal(3, rendu[^1].Faits);
        Assert.Equal(3, rendu[^1].Total);
        Assert.Equal(3, envoi.Count);
        Assert.Equal("A", envoi[^1].Machine);
        Assert.Equal(1, envoi[^1].Fraction);
    }
}
