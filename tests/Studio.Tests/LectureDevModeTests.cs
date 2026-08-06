using System.Text;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// La lecture d'un DEVMODE capturé : dire, en clair, sur quels réglages un produit tire.
///
/// Les cas sont bâtis sur la structure RÉELLE relevée le 06/08/2026 dans
/// <c>devmode-ID-FR-6.bin</c> (pilote DP-DS620, 220 octets publics + 1056 privés) : une
/// suite de chaînes ASCII terminées par un zéro, par paires réglage/option.
/// </summary>
public class LectureDevModeTests
{
    /// <summary>Fabrique un DEVMODE crédible dont la partie privée porte ces chaînes.</summary>
    private static byte[] Fabriquer(params string[] chaines)
    {
        const int dmSize = 220;

        var prive = new List<byte>();
        foreach (var chaine in chaines)
        {
            prive.AddRange(Encoding.ASCII.GetBytes(chaine));
            prive.Add(0);
        }

        var octets = new byte[dmSize + prive.Count];

        // dmSize et dmDriverExtra, les deux seuls champs publics que la lecture regarde
        BitConverter.GetBytes((ushort)dmSize).CopyTo(octets, 68);
        BitConverter.GetBytes((ushort)prive.Count).CopyTo(octets, 70);

        prive.CopyTo(octets, dmSize);
        return octets;
    }

    [Fact]
    public void Chaque_reglage_est_suivi_de_son_option()
    {
        var devMode = Fabriquer(
            "Orientation", "PORTRAIT",
            "Resolution", "Option1",
            "OVERCOATTYPE", "OPTYPE_LUSTER");

        var lus = LectureDevMode.Lire(devMode);

        Assert.Equal(3, lus.Count);
        Assert.Equal("Resolution", lus[1].Reglage);
        Assert.Equal("Option1", lus[1].Valeur);
    }

    /// <summary>
    /// <b>L'essai qui compte</b>, et celui qui manquait : le bloc privé RÉEL de la DS620
    /// ne commence pas par un réglage, il s'ouvre par les marqueurs d'Unidrv. Une lecture
    /// qui compte les chaînes deux par deux se décale alors d'un cran et annonce
    /// « OPTYPE_LUSTER = PRINTBUFFCONTROL » — ce qu'elle a fait au premier essai sur le
    /// fichier de la boutique, le 06/08/2026.
    /// </summary>
    [Fact]
    public void Les_marqueurs_du_pilote_en_tete_ne_decalent_rien()
    {
        var devMode = Fabriquer(
            "DINU\"", ".X", "SMTJ", "InputBin", "FORMSOURCE", "RESDLL", "UniresDLL",
            "Orientation", "PORTRAIT",
            "Resolution", "Option1",
            "PrintMargin", "MarginOff",
            "OVERCOATTYPE", "OPTYPE_LUSTER",
            "PRINTBUFFCONTROL", "PBC_NONCLEAR",
            "CUTTERCONTROL", "CUT_STANDARD",
            "PaperSize", "PC",
            "MediaType", "STANDARD",
            "ColorMode", "24bpp",
            "Halftone", "HT_PATSIZE_SUPERCELL_M", "TFSM");

        var lus = LectureDevMode.Lire(devMode).ToDictionary(r => r.Reglage, r => r.Valeur);

        Assert.Equal("OPTYPE_LUSTER", lus["OVERCOATTYPE"]);
        Assert.Equal("PBC_NONCLEAR", lus["PRINTBUFFCONTROL"]);
        Assert.Equal("Option1", lus["Resolution"]);
        Assert.Equal("PC", lus["PaperSize"]);
        Assert.Equal("24bpp", lus["ColorMode"]);
    }

    /// <summary>
    /// Les deux réglages que l'enquête du 06/08/2026 a désignés : ils doivent se voir, et
    /// se voir comme des avertissements, pas comme des lignes d'information.
    /// </summary>
    [Fact]
    public void Le_mode_rapide_et_le_tampon_non_vide_sont_signales()
    {
        var devMode = Fabriquer(
            "Resolution", "Option1",
            "PRINTBUFFCONTROL", "PBC_NONCLEAR");

        var alertes = LectureDevMode.Avertissements(devMode);

        Assert.Equal(2, alertes.Count);
        Assert.Contains(alertes, a => a.Contains("RAPIDE", StringComparison.Ordinal));

        // l'avertissement doit nommer la ligne du dialogue, sinon l'opérateur ne la trouve
        // pas : le pilote l'appelle « Réessayer l'impression », jamais « tampon »
        Assert.Contains(alertes, a => a.Contains("Réessayer l'impression", StringComparison.Ordinal));
        Assert.Contains(alertes, a => a.Contains("Désactiver", StringComparison.Ordinal));
        Assert.Contains(alertes, a => a.Contains("High-quality", StringComparison.Ordinal));
    }

