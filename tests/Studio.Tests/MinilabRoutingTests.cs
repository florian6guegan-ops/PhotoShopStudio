using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// Aiguillage des enveloppes vers le minilab Fuji. Ces vérifications ont lieu avant tout
/// rendu et sans minilab réel : elles portent sur les refus et le choix de machine, là où
/// une erreur enverrait des tirages sur la mauvaise machine ou dans le vide.
/// </summary>
public class MinilabRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioMinilab-" + Guid.NewGuid().ToString("N"));

    public MinilabRoutingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    /// <summary>Minilab factice : enregistre ce qu'on lui envoie, n'imprime rien.</summary>
    private sealed class FauxMinilab(params char[] ready) : IMinilabPrinter
    {
        public List<(De100PrintJob Job, char Machine)> Submitted { get; } = [];

        public IReadOnlyList<char> ReadyMachines() => ready;

        public De100Surface LoadedSurface(char machineId) => De100Surface.Lustre;

        public string Submit(De100PrintJob job, char machineId)
        {
            Submitted.Add((job, machineId));
            return $"OH-{Submitted.Count}";
        }
    }

    private static Product Minilab(string code = "10x15", string? machine = null) => new()
    {
        Code = code,
        Name = code,
        WidthMm = 102,
        HeightMm = 152,
        PrinterName = "",
        PrinterChannel = "Minilab DE100",
        Output = ProductOutput.FujiMinilab,
        MinilabMachineId = machine,
        Price = 0.60m,
    };

    private static Product Spouleur() => new()
    {
        Code = "10x15-dnp",
        Name = "10x15",
        WidthMm = 105,
        HeightMm = 156,
        PrinterName = "DP-DS620",
        Price = 0.60m,
    };

    private PrintOrchestrator Orchestrateur(IMinilabPrinter? minilab, params Product[] produits)
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var catalogDir = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogDir);
        return new PrintOrchestrator(new ProductCatalog(produits), store, catalogDir, minilab);
    }

    private static Order CommandeAvec(params string[] codes)
    {
        var envelope = new Envelope { Number = 1, PrinterChannel = "test" };
        foreach (var code in codes)
        {
            envelope.Lines.Add(new OrderLine
            {
                ProductCode = code,
                UnitPrice = 0.60m,
                Items = { new OrderItem { FileName = "001.jpg", OriginalName = "IMG.jpg", Quantity = 1 } },
            });
        }
        return new Order { DailyNumber = 1, Source = "Test", Envelopes = { envelope } };
    }

    // — refus —

    /// <summary>
    /// Sans relais, un tirage minilab doit être refusé net. Le renvoyer vers le spouleur
    /// le ferait disparaître dans le port « nul » en annonçant un succès imaginaire.
    /// </summary>
    [Fact]
    public void Sans_relais_le_tirage_minilab_est_refuse()
    {
        var orchestrateur = Orchestrateur(minilab: null, Minilab());
        var commande = CommandeAvec("10x15");

        var ex = Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Contains("relais", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EnvelopeStatus.Pending, commande.Envelopes[0].Status);
    }

    [Fact]
    public void Une_enveloppe_ne_peut_pas_melanger_minilab_et_spouleur()
    {
        var orchestrateur = Orchestrateur(new FauxMinilab('A'), Minilab(), Spouleur());
        var commande = CommandeAvec("10x15", "10x15-dnp");

        var ex = Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Contains("circuits", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Le refus doit tomber avant le rendu : rien ne doit être marqué envoyé.</summary>
    [Fact]
    public void Le_refus_intervient_avant_tout_rendu()
    {
        var orchestrateur = Orchestrateur(minilab: null, Minilab());
        var commande = CommandeAvec("10x15");

        Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Equal(EnvelopeStatus.Pending, commande.Envelopes[0].Status);
    }

    // — choix de la machine —

    [Fact]
    public void Sans_machine_prete_rien_n_est_envoye()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PrintOrchestrator.ChooseMachine([], requested: null));

        Assert.Contains("prête", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void La_machine_demandee_est_retenue_si_elle_est_prete()
    {
        Assert.Equal('B', PrintOrchestrator.ChooseMachine(['A', 'B'], requested: "B"));
    }

    /// <summary>
    /// Le DE100 de la boutique compte deux machines dont une souvent hors ligne : une
    /// machine demandée mais absente ne doit pas bloquer le tirage.
    /// </summary>
    [Fact]
    public void Une_machine_demandee_mais_hors_ligne_ne_bloque_pas()
    {
        Assert.Equal('B', PrintOrchestrator.ChooseMachine(['B'], requested: "A"));
    }

    [Fact]
    public void Sans_machine_demandee_on_prend_la_premiere_prete()
    {
        Assert.Equal('A', PrintOrchestrator.ChooseMachine(['A', 'B'], requested: null));
        Assert.Equal('A', PrintOrchestrator.ChooseMachine(['A', 'B'], requested: ""));
    }

    // — modèle —

    [Fact]
    public void Un_produit_minilab_ne_designe_aucune_file_Windows()
    {
        var produit = Minilab();

        Assert.Equal(ProductOutput.FujiMinilab, produit.Output);
        Assert.Empty(produit.PrinterName);
        Assert.Equal("Minilab DE100", produit.Channel);
    }

    /// <summary>
    /// Le minilab n'accepte pas les noms commerciaux : il attend « 152x102 », grand côté
    /// en premier. Relevé dans les journaux de DiLand le 31/07/2026.
    /// </summary>
    [Fact]
    public void Le_nom_de_format_minilab_est_en_millimetres_grand_cote_en_premier()
    {
        Assert.Equal("152x102", PrintOrchestrator.MinilabSizeName(Minilab(), 152, 102));
        Assert.Equal("203x152", PrintOrchestrator.MinilabSizeName(Minilab(), 203, 152));
    }

    [Fact]
    public void Un_produit_peut_imposer_son_propre_libelle_de_format()
    {
        var produit = Minilab();
        produit.MinilabPrintSizeName = "10x15POLA";

        Assert.Equal("10x15POLA", PrintOrchestrator.MinilabSizeName(produit, 152, 102));
    }
}
