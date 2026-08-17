using Studio.App.Views;
using Studio.Core.Domain;

namespace Studio.App.Infrastructure;

/// <summary>
/// Le parcours « photo d'identité », d'un seul tenant : document → support → choix des
/// photos → cadrage → récapitulatif.
///
/// Il partait de DEUX endroits — la tuile de l'accueil et celle de l'écran « type de
/// produit » — avec la même suite d'écrans recopiée mot pour mot. Ajouter l'écran de
/// sélection n'a donc corrigé qu'une moitié du logiciel : par l'accueil, celui qu'on
/// utilise réellement en boutique, on tombait toujours directement sur le cadrage, avec
/// les 455 photos de la carte dans la bande latérale.
///
/// C'est la règle déjà écrite pour les commandes de bornes : <b>les BOUTONS se doublent,
/// ce qu'ils font, non.</b>
/// </summary>
public static class ParcoursIdentite
{
    /// <summary>Ouvre le parcours, à partir du choix du document.</summary>
    public static void Ouvrir() =>
        Navigator.Go(new IdDocumentPickerView(
                // Une NORME : on choisit le support, puis les photos, puis on cadre.
                // `photos` est le nombre imposé par le raccourci — « planche de 6 » — qu'il
                // faut porter jusqu'au cadrage, seul endroit où il veut dire quelque chose.
                (document, photos) =>
                    Navigator.Go(new SourcePickerView((racine, profond) =>
                            Navigator.Go(new IdPhotoPickerView(racine, document, profond, photos),
                                $"{document.Country} — choisir les photos")),
                        "Photos d'identité — choisir le support"),

                // Un PRODUIT tiré tel quel — l'E-Photo. Voir OuvrirUnProduit.
                OuvrirUnProduit),
            "Photos d'identité — choisir le document");

    /// <summary>
    /// Ouvre un PRODUIT tiré tel quel depuis les raccourcis d'identité — l'E-Photo.
    ///
    /// Ce n'est pas une norme : la photo part ENTIÈRE sur un 10×15, bords blancs compris,
    /// sans gabarit ni recadrage d'identité. C'est donc l'écran des tirages qui la sert,
    /// produit déjà choisi — c'est lui qui sait poser une photo entière dans son format
    /// (<c>Product.DefaultFit</c> vaut <c>Fit</c> sur ce produit).
    ///
    /// <b>⚠ ON PASSE TOUJOURS PAR LE CHOIX DU SUPPORT, et c'est le point.</b> La photo d'une
    /// E-Photo n'arrive presque jamais sur une carte mémoire : le client l'envoie par
    /// courriel ou depuis son téléphone, et elle atterrit dans Téléchargements — d'où ce
    /// raccourci-là, ici et pas ailleurs. Sauter cet écran pour ouvrir la carte insérée,
    /// comme le fait « Ouvrir des photos », emmène l'opérateur exactement là où la photo
    /// n'est PAS.
    ///
    /// <b>Partagé, et non recopié.</b> Studio Photo Identité y arrive par un autre bouton —
    /// « changer de document » depuis l'écran de cadrage — et la première version y avait
    /// recopié ce parcours en sautant le choix du support. Les BOUTONS se doublent, ce
    /// qu'ils font, non.
    /// </summary>
    public static void OuvrirUnProduit(Product produit)
    {
        ArgumentNullException.ThrowIfNull(produit);

        Navigator.Go(
            new SourcePickerView(
                (racine, profond) => Navigator.Go(
                    new PhotoGridView(racine, produit.Code, avecSousDossiers: profond),
                    produit.Name),
                SourcePickerView.RaccourciTelechargements()),
            $"{produit.Name} — choisir le support");
    }
}
