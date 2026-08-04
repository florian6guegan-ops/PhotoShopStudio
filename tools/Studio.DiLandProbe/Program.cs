using Studio.Store.DiLand;

// Montre ce que Studio récupère des bornes, sur la vraie base de la boutique.
//
// Sert à vérifier en conditions réelles, DiLand allumé, que la lecture voit bien les
// commandes de bornes avec leur contenu — et qu'elle ne dérange pas DiLand : sa base
// n'est jamais ouverte, on lit une copie.
//
// Usage : DiLandProbe [nombre de commandes à montrer]
//         DiLandProbe xml [nombre]   — lecture SUR LE DISQUE, sans la base ni DiLand
//
// Le mode « xml » sert à vérifier que la lecture disque rend la même chose que la base :
// c'est le seul contrôle possible sans fermer DiLand en pleine journée. Il compare, pour
// chaque commande présente des deux côtés, le produit, le nombre de photos et les
// recadrages.

if (args.Length > 0 && args[0].Equals("xml", StringComparison.OrdinalIgnoreCase))
    return ComparerDisqueEtBase(args.Length > 1 && int.TryParse(args[1], out var c) ? c : 5);

// « sql <requête> » : lecture libre de la COPIE de la base. Sert au diagnostic — DiLand
// connaît des choses qu'aucune API n'expose, à commencer par ce qu'il envoie réellement au
// minilab pour un format donné.
if (args.Length > 1 && args[0].Equals("sql", StringComparison.OrdinalIgnoreCase))
    return Interroger(string.Join(' ', args.Skip(1)));

var combien = args.Length > 0 && int.TryParse(args[0], out var n) ? n : 5;

var travail = Path.Combine(Path.GetTempPath(), "studio-diland-probe");
var depot = new DiLandRepository(DiLandRepository.DefaultRoot, travail);

Console.WriteLine($"Dépôt DiLand : {DiLandRepository.DefaultRoot}");
if (!depot.IsAvailable)
{
    Console.WriteLine("Dépôt introuvable — DiLand est-il installé à cet emplacement ?");
    return 1;
}

var empreinteAvant = Empreinte(depot.DatabasePath);

var chrono = System.Diagnostics.Stopwatch.StartNew();
if (!depot.RefreshSnapshot())
{
    Console.WriteLine("Copie impossible pour l'instant — DiLand écrivait sans doute. Réessayer.");
    return 1;
}
Console.WriteLine($"Copie de la base : {chrono.ElapsedMilliseconds} ms");

// on repart assez loin en arrière pour avoir de quoi montrer
var dernier = depot.LastOrderId();
var commandes = depot.ReadKioskOrdersAfter(0, 4000)
    .OrderByDescending(c => c.Oid)
    .Take(combien)
    .ToList();

Console.WriteLine($"Dernière commande DiLand : {dernier}");
Console.WriteLine($"{commandes.Count} commande(s) de borne, de la plus récente :");
Console.WriteLine();

foreach (var commande in commandes)
{
    Console.WriteLine($"  {commande}");
    Console.WriteLine($"    dossier  : {commande.DirectoryName}");

    foreach (var ligne in depot.LinesOf(commande))
    {
        Console.WriteLine($"    produit  : {ligne} — {ligne.Price:0.00} €");

        var manquantes = ligne.Photos.Count(p => !File.Exists(depot.PhotoPath(commande, p)));
        var recadrees = ligne.Photos.Count(p => p.ApplyCrop);
        Console.WriteLine($"      photos : {ligne.Photos.Count} sur le disque"
            + $" ({recadrees} recadrée(s) à la borne"
            + (manquantes > 0 ? $", {manquantes} INTROUVABLE(S)" : "") + ")");

        foreach (var photo in ligne.Photos.Take(3))
            Console.WriteLine($"        {photo.DisplayName} ×{photo.Quantity}");
        if (ligne.Photos.Count > 3)
            Console.WriteLine($"        … et {ligne.Photos.Count - 3} autre(s)");
    }

    Console.WriteLine();
}

