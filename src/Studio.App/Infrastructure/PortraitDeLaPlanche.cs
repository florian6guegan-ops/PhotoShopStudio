using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Imaging.Geometry;

namespace Studio.App.Infrastructure;

/// <summary>
/// La GRANDE photo des deux formats de la rentrée : où elle tombe, à quelles cotes, et
/// comment son cadre se déduit de celui de l'identité.
///
/// Trois endroits en ont besoin — l'écran de cadrage pour proposer le cadre large et
/// l'annoncer, le récapitulatif pour montrer la planche, l'impression pour engager le
/// papier —, et aucun ne doit compter à sa façon : deux calculs de cotes finiraient par
/// diverger, et le portrait sortirait cadré à un rapport pour être posé dans un autre.
/// C'est la leçon déjà payée par la capacité des planches (voir <c>SheetSpec.LayoutGapMm</c>).
/// </summary>
public static class PortraitDeLaPlanche
{
    /// <summary>
    /// Les cotes de la grande photo, en millimètres, telle qu'elle sortira.
    ///
    /// Sur une <see cref="GenreDePlanche.Rentree"/>, c'est ce que les cases d'identité
    /// laissent du papier ; sur une <see cref="GenreDePlanche.PlancheEtTirage"/>, c'est le
    /// 10×15 qui accompagne la planche, donc les cotes de son produit, DEBOUT — un portrait
    /// se tire en hauteur.
    /// </summary>
    /// <param name="planche">Le papier retenu à l'écran de cadrage.</param>
    /// <param name="document">La norme visée : elle fixe la taille des cases.</param>
    /// <param name="identites">Nombre de cases d'identité posées sur la planche.</param>
    /// <returns>Null quand ce genre-là n'a pas de grande photo, ou qu'elle ne tient pas.</returns>
    public static (double LargeurMm, double HauteurMm)? Cotes(
        GenreDePlanche genre, Product planche, IdDocumentSpec document, int identites)
    {
        ArgumentNullException.ThrowIfNull(planche);
        ArgumentNullException.ThrowIfNull(document);

        switch (genre)
        {
            case GenreDePlanche.Rentree:
                if (planche.Sheet is not { } sheet) return null;

                var layout = PlancheRentree.Layout(
                    MmPx.ToPixels(planche.WidthMm, planche.Dpi),
                    MmPx.ToPixels(planche.HeightMm, planche.Dpi),
                    MmPx.ToPixels(document.WidthMm, planche.Dpi),
                    MmPx.ToPixels(document.HeightMm, planche.Dpi),
                    MmPx.ToPixels(sheet.LayoutGapMm, planche.Dpi),
                    identites,
                    bottomReserve: BandeBassePx(planche),
                    largeurMinimaleGrandePx:
                        MmPx.ToPixels(PlancheRentree.LargeurMinimaleGrandeMm, planche.Dpi),
                    // ⚠ LES MÊMES ARGUMENTS QUE LE RENDU, sans quoi le portrait serait cadré
                    // à un rapport pour être posé dans un autre. C'est la raison d'être de
                    // cette classe.
                    airAuBord: MmPx.ToPixels(PlancheRentree.AirAuBordMm, planche.Dpi));

                if (layout is null) return null;

                return (layout.Grande.Width * 25.4 / planche.Dpi,
                        layout.Grande.Height * 25.4 / planche.Dpi);

            case GenreDePlanche.PlancheEtTirage:
                if (TirageQuiAccompagne(planche) is not { } tirage) return null;

                // debout : un portrait se tire en hauteur, et c'est ce que le rendu fera
                // du canevas d'après le cadrage (voir CropMath.OrientCanvas)
                return (Math.Min(tirage.WidthMm, tirage.HeightMm),
                        Math.Max(tirage.WidthMm, tirage.HeightMm));

            default:
                return null;
        }
    }

    /// <summary>
    /// La hauteur réservée en bas de la planche pour la date, en pixels du produit — celle
    /// que le rendu réservera vraiment. Zéro quand la planche ne porte pas de date.
    /// </summary>
    public static int BandeBassePx(Product planche)
    {
        ArgumentNullException.ThrowIfNull(planche);

        return planche.Sheet?.DateStamp == true
            ? SheetFooterLayout.ReserveMinimalePx(
                SheetFooter.Pour(DateTime.Now, App.Services.Marque), planche.Dpi)
            : 0;
    }

