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

Console.WriteLine($"Logo écrit dans {sortie}");