// Reprise à blanc : est-ce que Studio saurait refaire ces commandes ?
// Rien n'est créé ici — on vérifie seulement que chaque produit vendu en borne a son
// équivalent au catalogue, car un produit inconnu ferait perdre une ligne de commande.
var cataloguePath = Path.Combine(@"D:\PhotoStudioData", "catalog", "products.json");
if (File.Exists(cataloguePath))
{
    var catalogue = Studio.Core.Catalog.ProductCatalog.Load(cataloguePath).All.ToList();
    var essai = new DiLandImporter(depot, commandes: null!, catalogue, registrePath: "");

    var produits = depot.ReadKioskOrdersAfter(0, 4000)
        .SelectMany(c => depot.LinesOf(c))
        .GroupBy(l => l.ProductName)
        .OrderByDescending(g => g.Count())
        .ToList();

    Console.WriteLine($"Produits vendus en borne : {produits.Count}");
    foreach (var groupe in produits)
    {
        var trouve = essai.MatchProduct(groupe.Key);
        Console.WriteLine($"  {groupe.Key,-22} {groupe.Count(),3} ligne(s)  → "
            + (trouve is null ? "AUCUN PRODUIT AU CATALOGUE" : $"{trouve.Code} ({trouve.Output})"));
    }
    Console.WriteLine();

    // Tous les produits du catalogue DiLand, pas seulement ceux deja vendus : un format
    // propose en borne mais absent de chez nous ferait perdre une ligne le jour ou un
    // client le commande.
    var tousDiLand = depot.AllProductNames();
    var apparies = tousDiLand.Where(n => essai.MatchProduct(n) is not null).ToList();
    var orphelins = tousDiLand.Except(apparies).ToList();

    Console.WriteLine($"Catalogue DiLand : {tousDiLand.Count} produits, "
        + $"{apparies.Count} repris par Studio, {orphelins.Count} sans equivalent :");
    foreach (var nom in orphelins)
        Console.WriteLine($"  - {nom}");
    Console.WriteLine();
}

// la garantie qui compte : on n'a pas touché à DiLand
var empreinteApres = Empreinte(depot.DatabasePath);
Console.WriteLine(empreinteAvant == empreinteApres
    ? "Base de DiLand inchangée."
    : "ATTENTION : la base de DiLand a changé pendant la lecture (DiLand a écrit de son côté).");
Console.WriteLine(File.Exists(depot.DatabasePath + "-wal") || File.Exists(depot.DatabasePath + "-shm")
    ? "ATTENTION : un journal SQLite est apparu."
    : "Aucun journal SQLite créé : DiLand n'a pas été verrouillé.");

return 0;

static string Empreinte(string chemin)
{
    var f = new FileInfo(chemin);
    return $"{f.Length}/{f.LastWriteTimeUtc:O}";
}

