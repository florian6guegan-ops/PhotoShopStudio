using Studio.Core.Domain;

namespace Studio.Store;

/// <summary>
/// Refait, à partir d'une commande déjà passée, le travail que l'opérateur avait fait sur
/// ses photos — cadrages, corrections, formats, quantités, finitions.
///
/// <b>Pourquoi.</b> « Commandes du jour » → « Modifier » ne rouvrait que le DOSSIER des
/// photos. Tout ce que l'opérateur avait réglé — un cadrage repris trois fois, une
/// exposition remontée, un format par photo, les quantités — était perdu, et il fallait
/// tout refaire pour retirer une seule photo. Signalé le 17/08/2026.
///
/// <b>Comment.</b> La grille sait DÉJÀ reprendre un travail : c'est le mécanisme des
/// commandes mises de côté (<see cref="TravailEnAttente"/>), éprouvé en boutique, qui gère
/// jusqu'aux doublons d'une même photo tirée en plusieurs formats. On ne réinvente donc
/// rien : on traduit la commande dans cette forme-là, et l'écran fait le reste.
/// C'est la règle du dépôt — <b>les BOUTONS se doublent, ce qu'ils font, non.</b>
/// </summary>
public static class TravailDepuisCommande
{
    /// <summary>
    /// Traduit les enveloppes retenues d'une commande en travail reprenable.
    /// </summary>
    /// <param name="enveloppes">
    /// Les enveloppes à reprendre — celles que l'écran affiche, pas forcément toutes celles
    /// de la commande.
    /// </param>
    /// <param name="dossierDesPhotos">Le dossier où l'écran retrouvera les fichiers.</param>
    /// <param name="titre">L'intitulé de l'écran, repris tel quel.</param>
    /// <returns>
    /// Un travail à passer en <c>enAttente</c> à la grille. <b>Son identifiant est NEUF</b>
    /// et ne désigne aucune commande mise de côté : rouvrir une commande du jour ne doit
    /// effacer ni modifier aucune attente existante. S'il est ensuite mis de côté, il
    /// devient une attente à part entière, ce qui est bien ce qu'on veut.
    /// </returns>
    public static TravailEnAttente Traduire(
        IEnumerable<Envelope> enveloppes, string dossierDesPhotos, string titre)
    {
        ArgumentNullException.ThrowIfNull(enveloppes);

        var lignes = enveloppes.SelectMany(e => e.Lines).ToList();

        var travail = new TravailEnAttente
        {
            Id = Guid.NewGuid(),
            PhotosDirectory = dossierDesPhotos,

            // Le dossier d'une commande ne contient QUE ses photos, à plat. Descendre en
            // dessous ramènerait les rendus et les fichiers de suivi.
            AvecSousDossiers = false,
            Titre = titre,
        };

        // LA PLANCHE PERSONNALISÉE, s'il y en a une. Sans elle, la reprise repartirait au
        // format du catalogue et remettrait tous les cadres au centre, au mauvais rapport —
        // le format demandé serait perdu une seconde fois.
        if (lignes.FirstOrDefault(l => l.IsCustomSheet) is { } planche)
        {
            travail.CustomWidthMm = planche.CustomCellWidthMm!.Value;
            travail.CustomHeightMm = planche.CustomCellHeightMm!.Value;

            // sur une ligne de planche, le code produit désigne le PAPIER
            travail.PaperCode = planche.ProductCode;
        }

        // Le montage des agrandissements, pour la même raison : le perdre ferait ressortir
        // la commande en un fichier par tirage, donc sur deux fois plus de papier.
        travail.MontageSheetCode = lignes
            .Select(l => l.MontageSheetCode)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        // Le produit de la barre : le plus représenté, comme le faisait l'écran des
        // commandes. Il ne sert qu'à préremplir la liste — chaque photo porte le sien.
        travail.ProduitParDefaut = lignes
            .Where(l => !l.IsCustomSheet)
            .GroupBy(l => l.ProductCode)
            .OrderByDescending(g => g.Sum(l => l.Items.Count))
            .Select(g => g.Key)
            .FirstOrDefault();

        foreach (var ligne in lignes)
        foreach (var article in ligne.Items)
            travail.Photos.Add(Traduire(ligne, article));

        return travail;
    }

