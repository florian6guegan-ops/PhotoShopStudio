using Studio.App.Views;

namespace Studio.App.Infrastructure;

/// <summary>
/// Où « rentrer » quand un écran a fini, ou qu'on l'annule.
///
/// <b>Pourquoi ce détour.</b> Seize écrans écrivaient <c>Navigator.Home(new HomeView())</c>
/// en toutes lettres. Sur un poste identité VERROUILLÉ, chacun de ces boutons « Annuler »
/// déposait donc le client dans le Studio complet de la boutique — le verrou tenait la
/// fenêtre et la sortie par le PIN, mais pas les retours à l'accueil. Le bouton Accueil,
/// lui, avait été traité ; ces seize-là ne l'étaient pas.
///
/// La règle du dépôt s'applique telle quelle : <b>les BOUTONS se doublent, ce qu'ils font,
/// non.</b> Un seul endroit décide de quel accueil il s'agit.
/// </summary>
public static class AccueilStudio
{
    /// <summary>
    /// Vrai quand le staff a ouvert le Studio complet par le PIN.
    ///
    /// <c>mode.json</c> dit toujours « identite » — c'est la SESSION qui est passée en
    /// opérateur, le temps du dépannage. Le comptoir referme le poste en quittant, sans
    /// reconfigurer quoi que ce soit.
    /// </summary>
    public static bool Deverrouille { get; set; }

    /// <summary>Poste identité ENCORE verrouillé : l'accueil est celui du parcours identité.</summary>
    public static bool EnIdentiteVerrouille => App.Services.Mode.IsIdentite && !Deverrouille;

    /// <summary>
    /// L'accueil que l'application HÔTE veut, quand elle en a un à elle.
    ///
    /// Studio Photo Identité n'a pas d'écran d'accueil : sa page de travail EST
    /// l'application, comme sur ID Maker — on n'y navigue pas dans des menus. Elle pose
    /// donc ici la fabrique de sa page, et « Client suivant » repart d'une page neuve
    /// plutôt que d'une tuile « Commencer ».
    ///
    /// Null dans le Studio complet, qui garde ses deux accueils.
    /// </summary>
    public static Func<System.Windows.Controls.UserControl>? PageDAccueil { get; set; }

    /// <summary>
    /// Ramène à l'accueil qui convient au poste : celui que l'hôte a posé s'il en a un,
    /// celui du parcours identité sur un poste identité verrouillé, l'accueil opérateur
    /// partout ailleurs.
    /// </summary>
    public static void Rentrer()
    {
        if (PageDAccueil is { } fabrique)
        {
            Navigator.Home(fabrique(), "Photos d'identité");
            return;
        }

        if (EnIdentiteVerrouille)
            Navigator.Home(new IdentiteHomeView(), "Photos d'identité");
        else
            Navigator.Home(new HomeView(), "Studio Photo");
    }
}
