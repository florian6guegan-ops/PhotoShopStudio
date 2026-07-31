using Studio.Printing.Devices.Dnp;

namespace Studio.Tests;

/// <summary>
/// Décodage des états renvoyés par <c>cspstat.dll</c> (SDK DNP), relevés par
/// rétro-ingénierie de DiLand le 31/07/2026.
/// </summary>
public class DnpStatusTests
{
    [Fact]
    public void Idle_est_pret()
    {
        var status = new DnpStatus(DnpStatus.Codes.UsualIdle);

        Assert.True(status.IsReady);
        Assert.False(status.IsBusy);
        Assert.False(status.NeedsOperator);
        Assert.False(status.IsFault);
        Assert.Equal(DnpStatusGroup.Usual, status.Group);
    }

    [Theory]
    [InlineData(DnpStatus.Codes.UsualPrinting)]
    [InlineData(DnpStatus.Codes.UsualCooling)]
    [InlineData(DnpStatus.Codes.UsualMotorCooling)]
    public void Les_etats_transitoires_sont_occupes_mais_pas_bloquants(uint raw)
    {
        var status = new DnpStatus(raw);

        Assert.True(status.IsBusy);
        Assert.False(status.IsReady);
        Assert.False(status.NeedsOperator);
    }

    [Theory]
    [InlineData(DnpStatus.Codes.UsualPaperEnd)]
    [InlineData(DnpStatus.Codes.UsualRibbonEnd)]
    [InlineData(DnpStatus.Codes.SettingCoverOpen)]
    [InlineData(DnpStatus.Codes.SettingPaperJam)]
    [InlineData(DnpStatus.Codes.SettingScrapBoxError)]
    public void Les_consommables_et_le_capot_appellent_l_operateur(uint raw)
    {
        var status = new DnpStatus(raw);

        Assert.True(status.NeedsOperator);
        Assert.False(status.IsReady);
        Assert.False(status.IsFault);
        Assert.NotEmpty(status.Message);
    }

    [Theory]
    [InlineData(DnpStatus.Codes.HardwareError01)]
    [InlineData(DnpStatus.Codes.HardwareError10)]
    [InlineData(DnpStatus.Codes.SystemError01)]
    public void Les_pannes_relevent_du_SAV(uint raw)
    {
        var status = new DnpStatus(raw);

        Assert.True(status.IsFault);
        Assert.False(status.IsReady);
    }

    [Fact]
    public void Le_timeout_est_une_defaillance_de_communication()
    {
        var status = new DnpStatus(DnpStatus.Codes.CommunicationTimeout);

        Assert.True(status.IsTimeout);
        Assert.True(status.IsCommunicationFailure);
        Assert.False(status.IsReady);
        Assert.Contains("ne répond pas", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Une_imprimante_injoignable_n_est_jamais_prete()
    {
        var status = new DnpStatus(0x80001234u);

        Assert.True(status.IsCommunicationFailure);
        Assert.False(status.IsReady);
        Assert.Equal(DnpStatusGroup.Unknown, status.Group);
    }

    [Fact]
    public void La_mise_a_jour_du_micrologiciel_est_reconnue()
    {
        var status = new DnpStatus(DnpStatus.Codes.FirmwareWriting);

        Assert.Equal(DnpStatusGroup.FirmwareUpdate, status.Group);
        Assert.Contains("ne pas éteindre", status.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Le_pourcentage_de_rouleau_restant_est_calcule()
    {
        var info = MakeInfo(mediaRemaining: 100, initial: 400);

        Assert.Equal(25.0, info.MediaRemainingPercent);
    }

    [Fact]
    public void Le_pourcentage_est_absent_si_la_capacite_initiale_est_inconnue()
    {
        var info = MakeInfo(mediaRemaining: 100, initial: 0);

        Assert.Null(info.MediaRemainingPercent);
    }

    private static DnpPrinterInfo MakeInfo(int mediaRemaining, int initial) => new(
        PortNumber: 1,
        SerialNumber: "SN123",
        FirmwareVersion: "1.21",
        Status: new DnpStatus(DnpStatus.Codes.UsualIdle),
        MediaRemaining: mediaRemaining,
        MediaInitialCount: initial,
        MediaSize: DnpMediaSize.Size6x4,
        MediaClass: DnpMediaClass.Rx,
        QueuedPrints: 0,
        LifetimePrints: 12345);
}
