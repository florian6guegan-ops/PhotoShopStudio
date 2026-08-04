using System.Windows.Controls;

namespace Studio.App.Infrastructure;

/// <summary>
/// Un écran dont le travail en cours peut être mis de côté et repris plus tard.
///
/// <b>Le geste du comptoir.</b> Un client hésite ou s'absente, un autre attend derrière :
/// on met de côté, on sert, on reprend là où on en était. Le bouton « Accueil » de
/// l'en-tête interroge la pile de navigation et s'adresse au premier écran qui sait le
/// faire — c'est ce qui permet de partir depuis un écran ENFANT (recadrage, corrections)
/// sans perdre la commande préparée en dessous.
///
/// Les écrans qui ne l'implémentent pas n'ont rien à garder : « Accueil » y revient
/// simplement à l'accueil.
/// </summary>
public interface ITravailReprenable
{
    /// <summary>
    /// Enregistre le travail en cours pour qu'il réapparaisse sur l'accueil.
    /// </summary>
    /// <returns>
    /// Vrai s'il y avait quelque chose à garder et que l'enregistrement a réussi. Faux
    /// pour un écran vide ou un enregistrement impossible — l'appelant revient à l'accueil
    /// dans les deux cas : le bouton doit toujours ramener à l'accueil, c'est sa promesse.
    /// </returns>
    bool EnregistrerPourReprise();

    /// <summary>Ce qui a été mis de côté, en une ligne, pour le dire à l'opérateur.</summary>
    string ResumeDeLAttente { get; }
}

/// <summary>Recherche d'un écran reprenable dans la pile de navigation.</summary>
public static class Reprises
{
    /// <summary>
    /// Le premier écran de la pile, du plus récent au plus ancien, qui sait se mettre de
    /// côté — ou null.
    ///
    /// On remonte la pile plutôt que de ne regarder que l'écran affiché : depuis le
    /// recadrage d'une photo, c'est la GRILLE qui porte la commande, deux écrans plus bas.
    /// </summary>
    public static ITravailReprenable? Trouver() =>
        Navigator.Ecrans.OfType<ITravailReprenable>().FirstOrDefault();
}
