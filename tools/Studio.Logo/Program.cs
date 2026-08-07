using Studio.Imaging;

// Redessine le logo de l'application dans src/Studio.App/Assets.
//
// À relancer quand la marque change, et à ce moment-là seulement : l'icône est VERSIONNÉE,
// elle n'est pas fabriquée à la compilation. Une icône qui se refait à chaque build change
// d'octets sans changer de dessin, et le dépôt en garde la trace à chaque commit.
//
// Usage : dotnet run --project tools/Studio.Logo [dossier de sortie]

var sortie = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
        "src", "Studio.App", "Assets");

sortie = Path.GetFullPath(sortie);
Directory.CreateDirectory(sortie);

MagickInit.Configure();

Studio.Logo.Logo.Ecrire(
    Path.Combine(sortie, "studio-photo.ico"),
    Path.Combine(sortie, "studio-photo.png"));

// Le curseur d'attente : le même diaphragme, qui tourne. Versionné comme l'icône, et
// refait ici pour la même raison — il doit suivre la marque quand elle change.
Studio.Logo.CurseurAttente.Ecrire(Path.Combine(sortie, "studio-attente.ani"));

// Une bande des images du cycle, sur demande : c'est le seul moyen de juger le mouvement
// sans installer le curseur. Elle n'est PAS versionnée — d'où le chemin à donner.
//   dotnet run --project tools/Studio.Logo -- <sortie> --apercu <fichier.png>
var apercu = Array.IndexOf(args, "--apercu");
if (apercu >= 0 && apercu + 1 < args.Length)
{
    Studio.Logo.CurseurAttente.EcrireApercu(args[apercu + 1]);
    Console.WriteLine($"Aperçu écrit dans {args[apercu + 1]}");
}

Console.WriteLine($"Logo et curseur écrits dans {sortie}");
