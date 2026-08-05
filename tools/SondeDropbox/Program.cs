using Studio.Core.Cloud;
using Studio.Web.Dropbox;

// Sonde de configuration Dropbox : dit quelle permission passe et laquelle bloque, sans
// avoir a relancer un televersement complet.
//
// Par defaut, LECTURE SEULE : rien n'est cree ni supprime dans le Dropbox du studio.
// Ajouter « --ecriture » pour verifier aussi les permissions d'ecriture et de partage :
// la sonde cree alors un dossier d'essai qu'elle supprime aussitot.

Console.OutputEncoding = System.Text.Encoding.UTF8;

var avecEcriture = args.Contains("--ecriture");
var config = args.FirstOrDefault(a => !a.StartsWith("--")) ?? @"D:\PhotoStudioData\config";

var reglages = DropboxSettings.Load(config);

Console.WriteLine($"Configuration : {config}");
Console.WriteLine($"Clé app       : {(string.IsNullOrWhiteSpace(reglages.AppKey) ? "ABSENTE" : reglages.AppKey)}");
Console.WriteLine($"Jeton         : {(string.IsNullOrWhiteSpace(reglages.RefreshToken) ? "ABSENT" : "présent (masqué)")}");
Console.WriteLine($"Racine        : « {reglages.RacineNormalisee()} »");
Console.WriteLine($"Actif         : {reglages.Actif}");
Console.WriteLine();

if (!reglages.EstUtilisable)
{
    Console.WriteLine($"ARRÊT : il manque {reglages.CeQuiManque()}.");
    return 1;
}

string jeton;
try
{
    jeton = await DropboxAuth.JetonDAccesAsync(reglages.AppKey, reglages.RefreshToken);
    Console.WriteLine("Jeton d'accès obtenu : OK");
}
catch (Exception ex)
{
    Console.WriteLine($"Jeton d'accès REFUSÉ : {ex.Message}");
    return 1;
}

using var client = new DropboxClient(jeton);
var echecs = 0;

async Task Verifier(string permission, string quoi, Func<Task> essai)
{
    Console.Write($"  {permission,-22} ({quoi})... ");
    try
    {
        await essai();
        Console.WriteLine("OK");
    }
    catch (Exception ex)
    {
        echecs++;
        Console.WriteLine("ÉCHEC");
        Console.WriteLine();
        Console.WriteLine(ex.Message);
        Console.WriteLine();
    }
}

Console.WriteLine("\n=== Permissions en lecture ===");

await Verifier("account_info.read", "nom du compte",
    async () => Console.Write($"compte « {await client.NomDuCompteAsync()} » — "));

await Verifier("files.metadata.read", "lister la racine",
    async () =>
    {
        var dossiers = await client.ListerLesDossiersAsync(reglages.RacineNormalisee());
        Console.Write($"{dossiers.Count} dossier(s) — ");

        var limite = DateTime.Now.AddDays(-reglages.RetentionJours);
        foreach (var d in dossiers.OrderBy(d => d.Nom))
        {
            var date = DropboxMenage.DateDuDossier(d.Nom);
            var sort = date is null ? "IGNORÉ (nom qui n'est pas le nôtre)"
                : date <= limite ? "à supprimer au prochain ménage"
                : $"gardé jusqu'au {date.Value.AddDays(reglages.RetentionJours):dd/MM/yyyy HH:mm}";
            Console.Write($"\n      • {d.Nom,-44} {sort}");
        }
        Console.Write("\n    ");
    });

// Ce que l'application voit de la racine du compte : avec un acces « App folder », elle
// est enfermee dans son propre dossier et ne peut RIEN voir d'autre.
Console.WriteLine("\n=== Ce que l'application voit à la racine du compte ===");
try
{
    var racineDuCompte = await client.ListerLesDossiersAsync("");
    foreach (var d in racineDuCompte.OrderBy(d => d.Nom))
        Console.WriteLine($"  • {d.Nom}");
    if (racineDuCompte.Count == 0) Console.WriteLine("  (aucun dossier)");
}
catch (Exception ex)
{
    Console.WriteLine($"  illisible : {ex.Message}");
}

if (!avecEcriture)
{
    Console.WriteLine("\n=== Permissions en écriture : NON VÉRIFIÉES ===");
    Console.WriteLine("  Relancez avec « --ecriture » pour les contrôler.");
    Console.WriteLine("  La sonde créera alors un dossier d'essai et le supprimera aussitôt.");
}
else
{
    Console.WriteLine("\n=== Permissions en écriture ===");

    var essaiDossier = $"{reglages.RacineNormalisee()}/_essai-studio-{DateTime.Now:yyyyMMdd-HHmmss}";

    await Verifier("files.content.write", "créer un dossier",
        () => client.CreerLeDossierAsync(essaiDossier));

    await Verifier("sharing.write", "créer un lien de partage",
        async () =>
        {
            var lien = await client.PartagerAsync(essaiDossier, 0, null);
            Console.Write($"{lien.Url[..Math.Min(40, lien.Url.Length)]}… — ");
        });

    await Verifier("files.content.write", "supprimer le dossier d'essai",
        () => client.SupprimerAsync(essaiDossier));

    Console.WriteLine($"\n  (dossier d'essai : {essaiDossier})");
}

Console.WriteLine();
if (echecs == 0)
{
    Console.WriteLine(avecEcriture
        ? "TOUT EST BON : l'envoi par Dropbox est prêt."
        : "Lecture OK. Relancez avec « --ecriture » pour finir de vérifier.");
    return 0;
}

Console.WriteLine($"{echecs} permission(s) en échec — voir les messages ci-dessus.");
return 1;
