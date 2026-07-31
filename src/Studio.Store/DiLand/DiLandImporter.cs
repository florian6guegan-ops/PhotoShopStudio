using System.Text.Json;
using Studio.Core.Domain;

namespace Studio.Store.DiLand;

/// <summary>Résultat de la reprise d'une commande de borne.</summary>
/// <param name="Source">La commande telle que DiLand l'a reçue.</param>
/// <param name="Created">La commande créée dans Studio, ou null si rien n'a pu être repris.</param>
/// <param name="Warnings">Ce qui n'a pas pu être repris, à montrer à l'opérateur.</param>
public sealed record DiLandImportOutcome(
    DiLandOrder Source,
    Order? Created,
    IReadOnlyList<string> Warnings)
{
    public bool Succeeded => Created is not null;

    public override string ToString() => Succeeded
        ? $"{Source} → commande {Created!.DailyNumber:000}"
        : $"{Source} → non repris ({string.Join(" ; ", Warnings)})";
}

/// <summary>
/// Reprend dans Studio les commandes que les bornes ont déposées dans DiLand.
///
/// DiLand garde les siennes : on ne fait que lire et copier. Ses photos ne sont ni
/// déplacées ni supprimées, et sa base n'est jamais ouverte (voir <see cref="DiLandRepository"/>).
///
/// Une commande n'est reprise qu'une fois. Le registre des reprises est tenu à part,
/// car le dossier d'une commande Studio porte son numéro du jour : sans registre, une
/// deuxième reprise créerait un doublon au lieu d'écraser.
/// </summary>
public sealed class DiLandImporter
{
    /// <summary>Ce qui apparaît comme origine sur les commandes reprises.</summary>
    public const string SourceName = "borne";

    private readonly DiLandRepository _depot;
    private readonly OrderService _commandes;
    private readonly IReadOnlyList<Product> _catalogue;
    private readonly string _registrePath;

    private HashSet<long>? _dejaReprises;

    public DiLandImporter(
        DiLandRepository depot,
        OrderService commandes,
        IReadOnlyList<Product> catalogue,
        string registrePath)
    {
        _depot = depot;
        _commandes = commandes;
        _catalogue = catalogue;
        _registrePath = registrePath;
    }

    /// <summary>Commandes de bornes pas encore reprises, de la plus ancienne à la plus récente.</summary>
    public IReadOnlyList<DiLandOrder> Pending(int limit = 50)
    {
        if (!_depot.RefreshSnapshot()) return [];

        var registre = Registre();
        return _depot.ReadKioskOrdersAfter(0, 4000)
            .Where(c => !registre.Contains(c.Oid))
            .TakeLast(limit)
            .ToList();
    }

    /// <summary>
    /// Décrit le contenu d'une commande pour l'opérateur : un libellé par produit, avec le
    /// nombre de tirages, et la mention de ce que Studio ne sait pas vendre. C'est ce qu'il
    /// faut lire avant de décider de reprendre.
    /// </summary>
    public IReadOnlyList<string> Describe(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        return _depot.LinesOf(order)
            .Select(l => MatchProduct(l.ProductName) is null
                ? $"{l.ProductName} × {l.PrintCount} (non repris)"
                : $"{l.ProductName} × {l.PrintCount}")
            .ToList();
    }

