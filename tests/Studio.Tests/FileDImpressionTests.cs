using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// DEUX COMMANDES QUI VISENT LE MÊME ROULEAU S'ATTENDENT.
///
/// Avant, une commande lancée pendant qu'une autre sortait partait immédiatement : les deux
/// envois se croisaient au minilab, et les tirages tombaient mélangés dans le même bac. La
/// seconde n'apparaissait nulle part tant qu'elle n'avait pas commencé, si bien qu'on la
/// relançait une seconde fois en croyant que le clic n'avait pas pris.
///
/// La file se décide sur ce que la commande DEMANDE — machine et finition —, jamais sur la
/// machine qu'elle finira par obtenir : celle-ci ne se connaît qu'après le rendu, et la
/// demander au clic ferait traverser le relais 32 bits sur le fil de l'interface.
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

    private static RessourceDImpression? Ressource(Order commande, string? imposee = null) =>
        RessourceDImpression.Pour(commande, Catalogue(), imposee);

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

        Assert.Equal("minilab:A:auto", auto!.Cle);
        Assert.Equal(auto.Cle, duProduit!.Cle);
    }

    /// <summary>Le produit désigne sa machine : elle sert quand l'opérateur n'a rien imposé.</summary>
    [Fact]
    public void La_machine_du_produit_sert_a_defaut()
    {
        Assert.Equal("minilab:B:auto", Ressource(Commande("MINI-B"))!.Cle);
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
