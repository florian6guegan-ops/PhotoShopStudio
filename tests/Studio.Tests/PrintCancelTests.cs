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
        public List<string> Envoyes { get; } = [];
        public List<string> Rappeles { get; } = [];

        /// <summary>Handles que la machine refuse de rappeler — un tirage déjà sorti.</summary>
        public HashSet<string> Refuse { get; } = [];

        /// <summary>Appelé après chaque envoi, avec le nombre d'envois faits.</summary>
        public Action<int>? ApresEnvoi { get; set; }

        public IReadOnlyList<char> ReadyMachines() => ['A'];
        public De100Surface LoadedSurface(char machineId) => De100Surface.Lustre;
        public int LoadedPaperWidthMm(char machineId) => 152;

        public string Submit(De100PrintJob job, char machineId)
        {
            var handle = $"OH-{Envoyes.Count + 1}";
            Envoyes.Add(handle);
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

    [Fact]
    public void Un_arret_apres_le_premier_envoi_rappelle_ce_qui_est_deja_parti()
    {
        var minilab = new FauxMinilab();
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(4);

        using var arret = new CancellationTokenSource();
        minilab.ApresEnvoi = faits => { if (faits == 2) arret.Cancel(); };

        var ex = Assert.Throws<PrintCanceledException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], ct: arret.Token));

        // deux partis, deux rappelés, les deux derniers jamais envoyés
        Assert.Equal(2, minilab.Envoyes.Count);
        Assert.Equal(minilab.Envoyes, minilab.Rappeles);
        Assert.Contains("rappel", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Une commande que la machine refuse de rappeler — déjà tirée — ne doit pas empêcher
    /// d'annuler les autres. C'est tout l'intérêt : sauver ce qui peut l'être.
    /// </summary>
    [Fact]
    public void Une_commande_qui_resiste_n_empeche_pas_d_annuler_les_autres()
    {
        var minilab = new FauxMinilab();
        minilab.Refuse.Add("OH-1");

        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(4);

        using var arret = new CancellationTokenSource();
        minilab.ApresEnvoi = faits => { if (faits == 3) arret.Cancel(); };

        Assert.Throws<PrintCanceledException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], ct: arret.Token));

        Assert.Equal(3, minilab.Envoyes.Count);
        Assert.Equal(new[] { "OH-2", "OH-3" }, minilab.Rappeles);
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