    /// <summary>
    /// Reprend une commande : copie ses photos dans le dossier Studio et crée la commande
    /// avec ses produits, ses quantités et ses recadrages.
    ///
    /// Une commande déjà reprise n'est pas reprise deux fois.
    /// </summary>
    public DiLandImportOutcome Import(DiLandOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var avertissements = new List<string>();

        if (Registre().Contains(order.Oid))
            return new DiLandImportOutcome(order, null, ["déjà reprise"]);

        var photos = new List<DraftItem>();

        foreach (var ligne in _depot.LinesOf(order))
        {
            var produit = MatchProduct(ligne.ProductName);
            if (produit is null)
            {
                avertissements.Add($"produit inconnu au catalogue : « {ligne.ProductName} »");
                continue;
            }

            foreach (var photo in ligne.Photos)
            {
                var chemin = _depot.PhotoPath(order, photo);
                if (!File.Exists(chemin))
                {
                    avertissements.Add($"photo introuvable : {photo.DisplayName}");
                    continue;
                }

                photos.Add(new DraftItem(
                    SourcePath: chemin,
                    Product: produit,
                    Quantity: Math.Max(1, photo.Quantity),
                    Crop: CropOf(photo),
                    RotationQuarterTurns: QuarterTurns(photo.Angle),
                    FitOverride: null,
                    Adjustments: new ImageAdjustments()));
            }
        }

        if (photos.Count == 0)
        {
            avertissements.Add("aucune photo reprise");
            return new DiLandImportOutcome(order, null, avertissements);
        }

        var creee = _commandes.CreateOrder(
            SourceName,
            photos,
            customerName: string.IsNullOrWhiteSpace(order.EndUserName)
                ? $"Borne {order.DailyNumber}"
                : order.EndUserName);

        Enregistrer(order.Oid);

        return new DiLandImportOutcome(order, creee, avertissements);
    }

    /// <summary>
    /// Retrouve le produit Studio correspondant au produit DiLand.
    ///
    /// Le catalogue a été repris de DiLand, donc les noms coïncident (« 10x15 »,
    /// « Bord blanc 10x15 », « 21x29,7 »). On compare malgré tout à la casse, aux espaces
    /// et à la virgule décimale près, pour qu'un écart de saisie ne fasse pas perdre une
    /// commande. À nom égal, le minilab l'emporte : c'est lui qui tire les commandes de
    /// bornes, et le catalogue contient aussi un « 10x15 » sur la DNP.
    /// </summary>
    public Product? MatchProduct(string dilandName)
    {
        if (string.IsNullOrWhiteSpace(dilandName)) return null;

        var cible = Normaliser(dilandName);

        var candidats = _catalogue
            .Where(p => p.Enabled && Normaliser(p.Name) == cible)
            .ToList();

        return candidats.FirstOrDefault(p => p.Output == ProductOutput.FujiMinilab)
            ?? candidats.FirstOrDefault();
    }

    private static string Normaliser(string nom) =>
        nom.Replace(" ", "").Replace(",", ".").Trim().ToLowerInvariant();

    /// <summary>
    /// Le recadrage fait à la borne, ou l'image entière. DiLand l'exprime déjà en
    /// fractions de l'image, comme nous ; un rectangle incohérent est ignoré plutôt que
    /// de produire un tirage faux.
    /// </summary>
    private static CropSpec CropOf(DiLandOrderPhoto photo)
    {
        if (!photo.ApplyCrop) return CropSpec.Full;

        var crop = new CropSpec(photo.CropX, photo.CropY, photo.CropWidth, photo.CropHeight);
        return crop.IsValid ? crop : CropSpec.Full;
    }

    /// <summary>DiLand stocke des degrés, nous des quarts de tour horaires.</summary>
    internal static int QuarterTurns(double angle)
    {
        var quarts = (int)Math.Round(angle / 90.0) % 4;
        return quarts < 0 ? quarts + 4 : quarts;
    }

    // — registre des reprises —

    private HashSet<long> Registre()
    {
        if (_dejaReprises is not null) return _dejaReprises;

        try
        {
            _dejaReprises = File.Exists(_registrePath)
                ? JsonSerializer.Deserialize<HashSet<long>>(File.ReadAllText(_registrePath)) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            // registre illisible : on repart d'un registre vide plutôt que de bloquer la
            // reprise ; le pire risque est un doublon, visible et corrigeable
            _dejaReprises = [];
        }

        return _dejaReprises;
    }

    private void Enregistrer(long oid)
    {
        var registre = Registre();
        registre.Add(oid);

        var dossier = Path.GetDirectoryName(_registrePath);
        if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);

        AtomicFile.WriteAllText(_registrePath, JsonSerializer.Serialize(registre));
    }
}
