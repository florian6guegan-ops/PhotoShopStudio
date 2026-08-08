using ImageMagick;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// La lecture des photos posées sur un support qui peut s'en aller.
///
/// <b>Le défaut du 07/08/2026.</b> ImageMagick ouvre un fichier en le PROJETANT en
/// mémoire. Si le support disparaît pendant qu'on travaille — une carte retirée un peu
/// vite au comptoir — l'accès à une page déjà projetée lève STATUS_IN_PAGE_ERROR : une
/// faute système, que le CLR ne sait pas rattraper. Studio est mort deux fois ce jour-là,
/// à 18:33 et 18:37, pendant que Windows enregistrait 236 erreurs de lecture sur le
/// lecteur de cartes — sans une ligne au journal, puisque le processus disparaît.
///
/// La carte, vérifiée le lendemain, était saine : elle avait seulement perdu le contact.
/// Le geste est banal, et DiLand y survit. Lus en octets, les mêmes incidents donnent une
/// IOException ordinaire, que l'appelant intercepte.
/// </summary>
public class SupportAmovibleTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "StudioSupport-" + Guid.NewGuid().ToString("N"));

    public SupportAmovibleTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Un disque du poste garde la projection : c'est ce qu'on peut faire de mieux tant
    /// que le support ne bouge pas, et rien n'y est recopié.
    /// </summary>
    [Fact]
    public void Un_disque_du_poste_n_est_pas_traite_comme_amovible()
    {
        Assert.False(MagickInit.SurSupportQuiPeutDisparaitre(_dossier));
        Assert.False(MagickInit.SurSupportQuiPeutDisparaitre(
            Path.Combine(_dossier, "photo.jpg")));
    }

    /// <summary>
    /// Un partage réseau tombe comme une carte, et donne la même faute. Il n'a pas de
    /// lettre de lecteur : <c>DriveInfo</c> n'en dirait rien, alors que c'est le cas le
    /// plus fragile de tous.
    /// </summary>
    [Theory]
    [InlineData(@"\\serveur\photos\IMG_0001.jpg")]
    [InlineData(@"\\192.168.1.20\partage\DCIM\IMG_0002.jpg")]
    public void Un_partage_reseau_est_traite_comme_amovible(string chemin)
    {
        Assert.True(MagickInit.SurSupportQuiPeutDisparaitre(chemin));
    }

    /// <summary>Dans le doute — chemin vide ou malformé — on ne recopie rien.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("|||")]
    public void Un_chemin_douteux_ne_declenche_pas_la_recopie(string chemin)
    {
        Assert.False(MagickInit.SurSupportQuiPeutDisparaitre(chemin));
    }

    /// <summary>
    /// La lecture rend la même image dans les deux voies : la robustesse ne doit rien
    /// changer à ce qu'on décode.
    /// </summary>
    [Fact]
    public void La_lecture_rend_la_meme_image_quelle_que_soit_la_voie()
    {
        var photo = Path.Combine(_dossier, "photo.jpg");
        using (var source = new MagickImage(MagickColors.Firebrick, 640, 480))
            source.Write(photo, MagickFormat.Jpeg);

        using var lue = MagickInit.Lire(photo, 0);

        Assert.Equal(640u, lue.Width);
        Assert.Equal(480u, lue.Height);
    }

    /// <summary>
    /// <b>Ce qu'on gagne vraiment.</b> Un fichier qui disparaît doit donner une exception
    /// d'entrée-sortie ORDINAIRE — celle qu'un catch attrape et qu'on montre à
    /// l'opérateur — et non emporter le processus.
    /// </summary>
    [Fact]
    public void Un_fichier_disparu_leve_une_exception_rattrapable()
    {
        var absent = Path.Combine(_dossier, "carte-retiree.jpg");

        Assert.ThrowsAny<Exception>(() => MagickInit.Lire(absent, 0));
    }

    /// <summary>
    /// L'indication de taille — le décodage économe des JPEG — doit survivre au passage
    /// par les octets, sans quoi la lecture d'une carte deviendrait bien plus lente.
    /// </summary>
    [Fact]
    public void Le_decodage_econome_reste_possible()
    {
        var photo = Path.Combine(_dossier, "grande.jpg");
        using (var source = new MagickImage(MagickColors.SteelBlue, 4000, 3000))
            source.Write(photo, MagickFormat.Jpeg);

        using var vignette = MagickInit.Lire(photo, 400);

        // JPEG réduit au décodage : l'image lue est plus petite que l'originale
        Assert.True(vignette.Width < 4000, $"décodage non économe : {vignette.Width} px");
    }
}
