using System.IO.Compression;
using Studio.Printing;

// Fabrique un rapport de diagnostic sur les VRAIES données du poste, et montre ce qu'il
// contient — sans rien envoyer.
//
// C'est le contrôle qu'on ne peut pas faire autrement : les essais travaillent sur des
// dossiers fabriqués, et ce qui compte ici est qu'aucun secret du poste réel ne parte.
//
// Usage : RapportProbe [dossier des journaux] [dossier de configuration]

var logs = args.Length > 0 ? args[0] : @"D:\PhotoStudioData\logs";
var config = args.Length > 1 ? args[1] : @"D:\PhotoStudioData\config";
var sortie = Path.Combine(Path.GetTempPath(), RapportDiagnostic.NomPropose());

Console.WriteLine($"Journaux      : {logs}");
Console.WriteLine($"Configuration : {config}");
Console.WriteLine();

var contenu = RapportDiagnostic.Fabriquer(logs, config, sortie,
    note: "Contrôle de la sonde — aucun envoi.");

Console.WriteLine($"Archive : {sortie}");
Console.WriteLine($"Taille  : {contenu.TailleLisible}");
Console.WriteLine();

using var zip = ZipFile.OpenRead(sortie);

Console.WriteLine("Contenu :");
foreach (var entree in zip.Entries.OrderBy(e => e.FullName))
    Console.WriteLine($"  {entree.FullName,-40} {entree.Length / 1024.0,8:0.0} Ko");

// Le contrôle qui compte : rien de ce qui porte un secret ne doit être là.
Console.WriteLine();
var interdits = zip.Entries
    .Where(e => RapportDiagnostic.EstSensible(Path.GetFileName(e.FullName)))
    .ToList();

if (interdits.Count == 0)
{
    Console.WriteLine("OK — aucun réglage sensible dans l'archive.");
}
else
{
    Console.WriteLine("DANGER — ces fichiers ne devraient pas y être :");
    foreach (var entree in interdits) Console.WriteLine($"  {entree.FullName}");
    return 1;
}

// Et la preuve par le contenu : on cherche le mot de passe réel du poste dans l'archive.
var motDePasse = MotDePasseDuPoste(config);
if (motDePasse is not null)
{
    foreach (var entree in zip.Entries)
    {
        using var flux = entree.Open();
        using var lecteur = new StreamReader(flux);
        if (lecteur.ReadToEnd().Contains(motDePasse, StringComparison.Ordinal))
        {
            Console.WriteLine($"DANGER — le mot de passe du poste se retrouve dans {entree.FullName}");
            return 1;
        }
    }

    Console.WriteLine("OK — le mot de passe du poste ne se retrouve nulle part dans l'archive.");
}

return 0;

// Le mot de passe réellement configuré sur ce poste, pour le chercher dans l'archive.
static string? MotDePasseDuPoste(string config)
{
    var chemin = Path.Combine(config, "mail.json");
    if (!File.Exists(chemin)) return null;

    using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(chemin));

    foreach (var propriete in document.RootElement.EnumerateObject())
        if (propriete.Name.Contains("MotDePasse", StringComparison.OrdinalIgnoreCase)
            && propriete.Value.GetString() is { Length: > 3 } valeur)
            return valeur;

    return null;
}
