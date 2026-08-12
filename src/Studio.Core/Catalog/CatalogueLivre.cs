using Studio.Core.Domain;

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

            // CE QUE L'EXPLOITANT A AJOUTÉ LUI RESTE.
            //
            // Le catalogue d'amorçage se remplace sans regret — il n'imprime nulle part —,
            // mais un produit créé À CÔTÉ est un vrai travail : le 40×50 de
            // DESKTOP-KT88VDM avait été fabriqué à la main en dupliquant un 30×40. Le
            // perdre à la faveur d'une reprise serait payer la correction du prix d'un
            // dégât, et l'exploitant n'aurait aucune raison de faire le lien.
            var ajoutes = reprise ? ProduitsAjoutes(cible, livre) : [];

            File.Copy(livre, cible, overwrite: true);

            if (ajoutes.Count > 0)
            {
                var fusionne = ProductCatalog.Load(cible).All.ToList();
                fusionne.AddRange(ajoutes);
                ProductCatalog.Save(cible, fusionne);
            }

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
    /// Garantit que le poste a un catalogue, et le meilleur dont on dispose : celui livré
    /// avec l'application, sinon les produits d'amorçage.
    ///
    /// <b>C'est le point d'entrée, et il n'y en a qu'un.</b> Le démarrage enchaînait
    /// lui-même les deux décisions :
    ///
    /// <code>
    /// if (!File.Exists(productsJson) &amp;&amp; !PoserSiAbsent(...))
    ///     Save(productsJson, CreateDefaultProducts());
    /// </code>
    ///
    /// — et le court-circuit du <c>&amp;&amp;</c> faisait que <see cref="PoserSiAbsent"/>
    /// n'était JAMAIS appelée quand un catalogue existait, c'est-à-dire dans le seul cas
    /// où la reprise a quelque chose à faire. Le poste de Créteil, mis à jour en 1.3.2 le
    /// 07/08/2026 à 23:06, a donc gardé ses cinq produits d'amorçage. La méthode était
    /// pourtant juste et vérifiée : c'est son appel qui ne l'était pas. D'où cet
    /// enchaînement, écrit une fois et vérifiable de bout en bout.
    /// </summary>
    /// <param name="dossierCatalogue">Le <c>catalog\</c> du dossier de données.</param>
    /// <param name="dossierLivre">Où chercher ; par défaut <see cref="DossierParDefaut"/>.</param>
    public static void AssurerUnCatalogue(string dossierCatalogue, string? dossierLivre = null)
    {
        if (PoserSiAbsent(dossierCatalogue, dossierLivre)) return;

        // Rien n'a été posé : soit le poste a son propre catalogue et on n'y touche pas,
        // soit rien n'était livré et il faut bien lui donner de quoi démarrer.
        var cible = Path.Combine(dossierCatalogue, "products.json");
        if (File.Exists(cible)) return;

        Directory.CreateDirectory(dossierCatalogue);
        ProductCatalog.Save(cible, ProductCatalog.CreateDefaultProducts());
    }

    /// <summary>
    /// Va chercher, dans le dossier couleur de Windows, les profils ICC que le catalogue
    /// réclame et qui ne sont pas encore dans <c>catalog\icc</c>.
    ///
    /// <b>Ils sont déjà sur le poste, posés par les pilotes.</b> Le catalogue de la
    /// boutique nomme <c>DS620-R0.icc</c> pour la planche d'identité ; ce fichier de
    /// 1,6 Mo n'est pas versionné — c'est un fichier du fabricant — mais le pilote DNP
    /// l'installe dans le dossier couleur du spouleur en même temps que lui. Il n'y a donc
    /// rien à livrer ni à télécharger : seulement à le recopier là où le catalogue le
    /// cherche.
    ///
    /// Sans cela, le poste de Créteil a tiré ses planches d'identité SANS GESTION COULEUR
    /// depuis son installation, et rien ne le disait — le profil manquant ne fait pas
    /// échouer l'impression, il la laisse partir en sRGB présumé.
    ///
    /// <b>N'écrase jamais un profil déjà importé</b> : l'atelier a pu en poser un corrigé.
    /// </summary>
    /// <param name="dossierCatalogue">Le <c>catalog\</c> du dossier de données.</param>
    /// <param name="dossierCouleurWindows">Où les pilotes déposent leurs profils.</param>
    /// <returns>Les noms de fichiers effectivement importés.</returns>
    public static IReadOnlyList<string> ImporterLesProfilsManquants(
        string dossierCatalogue, string dossierCouleurWindows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dossierCatalogue);

        var productsJson = Path.Combine(dossierCatalogue, "products.json");
        if (!File.Exists(productsJson) || !Directory.Exists(dossierCouleurWindows)) return [];

        try
        {
            var reclames = ProfilsReclames(ProductCatalog.Load(productsJson));
            if (reclames.Count == 0) return [];

            var dossierIcc = Path.Combine(dossierCatalogue, "icc");
            var importes = new List<string>();

            // Relevé UNE fois : le dossier couleur de Windows en contient couramment
            // plusieurs dizaines, et on le parcourrait sinon par profil réclamé.
            var poses = Directory.EnumerateFiles(dossierCouleurWindows, "*.icc").ToList();

            foreach (var nom in reclames)
            {
                var cible = Path.Combine(dossierIcc, nom);
                if (File.Exists(cible)) continue;

                var source = TrouverLeProfil(poses, dossierCouleurWindows, nom);
                if (source is null) continue;

                Directory.CreateDirectory(dossierIcc);
                File.Copy(source, cible);
                importes.Add(nom);
            }

            return importes;
        }
        catch (Exception)
        {
            // catalogue illisible, dossier en lecture seule : un profil manquant ne vaut
            // pas d'empêcher le démarrage — le tirage part en sRGB présumé, comme avant
            return [];
        }
    }

    /// <summary>
    /// Le fichier du dossier couleur qui correspond au profil réclamé, ou null.
    ///
    /// <b>Le nom exact ne suffit pas.</b> Le même profil DNP s'installe sous des noms
    /// différents selon la version du pilote : <c>DS620-R0.icc</c> à Maisons-Alfort,
    /// <c>PD_DS620-R0.icc</c> sur le poste DESKTOP-KT88VDM (constaté le 12/08/2026). Le
    /// catalogue, lui, nomme un seul des deux — et le poste qui ne l'avait pas sous ce
    /// nom-là s'est retrouvé sans gestion des couleurs, puis sans impression du tout tant
    /// que le profil manquant faisait échouer le rendu.
    ///
    /// On accepte donc un PRÉFIXE de fabricant, et rien de plus : le nom réclamé doit
    /// terminer celui du fichier, sur une frontière propre. Un profil qui se contenterait
    /// de contenir le nom quelque part ne serait pas le même produit.
    /// </summary>
    private static string? TrouverLeProfil(
        IEnumerable<string> poses, string dossierCouleurWindows, string nom)
    {
        var exact = Path.Combine(dossierCouleurWindows, nom);
        if (File.Exists(exact)) return exact;

        return poses.FirstOrDefault(chemin =>
        {
            var fichier = Path.GetFileName(chemin);
            if (!fichier.EndsWith(nom, StringComparison.OrdinalIgnoreCase)) return false;

            // « PD_DS620-R0.icc » convient ; « MONDS620-R0.icc » non — le préfixe doit se
            // terminer par un séparateur, sinon on rapproche deux profils sans rapport.
            var prefixe = fichier[..^nom.Length];
            return prefixe.Length > 0 && (prefixe.EndsWith('_') || prefixe.EndsWith('-'));
        });
    }

    /// <summary>
    /// Les profils nommés par le catalogue : ceux des produits, et ceux des finitions —
    /// le DE100 en a un par média, et ce sont ceux-là qu'on oublie.
    /// </summary>
    private static HashSet<string> ProfilsReclames(ProductCatalog catalogue)
    {
        var noms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var produit in catalogue.All)
        {
            if (!string.IsNullOrWhiteSpace(produit.IccProfile)) noms.Add(produit.IccProfile!);

            foreach (var finition in produit.Finishes ?? [])
                if (!string.IsNullOrWhiteSpace(finition.IccProfile)) noms.Add(finition.IccProfile!);
        }

        return noms;
    }

    /// <summary>
    /// Ce catalogue porte-t-il encore les produits d'amorçage INTACTS ?
    ///
    /// <b>Ce n'est plus « à l'identique et sans rien de plus ».</b> La reconnaissance
    /// portait sur l'ensemble EXACT des codes (<c>SetEquals</c>), et c'était trop strict
    /// d'un cran : elle se désarmait au premier produit ajouté — c'est-à-dire au premier
    /// geste d'un poste neuf. Le poste <c>DESKTOP-KT88VDM</c> l'a payé, découvert le
    /// 12/08/2026 : son opérateur avait dupliqué un 30×40 pour se faire un 40×50, la
    /// reprise ne l'a donc plus jamais reconnu, et ses cinq produits d'amorçage sont restés
    /// des semaines. <b>Toutes ses commandes partaient dans « Microsoft Print to PDF »</b>,
    /// pendant qu'il cherchait la panne du côté de sa DNP et de son DE100.
    ///
    /// On regarde donc les produits d'amorçage EUX-MÊMES, et non le contour de la liste :
    /// s'ils sont tous là et qu'aucun n'a été retouché — même code, mêmes cotes, même
    /// imprimante —, personne ne s'en est servi pour imprimer quoi que ce soit. Ce qui a
    /// été ajouté À CÔTÉ ne prouve rien : <see cref="PoserSiAbsent"/> le conserve.
    ///
    /// Le PRIX ne compte toujours pas — on ne va pas garder un catalogue d'amorçage sous
    /// prétexte que son 10×15 est passé à 0,65 €, alors qu'il ne sait toujours imprimer que
    /// sur « Microsoft Print to PDF ». Le NOM non plus.
    ///
    /// Reste volontairement strict sur ce qui compte : dès qu'un de ces cinq produits vise
    /// une vraie machine, l'exploitant a configuré son poste et le fichier lui appartient.
    /// </summary>
    public static bool EstLeCatalogueDAmorcage(string productsJson)
    {
        try
        {
            var presents = ProductCatalog.Load(productsJson).All
                .ToDictionary(p => p.Code, StringComparer.OrdinalIgnoreCase);

            var amorcage = ProductCatalog.CreateDefaultProducts();

            foreach (var attendu in amorcage)
            {
                if (!presents.TryGetValue(attendu.Code, out var trouve)) return false;
                if (!EstIntact(trouve, attendu)) return false;
            }

            return true;
        }
        catch (Exception)
        {
            // illisible : ce n'est pas à nous d'en décider, et surtout pas de l'écraser
            return false;
        }
    }

    /// <summary>
    /// Les produits du poste que le catalogue livré ne porte pas, et qui ne sont pas ceux
    /// d'amorçage : autrement dit, ce que l'exploitant a créé de sa main.
    ///
    /// Rendu vide au moindre doute — une liste illisible ne doit pas empêcher la reprise,
    /// qui reste le but principal.
    /// </summary>
    private static List<Product> ProduitsAjoutes(string catalogueDuPoste, string catalogueLivre)
    {
        try
        {
            var connus = ProductCatalog.Load(catalogueLivre).All
                .Select(p => p.Code)
                .Concat(ProductCatalog.CreateDefaultProducts().Select(p => p.Code))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            return ProductCatalog.Load(catalogueDuPoste).All
                .Where(p => !connus.Contains(p.Code))
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Ce produit est-il resté celui d'amorçage ?
    ///
    /// L'IMPRIMANTE d'abord, parce que c'est elle qui décide si une commande sort ou
    /// non ; les COTES ensuite, parce qu'on ne configure pas un poste sans toucher au
    /// format. Les deux ensemble ne se retrouvent pas par hasard sur un poste en service.
    /// </summary>
    private static bool EstIntact(Product trouve, Product amorcage) =>
        string.Equals(trouve.PrinterName, amorcage.PrinterName, StringComparison.OrdinalIgnoreCase)
        && Math.Abs(trouve.WidthMm - amorcage.WidthMm) < 0.01
        && Math.Abs(trouve.HeightMm - amorcage.HeightMm) < 0.01;
}
