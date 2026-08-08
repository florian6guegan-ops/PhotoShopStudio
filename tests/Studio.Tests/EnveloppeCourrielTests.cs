using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// L'enveloppe d'envoi par courriel, qui ne passe par aucune machine.
///
/// <b>Le doublon du 08/08/2026.</b> L'envoi des photos clôt l'enveloppe ; l'opérateur a
/// ensuite cliqué sur « Imprimer » — ce qui est naturel, la commande est sous ses yeux — et
/// le journal de la commande 08-002 a reçu un SECOND événement « printed », identique au
/// premier, vingt-trois secondes après. Rien n'a été réexpédié, mais c'est ce genre de
/// doublon qui rend un historique impossible à relire des mois plus tard.
/// </summary>
public class EnveloppeCourrielTests : IDisposable
{
    private readonly string _racine =
        Path.Combine(Path.GetTempPath(), "StudioCourriel-" + Guid.NewGuid().ToString("N"));

    public EnveloppeCourrielTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private (PrintOrchestrator Orchestrateur, OrderFolderStore Store) Atelier()
    {
        var store = new OrderFolderStore(Path.Combine(_racine, "orders"));
        var catalogDir = Path.Combine(_racine, "catalog");
        Directory.CreateDirectory(catalogDir);

        var produit = MailProduct.Creer();
        var catalogue = new ProductCatalog([produit]);

        return (new PrintOrchestrator(catalogue, store, catalogDir, minilab: null), store);
    }

    private static Order Commande()
    {
        var enveloppe = new Envelope { Number = 1, PrinterChannel = "courriel" };
        enveloppe.Lines.Add(new OrderLine
        {
            ProductCode = MailProduct.Code,
            UnitPrice = MailProduct.PrixParDefaut,
            Items = { new OrderItem { FileName = "001.jpg", OriginalName = "IMG.jpg", Quantity = 1 } },
        });

        return new Order { DailyNumber = 2, Source = "Test", Envelopes = { enveloppe } };
    }

    private int Clotures(Order commande)
    {
        var dossier = Directory.EnumerateDirectories(Path.Combine(_racine, "orders"), "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "order.log")));

        if (dossier is null) return 0;

        return File.ReadAllLines(Path.Combine(dossier, "order.log"))
            .Count(l => l.Contains("\"printed\"", StringComparison.Ordinal));
    }

    [Fact]
    public void L_enveloppe_est_close_sans_rien_imprimer()
    {
        var (orchestrateur, _) = Atelier();
        var commande = Commande();

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.Equal(EnvelopeStatus.Printed, commande.Envelopes[0].Status);
        Assert.Equal(OrderStatus.Ready, commande.Status);
        Assert.Equal(1, Clotures(commande));
    }

    /// <summary>
    /// <b>Le défaut corrigé.</b> Relancer l'impression sur une enveloppe déjà envoyée ne
    /// doit rien réécrire : ni état, ni second événement.
    /// </summary>
    [Fact]
    public void Relancer_l_impression_n_ajoute_pas_une_seconde_cloture()
    {
        var (orchestrateur, _) = Atelier();
        var commande = Commande();

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);
        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);
        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.Equal(1, Clotures(commande));
        Assert.Equal(EnvelopeStatus.Printed, commande.Envelopes[0].Status);
        Assert.Equal(OrderStatus.Ready, commande.Status);
    }
}