    /// <summary>
    /// Le papier du tirage qui accompagne la planche, pour
    /// <see cref="GenreDePlanche.PlancheEtTirage"/>.
    ///
    /// Le code du catalogue d'abord ; à défaut, un 10×15 de la MÊME machine que la
    /// planche — un poste qui a nommé son produit autrement ne doit pas se retrouver sans
    /// format, et sortir la grande photo sur une autre machine ferait attendre le client
    /// devant deux files.
    /// </summary>
    /// <returns>Null si le catalogue n'a rien qui convienne.</returns>
    public static Product? TirageQuiAccompagne(Product planche)
    {
        ArgumentNullException.ThrowIfNull(planche);

        var catalogue = App.Services.Catalog;

        if (catalogue.Find(PlancheDeRentree.CodeDuTirage) is { Enabled: true } attendu
            && attendu.Sheet is null)
            return attendu;

        bool EstUn10x15(Product p)
        {
            var petit = Math.Min(p.WidthMm, p.HeightMm);
            var grand = Math.Max(p.WidthMm, p.HeightMm);
            return Math.Abs(petit - 102) <= 4 && Math.Abs(grand - 152) <= 5;
        }

        return catalogue.Enabled
            .Where(p => p.Sheet is null && EstUn10x15(p))
            .OrderByDescending(p => string.Equals(p.Channel, planche.Channel,
                StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.Price)
            .FirstOrDefault();
    }

    /// <summary>
    /// Le cadre de la grande photo : celui que l'opérateur a posé s'il l'a fait, celui
    /// qu'on déduit du cadre d'identité sinon.
    ///
    /// <b>Rien n'est exigé de l'opérateur</b>, et c'est la décision qui compte ici : le
    /// cadre large est calculé autour du visage déjà cadré (voir <c>CadrageElargi</c>), si
    /// bien qu'une planche de rentrée se vend exactement comme une planche ordinaire — on
    /// cadre l'identité, on imprime. Le bouton « Cadrer la grande photo » n'est là que pour
    /// les fois où la proposition ne convient pas.
    ///
    /// Une photo illisible ne fait rien échouer : on retombe sur le cadre d'identité, le
    /// portrait sortira serré, et la planche reste vendable.
    /// </summary>
    /// <param name="cheminPhoto">Le fichier, tel que la planche le portera.</param>
    /// <param name="identite">Le cadre normé posé à l'écran.</param>
    /// <param name="pose">Le cadre large déjà réglé à la main, ou null.</param>
    /// <param name="cotes">Les cotes de la grande photo, rendues par <see cref="Cotes"/>.</param>
    /// <param name="redressementDegres">
    /// Le redressement posé à l'écran d'identité.
    ///
    /// ⚠ <b>Il ne s'oubliait pas sans conséquence.</b> Le rendu redresse AVANT de recadrer,
    /// donc la photo est plus grande que son fichier et ses coins sont BLANCS. Le cadre
    /// large, calculé ici sans le savoir, allait les chercher : la planche du 20/08/2026
    /// est sortie avec un biseau blanc en haut à droite du portrait. L'opérateur ne voit
    /// jamais ce cadre avant le papier — c'est tout l'intérêt du format —, donc personne ne
    /// pouvait le rattraper à l'écran.
    /// </param>
    public static CropSpec Cadre(
        string cheminPhoto, CropSpec identite, CropSpec? pose,
        (double LargeurMm, double HauteurMm)? cotes,
        double redressementDegres = 0)
    {
        if (pose is { } deja) return deja;
        if (cotes is not { } taille) return identite;

        try
        {
            // aucune rotation par quarts de tour sur l'écran d'identité : le redressement
            // s'y fait en degrés, et le cadre est posé sur l'image telle qu'on la voit
            var (largeur, hauteur) = ImagePipeline.GetOrientedSize(cheminPhoto, 0);

            return CadrageElargi.Depuis(identite, largeur, hauteur,
                taille.LargeurMm, taille.HauteurMm,
                redressementDegres: redressementDegres);
        }
        catch (Exception ex)
        {
            FileLog.Write($"Cadre de la grande photo non calculé — {cheminPhoto}", ex);
            return identite;
        }
    }
}
