using System.IO;
using Microsoft.Win32;
using Studio.Core.Domain;

namespace Studio.App.Infrastructure;

/// <summary>
/// Les dossiers épinglés du poste, posés dans TOUTES les boîtes de fichiers de Windows —
/// et proposés en tuile dans le choix du support.
///
/// <b>Ce que ça règle.</b> Les photos d'un client arrivent par trois chemins qui ne changent
/// jamais : sa clé, le Bureau où on les a déposées, les Téléchargements du navigateur, ou le
/// dossier où l'on range ce qui vient de WeTransfer. Les boîtes de Windows, elles, s'ouvrent
/// sur « Ce PC » et il faut redescendre l'arborescence à chaque fois, devant le client. Le
/// volet de gauche d'une boîte de fichiers existe exactement pour ça — encore faut-il y
/// mettre quelque chose.
///
/// <b>Un seul endroit pour tout le monde.</b> Six boîtes s'ouvrent dans l'application
/// (profils ICC, DEVMODE, dossier de sauvegarde, journal, logo de la marque, point de départ
/// des photos) ; chacune reçoit les mêmes favoris, sans avoir à les connaître.
///
/// <b>Un favori qui ne mène nulle part n'est jamais posé</b> : un dossier absent — le poste
/// n'a pas de dossier WeTransfer, ou l'opérateur l'a supprimé — ferait une entrée morte dans
/// le volet, et Windows refuse d'ailleurs de l'y mettre. On le saute en silence.
/// </summary>
public static class DossiersFavoris
{
    /// <summary>
    /// Le réglage en vigueur. Posé au démarrage par <c>AppServices</c> ; sans lui, on
    /// retombe sur les trois favoris par défaut, qui sont ce qu'on veut de toute façon.
    /// </summary>
    public static FavorisSettings Reglage { get; set; } = new();

    /// <summary>
    /// Les favoris qui mènent vraiment quelque part, dans l'ordre du réglage.
    /// </summary>
    /// <returns>Libellé et chemin résolu.</returns>
    public static IReadOnlyList<(string Libelle, string Chemin)> Actifs()
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actifs = new List<(string, string)>();

        foreach (var favori in Reglage.Effectifs)
        {
            if (!favori.Actif) continue;
            if (DossiersUtilisateur.Resoudre(favori) is not { } chemin) continue;

            // Deux favoris qui tombent sur le même dossier : le volet de Windows n'en
            // montrerait qu'un, autant ne pas le lui demander deux fois.
            //
            // La barre finale est retirée à la main : GetFullPath ne la normalise PAS, et
            // « D:\Photos » et « D:\Photos\ » passaient donc pour deux dossiers différents.
            if (!vus.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(chemin)))) continue;

            actifs.Add((favori.Libelle, chemin));
        }

        return actifs;
    }

    /// <summary>
    /// Épingle les favoris dans le volet de gauche d'une boîte Windows.
    ///
    /// Vaut pour les trois sortes — ouvrir un fichier, en enregistrer un, choisir un
    /// dossier : elles héritent toutes de <see cref="CommonItemDialog"/>, qui porte
    /// <c>CustomPlaces</c>.
    ///
    /// Ne lève jamais. Une boîte de fichiers qui refuse de s'ouvrir parce qu'un favori a
    /// déplu serait un défaut bien pire que celui qu'on corrige.
    /// </summary>
    public static void Epingler(CommonItemDialog boite)
    {
        ArgumentNullException.ThrowIfNull(boite);

        foreach (var (_, chemin) in Actifs())
        {
            try
            {
                boite.CustomPlaces.Add(new FileDialogCustomPlace(chemin));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException
                                           or UnauthorizedAccessException)
            {
                // dossier devenu illisible entre la résolution et ici : tant pis pour
                // celui-là, les autres sont posés
            }
        }
    }

    /// <summary>
    /// Les mêmes favoris, en tuiles pour l'écran de choix du support.
    ///
    /// La boîte de Windows n'est pas le seul chemin vers les photos : l'écran « d'où
    /// viennent les photos ? » propose les cartes et les clés, et c'est là qu'un opérateur
    /// regarde en premier. Les favoris y ont donc leur tuile, avec la même liste et le même
    /// réglage — sans quoi il faudrait passer par « Parcourir » pour atteindre le Bureau.
    ///
    /// <b>Sans descendre dans les sous-dossiers</b>, comme tout dossier désigné à la main :
    /// « Téléchargements » contient tout ce que le navigateur a rapporté depuis des mois, et
    /// le parcourir en profondeur ramènerait des dizaines de milliers de fichiers.
    /// </summary>
    public static IReadOnlyList<Views.SourcePickerView.DossierRaccourci> Raccourcis() =>
        Actifs()
            .Select(f => new Views.SourcePickerView.DossierRaccourci(
                Picto(f.Libelle) + "  " + f.Libelle, f.Chemin))
            .ToList();

    /// <summary>
    /// Le pictogramme d'une tuile de raccourci. Purement décoratif : il rend les trois
    /// dossiers reconnaissables du coin de l'œil, ce qu'un libellé seul ne fait pas.
    /// </summary>
    private static string Picto(string libelle) => libelle.ToLowerInvariant() switch
    {
        var l when l.Contains("bureau") => "🖥",
        var l when l.Contains("téléchargement") || l.Contains("telechargement") => "⬇",
        var l when l.Contains("transfer") => "☁",
        _ => "🗀",
    };
}
