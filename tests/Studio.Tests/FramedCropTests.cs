using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// Le recadrage façon DiLand : cadre fixe, photo mobile derrière.
///
/// Ce que la boutique demande : que le tirage sorte AU FORMAT choisi, quoi qu'on fasse
/// avec la souris. C'est le défaut constaté le 01/08/2026 — le cadrage dérivait parce
/// que Studio déplaçait un rectangle au lieu de déplacer la photo.
///
/// Ces vérifications portent sur la géométrie seule, sans souris ni écran : c'est
/// précisément ce qui n'avait jamais été vérifié.
/// </summary>
public class FramedCropTests
{
    /// <summary>Une photo 3:2 (4000×6016 en portrait chez le client) et un cadre 10×15.</summary>
    private static FramedCrop Paysage() => new(6000, 4000, 152, 102);
    private static FramedCrop Portrait() => new(4000, 6000, 102, 152);

    // — le format, qui est tout l'enjeu —

    /// <summary>
    /// Le cœur du sujet : quoi qu'on fasse, le cadre garde le rapport du tirage. Le
    /// recadrage rendu doit redonner ce rapport une fois rapporté aux pixels de l'image.
    /// </summary>
    [Fact]
    public void Le_format_du_cadre_survit_a_tous_les_gestes()
    {
        var cadre = Paysage();
        var attendu = 152 / 102.0;

        cadre.ZoomIn();
        cadre.ZoomIn();
        cadre.Move(-40, 25);
        cadre.ZoomOut();
        cadre.Move(15, -60);

        var crop = cadre.ToCropSpec();
        var obtenu = crop.Width * 6000 / (crop.Height * 4000);

        Assert.Equal(attendu, obtenu, 3);
    }

    [Fact]
    public void Le_format_tient_aussi_en_portrait()
    {
        var cadre = Portrait();
        var attendu = 102 / 152.0;

        cadre.ZoomIn();
        cadre.Move(-20, -30);

        var crop = cadre.ToCropSpec();
        var obtenu = crop.Width * 4000 / (crop.Height * 6000);

        Assert.Equal(attendu, obtenu, 3);
    }

    /// <summary>Au départ, la photo couvre le cadre et le remplit d'un bord à l'autre.</summary>
    [Fact]
    public void Au_depart_la_photo_couvre_le_cadre()
    {
        var crop = Paysage().ToCropSpec();

        // le grand côté de la photo est mieux proportionné que le cadre : c'est la
        // hauteur qui est pleine et la largeur qui est rognée, ou l'inverse — mais dans
        // tous les cas l'un des deux côtés est pris en entier
        Assert.True(crop.Width >= 0.999 || crop.Height >= 0.999,
            $"aucun côté pris en entier : {crop.Width:0.###} × {crop.Height:0.###}");
        Assert.True(crop.IsValid);
    }

    // — les bornes, qui évitent les bandes blanches —

    /// <summary>
    /// Une photo qu'on pousse à bout ne doit jamais découvrir le cadre : le tirage
    /// sortirait avec une bande blanche que personne n'a demandée.
    /// </summary>
    [Theory]
    [InlineData(-10000.0, 0.0)]
    [InlineData(10000.0, 0.0)]
    [InlineData(0.0, -10000.0)]
    [InlineData(0.0, 10000.0)]
    public void La_photo_ne_peut_pas_decouvrir_le_cadre(double dx, double dy)
    {
        var cadre = Paysage();

        cadre.Move(dx, dy);

        Assert.True(cadre.X <= 0.001, $"bord gauche découvert : X = {cadre.X:0.##}");
        Assert.True(cadre.Y <= 0.001, $"bord haut découvert : Y = {cadre.Y:0.##}");
        Assert.True(cadre.X + cadre.Width >= cadre.FrameWidth - 0.001, "bord droit découvert");
        Assert.True(cadre.Y + cadre.Height >= cadre.FrameHeight - 0.001, "bord bas découvert");

        var crop = cadre.ToCropSpec();
        Assert.InRange(crop.X, 0, 1);
        Assert.InRange(crop.Y, 0, 1);
    }

    /// <summary>Dézoomer à l'infini ne doit pas non plus faire apparaître de blanc.</summary>
    [Fact]
    public void Le_dezoom_s_arrete_quand_la_photo_couvre_juste_le_cadre()
    {
        var cadre = Paysage();

        for (var i = 0; i < 200; i++) cadre.ZoomOut();

        Assert.True(cadre.Width >= cadre.FrameWidth - 0.001);
        Assert.True(cadre.Height >= cadre.FrameHeight - 0.001);
    }

    // — le zoom —

    [Fact]
    public void Zoomer_agrandit_la_photo_et_reduit_ce_que_le_cadre_retient()
    {
        var cadre = Paysage();
        var avant = cadre.ToCropSpec();

        cadre.ZoomIn();

        var apres = cadre.ToCropSpec();
        Assert.True(apres.Width < avant.Width,
            $"le cadre devrait retenir moins large : {avant.Width:0.###} → {apres.Width:0.###}");
    }

