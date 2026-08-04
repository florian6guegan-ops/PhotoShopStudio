using Studio.Printing.Devices.Fuji;

namespace Studio.Tests;

/// <summary>
/// Ces tests verrouillent la correction du défaut relevé dans le pilote DE100 de DiLand
/// (rétro-ingénierie du 31/07/2026) : seuls les statuts Complete et Canceled y étaient
/// traités, si bien qu'une commande finissant en Error, Hold ou Busy restait « en cours »
/// pour toujours et était renvoyée sans fin.
/// </summary>
public class De100JobTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(30);

    private static De100JobTracker NewTracker() => new(Timeout);

    private static De100JobTracker WithTrackedJob(string jobId = "J1", string handle = "OH-1")
    {
        var tracker = NewTracker();
        tracker.Track(jobId, handle, T0);
        return tracker;
    }

    [Fact]
    public void Complete_marque_le_tirage_imprime_et_cesse_le_suivi()
    {
        var tracker = WithTrackedJob();

        var result = Assert.Single(tracker.Report("OH-1", De100OrderStatus.Complete, T0.AddMinutes(2)));

        Assert.Equal(De100JobOutcome.Printed, result.Outcome);
        Assert.Equal("J1", result.JobId);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void Canceled_cesse_le_suivi()
    {
        var tracker = WithTrackedJob();

        var result = Assert.Single(tracker.Report("OH-1", De100OrderStatus.Canceled, T0.AddMinutes(1)));

        Assert.Equal(De100JobOutcome.Canceled, result.Outcome);
        Assert.Equal(0, tracker.PendingCount);
    }

    /// <summary>Le cas que DiLand laissait filer : la commande doit être close, pas oubliée en file.</summary>
    [Fact]
    public void Error_donne_une_issue_definitive_au_lieu_de_boucler()
    {
        var tracker = WithTrackedJob();

        var result = Assert.Single(tracker.Report("OH-1", De100OrderStatus.Error, T0.AddMinutes(3)));

        Assert.Equal(De100JobOutcome.Failed, result.Outcome);
        Assert.Contains("erreur", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tracker.PendingCount);
    }

    /// <summary>
    /// Le MOTIF de la machine (<c>ST_PRINT_INFO.errmsg</c>) accompagne le statut, il ne le
    /// remplace pas : le statut situe le moment, le motif dit la cause. Sans lui, le
    /// 21×29,7 des commandes 04-015, 04-020 et 04-027 du 04/08/2026 n'a laissé que
    /// « erreur signalée par le minilab », trois fois de suite.
    /// </summary>
    [Fact]
    public void Le_motif_de_la_machine_accompagne_le_statut()
    {
        var tracker = WithTrackedJob();

        var result = Assert.Single(tracker.Report(
            "OH-1", De100OrderStatus.Error, T0.AddMinutes(3),
            motif: "Paper size mismatch. Load the correct paper."));

        Assert.Contains("erreur", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Paper size mismatch", result.Reason, StringComparison.Ordinal);
    }

    /// <summary>Machine muette : on garde le libellé du statut, sans tiret orphelin.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Sans_motif_la_raison_reste_le_seul_statut(string motif)
    {
        var tracker = WithTrackedJob();

        var result = Assert.Single(tracker.Report(
            "OH-1", De100OrderStatus.Error, T0.AddMinutes(3), motif));

        Assert.Equal(De100JobTracker.Describe(De100OrderStatus.Error), result.Reason);
    }

    [Theory]
    [InlineData(De100OrderStatus.PrintWaiting)]
    [InlineData(De100OrderStatus.Printing)]
    [InlineData(De100OrderStatus.ImageProcessWaiting)]
    [InlineData(De100OrderStatus.ImageProcessing)]
    [InlineData(De100OrderStatus.Hold)]
    [InlineData(De100OrderStatus.Busy)]
    public void Les_statuts_non_definitifs_laissent_le_tirage_en_suivi(De100OrderStatus status)
    {
        var tracker = WithTrackedJob();

        Assert.Empty(tracker.Report("OH-1", status, T0.AddMinutes(5)));
        Assert.Equal(1, tracker.PendingCount);
    }

    /// <summary>
    /// Cœur de la protection anti-tempête : un minilab qui répète « Busy » ne doit pas
    /// pouvoir repousser l'échéance. Celle-ci court depuis la soumission, pas depuis
    /// le dernier signe de vie.
    /// </summary>
    [Fact]
    public void Busy_repete_ne_repousse_pas_l_echeance()
    {
        var tracker = WithTrackedJob();

        for (var minute = 1; minute <= 29; minute++)
            Assert.Empty(tracker.Report("OH-1", De100OrderStatus.Busy, T0.AddMinutes(minute)));

        var result = Assert.Single(tracker.Report("OH-1", De100OrderStatus.Busy, T0.AddMinutes(31)));

        Assert.Equal(De100JobOutcome.TimedOut, result.Outcome);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void Une_commande_suspendue_finit_par_expirer()
    {
        var tracker = WithTrackedJob();
        tracker.Report("OH-1", De100OrderStatus.Hold, T0.AddMinutes(1));

        var expired = tracker.SweepTimeouts(T0 + Timeout);

        var result = Assert.Single(expired);
        Assert.Equal(De100JobOutcome.TimedOut, result.Outcome);
        Assert.Contains("suspendue", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void Un_minilab_muet_finit_par_liberer_le_tirage()
    {
        var tracker = WithTrackedJob();

        Assert.Empty(tracker.SweepTimeouts(T0.AddMinutes(29)));

        var expired = tracker.SweepTimeouts(T0.AddMinutes(30));

        Assert.Single(expired);
        Assert.Equal(De100JobOutcome.TimedOut, expired[0].Outcome);
    }

    /// <summary>Une notification tardive ne doit jamais faire réapparaître un tirage déjà clos.</summary>
    [Fact]
    public void Une_notification_apres_issue_est_ignoree()
    {
        var tracker = WithTrackedJob();
        tracker.Report("OH-1", De100OrderStatus.Complete, T0.AddMinutes(2));

        Assert.Empty(tracker.Report("OH-1", De100OrderStatus.Error, T0.AddMinutes(4)));
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void Un_handle_inconnu_ne_cree_pas_de_suivi()
    {
        var tracker = NewTracker();

        Assert.Empty(tracker.Report("jamais-vu", De100OrderStatus.Complete, T0));
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void Un_statut_inconnu_est_tranche_plutot_que_laisse_en_suspens()
    {
        var tracker = WithTrackedJob();

        var result = Assert.Single(tracker.Report("OH-1", (De100OrderStatus)42, T0.AddMinutes(1)));

        Assert.Equal(De100JobOutcome.Failed, result.Outcome);
        Assert.Equal(0, tracker.PendingCount);
    }

    /// <summary>
    /// Une commande minilab porte TOUTES les photos d'une enveloppe depuis le 04/08/2026.
    /// La machine notifie par commande : un seul callback doit donc rendre son verdict à
    /// chacune des photos, sans quoi cinq sur six resteraient sans issue et le compte des
    /// tirages restants ne descendrait jamais.
    /// </summary>
    [Fact]
    public void Un_seul_verdict_de_commande_clot_toutes_ses_photos()
    {
        var tracker = NewTracker();
        tracker.Track(["J1", "J2", "J3"], "OH-1", T0);

        Assert.Equal(3, tracker.PendingCount);
        Assert.Equal(["J1", "J2", "J3"], tracker.PendingJobIds);

        var issues = tracker.Report("OH-1", De100OrderStatus.Complete, T0.AddMinutes(2));

        Assert.Equal(3, issues.Count);
        Assert.All(issues, i => Assert.Equal(De100JobOutcome.Printed, i.Outcome));
        Assert.Equal(["J1", "J2", "J3"], issues.Select(i => i.JobId));
        Assert.Equal(0, tracker.PendingCount);
    }

    /// <summary>Une commande muette expire ENTIÈRE : aucune photo ne reste suivie pour toujours.</summary>
    [Fact]
    public void Une_commande_muette_expire_avec_toutes_ses_photos()
    {
        var tracker = NewTracker();
        tracker.Track(["J1", "J2", "J3"], "OH-1", T0);

        var expirees = tracker.SweepTimeouts(T0 + Timeout);

        Assert.Equal(3, expirees.Count);
        Assert.All(expirees, e => Assert.Equal(De100JobOutcome.TimedOut, e.Outcome));
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void Une_commande_sans_aucun_tirage_est_refusee()
    {
        var tracker = NewTracker();

        Assert.Throws<ArgumentException>(() => tracker.Track([], "OH-1", T0));
    }

    [Fact]
    public void Chaque_tirage_est_suivi_separement()
    {
        var tracker = NewTracker();
        tracker.Track("J1", "OH-1", T0);
        tracker.Track("J2", "OH-2", T0);

        tracker.Report("OH-1", De100OrderStatus.Complete, T0.AddMinutes(1));

        Assert.Equal(1, tracker.PendingCount);
        Assert.Equal(["J2"], tracker.PendingJobIds);
    }

    [Fact]
    public void Forget_retire_le_tirage_sans_produire_de_resultat()
    {
        var tracker = WithTrackedJob();

        Assert.True(tracker.Forget("OH-1"));
        Assert.False(tracker.Forget("OH-1"));
        Assert.Equal(0, tracker.PendingCount);
    }

    [Fact]
    public void Un_delai_nul_ou_negatif_est_refuse()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new De100JobTracker(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new De100JobTracker(TimeSpan.FromMinutes(-1)));
    }
}
