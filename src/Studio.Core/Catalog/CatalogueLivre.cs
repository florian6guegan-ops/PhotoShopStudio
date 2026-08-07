namespace Studio.Core.Catalog;

/// <summary>
/// Le catalogue livré AVEC l'application, posé dans le dossier de données au premier
/// démarrage d'un poste neuf.
///
/// <b>C'est ce qui manquait à l'installation du 07/08/2026.</b> Un poste neuf n'a pas de
/// <c>catalog\products.json</c> : l'application lui en fabriquait un avec
/// <see cref="ProductCatalog.CreateDefaultProducts"/> — cinq produits d'amorçage, dont
/// quatre pointent sur « Microsoft Print to PDF ». Le collègue de Créteil s'est donc
/// retrouvé devant un logiciel qui démarre, qui affiche des tirages, et dont rien ne sort
/// sur les machines qu'il a pourtant sous la main. Le vrai catalogue existait, versionné,
/// dans <c>catalog\boutique\</c> — mais il ne partait pas dans l'archive publiée, et rien
/// dans le guide ne disait comment l'obtenir.
///
/// Le dossier livré est celui que <c>tools\Publier.ps1</c> recopie à côté de l'exécutable.
///
/// <b>Ne remplace JAMAIS un catalogue existant.</b> Un poste qui tourne a des prix, des
/// formats et des réglages pilote qui lui appartiennent : les écraser à la faveur d'une
/// mise à jour serait bien pire que l'absence qu'on corrige ici.
/// </summary>
public static class CatalogueLivre
{
    /// <summary>
    /// Nom du dossier, à côté de l'exécutable. Il est en dur des deux côtés — ici et dans
    /// <c>Publier.ps1</c> — et c'est le seul lien entre les deux.
    /// </summary>
    public const string NomDuDossier = "catalogue";

    /// <summary>Le dossier livré avec cette installation.</summary>
    public static string DossierParDefaut =>
        Path.Combine(AppContext.BaseDirectory, NomDuDossier);

    /// <summary>
    /// Pose le catalogue livré dans <paramref name="dossierCatalogue"/> s'il n'en a pas
    /// encore. Rend vrai si quelque chose a été posé.
    ///
    /// <b>Un catalogue livré illisible ne doit pas empêcher de démarrer.</b> Il est donc
    /// relu avant d'être recopié : au moindre doute on rend faux, et l'appelant retombe
    /// sur les produits d'amorçage. Un poste qui démarre avec un catalogue pauvre se
    /// rattrape depuis l'écran Catalogue ; un poste qui ne démarre pas, non.
    /// </summary>
    /// <param name="dossierCatalogue">Le <c>catalog\</c> du dossier de données.</param>
    /// <param name="dossierLivre">Où chercher ; par défaut <see cref="DossierParDefaut"/>.</param>
    public static bool PoserSiAbsent(string dossierCatalogue, string? dossierLivre = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dossierCatalogue);

        var cible = Path.Combine(dossierCatalogue, "products.json");
        var reprise = File.Exists(cible);

        // Un poste installé AVANT cette correction porte déjà un catalogue : celui
        // d'amorçage, que la version précédente lui a fabriqué faute de mieux. Sans cette
        // reprise, il le garderait pour toujours — la correction ne servirait qu'aux
        // installations neuves, et le poste de Créteil resterait exactement où il est.
        if (reprise && !EstLeCatalogueDAmorcage(cible)) return false;

        var source = dossierLivre ?? DossierParDefaut;
        var livre = Path.Combine(source, "products.json");
        if (!File.Exists(livre)) return false;

        try
        {
            // relu pour de bon, et non simplement « le fichier existe » : un JSON tronqué
            // par une archive mal décompressée passerait le test de présence et ferait
            // échouer le démarrage juste après, là où plus rien ne peut le rattraper.
            if (ProductCatalog.Load(livre).All.Count == 0) return false;

            Directory.CreateDirectory(dossierCatalogue);

            // On ne remplace jamais un fichier sans filet, même celui-là : si la
            // reconnaissance se trompait un jour, l'ancien resterait à côté.
            if (reprise)
            {
                var horodatage = DateTime.Now.ToString("yyyy-MM-dd-HHmmss");
                File.Copy(cible, Path.Combine(dossierCatalogue, $"products.amorcage-{horodatage}.json"), overwrite: true);
            }

            File.Copy(livre, cible, overwrite: true);

            // Les réglages pilote capturés au dialogue. Sans eux, la planche d'identité
            // sort avec les réglages par défaut de la DS620 — mauvaise finition, mauvaise
            // découpe — et ils ne se recapturent qu'avec l'imprimante sous la main.
            foreach (var devmode in Directory.EnumerateFiles(source, "devmode-*.bin"))
                File.Copy(devmode, Path.Combine(dossierCatalogue, Path.GetFileName(devmode)), overwrite: true);

            return true;
        }
        catch (Exception)
        {
            // dossier en lecture seule, archive incomplète, JSON illisible : on laisse
            // l'appelant poser les produits d'amorçage
            return false;
        }
    }

    /// <summary>
    /// Ce catalogue est-il celui d'amorçage, jamais touché depuis ?
    ///
    /// Reconnu sur ses codes, à l'identique et sans rien de plus : dès qu'un produit a été
    /// ajouté, retiré ou renommé, quelqu'un s'en est servi et le fichier lui appartient.
    /// Les PRIX et les réglages, eux, ne comptent pas — on ne va pas garder un catalogue
    /// d'amorçage sous prétexte que son 10×15 a été passé à 0,65 €, alors qu'il ne sait
    /// toujours imprimer que sur « Microsoft Print to PDF ».
    ///
    /// Volontairement strict : dans le doute, on ne touche à rien.
    /// </summary>
    public static bool EstLeCatalogueDAmorcage(string productsJson)
    {
        try
        {
            var codes = ProductCatalog.Load(productsJson).All
                .Select(p => p.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var amorcage = ProductCatalog.CreateDefaultProducts()
                .Select(p => p.Code)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return codes.SetEquals(amorcage);
        }
        catch (Exception)
        {
            // illisible : ce n'est pas à nous d'en décider, et surtout pas de l'écraser
            return false;
        }
    }
}