    /// <summary>Zoomer puis dézoomer d'autant doit ramener au même endroit.</summary>
    [Fact]
    public void Le_zoom_est_reversible()
    {
        var cadre = Paysage();
        var depart = cadre.ToCropSpec();

        cadre.ZoomIn();
        cadre.ZoomOut();

        var arrivee = cadre.ToCropSpec();
        Assert.Equal(depart.X, arrivee.X, 3);
        Assert.Equal(depart.Width, arrivee.Width, 3);
    }

    // — aller-retour avec le rendu —

    /// <summary>
    /// Rouvrir une photo doit la retrouver exactement où on l'avait laissée : le
    /// recadrage enregistré doit se relire sans dérive.
    /// </summary>
    [Fact]
    public void Un_recadrage_enregistre_se_relit_a_l_identique()
    {
        var cadre = Paysage();
        cadre.ZoomIn();
        cadre.Move(-30, -20);
        var enregistre = cadre.ToCropSpec();

        var rouvert = Paysage();
        rouvert.SetFromCropSpec(enregistre);
        var relu = rouvert.ToCropSpec();

        Assert.Equal(enregistre.X, relu.X, 3);
        Assert.Equal(enregistre.Y, relu.Y, 3);
        Assert.Equal(enregistre.Width, relu.Width, 3);
        Assert.Equal(enregistre.Height, relu.Height, 3);
    }

    // — le redressement, qui doit contraindre le cadre —

    /// <summary>
    /// Une photo redressée n'offre plus le même rectangle utile : ses coins deviennent
    /// vides. La photo doit donc grandir pour continuer à couvrir le cadre — sinon le
    /// tirage sort avec des angles blancs. Demande explicite de l'exploitant le
    /// 01/08/2026 : « le redressement doit prendre en compte le cadrage ».
    /// </summary>
    [Theory]
    [InlineData(2.0)]
    [InlineData(5.0)]
    [InlineData(12.0)]
    [InlineData(-8.0)]
    public void Redresser_oblige_la_photo_a_grandir(double degres)
    {
        var cadre = Paysage();
        var avant = cadre.Width;

        cadre.RotationDegrees = degres;

        Assert.True(cadre.Width > avant,
            $"à {degres}° la photo devrait grandir : {avant:0.##} → {cadre.Width:0.##}");
    }

    /// <summary>
    /// Le test qui compte : à n'importe quel angle, le cadre incliné doit tenir
    /// entièrement dans la photo. On le vérifie sur les quatre coins.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(10.0)]
    [InlineData(-15.0)]
    public void Le_cadre_incline_reste_dans_la_photo(double degres)
    {
        var cadre = Paysage();
        cadre.RotationDegrees = degres;
        cadre.Move(10_000, 10_000); // on le pousse dans un coin

        var radians = degres * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var requiseW = cadre.FrameWidth * cos + cadre.FrameHeight * sin;
        var requiseH = cadre.FrameWidth * sin + cadre.FrameHeight * cos;

        var gauche = (cadre.FrameWidth - requiseW) / 2;
        var haut = (cadre.FrameHeight - requiseH) / 2;

        Assert.True(cadre.X <= gauche + 0.001, $"déborde à gauche à {degres}°");
        Assert.True(cadre.Y <= haut + 0.001, $"déborde en haut à {degres}°");
        Assert.True(cadre.X + cadre.Width >= gauche + requiseW - 0.001, $"déborde à droite à {degres}°");
        Assert.True(cadre.Y + cadre.Height >= haut + requiseH - 0.001, $"déborde en bas à {degres}°");
    }

    /// <summary>Le format reste celui du tirage, redressement ou pas.</summary>
    [Fact]
    public void Le_format_tient_meme_redresse()
    {
        var cadre = Paysage();
        cadre.RotationDegrees = 7;
        cadre.ZoomIn();
        cadre.Move(-25, 15);

        var crop = cadre.ToCropSpec();
        var obtenu = crop.Width * 6000 / (crop.Height * 4000);

        Assert.Equal(152 / 102.0, obtenu, 3);
    }

    /// <summary>Revenir à zéro degré doit redonner le cadrage plein.</summary>
    [Fact]
    public void Annuler_le_redressement_libere_la_photo()
    {
        var cadre = Paysage();
        var depart = cadre.Width;

        cadre.RotationDegrees = 10;
        cadre.RotationDegrees = 0;

        // la photo reste agrandie (on ne rétrécit pas dans son dos), mais elle peut
        // à nouveau être ramenée au plus juste
        cadre.Reset();
        Assert.Equal(depart, cadre.Width, 3);
    }

    [Fact]
    public void Un_recadrage_illisible_ramene_au_cadrage_de_depart()
    {
        var cadre = Paysage();
        var depart = cadre.ToCropSpec();

        cadre.ZoomIn();
        cadre.SetFromCropSpec(new Studio.Core.Domain.CropSpec(0, 0, 0, 0));

        Assert.Equal(depart.Width, cadre.ToCropSpec().Width, 3);
    }
}
