using Studio.Store.DiLand;

// Montre ce que Studio récupère des bornes, sur la vraie base de la boutique.
//
// Sert à vérifier en conditions réelles, DiLand allumé, que la lecture voit bien les
// commandes de bornes avec leur contenu — et qu'elle ne dérange pas DiLand : sa base
// n'est jamais ouverte, on lit une copie.
//
// Usage : DiLandProbe [nombre de commandes à montrer]

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