/// <summary>
/// Compare la lecture SUR LE DISQUE à celle de la base, sur les vraies commandes de la
/// boutique.
///
/// C'est le seul contrôle possible sans fermer DiLand en pleine journée : si les deux
/// lectures s'accordent pendant qu'il tourne, celle du disque tiendra quand il sera
/// tombé — c'est-à-dire au moment où l'on en a besoin.
/// </summary>
static int ComparerDisqueEtBase(int combien)
{
    var travail = Path.Combine(Path.GetTempPath(), "studio-diland-probe");
    var depot = new DiLandRepository(DiLandRepository.DefaultRoot, travail);

    Console.WriteLine($"Dépôt DiLand : {DiLandRepository.DefaultRoot}");
    Console.WriteLine($"  intégrées : {depot.OrdersDirectory}");
    Console.WriteLine($"  en attente : {depot.IncomingOrdersDirectory}");
    Console.WriteLine();

    var surLeDisque = depot.ReadKioskOrdersFromDisk(4000);
    Console.WriteLine($"{surLeDisque.Count} commande(s) lisible(s) SANS la base.");

    // la base, pour comparer — indisponible n'est pas une erreur ici, c'est même le cas
    // qu'on cherche à couvrir
    var parDossier = depot.RefreshSnapshot()
        ? depot.ReadKioskOrdersAfter(0, 4000)
            .ToDictionary(c => c.DirectoryName, StringComparer.OrdinalIgnoreCase)
        : [];

    Console.WriteLine(parDossier.Count == 0
        ? "Base indisponible ou vide : rien à comparer, la lecture disque est seule."
        : $"{parDossier.Count} commande(s) dans la base.");
    Console.WriteLine();

    var seulesSurLeDisque = 0;
    var ecarts = 0;

    foreach (var contenu in surLeDisque.TakeLast(combien))
    {
        var commande = contenu.Order;
        Console.WriteLine($"  {commande}");
        Console.WriteLine($"    dossier : {commande.DirectoryName}");

        foreach (var ligne in contenu.Lines)
        {
            var recadrees = ligne.Photos.Count(p => p.ApplyCrop);
            var redressees = ligne.Photos.Count(p => Math.Abs(p.FineRotationDegrees) > 0.001);
            Console.WriteLine($"    produit : {ligne} — {ligne.Price:0.00} €");
            Console.WriteLine($"      photos : {ligne.Photos.Count} "
                + $"({recadrees} recadrée(s), {redressees} redressée(s))");

            foreach (var photo in ligne.Photos.Take(2))
                Console.WriteLine($"        {photo.DisplayName} ×{photo.Quantity} "
                    + $"crop {photo.CropX:0.###},{photo.CropY:0.###} "
                    + $"{photo.CropWidth:0.###}×{photo.CropHeight:0.###}");
        }

        if (!parDossier.TryGetValue(commande.DirectoryName, out var deLaBase))
        {
            seulesSurLeDisque++;
            Console.WriteLine("    → ABSENTE DE LA BASE : DiLand ne l'a pas encore intégrée.");
            Console.WriteLine();
            continue;
        }

        // le contrôle : mêmes photos, mêmes recadrages des deux côtés
        var duDisque = contenu.Lines.SelectMany(l => l.Photos)
            .OrderBy(p => p.FileName, StringComparer.Ordinal).ToList();
        var deLaBasePhotos = depot.LinesOf(deLaBase).SelectMany(l => l.Photos)
            .OrderBy(p => p.FileName, StringComparer.Ordinal).ToList();

        if (duDisque.Count != deLaBasePhotos.Count)
        {
            ecarts++;
            Console.WriteLine($"    → ÉCART : {duDisque.Count} photo(s) au disque, "
                + $"{deLaBasePhotos.Count} en base.");
        }
        else
        {
            var differentes = duDisque.Where((p, i) =>
                p.FileName != deLaBasePhotos[i].FileName
                || Math.Abs(p.CropWidth - deLaBasePhotos[i].CropWidth) > 0.001
                || Math.Abs(p.CropHeight - deLaBasePhotos[i].CropHeight) > 0.001
                || Math.Abs(p.FineRotationDegrees - deLaBasePhotos[i].FineRotationDegrees) > 0.001)
                .ToList();

            if (differentes.Count > 0)
            {
                ecarts++;
                Console.WriteLine($"    → ÉCART sur {differentes.Count} photo(s).");
            }
            else
            {
                Console.WriteLine("    → identique à la base.");
            }
        }

        Console.WriteLine();
    }

    Console.WriteLine($"{seulesSurLeDisque} commande(s) visible(s) SEULEMENT par le disque "
        + "— invisibles sans cette lecture.");
    Console.WriteLine(ecarts == 0
        ? "Aucun écart entre le disque et la base."
        : $"ATTENTION : {ecarts} commande(s) diffèrent entre le disque et la base.");

    return ecarts == 0 ? 0 : 1;
}

/// Lecture libre de la copie de la base DiLand, pour le diagnostic.
int Interroger(string sql)
{
    var dossier = Path.Combine(Path.GetTempPath(), "studio-diland-probe");
    var repo = new DiLandRepository(DiLandRepository.DefaultRoot, dossier);

    if (!repo.IsAvailable)
    {
        Console.WriteLine("Dépôt DiLand introuvable.");
        return 1;
    }

    if (!repo.RefreshSnapshot())
    {
        Console.WriteLine("Copie impossible pour l'instant — DiLand écrivait sans doute. Réessayer.");
        return 1;
    }

    try
    {
        var lignes = repo.Interroger(sql);
        foreach (var ligne in lignes) Console.WriteLine(string.Join(" | ", ligne));
        Console.WriteLine($"\n({lignes.Count - 1} ligne(s))");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Requête refusée : {ex.Message}");
        return 1;
    }
}
