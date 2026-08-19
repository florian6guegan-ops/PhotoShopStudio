using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing.Devices.Fuji;

namespace Studio.Tests;

/// <summary>
/// DEUX COMMANDES QUI VISENT LE MÊME ROULEAU S'ATTENDENT.
///
/// Avant, une commande lancée pendant qu'une autre sortait partait immédiatement : les deux
/// envois se croisaient au minilab, et les tirages tombaient mélangés dans le même bac. La
/// seconde n'apparaissait nulle part tant qu'elle n'avait pas commencé, si bien qu'on la
/// relançait une seconde fois en croyant que le clic n'avait pas pris.
///
/// La file se décide sur ce que la commande DEMANDE, jamais sur la machine qu'elle finira
/// par obtenir : celle-ci ne se connaît qu'après le rendu, et la demander au clic ferait
/// traverser le relais 32 bits sur le fil de l'interface.
///
/// <b>Et ce qu'elle demande tient en une machine.</b> La finition n'est pas une seconde
/// dimension : le brillant est monté sur la DE100, le lustré sur la DE100-2. Demander du
/// brillant, c'est demander la machine A. Les compter séparément faisait passer
/// « brillant » et « machine A » pour deux machines différentes, et la file restait vide.
///
/// <b>Et jamais de détournement.</b> Deux commandes en brillant s'attendent, même si la
/// machine d'à côté est libre : l'opérateur a monté un rouleau, c'est ce rouleau qu'il veut.
/// </summary>
public class FileDImpressionTests
{
    private static ProductCatalog Catalogue() => new(
    [
        new Product
        {
            Code = "MINI10x15", Name = "10×15", Output = ProductOutput.FujiMinilab,
            WidthMm = 102, HeightMm = 152,
        },
        new Product
        {
            Code = "MINI13x18", Name = "13×18", Output = ProductOutput.FujiMinilab,
            WidthMm = 127, HeightMm = 178,
        },
        new Product
        {
            Code = "MINI-B", Name = "10×15 machine B", Output = ProductOutput.FujiMinilab,
            WidthMm = 102, HeightMm = 152, MinilabMachineId = "B",
        },
        new Product
        {
            Code = "DNP10x15", Name = "10×15 DS620", Output = ProductOutput.Printer,
            WidthMm = 102, HeightMm = 152, PrinterName = "DS620",
        },
        new Product
        {
            Code = "GRAND", Name = "40×60", Output = ProductOutput.ManualFile,
            WidthMm = 400, HeightMm = 600,
        },
    ]);

    /// <param name="finition">Ce que le client a demandé ; null au comptoir.</param>
    private static Order Commande(string code, int tirages = 1, string? finition = null) => new()
    {
        Envelopes =
        [
            new Envelope
            {
                Number = 1,
                Lines =
                [
                    new OrderLine
                    {
                        ProductCode = code,
                        Items = [new OrderItem { FileName = "p.jpg", Quantity = tirages, Finish = finition }],
                    },
                ],
            },
        ],
    };

    private static RessourceDImpression? Ressource(
        Order commande, string? imposee = null, IReadOnlyList<De100PrinterInfo>? etats = null) =>
        RessourceDImpression.Pour(commande, Catalogue(), imposee, etats);

    /// <summary>Une machine du relais, réduite à ce qui décide de la file : son rouleau.</summary>
    private static De100PrinterInfo Machine(char id, De100Surface surface) => new(
        id, De100PrinterStatus.Ready, "", "DE100", "", "", 0,
        new De100Media(1, "", 152, 0, surface, 0), null, []);

    /// <summary>Le cas du comptoir : deux commandes de suite, sans rien préciser.</summary>
    [Fact]
    public void Deux_commandes_minilab_visent_la_meme_machine()
    {
        var a = Ressource(Commande("MINI10x15"));
        var b = Ressource(Commande("MINI13x18"));

        Assert.NotNull(a);
        Assert.Equal(a!.Cle, b!.Cle);
    }

    /// <summary>
    /// <b>La règle voulue en boutique.</b> Deux commandes en brillant s'attendent : la
    /// seconde ne file pas sur la machine d'à côté sous prétexte qu'elle est libre.
    /// </summary>
    [Fact]
    public void Meme_finition_meme_file()
    {
        var a = Ressource(Commande("MINI10x15", finition: "Brillant"));
        var b = Ressource(Commande("MINI13x18", finition: "brillant"));

        Assert.Equal(a!.Cle, b!.Cle);
    }

    /// <summary>Deux rouleaux différents, deux machines : rien n'attend.</summary>
    [Fact]
    public void Deux_finitions_deux_files()
    {
        var brillant = Ressource(Commande("MINI10x15", finition: "Brillant"));
        var lustre = Ressource(Commande("MINI10x15", finition: "Lustré"));

        Assert.NotEqual(brillant!.Cle, lustre!.Cle);
        Assert.False(RessourceDImpression.Croisent(brillant.Cle, lustre.Cle));
    }

    /// <summary>
    /// LA FINITION EST LA MACHINE. Le brillant est monté sur la DE100, le lustré sur la
    /// DE100-2 : demander du brillant et imposer la machine A, c'est demander la même
    /// chose. Tant que la clé portait les deux séparément, ces deux commandes-là
    /// s'imprimaient ensemble.
    /// </summary>
    [Fact]
    public void Le_brillant_c_est_la_machine_A()
    {
        var parLaFinition = Ressource(Commande("MINI10x15", finition: "Brillant"));
        var parLaMachine = Ressource(Commande("MINI13x18"), imposee: "A");

        Assert.Equal("minilab:A", parLaFinition!.Cle);
        Assert.Equal(parLaFinition.Cle, parLaMachine!.Cle);
    }

