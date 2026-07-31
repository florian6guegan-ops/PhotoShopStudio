using Studio.Printing.Devices.Fuji;
using Studio.Printing.Devices.Fuji.Bridge;

namespace Studio.Tests;

/// <summary>
/// Protocole du relais 32 bits vers le minilab DE100. C'est la seule partie du relais
/// vérifiable sans matériel : le reste demande le SDK Fuji et le minilab lui-même.
/// </summary>
public class De100ProtocolTests
{
    [Fact]
    public void Un_message_survit_a_l_aller_retour()
    {
        var original = De100Protocol.Request(De100Commands.ListMachines);

        Assert.True(De100Protocol.TryDecode(De100Protocol.Encode(original), out var relu));
        Assert.Equal(original.Kind, relu.Kind);
        Assert.Equal(original.Id, relu.Id);
        Assert.Equal(original.Name, relu.Name);
    }

    /// <summary>Un message doit tenir sur une ligne : le tube est lu ligne par ligne.</summary>
    [Fact]
    public void Un_message_ne_contient_jamais_de_retour_a_la_ligne()
    {
        var job = new De100PrintJob("J1", @"D:\photos\une photo.jpg", 152, 102, "10x15");
        var encode = De100Protocol.Encode(De100Protocol.Request(De100Commands.Submit,
            new De100SubmitRequest(job, 'A')));

        Assert.DoesNotContain('\n', encode);
        Assert.DoesNotContain('\r', encode);
    }

    [Fact]
    public void Les_requetes_portent_des_identifiants_distincts()
    {
        var a = De100Protocol.Request(De100Commands.Ping);
        var b = De100Protocol.Request(De100Commands.Ping);

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEmpty(a.Id);
    }

    [Fact]
    public void Une_reponse_reprend_l_identifiant_de_la_requete()
    {
        var requete = De100Protocol.Request(De100Commands.IsReady, "A");
        var reponse = De100Protocol.Success(requete, true);

        Assert.Equal(requete.Id, reponse.Id);
        Assert.Equal(requete.Name, reponse.Name);
        Assert.True(reponse.Ok);
        Assert.True(De100Protocol.Payload<bool>(reponse));
    }

    [Fact]
    public void Un_echec_transporte_le_message_d_erreur()
    {
        var requete = De100Protocol.Request(De100Commands.Submit);
        var echec = De100Protocol.Failure(requete, "Le minilab a refusé la commande.");

        Assert.False(echec.Ok);
        Assert.Equal("Le minilab a refusé la commande.", echec.Error);
        Assert.Equal(requete.Id, echec.Id);
    }

    [Fact]
    public void Un_evenement_n_a_pas_d_identifiant_de_correlation()
    {
        var evenement = De100Protocol.Event(De100Events.JobFinished);

        Assert.Equal(De100MessageKind.Event, evenement.Kind);
        Assert.Empty(evenement.Id);
    }

    /// <summary>La demande de tirage doit traverser le tube sans rien perdre.</summary>
    [Fact]
    public void Une_demande_de_tirage_traverse_intacte()
    {
        var job = new De100PrintJob(
            JobId: "cmd42-env1-003",
            ImagePath: @"D:\PhotoStudioData\orders\2026-07-31\42\renders\env01-10x15-003.png",
            WidthMm: 152,
            HeightMm: 102,
            PrintSizeName: "10x15",
            Surface: De100Surface.Lustre,
            Copies: 3,
            HighQuality: true,
            ColorMode: "Standard");

        var message = De100Protocol.Request(De100Commands.Submit, new De100SubmitRequest(job, 'B'));
        Assert.True(De100Protocol.TryDecode(De100Protocol.Encode(message), out var relu));

        var demande = De100Protocol.Payload<De100SubmitRequest>(relu);

        Assert.NotNull(demande);
        Assert.Equal('B', demande.MachineId);
        Assert.Equal(job.JobId, demande.Job.JobId);
        Assert.Equal(job.ImagePath, demande.Job.ImagePath);
        Assert.Equal(De100Surface.Lustre, demande.Job.Surface);
        Assert.Equal(3, demande.Job.Copies);
        Assert.True(demande.Job.HighQuality);
        Assert.Equal(152, demande.Job.WidthMm);
    }

    /// <summary>L'issue d'un tirage remonte par événement : c'est elle qui clôt le suivi.</summary>
    [Fact]
    public void L_issue_d_un_tirage_traverse_intacte()
    {
        var resultat = new De100JobResult("cmd42-env1-003", "OH-77", De100JobOutcome.Failed,
            "erreur signalée par le minilab");

        var message = De100Protocol.Event(De100Events.JobFinished, resultat);
        Assert.True(De100Protocol.TryDecode(De100Protocol.Encode(message), out var relu));

        var relue = De100Protocol.Payload<De100JobResult>(relu);

        Assert.NotNull(relue);
        Assert.Equal(De100JobOutcome.Failed, relue.Outcome);
        Assert.Equal("cmd42-env1-003", relue.JobId);
        Assert.Equal("OH-77", relue.OrderHandle);
    }

    [Fact]
    public void Un_evenement_machine_traverse_intact()
    {
        var evt = new De100MachineEvent('A', De100ErrorLevel.Warning, "W123",
            "Magasin bientôt vide", IsActive: true);

        var message = De100Protocol.Event(De100Events.MachineEvent, evt);
        Assert.True(De100Protocol.TryDecode(De100Protocol.Encode(message), out var relu));

        var relue = De100Protocol.Payload<De100MachineEvent>(relu);

        Assert.NotNull(relue);
        Assert.Equal(De100ErrorLevel.Warning, relue.Level);
        Assert.Equal("Magasin bientôt vide", relue.Message);
        Assert.True(relue.IsActive);
    }

    /// <summary>
    /// Une ligne tronquée ou parasite ne doit jamais faire tomber la boucle de lecture :
    /// on l'ignore et on continue.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pas du json")]
    [InlineData("{\"Kind\":\"request\"")]
    [InlineData("{}")]
    [InlineData("null")]
    public void Une_ligne_illisible_est_rejetee_sans_lever(string? ligne)
    {
        Assert.False(De100Protocol.TryDecode(ligne, out _));
    }

    [Fact]
    public void Une_charge_utile_absente_donne_la_valeur_par_defaut()
    {
        var message = De100Protocol.Request(De100Commands.Ping);

        Assert.Null(De100Protocol.Payload<string>(message));
        Assert.False(De100Protocol.Payload<bool>(message));
    }

    [Fact]
    public void La_liste_des_machines_traverse_intacte()
    {
        var message = De100Protocol.Success(
            De100Protocol.Request(De100Commands.ListMachines),
            new List<char> { 'A', 'B' });

        Assert.True(De100Protocol.TryDecode(De100Protocol.Encode(message), out var relu));
        var machines = De100Protocol.Payload<List<char>>(relu);

        Assert.Equal(['A', 'B'], machines);
    }

    [Fact]
    public void Le_nom_du_tube_est_fixe()
    {
        // client et relais doivent viser le même tube : la constante est partagée
        Assert.Equal("studio-photo-de100", De100Protocol.PipeName);
    }
}
