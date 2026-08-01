using System.Text.Json;
using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// Le journal des commandes de bornes : ce qui reste à faire, et ce qui a été fait.
///
/// Ce que la boutique demande : une commande reste affichée tant que le tirage n'est pas
/// sorti, puis bascule dans un historique consultable pendant un mois — et cet historique
/// ne doit dépendre ni de DiLand, ni de la mémoire de l'application.
/// </summary>
public class KioskOrderJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "KioskJournal-" + Guid.NewGuid().ToString("N"));

    public KioskOrderJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string Fichier => Path.Combine(_root, "bornes.json");

    private KioskOrderJournal Journal() => new(Fichier);

    // — cycle de vie —

    [Fact]
    public void Une_commande_inconnue_est_a_traiter()
    {
        Assert.False(Journal().IsClosed(42));
        Assert.Null(Journal().Find(42));
    }

    [Fact]
    public void Une_commande_en_cours_reste_a_traiter()
    {
        var journal = Journal();

        journal.MarkInProgress(42);

        Assert.False(journal.IsClosed(42));
        Assert.Equal(KioskOrderStage.InProgress, journal.Find(42)!.Stage);
        Assert.Empty(journal.History());
    }

    [Fact]
    public void Le_tirage_ferme_la_commande()
    {
        var journal = Journal();
        journal.MarkInProgress(42);

        journal.MarkPrinted(42);

        Assert.True(journal.IsClosed(42));
        Assert.Equal(42, journal.History().Single().Oid);
    }

    /// <summary>Le journal est sur disque : ce qui a été tiré le reste après redémarrage.</summary>
    [Fact]
    public void Ce_qui_est_tire_le_reste_apres_redemarrage()
    {
        Journal().MarkPrinted(42);

        Assert.True(Journal().IsClosed(42));
    }

    /// <summary>
    /// Rouvrir les photos d'une commande déjà tirée — pour vérifier un tirage, par
    /// exemple — ne doit pas la faire réapparaître dans la liste du jour.
    /// </summary>
    [Fact]
    public void Une_commande_tiree_ne_redevient_pas_en_cours()
    {
        var journal = Journal();
        journal.MarkPrinted(42);

        journal.MarkInProgress(42);

        Assert.True(journal.IsClosed(42));
    }

    [Fact]
    public void Une_commande_close_par_erreur_peut_revenir()
    {
        var journal = Journal();
        journal.Dismiss(42);

        journal.Reopen(42);

        Assert.False(journal.IsClosed(42));
        Assert.Empty(journal.History());
    }

    /// <summary>La commande Studio rattachée survit au redémarrage : c'est elle qui ferme la boucle.</summary>
    [Fact]
    public void La_commande_Studio_rattachee_est_conservee()
    {
        var id = Guid.NewGuid();

        Journal().AttachStudioOrder(42, id);

        Assert.Equal(id, Journal().Find(42)!.StudioOrderId);
    }

    // — historique —

    /// <summary>Le contenu est figé au journal : DiLand peut purger sa base sans nous gêner.</summary>
    [Fact]
    public void L_historique_garde_ce_qu_il_faut_afficher()
    {
        var journal = Journal();
        journal.Describe(42, 12445, "31-006", new DateTime(2026, 7, 31, 15, 30, 0),
            "FLO TEST", "10x15 × 2", 1.20m);
        journal.MarkPrinted(42);

        var entree = Journal().History().Single();

        Assert.Equal(12445, entree.Number);
        Assert.Equal("FLO TEST", entree.CustomerName);
        Assert.Equal("10x15 × 2", entree.Summary);
        Assert.Equal(1.20m, entree.Total);
    }

    /// <summary>Un mois de conservation : au-delà, le journal s'allège tout seul.</summary>
    [Fact]
    public void L_historique_s_efface_apres_un_mois()
    {
        EcrireJournal(
            Entree(1, KioskOrderStage.Printed, DateTimeOffset.Now.AddDays(-10)),
            Entree(2, KioskOrderStage.Printed, DateTimeOffset.Now.AddDays(-40)));

        var historique = Journal().History();

        Assert.Equal([1L], historique.Select(e => e.Oid));
    }

    /// <summary>Une commande jamais close n'est pas purgée : elle est encore à faire.</summary>
    [Fact]
    public void Une_commande_a_traiter_n_est_jamais_purgee()
    {
        EcrireJournal(Entree(1, KioskOrderStage.InProgress, closedAt: null,
            orderedAt: DateTime.Now.AddDays(-90)));

        Assert.False(Journal().IsClosed(1));
        Assert.NotNull(Journal().Find(1));
    }

    // — reprise de l'ancien registre —

    /// <summary>
    /// Avant le journal, on ne gardait qu'une liste d'OID déjà repris. Ces commandes-là
    /// ont été traitées : les faire remonter d'un coup dans la liste du jour serait pire
    /// que de les archiver.
    /// </summary>
    [Fact]
    public void L_ancien_registre_est_repris_comme_de_l_historique()
    {
        File.WriteAllText(Fichier, "[10,11,12]");

        var journal = Journal();

        Assert.True(journal.IsClosed(10));
        Assert.Equal(3, journal.History().Count);
    }

    [Fact]
    public void Un_journal_illisible_ne_bloque_pas_la_boutique()
    {
        File.WriteAllText(Fichier, "{ ceci n'est pas du JSON");

        var journal = Journal();

        Assert.False(journal.IsClosed(42));
        journal.MarkPrinted(42);
        Assert.True(Journal().IsClosed(42));
    }

    // — outils —

    private static KioskOrderEntry Entree(long oid, KioskOrderStage stage,
        DateTimeOffset? closedAt = null, DateTime? orderedAt = null) => new()
        {
            Oid = oid,
            Number = (int)oid,
            OrderedAt = orderedAt ?? DateTime.Now,
            Stage = stage,
            ClosedAt = closedAt,
        };

    private void EcrireJournal(params KioskOrderEntry[] entrees) =>
        File.WriteAllText(Fichier, JsonSerializer.Serialize(entrees, new JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }));
}
