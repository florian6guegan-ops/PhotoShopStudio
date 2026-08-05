using System.Diagnostics;
using ImageMagick;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Coût du redressement fin.
///
/// Il pesait 92 % du rendu d'une planche d'identité — 11 937 ms sur 13 044, mesurés le
/// 05/08/2026 : on faisait tourner les 24 Mpx d'un reflex pour n'en garder ensuite que
/// 0,2, la cellule 35 × 45 mm faisant 413 × 531 px. Ce Magick.NET est bâti sans OpenMP,
/// donc seul le nombre de pixels compte.
///
/// Ces épreuves tiennent les deux bouts : le rendu doit rester GÉOMÉTRIQUEMENT le même, et
/// il ne doit plus coûter le prix de la pleine résolution.
/// </summary>
public class RedressementCoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Redressement-" + Guid.NewGuid().ToString("N"));

    public RedressementCoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    /// <summary>Une source « reflex » : assez grande pour que le pré-dimensionnement se déclenche.</summary>
    private string GrandeSource()
    {
        var chemin = Path.Combine(_root, "reflex.jpg");

        // un damier plutôt qu'un aplat : un aplat resterait identique à toute résolution et
        // ne prouverait rien sur la géométrie
        using var image = new MagickImage(MagickColors.White, 6000, 4000);
        var dessin = new ImageMagick.Drawing.Drawables().FillColor(MagickColors.Black);
        for (var y = 0; y < 4000; y += 400)
            for (var x = 0; x < 6000; x += 400)
                if ((x / 400 + y / 400) % 2 == 0)
                    dessin.Rectangle(x, y, x + 399, y + 399);
        image.Draw(dessin);
        image.Write(chemin, MagickFormat.Jpeg);

        return chemin;
    }

    /// <summary>Le cadrage serré d'une photo d'identité : on ne garde qu'un tiers de la source.</summary>
    private static CropSpec CadrageIdentite => new(0.185, 0.153, 0.632, 0.540);

    private static RenderRequest Demande(string source, double degres) =>
        new(source, 413, 531, CadrageIdentite, 0, degres, FitMode.Fill, 0, new ImageAdjustments());

    /// <summary>
    /// Le rendu redressé sort aux dimensions exactes du produit. C'est la première chose
    /// que le pré-dimensionnement pourrait casser.
    /// </summary>
    [Fact]
    public void Le_rendu_redresse_garde_les_dimensions_du_produit()
    {
        var sortie = Path.Combine(_root, "cellule.png");
        ImagePipeline.RenderToFile(Demande(GrandeSource(), 2.25), sortie);

        using var rendu = new MagickImage(sortie);
        Assert.Equal(413u, rendu.Width);
        Assert.Equal(531u, rendu.Height);
    }

    /// <summary>
    /// <b>L'épreuve qui compte.</b> Réduire AVANT de faire tourner doit donner la MÊME
    /// image qu'en faisant tourner la pleine résolution : le cadrage est en coordonnées
    /// relatives, et une rotation suivie d'une homothétie vaut l'homothétie suivie de la
    /// rotation.
    ///
    /// On compare au rendu obtenu depuis une source déjà petite — trop petite pour que le
    /// pré-dimensionnement s'applique — donc passée par le chemin d'avant.
    /// </summary>
    [Fact]
    public void Le_pre_dimensionnement_ne_deplace_pas_l_image()
    {
        var grande = GrandeSource();

        // la même image, réduite d'avance : le pré-dimensionnement n'aura rien à y faire
        var petite = Path.Combine(_root, "petite.jpg");
        using (var image = new MagickImage(grande))
        {
            image.Resize(new MagickGeometry(1500, 1000) { IgnoreAspectRatio = true });
            image.Write(petite, MagickFormat.Jpeg);
        }

        var aGrande = Path.Combine(_root, "depuis-grande.png");
        var aPetite = Path.Combine(_root, "depuis-petite.png");
        ImagePipeline.RenderToFile(Demande(grande, 2.25), aGrande);
        ImagePipeline.RenderToFile(Demande(petite, 2.25), aPetite);

        using var a = new MagickImage(aGrande);
        using var b = new MagickImage(aPetite);

        // Les deux ne peuvent pas être identiques au bit près — la source réduite a perdu
        // du détail avant même d'entrer dans le pipeline. Ce qu'on vérifie, c'est que RIEN
        // NE S'EST DÉPLACÉ : un décalage, même d'un pixel, ferait exploser cet écart sur
        // un damier à fort contraste.
        var ecart = a.Compare(b, ErrorMetric.RootMeanSquared);
        Assert.True(ecart < 0.10, $"le rendu s'est déplacé (écart RMS {ecart:F4})");
    }

    /// <summary>
    /// Le redressement ne doit plus coûter le prix de la pleine résolution.
    ///
    /// Le seuil est large — dix fois la marge d'un poste sain — parce qu'une épreuve de
    /// durée qui échoue au hasard ne vaut rien. Elle rattraperait un retour au chemin
    /// d'avant, où le même rendu prenait plus de treize secondes.
    /// </summary>
    [Fact]
    public void Le_redressement_ne_coute_plus_la_pleine_resolution()
    {
        var source = GrandeSource();
        var sortie = Path.Combine(_root, "chrono.png");

        // premier rendu à vide : on ne veut pas mesurer l'initialisation de Magick.NET
        ImagePipeline.RenderToFile(Demande(source, 0), Path.Combine(_root, "chauffe.png"));

        var chrono = Stopwatch.StartNew();
        ImagePipeline.RenderToFile(Demande(source, 2.25), sortie);
        chrono.Stop();

        Assert.True(chrono.ElapsedMilliseconds < 8000,
            $"le redressement a repris le chemin de la pleine résolution ({chrono.ElapsedMilliseconds} ms)");
    }

    /// <summary>
    /// Une source déjà plus petite que le besoin n'est JAMAIS agrandie : inventer des
    /// pixels pour les faire tourner coûterait le prix fort pour rien.
    /// </summary>
    [Fact]
    public void Une_source_trop_petite_n_est_pas_agrandie()
    {
        var petite = Path.Combine(_root, "minuscule.jpg");
        using (var image = new MagickImage(MagickColors.SteelBlue, 500, 700))
            image.Write(petite, MagickFormat.Jpeg);

        var sortie = Path.Combine(_root, "minuscule-rendu.png");
        ImagePipeline.RenderToFile(Demande(petite, 2.25), sortie);

        using var rendu = new MagickImage(sortie);
        Assert.Equal(413u, rendu.Width);
        Assert.Equal(531u, rendu.Height);
    }

    /// <summary>
    /// Sans redressement, rien ne change : le pré-dimensionnement ne s'applique qu'à la
    /// rotation fine, et le chemin ordinaire — recadrer puis mettre à l'échelle — reste
    /// celui d'avant.
    /// </summary>
    [Fact]
    public void Sans_redressement_le_chemin_ne_change_pas()
    {
        var source = GrandeSource();
        var sortie = Path.Combine(_root, "sans-redressement.png");

        ImagePipeline.RenderToFile(Demande(source, 0), sortie);

        using var rendu = new MagickImage(sortie);
        Assert.Equal(413u, rendu.Width);
        Assert.Equal(531u, rendu.Height);
    }
}
