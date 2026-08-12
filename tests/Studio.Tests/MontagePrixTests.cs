using Studio.Core.Domain;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// ⚠ <b>L'essai qui garde la caisse honnête.</b>
///
/// Le montage est une économie de PAPIER pour la boutique : deux 24×30 composés sur une même
/// feuille restent deux 24×30 au ticket. Décision de l'exploitant, 12/08/2026, et c'est la
/// décision la plus importante du lot parce qu'elle DIVERGE de l'existant : la planche
/// « personnalisée » de l'impression rapide, elle, facture le papier.
///
/// Confondre les deux ferait payer une seule feuille 40×60 là où le client doit deux 24×30 —
/// ou l'inverse. Les deux mécaniques partagent la géométrie, jamais la politique de prix.
/// </summary>
public class MontagePrixTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "MontagePrix-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private static readonly Product VingtQuatre30 = new()
    {
        Code = "24x30", Name = "24×30", WidthMm = 240, HeightMm = 300,
        Price = 12.00m, Enabled = true, Output = ProductOutput.ManualFile,
    };

    private OrderService Service() => new(
        new OrderFolderStore(Path.Combine(_root, "commandes")),
        new DailyCounter(Path.Combine(_root, "compteur.json")));

    /// <summary>Une photo bidon sur le disque : le service copie les originaux.</summary>
    private string UnePhoto(string nom)
    {
        var dossier = Path.Combine(_root, "photos");
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nom);
        File.WriteAllBytes(chemin, [0xFF, 0xD8, 0xFF, 0xD9]);
        return chemin;
    }

    private DraftItem Tirage(string nom, int quantite, string? feuille) =>
        new(UnePhoto(nom), VingtQuatre30, quantite, CropSpec.Full, 0, 0, null,
            new ImageAdjustments(), MontageSheetCode: feuille);

    /// <summary>
    /// Deux 24×30 montés sur une feuille coûtent exactement ce que coûtent deux 24×30
    /// rendus séparément. C'est tout le contrat.
    /// </summary>
    [Fact]
    public void Le_montage_ne_change_pas_le_prix()
    {
        var sans = Service().CreateOrder("Operateur",
            [Tirage("a.jpg", 1, null), Tirage("b.jpg", 1, null)]);

        var avec = Service().CreateOrder("Operateur",
            [Tirage("c.jpg", 1, "40x60"), Tirage("d.jpg", 1, "40x60")]);

        Assert.Equal(24.00m, sans.Total);
        Assert.Equal(sans.Total, avec.Total);
    }

    /// <summary>
    /// Le prix suit le nombre de TIRAGES, jamais le nombre de feuilles. Cinq tirages
    /// tiennent sur trois feuilles de deux ; le client en paie cinq.
    /// </summary>
    [Fact]
    public void Le_prix_compte_les_tirages_et_non_les_feuilles()
    {
        var commande = Service().CreateOrder("Operateur", [Tirage("a.jpg", 5, "40x60")]);
        var ligne = commande.Envelopes.Single().Lines.Single();

        Assert.Equal(5, ligne.TotalPrints);
        Assert.Equal(60.00m, ligne.Total);
    }

    /// <summary>
    /// ⚠ Un montage n'est PAS une planche personnalisée. Si <c>IsCustomSheet</c> passait à
    /// vrai, <c>Total</c> basculerait sur <c>SheetCount</c> et facturerait le papier.
    /// </summary>
    [Fact]
    public void Un_montage_nest_pas_une_planche_personnalisee()
    {
        var commande = Service().CreateOrder("Operateur", [Tirage("a.jpg", 2, "40x60")]);
        var ligne = commande.Envelopes.Single().Lines.Single();

        Assert.True(ligne.IsMontage);
        Assert.False(ligne.IsCustomSheet);
        Assert.Equal(0, ligne.SheetCount);
    }

    [Fact]
    public void La_feuille_choisie_arrive_jusqua_la_ligne()
    {
        var commande = Service().CreateOrder("Operateur", [Tirage("a.jpg", 1, "40x60")]);

        Assert.Equal("40x60", commande.Envelopes.Single().Lines.Single().MontageSheetCode);
    }

    /// <summary>Sans feuille, la ligne est celle d'avant — rien n'a changé.</summary>
    [Fact]
    public void Sans_feuille_la_ligne_ne_porte_aucun_montage()
    {
        var commande = Service().CreateOrder("Operateur", [Tirage("a.jpg", 1, null)]);
        var ligne = commande.Envelopes.Single().Lines.Single();

        Assert.Null(ligne.MontageSheetCode);
        Assert.False(ligne.IsMontage);
    }

    /// <summary>
    /// ⚠ La feuille survit à une mise de côté.
    ///
    /// L'opérateur choisit le montage à un écran qu'il ne reverra PAS en reprenant : perdue
    /// en silence, la reprise repartirait en un fichier par tirage, donc sur deux fois plus
    /// de papier, et personne ne s'en apercevrait avant de compter les feuilles.
    /// </summary>
    [Fact]
    public void La_feuille_survit_a_une_mise_de_cote()
    {
        var attente = new AttenteStore(Path.Combine(_root, "attente"));
        var travail = new TravailEnAttente
        {
            PhotosDirectory = Path.Combine(_root, "photos"),
            ProduitParDefaut = "24x30",
            MontageSheetCode = "40x60",
        };

        attente.Enregistrer(travail);

        Assert.Equal("40x60", attente.Lire(travail.Id)!.MontageSheetCode);
    }
}
