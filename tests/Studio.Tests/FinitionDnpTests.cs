using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Dnp;

namespace Studio.Tests;

/// <summary>
/// La finition annoncée à la DS620 lors d'un envoi direct.
///
/// Le pilote Windows porte la sienne dans son DEVMODE ; l'envoi direct, lui, ne lit rien et
/// doit la déclarer. Se tromper ne fait pas échouer le tirage — il sort, avec la mauvaise
/// surface, et la feuille est perdue. D'où ces cas.
/// </summary>
public class FinitionDnpTests
{
    private static Product Produit(params string[] finitions) => new()
    {
        Code = "10x15",
        Name = "10x15",
        PrinterName = "DP-DS620",
        Finishes = finitions.Select(f => new FinishOption { Name = f }).ToList(),
    };

    /// <summary>
    /// <b>Le défaut est le BRILLANT.</b> Aucun produit du catalogue ne nomme de finition
    /// (relevé du 07/08/2026 : les 45 produits ont une liste vide), et la planche identité
    /// tire sur un DEVMODE portant <c>OPTYPE_LUSTER</c> — nom interne du pilote pour le
    /// brillant. Le défaut était le lustré : tout sortait avec la mauvaise surface.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Brillant")]
    [InlineData("Glossy")]
    public void Sans_finition_nommee_on_tire_en_brillant(string? finition)
    {
        Assert.Equal(DnpOvercoat.Glossy, PrintOrchestrator.FinitionDnp(Produit(), finition));
    }

    [Theory]
    [InlineData("Mat", DnpOvercoat.Matte)]
    [InlineData("Papier mat", DnpOvercoat.Matte)]
    [InlineData("Lustré", DnpOvercoat.Luster)]
    [InlineData("Luster", DnpOvercoat.Luster)]
    [InlineData("Mat fin", DnpOvercoat.FineMatte)]
    public void Une_finition_nommee_est_respectee(string finition, DnpOvercoat attendue)
    {
        Assert.Equal(attendue, PrintOrchestrator.FinitionDnp(Produit(), finition));
    }

    /// <summary>
    /// « Mat fin » passe avant « mat », dont il contient le nom — sans quoi le mat fin
    /// sortirait en mat ordinaire.
    /// </summary>
    [Fact]
    public void Le_mat_fin_n_est_pas_pris_pour_du_mat()
    {
        Assert.Equal(DnpOvercoat.FineMatte,
            PrintOrchestrator.FinitionDnp(Produit(), "Mat fin"));
    }

    /// <summary>
    /// La recherche porte sur les MOTS : « format » contient « mat », et une recherche sur
    /// la chaîne entière ferait sortir en mat un tirage qui ne l'a jamais demandé.
    /// </summary>
    [Fact]
    public void Un_nom_contenant_format_ne_devient_pas_du_mat()
    {
        Assert.Equal(DnpOvercoat.Glossy,
            PrintOrchestrator.FinitionDnp(Produit(), "Grand format"));
    }

    /// <summary>
    /// Sans finition choisie sur la ligne de commande, c'est la première du produit qui
    /// vaut — le choix par défaut offert à l'opérateur.
    /// </summary>
    [Fact]
    public void Sans_choix_sur_la_ligne_la_premiere_finition_du_produit_vaut()
    {
        Assert.Equal(DnpOvercoat.Matte,
            PrintOrchestrator.FinitionDnp(Produit("Mat", "Brillant"), null));
    }
}