    /// <summary>
    /// <b>On ne traduit PAS la finition.</b> Le dialogue du pilote affiche « Brillant » là
    /// où le DEVMODE porte <c>OPTYPE_LUSTER</c> (copie d'écran du 06/08/2026) : les noms
    /// internes ne disent pas ce que l'opérateur lit, et annoncer « lustré » sur un tirage
    /// brillant coûte la feuille. On rend le nom brut, et on le dit.
    /// </summary>
    [Fact]
    public void La_finition_est_rendue_sans_traduction_inventee()
    {
        var lus = LectureDevMode.Lire(Fabriquer("OVERCOATTYPE", "OPTYPE_LUSTER"));

        Assert.Single(lus);
        Assert.Contains("OPTYPE_LUSTER", lus[0].Libelle, StringComparison.Ordinal);
        Assert.False(lus[0].Inquietant);
    }

    [Fact]
    public void Les_bons_reglages_ne_disent_rien()
    {
        var devMode = Fabriquer(
            "Resolution", "Option2",
            "PRINTBUFFCONTROL", "PBC_CLEAR",
            "ColorMode", "24bpp");

        Assert.Empty(LectureDevMode.Avertissements(devMode));
        Assert.Equal(3, LectureDevMode.Resume(devMode).Count);
    }

    /// <summary>
    /// Le noir et blanc imposé par le pilote est le genre de réglage qui gâche une commande
    /// entière sans que rien à l'écran ne l'annonce.
    /// </summary>
    [Fact]
    public void Le_noir_et_blanc_du_pilote_est_signale()
    {
        var alertes = LectureDevMode.Avertissements(Fabriquer("ColorMode", "Mono"));

        Assert.Single(alertes);
        Assert.Contains("NOIR ET BLANC", alertes[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// Ce qu'on ne sait pas nommer est passé, sans gêner ce qui suit : un pilote publie
    /// une vingtaine de chaînes dont la plupart ne nous regardent pas.
    /// </summary>
    [Fact]
    public void Ce_qui_n_est_pas_un_reglage_connu_est_ignore()
    {
        var devMode = Fabriquer("MACHIN", "TRUC", "ColorMode", "24bpp");

        var lus = LectureDevMode.Lire(devMode);

        Assert.Single(lus);
        Assert.Equal("ColorMode", lus[0].Reglage);
        Assert.Single(LectureDevMode.Resume(devMode));
    }

    /// <summary>
    /// Rien ne doit jamais lever ici : c'est une lecture d'affichage, sur des octets qui
    /// viennent d'un pilote tiers.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(60)]
    [InlineData(71)]
    public void Un_bloc_trop_court_ne_leve_pas(int taille)
    {
        Assert.Empty(LectureDevMode.Lire(new byte[taille]));
    }

    [Fact]
    public void Un_devmode_absent_ne_leve_pas()
    {
        Assert.Empty(LectureDevMode.Lire(null));
        Assert.Empty(LectureDevMode.Avertissements(null));
        Assert.Empty(LectureDevMode.Resume(null));
    }

    /// <summary>
    /// Un DEVMODE qui s'annonce plus grand qu'il n'est ne doit pas faire sortir la lecture
    /// du tableau : les octets viennent d'un fichier, qui peut avoir été tronqué.
    /// </summary>
    [Fact]
    public void Une_taille_annoncee_trop_grande_est_bornee()
    {
        var devMode = Fabriquer("ColorMode", "24bpp");
        BitConverter.GetBytes((ushort)9000).CopyTo(devMode, 70); // dmDriverExtra menteur

        Assert.Single(LectureDevMode.Lire(devMode));
    }

    /// <summary>Un réglage en fin de bloc, sans son option, ne devient pas un réglage vide.</summary>
    [Fact]
    public void Un_reglage_sans_option_est_ignore()
    {
        var lus = LectureDevMode.Lire(Fabriquer("ColorMode", "24bpp", "Orientation"));

        Assert.Single(lus);
        Assert.Equal("ColorMode", lus[0].Reglage);
    }
}
