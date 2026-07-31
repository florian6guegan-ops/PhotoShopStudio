using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// Aiguillage entre le circuit automatique (spouleur Windows) et le circuit manuel
/// (fichiers repris dans Photoshop pour l'Epson SC-P800). Ces vérifications se font
/// avant tout rendu : elles n'ont besoin ni d'image ni d'imprimante.
/// </summary>
public class ManualPrintRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioManual-" + Guid.NewGuid().ToString("N"));

    public ManualPrintRoutingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private static Product Automatique() => new()
    {
        Code = "10x15",
        Name = "10x15",
        WidthMm = 102,
        HeightMm = 152,
        PrinterName = "DP-DS620",
        Price = 0.60m,
    };

    private static Product Manuel() => new()
    {
        Code = "50x70",
        Name = "50x70",
        WidthMm = 500,
        HeightMm = 700,
        PrinterName = "",
        PrinterChannel = "Agrandissements (Photoshop)",
        Output = ProductOutput.ManualFile,
        Price = 24.90m,
    };

    private PrintOrchestrator Orchestrateur(params Product[] produits)
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var catalogDir = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogDir);
        return new PrintOrchestrator(new ProductCatalog(produits), store, catalogDir);
    }

    private static Order CommandeAvec(params string[] codesProduits)
    {
        var envelope = new Envelope { Number = 1, PrinterChannel = "test" };
        foreach (var code in codesProduits)
        {
            envelope.Lines.Add(new OrderLine
            {
                ProductCode = code,
                UnitPrice = 1m,
                Items = { new OrderItem { FileName = "001.jpg", OriginalName = "IMG.jpg", Quantity = 1 } },
            });
        }
        return new Order { DailyNumber = 1, Source = "Test", Envelopes = { envelope } };
    }

    /// <summary>
    /// Le cas dangereux : si les deux circuits cohabitaient dans une enveloppe, soit les
    /// grands formats partiraient au spouleur, soit les petits ne seraient jamais imprimés.
    /// Mieux vaut refuser franchement.
    /// </summary>
    [Fact]
    public void Une_enveloppe_ne_peut_pas_melanger_les_deux_circuits()
    {
        var orchestrateur = Orchestrateur(Automatique(), Manuel());
        var commande = CommandeAvec("10x15", "50x70");

        var ex = Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Contains("circuits", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("canaux d'impression distincts", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Le refus doit tomber avant le rendu : rien ne doit avoir été écrit sur le disque
    /// ni marqué comme spoulé.
    /// </summary>
    [Fact]
    public void Le_refus_intervient_avant_tout_rendu()
    {
        var orchestrateur = Orchestrateur(Automatique(), Manuel());
        var commande = CommandeAvec("10x15", "50x70");

        Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Equal(EnvelopeStatus.Pending, commande.Envelopes[0].Status);
    }

    [Fact]
    public void Une_enveloppe_entierement_manuelle_est_acceptee()
    {
        var orchestrateur = Orchestrateur(Manuel());
        var commande = CommandeAvec("50x70");

        // le rendu échouera faute de photo réelle, mais surtout PAS sur le contrôle de mixité
        var ex = Record.Exception(() => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        if (ex is InvalidOperationException invalide)
            Assert.DoesNotContain("circuits", invalide.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Un_produit_manuel_ne_designe_aucune_file_Windows()
    {
        var produit = Manuel();

        Assert.Equal(ProductOutput.ManualFile, produit.Output);
        Assert.Empty(produit.PrinterName);
        Assert.Equal("Agrandissements (Photoshop)", produit.Channel);
    }

    [Fact]
    public void Aucune_enveloppe_n_attend_d_impression_manuelle_au_depart()
    {
        var orchestrateur = Orchestrateur(Manuel());
        var commande = CommandeAvec("50x70");

        Assert.Empty(orchestrateur.FindEnvelopesAwaitingManualPrint([commande]));
    }
}
