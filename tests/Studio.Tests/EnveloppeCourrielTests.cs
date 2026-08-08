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
    /// Relancer sans confirmation est refusé net — c'est la garde d'idempotence qui vaut
    /// pour TOUS les circuits, et elle protège déjà du double clic distrait.
    /// </summary>
    [Fact]
    public void Relancer_sans_confirmation_est_refuse()
    {
        var (orchestrateur, _) = Atelier();
        var commande = Commande();

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Contains("déjà été envoyée", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, Clotures(commande));
    }

    /// <summary>
    /// <b>Le défaut corrigé.</b> C'est par ce chemin-là que le doublon est passé en
    /// boutique : l'opérateur a confirmé, et la garde d'idempotence s'efface devant lui —
    /// à juste titre pour une vraie impression, qu'on veut parfois refaire. Mais une
    /// enveloppe COURRIEL n'a rien à réimprimer : la confirmer une seconde fois ne
    /// renvoyait aucun message et n'ajoutait qu'un événement « printed » en double dans le
    /// journal de la commande. Vu sur la commande 08-002 du 08/08/2026, deux clôtures à
    /// vingt-trois secondes d'écart.
    /// </summary>
    [Fact]
    public void Relancer_avec_confirmation_n_ajoute_pas_une_seconde_cloture()
    {
        var (orchestrateur, _) = Atelier();
        var commande = Commande();

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);
        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], operatorConfirmed: true);
        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0], operatorConfirmed: true);

        Assert.Equal(1, Clotures(commande));
        Assert.Equal(EnvelopeStatus.Printed, commande.Envelopes[0].Status);
        Assert.Equal(OrderStatus.Ready, commande.Status);
    }
}