    /// <summary>Et le lustré, la machine B — l'autre DE100.</summary>
    [Fact]
    public void Le_lustre_c_est_la_machine_B()
    {
        var parLaFinition = Ressource(Commande("MINI10x15", finition: "Lustré"));
        var parLaMachine = Ressource(Commande("MINI-B"));

        Assert.Equal("minilab:B", parLaFinition!.Cle);
        Assert.Equal(parLaFinition.Cle, parLaMachine!.Cle);
    }

    /// <summary>
    /// Le rouleau qu'on déplace d'une machine à l'autre : c'est le relais qui a le dernier
    /// mot, pas le rangement habituel de la boutique.
    /// </summary>
    [Fact]
    public void Un_rouleau_deplace_emmene_sa_file()
    {
        var lustreEnA = Ressource(
            Commande("MINI10x15", finition: "Lustré"),
            etats: [Machine('A', De100Surface.Lustre), Machine('B', De100Surface.Glossy)]);

        Assert.Equal("minilab:A", lustreEnA!.Cle);
    }

    /// <summary>
    /// Personne n'a tranché : le rouleau chargé décidera, et ce peut être celui qu'une
    /// autre commande occupe. On attend — deux paquets mélangés dans le bac coûtent plus
    /// cher qu'une minute de patience.
    /// </summary>
    [Fact]
    public void Sans_rien_de_precise_on_attend_tout_le_monde()
    {
        var rien = Ressource(Commande("MINI10x15"));
        var brillant = Ressource(Commande("MINI13x18", finition: "Brillant"));
        var lustre = Ressource(Commande("MINI13x18", finition: "Lustré"));

        Assert.Equal("minilab:auto", rien!.Cle);
        Assert.True(RessourceDImpression.Croisent(rien.Cle, brillant!.Cle));
        Assert.True(RessourceDImpression.Croisent(lustre!.Cle, rien.Cle));
    }

    /// <summary>La DS620 ne croise jamais le minilab, et deux files Windows sont distinctes.</summary>
    [Fact]
    public void Le_spouleur_ne_croise_rien_d_autre_que_lui_meme()
    {
        var dnp = Ressource(Commande("DNP10x15"))!.Cle;

        Assert.False(RessourceDImpression.Croisent(dnp, "minilab:auto"));
        Assert.False(RessourceDImpression.Croisent(dnp, "spouleur:ds820"));
        Assert.True(RessourceDImpression.Croisent(dnp, dnp));
    }

    /// <summary>
    /// La machine choisie par l'opérateur dans la barre prime sur celle du produit — même
    /// règle que <c>PrintOrchestrator.MachineDemandee</c>. Sans cela, deux commandes qui
    /// partent sur la même machine porteraient deux clés et s'imprimeraient ensemble.
    /// </summary>
    [Fact]
    public void La_machine_imposee_par_l_operateur_l_emporte()
    {
        var auto = Ressource(Commande("MINI10x15"), imposee: "A");
        var duProduit = Ressource(Commande("MINI-B"), imposee: "A");

        Assert.Equal("minilab:A", auto!.Cle);
        Assert.Equal(auto.Cle, duProduit!.Cle);
    }

    /// <summary>Le produit désigne sa machine : elle sert quand l'opérateur n'a rien imposé.</summary>
    [Fact]
    public void La_machine_du_produit_sert_a_defaut()
    {
        Assert.Equal("minilab:B", Ressource(Commande("MINI-B"))!.Cle);
    }

    /// <summary>La DS620 n'est pas le minilab : les deux travaillent en même temps.</summary>
    [Fact]
    public void Le_spouleur_a_sa_propre_file()
    {
        var minilab = Ressource(Commande("MINI10x15"));
        var dnp = Ressource(Commande("DNP10x15"));

        Assert.NotEqual(minilab!.Cle, dnp!.Cle);
        Assert.Equal("DS620", dnp.Libelle);
    }

    /// <summary>
    /// Un agrandissement se tire à la main sur l'Epson, bien plus tard : le mettre en file
    /// ferait attendre une commande qui n'occupe aucune machine.
    /// </summary>
    [Fact]
    public void Un_agrandissement_n_occupe_aucune_machine()
    {
        Assert.Null(Ressource(Commande("GRAND")));
    }

    /// <summary>Une enveloppe déjà sortie ne retient plus rien.</summary>
    [Fact]
    public void Une_enveloppe_close_ne_compte_pas()
    {
        var commande = Commande("MINI10x15");
        commande.Envelopes[0].Status = EnvelopeStatus.Printed;

        Assert.Null(Ressource(commande));
    }

    /// <summary>
    /// Le bandeau annonce des TIRAGES, exemplaires compris — c'est ce que l'opérateur
    /// verra tomber dans le bac, et ce qu'il compare à ce qu'il a encaissé.
    /// </summary>
    [Fact]
    public void Le_compte_annonce_les_exemplaires()
    {
        Assert.Equal(7, RessourceDImpression.TiragesDe(Commande("MINI10x15", tirages: 7)));
    }

    /// <summary>Ce qui est déjà sorti ne s'annonce pas une seconde fois.</summary>
    [Fact]
    public void Le_compte_ignore_ce_qui_est_deja_sorti()
    {
        var commande = Commande("MINI10x15", tirages: 7);
        commande.Envelopes[0].Status = EnvelopeStatus.Printed;

        Assert.Equal(0, RessourceDImpression.TiragesDe(commande));
    }
}
