using System.IO;
using Studio.Core.Catalog;
using Studio.Imaging.Geometry;

namespace Studio.App.Infrastructure;

/// <summary>
/// Le référentiel des 274 documents d'identité, et la façon de le retrouver.
///
/// Trois écrans en ont besoin — le choix du document, les raccourcis, et la reprise d'une
/// planche depuis les commandes du jour — et chacun en avait sa copie, chemin de repli
/// compris. La troisième copie est celle de trop : un référentiel introuvable ne doit pas
/// se rattraper à trois endroits différents.
/// </summary>
public static class ReferentielIdentite
{
    private const string NomDuFichier = "diland-id-documents.json";

    /// <summary>
    /// Charge le référentiel. Un fichier absent ou illisible rend la seule norme
    /// française : l'écran doit rester utilisable, c'est celle qu'on tire tous les jours.
    /// </summary>
    public static IReadOnlyList<IdDocumentSpec> Charger()
    {
        try
        {
            var chemin = Path.Combine(App.Services.CatalogDir, NomDuFichier);

            // …puis celui LIVRÉ avec l'application.
            //
            // Il manquait, et le repli sur la seule norme française passait pour un choix :
            // le poste de Créteil n'a proposé que le 35×45 français, alors que les 274
            // documents existaient dans le dépôt. Le chemin ci-dessous n'y menait que sur
            // un poste de développement — remonter l'arborescence depuis l'exécutable ne
            // trouve un dossier « catalog\ » que quand on tourne depuis les sources.
            //
            // Lu depuis l'installation plutôt que recopié dans les données : c'est un
            // référentiel de NORMES, pas une donnée du poste. Chaque version livre le sien,
            // et il n'y a rien à resynchroniser. Le dossier de données l'emporte tout de
            // même, pour qu'un poste puisse le corriger sans attendre une version.
            if (!File.Exists(chemin))
                chemin = Path.Combine(
                    AppContext.BaseDirectory, CatalogueLivre.NomDuDossier, NomDuFichier);

            // le référentiel vit dans le dépôt tant qu'il n'a pas été installé
            if (!File.Exists(chemin) && TrouverDansLeDepot() is { } depot) chemin = depot;

            return File.Exists(chemin) ? IdDocumentCatalog.Load(chemin) : [IdDocumentSpec.France];
        }
        catch (Exception ex)
        {
            FileLog.Write("Référentiel des documents d'identité illisible", ex);
            return [IdDocumentSpec.France];
        }
    }

    private static string? TrouverDansLeDepot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidat = Path.Combine(dir.FullName, "catalog", NomDuFichier);
            if (File.Exists(candidat)) return candidat;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>
    /// Le document dont le TIRAGE fait ces cotes, à un demi-millimètre près.
    ///
    /// C'est tout ce qu'une commande enregistrée garde d'une planche d'identité : la
    /// taille de sa case (<c>OrderItem.SheetCellWidthMm</c>). Le pays n'y est pas, et
    /// c'est assumé — la géométrie du cadrage ne dépend que des cotes et des bornes de
    /// visage, jamais du nom du pays.
    ///
    /// La norme française passe EN PREMIER : le référentiel de DiLand porte trois entrées
    /// en 35 × 45 pour la France, qui laissent la marge de crâne estimée, là où la nôtre la
    /// fixe à 3 mm. Sur le format qu'on tire toute la journée, c'est la nôtre qu'il faut.
    /// </summary>
    /// <returns>Null si aucun document ne correspond.</returns>
    public static IdDocumentSpec? ParLesCotes(double largeurMm, double hauteurMm)
    {
        if (largeurMm <= 0 || hauteurMm <= 0) return null;

        bool Correspond(IdDocumentSpec d) =>
            Math.Abs(d.WidthMm - largeurMm) <= 0.5 && Math.Abs(d.HeightMm - hauteurMm) <= 0.5;

        return Correspond(IdDocumentSpec.France)
            ? IdDocumentSpec.France
            : Charger().FirstOrDefault(Correspond);
    }
}