    /// <summary>
    /// La même chose pour une PLANCHE D'IDENTITÉ, qui se reprend dans son propre écran.
    ///
    /// <b>⚠ Les repères de crâne et de menton ne sont PAS dans la commande.</b> Elle ne garde
    /// que le cadrage, les corrections et les fonds ; les repères n'appartiennent qu'au
    /// travail mis de côté. La photo revient donc avec <c>Prete = false</c>, de sorte que la
    /// détection de visage les retrouve — et l'écran, lui, sait ne PAS recadrer par-dessus un
    /// cadrage repris (voir <c>IdPhotoView.OuvrirLaPhotoAsync</c>). On récupère ainsi les
    /// deux : les repères par la détection, le cadrage tel que l'opérateur l'avait laissé.
    /// </summary>
    /// <param name="articles">Les articles des lignes d'identité de la commande.</param>
    /// <param name="norme">
    /// La norme visée, déjà remplie par l'appelant — lui seul sait la déduire de la taille de
    /// case enregistrée. Cette méthode n'y touche pas et ne fait qu'ajouter ses photos.
    /// </param>
    public static TravailEnAttente TraduireIdentite(
        IEnumerable<OrderItem> articles, string dossierDesPhotos, string titre,
        IdentiteEnAttente norme)
    {
        ArgumentNullException.ThrowIfNull(articles);
        ArgumentNullException.ThrowIfNull(norme);

        foreach (var article in articles)
            norme.Photos.Add(new PhotoIdentiteEnAttente
            {
                FileName = article.FileName,
                Selected = true,
                Quantity = Math.Max(1, article.Quantity),

                // 0 = « planche pleine » : l'écran recalera sur la capacité du papier
                Copies = article.SheetCopiesOverride ?? 0,

                // Voir plus haut : sans repères en magasin, on laisse la détection les
                // retrouver, et l'écran garde le cadrage repris.
                Prete = false,

                CropX = article.Crop.X,
                CropY = article.Crop.Y,
                CropWidth = article.Crop.Width,
                CropHeight = article.Crop.Height,

                Redressement = article.FineRotationDegrees,

                // Les trois cases vivent DANS les réglages une fois la commande écrite
                // (voir IdPhotoView.ReglagesDe) : c'est de là qu'il faut les relire.
                NoirEtBlanc = article.Adjustments.Grayscale,
                FondBlanc = article.Adjustments.WhiteBackground,
                FondGris = article.Adjustments.GrayBackground,

                Corrections = article.Adjustments.Clone(),
            });

        return new TravailEnAttente
        {
            Id = Guid.NewGuid(),
            PhotosDirectory = dossierDesPhotos,
            AvecSousDossiers = false,
            Titre = titre,
            Identite = norme,
        };
    }

    /// <summary>
    /// Une photo de la commande, telle que l'opérateur l'avait réglée.
    ///
    /// <b>Elle revient COCHÉE</b> : elle faisait partie de la commande, donc l'opérateur
    /// l'avait retenue. Rouvrir avec tout décoché obligerait à re-cocher quinze photos pour
    /// en retirer une.
    /// </summary>
    private static PhotoEnAttente Traduire(OrderLine ligne, OrderItem article) => new()
    {
        FileName = article.FileName,
        Selected = true,
        Quantity = Math.Max(1, article.Quantity),

        // Sur une planche personnalisée, le code de la ligne est le PAPIER et non un format
        // de photo : le poser sur la photo lui donnerait le rapport du papier, et le cadrage
        // repris ne voudrait plus rien dire. L'écran retrouve le bon par la taille de case.
        ProductCode = ligne.IsCustomSheet ? null : ligne.ProductCode,
        Finish = article.Finish,

        CropX = article.Crop.X,
        CropY = article.Crop.Y,
        CropWidth = article.Crop.Width,
        CropHeight = article.Crop.Height,

        RotationQuarterTurns = article.RotationQuarterTurns,
        FineRotationDegrees = article.FineRotationDegrees,

        Fit = article.FitOverride,
        CutBorder = article.CutBorder,

        // une COPIE : les réglages appartiennent à la commande enregistrée, et la reprise ne
        // doit pas les modifier sous elle
        Adjustments = article.Adjustments.Clone(),
    };
}
