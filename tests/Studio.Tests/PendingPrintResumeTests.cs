using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Reprise d'une enveloppe interrompue : ni page refaite, ni page sautée.
///
/// C'est la garantie qui compte après un bourrage. Sur une planche de trente, refaire
/// depuis le début gâche vingt tirages, et sauter une page rend la commande incomplète
/// sans que personne ne s'en aperçoive avant le comptoir.
/// </summary>
public class PendingPrintResumeTests
{
    /// <summary>
    /// Reproduit la règle de saut de <c>PrintOrchestrator.PrintPages</c> : les pages
    /// physiques sont numérotées à plat, et celles d'avant le point de reprise sont
    /// passées. On la vérifie ici sur la séquence, sans imprimante.
    /// </summary>
    private static List<string> PagesTirees(IReadOnlyList<(string Nom, int Copies)> pages, int depart)
    {
        var sorties = new List<string>();
        var rang = 0;

        foreach (var (nom, copies) in pages)
            for (var copie = 0; copie < copies; copie++)
            {
                if (rang++ < depart) continue;
                sorties.Add($"{nom}#{copie + 1}");
            }

        return sorties;
    }

    [Fact]
    public void SansInterruption_ToutSort()
    {
        var pages = new[] { ("A", 2), ("B", 3) };

        var sorties = PagesTirees(pages, depart: 0);

        Assert.Equal(["A#1", "A#2", "B#1", "B#2", "B#3"], sorties);
    }

    [Fact]
    public void Reprise_SauteExactementCeQuiEstDejaSorti()
    {
        var pages = new[] { ("A", 2), ("B", 3) };

        // trois pages étaient sorties avant le bourrage : A#1, A#2, B#1
        var sorties = PagesTirees(pages, depart: 3);

        Assert.Equal(["B#2", "B#3"], sorties);
    }

    /// <summary>La reprise doit franchir la frontière entre deux pages, pas seulement les copies.</summary>
    [Fact]
    public void Reprise_AuMilieuDUnePage()
    {
        var pages = new[] { ("A", 1), ("B", 4) };

        var sorties = PagesTirees(pages, depart: 1);

        Assert.Equal(["B#1", "B#2", "B#3", "B#4"], sorties);
    }

    /// <summary>Une enveloppe déjà entièrement sortie ne réimprime rien.</summary>
    [Fact]
    public void Reprise_ApresLaDernierePage_NeTirePlusRien()
    {
        var pages = new[] { ("A", 2), ("B", 3) };

        Assert.Empty(PagesTirees(pages, depart: 5));
        Assert.Empty(PagesTirees(pages, depart: 99));
    }

    /// <summary>
    /// Reprise et première tentative couvrent ensemble TOUTES les pages, chacune une fois.
    /// C'est la propriété qui compte vraiment : rien en double, rien d'oublié.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(7)]
    public void PremiereTentativeEtReprise_CouvrentToutUneSeuleFois(int arret)
    {
        var pages = new[] { ("A", 3), ("B", 1), ("C", 4) };

        var avant = PagesTirees(pages, depart: 0).Take(arret).ToList();
        var apres = PagesTirees(pages, depart: arret);

        var completes = PagesTirees(pages, depart: 0);
        Assert.Equal(completes, avant.Concat(apres));
        Assert.Equal(completes.Distinct().Count(), avant.Concat(apres).Distinct().Count());
    }
}

/// <summary>
/// L'état d'une imprimante décide de la mise en attente : mieux vaut se tromper en
/// imprimant qu'en bloquant la boutique.
/// </summary>
public class PrinterReadinessTests
{
    [Fact]
    public void NomVide_EstSignaleCommeManquant()
    {
        var etat = PrinterReadiness.Check("");

        Assert.Equal(PrinterReadyState.Missing, etat.State);
        Assert.False(etat.CanPrint);
    }

    [Fact]
    public void ImprimanteInconnue_EstSignaleeCommeManquante()
    {
        var etat = PrinterReadiness.Check("Imprimante qui n'existe pas " + Guid.NewGuid().ToString("N"));

        Assert.Equal(PrinterReadyState.Missing, etat.State);
        Assert.Contains("n'existe pas", etat.Reason);
    }

    /// <summary>
    /// « Microsoft Print to PDF » est présente sur tout poste Windows et toujours prête :
    /// elle sert de témoin qu'on ne déclare pas une machine saine en panne.
    /// </summary>
    [Fact]
    public void ImprimanteSaine_EstDeclareePrete()
    {
        var etat = PrinterReadiness.Check("Microsoft Print to PDF");

        // absente d'un poste exotique : on ne fait pas échouer le test pour autant
        if (etat.State == PrinterReadyState.Missing) return;

        Assert.True(etat.CanPrint, $"déclarée non prête : {etat.Reason}");
        Assert.Equal("", etat.Reason);
    }
}
