using System.IO;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Studio.App.Infrastructure;

/// <summary>Comment les photos sont classées dans une planche.</summary>
public enum CritereDeTri
{
    /// <summary>La plus récente d'abord — ce que le client vient de prendre.</summary>
    DateRecente,

    /// <summary>La plus ancienne d'abord.</summary>
    DateAncienne,

    /// <summary>Ordre alphabétique.</summary>
    NomCroissant,

    /// <summary>Ordre alphabétique inverse.</summary>
    NomDecroissant,

    /// <summary>Les fichiers les plus lourds d'abord — un scan avant une capture d'écran.</summary>
    TailleDecroissante,
}

/// <summary>
/// Le classement des photos, et le menu qui le choisit.
///
/// <b>Ce que ça remplace.</b> Le bouton « trier » BASCULAIT entre deux ordres, sans jamais
/// dire lequel était en cours ni qu'il en existait d'autres : on appuyait, la planche
/// changeait, et il fallait appuyer encore pour revenir. Sur une carte de mille photos, on
/// ne savait même pas si l'on regardait les plus récentes ou les premières de l'alphabet.
///
/// Ici, un menu montre les cinq classements habituels, coche celui qui s'applique, et le
/// bouton porte son nom. Le même menu sert à la planche des tirages et à celle des photos
/// d'identité : deux listes séparées auraient fini par diverger.
/// </summary>
public static class MenuDeTri
{
    /// <summary>Ce que le bouton affiche.</summary>
    public static string Libelle(CritereDeTri critere) => critere switch
    {
        CritereDeTri.DateRecente => "plus récentes",
        CritereDeTri.DateAncienne => "plus anciennes",
        CritereDeTri.NomCroissant => "nom A → Z",
        CritereDeTri.NomDecroissant => "nom Z → A",
        CritereDeTri.TailleDecroissante => "plus lourdes",
        _ => "trier",
    };

    /// <summary>L'entrée du menu, plus explicite que le libellé du bouton.</summary>
    private static string Entree(CritereDeTri critere) => critere switch
    {
        CritereDeTri.DateRecente => "Date — la plus récente d'abord",
        CritereDeTri.DateAncienne => "Date — la plus ancienne d'abord",
        CritereDeTri.NomCroissant => "Nom — de A à Z",
        CritereDeTri.NomDecroissant => "Nom — de Z à A",
        CritereDeTri.TailleDecroissante => "Poids du fichier — la plus lourde d'abord",
        _ => "trier",
    };

    /// <summary>
    /// Déroule les classements sous un bouton, celui en cours coché.
    /// </summary>
    /// <param name="bouton">Le bouton sous lequel le menu s'ouvre.</param>
    /// <param name="actuel">Classement en vigueur, coché dans la liste.</param>
    /// <param name="choisi">Appelé avec le classement retenu.</param>
    public static void Ouvrir(ButtonBase bouton, CritereDeTri actuel, Action<CritereDeTri> choisi)
    {
        ArgumentNullException.ThrowIfNull(bouton);
        ArgumentNullException.ThrowIfNull(choisi);

        var menu = new ContextMenu { PlacementTarget = bouton, Placement = PlacementMode.Bottom };

        foreach (var critere in Enum.GetValues<CritereDeTri>())
        {
            var entree = new MenuItem
            {
                Header = Entree(critere),
                FontSize = 17,
                IsCheckable = true,
                IsChecked = critere == actuel,
                StaysOpenOnClick = false,
            };

            var retenu = critere;
            entree.Click += (_, _) => choisi(retenu);
            menu.Items.Add(entree);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// Classe des photos selon le critère demandé.
    ///
    /// Le tri par date passe par <see cref="Studio.Sources.PhotoScanner.TrierParDateDecroissante"/>
    /// et non par une seconde règle écrite ici : la date d'une photo copiée depuis une carte
    /// demande un choix — la plus ancienne des deux que Windows tient —, et deux règles
    /// finiraient par diverger. L'écart se verrait au premier appui sur « trier ».
    /// </summary>
    /// <param name="photos">Ce qu'il faut classer.</param>
    /// <param name="chemin">Où trouver le fichier d'un élément.</param>
    /// <param name="nom">Ce qui s'affiche sous la vignette ; sert au tri par nom.</param>
    public static List<T> Appliquer<T>(
        IEnumerable<T> photos, CritereDeTri critere, Func<T, string> chemin, Func<T, string> nom)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(chemin);
        ArgumentNullException.ThrowIfNull(nom);

        var liste = photos.ToList();

        return critere switch
        {
            CritereDeTri.NomCroissant =>
                liste.OrderBy(nom, StringComparer.OrdinalIgnoreCase).ToList(),
            CritereDeTri.NomDecroissant =>
                liste.OrderByDescending(nom, StringComparer.OrdinalIgnoreCase).ToList(),
            CritereDeTri.TailleDecroissante =>
                ParPoids(liste, chemin),
            CritereDeTri.DateAncienne =>
                ParDate(liste, chemin, recentesDAbord: false),
            _ => ParDate(liste, chemin, recentesDAbord: true),
        };
    }

    /// <summary>
    /// Le rang que le tri de référence donne à chaque fichier, puis les photos dans cet ordre.
    ///
    /// Le regroupement par chemin n'est pas une précaution de style : une même photo peut
    /// figurer DEUX fois dans une planche — le bouton « Dupliquer » la tire en 10×15 et en
    /// 15×20 — et un dictionnaire direct lèverait sur la clé en double.
    /// </summary>
    private static List<T> ParDate<T>(List<T> photos, Func<T, string> chemin, bool recentesDAbord)
    {
        var rangs = Studio.Sources.PhotoScanner
            .TrierParDateDecroissante(photos.Select(chemin).Distinct(StringComparer.OrdinalIgnoreCase))
            .Select((c, rang) => (c, rang))
            .ToDictionary(x => x.c, x => x.rang, StringComparer.OrdinalIgnoreCase);

        int Rang(T photo) => rangs.TryGetValue(chemin(photo), out var rang) ? rang : int.MaxValue;

        return recentesDAbord
            ? photos.OrderBy(Rang).ToList()
            : photos.OrderByDescending(Rang).ToList();
    }

    /// <summary>
    /// Le poids est lu UNE fois par fichier et gardé le temps du tri : un comparateur qui
    /// le relirait le demanderait O(n log n) fois au disque. Un fichier illisible part en
    /// fin de liste — il s'agit d'un ordre d'affichage, pas d'une opération qui ait le droit
    /// d'échouer.
    /// </summary>
    private static List<T> ParPoids<T>(List<T> photos, Func<T, string> chemin)
    {
        static long Poids(string fichier)
        {
            try
            {
                var infos = new FileInfo(fichier);
                return infos.Exists ? infos.Length : -1;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return -1;
            }
        }

        var poids = photos
            .Select(chemin)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c, Poids, StringComparer.OrdinalIgnoreCase);

        return photos
            .OrderByDescending(p => poids.TryGetValue(chemin(p), out var taille) ? taille : -1)
            .ToList();
    }
}
