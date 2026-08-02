using Studio.Core.Domain;

namespace Studio.Core.Catalog;

/// <summary>Un format normalisé proposé d'un clic (A4, A3, A2…).</summary>
public sealed record StandardSize(string Name, double WidthMm, double HeightMm);

/// <summary>
/// Agrandissements à taille libre : quel format du catalogue les contient, et donc à quel
/// prix ils se vendent.
///
/// <b>La règle vient de l'exploitant (02/08/2026) : « si ça tient dans un 30×40, le prix
/// d'un 30×40 ; si c'est dans un 40×50, le prix d'un 40×50. »</b> Pas de tarif au dm² à
/// tenir à jour, pas de prix tapé à la main devant le client — le catalogue reste la seule
/// source, et un changement de tarif se répercute tout seul.
/// </summary>
public static class EnlargementSizes
{
    /// <summary>
    /// Les formats qu'on redemande sans arrêt au comptoir. Ils évitent de retaper des
    /// centimètres qu'on connaît par cœur — et de se tromper d'un millimètre sur un A3.
    /// </summary>
    public static IReadOnlyList<StandardSize> Standards { get; } =
    [
        new("A4", 210, 297),
        new("A3", 297, 420),
        new("A3+", 329, 483),
        new("A2", 420, 594),
        new("A1", 594, 841),
        new("A0", 841, 1189),
    ];

    /// <summary>
    /// Le produit du catalogue qui portera cet agrandissement, ou null si aucun n'est assez
    /// grand.
    ///
    /// Départages, dans cet ordre : <b>le moins cher</b> — c'est le prix qu'on annonce —
    /// puis le plus petit, pour ne pas gâcher de papier à prix égal. Les deux orientations
    /// sont essayées : un A3 (297 × 420) ne tient pas dans un 30×40 mais tient dans un
    /// 30×45, et un tirage se pose dans le sens qu'on veut.
    ///
    /// Un produit à 0,00 € est écarté : c'est un format non tarifé (le 70×100 l'est), et il
    /// gagnerait toujours au moins-cher en faisant travailler la boutique pour rien.
    /// </summary>
    public static Product? PaperFor(double widthMm, double heightMm, IEnumerable<Product> enlargements)
    {
        ArgumentNullException.ThrowIfNull(enlargements);
        if (widthMm <= 0 || heightMm <= 0) return null;

        return enlargements
            .Where(p => p.Price > 0)
            .Where(p => Contient(p.WidthMm, p.HeightMm, widthMm, heightMm))
            .OrderBy(p => p.Price)
            .ThenBy(p => p.WidthMm * p.HeightMm)
            .FirstOrDefault();
    }

    /// <summary>Jeu admis : le catalogue est en millimètres ronds, les formats normalisés aussi.</summary>
    private const double Tolerance = 0.5;

    private static bool Contient(double papierW, double papierH, double demandeW, double demandeH) =>
        (papierW + Tolerance >= demandeW && papierH + Tolerance >= demandeH)
        || (papierW + Tolerance >= demandeH && papierH + Tolerance >= demandeW);

    /// <summary>
    /// Code du produit engendré pour une taille libre, en millimètres entiers.
    ///
    /// Il doit être STABLE : redemander deux fois le même format doit retomber sur le même
    /// produit, sinon le catalogue se remplirait d'un doublon par commande. C'est aussi ce
    /// code que porteront les commandes, et <c>ProductCatalog.Require</c> devra le retrouver
    /// des semaines plus tard pour une réimpression.
    /// </summary>
    public static string CodeFor(double widthMm, double heightMm) =>
        $"agr-{Math.Round(Math.Min(widthMm, heightMm)):0}x{Math.Round(Math.Max(widthMm, heightMm)):0}";

    /// <summary>
    /// Le produit à ajouter au catalogue pour cette taille : un agrandissement ordinaire,
    /// au prix et au tarif dégressif du papier qui le contient.
    ///
    /// Les paliers sont RECOPIÉS et non partagés : le produit doit garder le tarif du jour
    /// où il a été créé, même si celui du papier bouge ensuite.
    /// </summary>
    public static Product Create(double widthMm, double heightMm, Product paper, string? name = null) =>
        new()
        {
            Code = CodeFor(widthMm, heightMm),
            Name = name ?? $"{widthMm / 10:0.#} × {heightMm / 10:0.#} cm",
            WidthMm = widthMm,
            HeightMm = heightMm,
            Output = ProductOutput.ManualFile,
            PrinterName = "",
            Dpi = paper.Dpi,
            Price = paper.Price,
            PriceTiers = paper.PriceTiers
                .Select(t => new PriceTier { FromQuantity = t.FromQuantity, UnitPrice = t.UnitPrice })
                .ToList(),
            DefaultFit = FitMode.Fill,
            Enabled = true,
        };
}
