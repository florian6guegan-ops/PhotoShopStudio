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

    // — poignées de coin —

    /// <summary>
    /// Le geste de DiLand : on tire un coin, le coin opposé ne bouge pas. C'est toute la
    /// différence avec le zoom, qui pousse des deux côtés à la fois.
    /// </summary>
    [Fact]
    public void Tirer_un_coin_laisse_le_coin_oppose_en_place()
    {
        var cadre = Paysage();
        cadre.ZoomIn(); // de la marge, pour pouvoir agrandir comme rétrécir

        // on tire le coin bas-droit : l'ancre est le coin haut-gauche
        var (ancreX, ancreY) = (cadre.X, cadre.Y);

        cadre.ResizeAnchored(cadre.Width * 1.5, ancreX, ancreY);

        Assert.Equal(ancreX, cadre.X, 6);
        Assert.Equal(ancreY, cadre.Y, 6);
    }

    /// <summary>Un seul côté s'écarte : l'autre bord reste là où il était.</summary>
    [Fact]
    public void Tirer_un_coin_n_agrandit_que_de_son_cote()
    {
        var cadre = Paysage();
        cadre.ZoomIn();

        var borddroitAvant = cadre.X + cadre.Width;
        // on tire le coin haut-GAUCHE : l'ancre est le coin bas-droit
        cadre.ResizeAnchored(cadre.Width * 1.4, cadre.X + cadre.Width, cadre.Y + cadre.Height);

        Assert.Equal(borddroitAvant, cadre.X + cadre.Width, 6);
        Assert.True(cadre.X < 0, "le bord gauche aurait dû s'écarter");
    }

    /// <summary>Le format du tirage tient, poignées comprises.</summary>
    [Fact]
    public void Le_format_tient_aussi_a_la_poignee()
    {
        var cadre = Paysage();
        cadre.ResizeAnchored(cadre.Width * 2, cadre.X, cadre.Y);

        var crop = cadre.ToCropSpec();
        Assert.Equal(152 / 102.0, crop.Width * 6000 / (crop.Height * 4000), 3);
    }

    /// <summary>Rétrécir à la poignée ne doit pas découvrir le cadre.</summary>
    [Fact]
    public void La_poignee_ne_peut_pas_decouvrir_le_cadre()
    {
        var cadre = Paysage();

        cadre.ResizeAnchored(1, cadre.X, cadre.Y); // on tire jusqu'à l'absurde

        Assert.True(cadre.X <= 0.001 && cadre.Y <= 0.001);
        Assert.True(cadre.X + cadre.Width >= cadre.FrameWidth - 0.001);
        Assert.True(cadre.Y + cadre.Height >= cadre.FrameHeight - 0.001);
    }

    // — le pas de zoom, repris de DiLand —

    /// <summary>
    /// Le pas de zoom ne dépend PLUS de la définition de la photo.
    ///
    /// Il l'a suivie un temps, à la façon de DiLand : une photo de 6000 px avançait alors
    /// de la moitié de sa largeur en un cran, jugé bien trop brutal le 01/08/2026. Un pas
    /// multiplicatif constant donne le même geste sur toutes les photos — l'opérateur n'a
    /// pas à réapprendre la molette en passant d'un scan à un fichier d'appareil photo.
    /// </summary>
    [Fact]
    public void Le_pas_de_zoom_est_le_meme_quelle_que_soit_la_definition()
    {
        var grande = new FramedCrop(6000, 4000, 152, 102);
        var petite = new FramedCrop(1200, 800, 152, 102);

        var departGrande = grande.Width;
        var departPetite = petite.Width;

        grande.ZoomIn();
        petite.ZoomIn();

        var partGrande = (grande.Width - departGrande) / departGrande;
        var partPetite = (petite.Width - departPetite) / departPetite;

        Assert.Equal(partPetite, partGrande, 6);
        Assert.Equal(FramedCrop.PasZoomMolette - 1, partGrande, 6);
    }

    /// <summary>
    /// Le cran doit rester DOUX : ni inerte, ni brutal. La borne haute est celle qui a
    /// motivé le changement — un cran qui emportait la moitié de la photo.
    /// </summary>
    [Fact]
    public void Un_cran_de_molette_reste_doux()
    {
        var cadre = Paysage();
        var depart = cadre.Width;

        cadre.ZoomIn();

        var part = (cadre.Width - depart) / depart;
        Assert.InRange(part, 0.05, 0.20);
    }

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

    // « Le format tient même redressé » vivait ici ; il rapportait le recadrage aux
    // pixels de la photo droite, repère qui n'est plus le bon depuis que les fractions se
    // comptent sur le canevas redressé. Repris tel quel par
    // Le_format_survit_au_passage_par_le_canevas, à 7° compris.

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

    /// <summary>
    /// Le test qui relie l'écran au papier.
    ///
    /// Le rendu tourne l'image PUIS applique les fractions du recadrage sur le canevas
    /// obtenu, coins vides compris. On refait donc ici le chemin du rendu à l'envers :
    /// les quatre coins du cadre, ramenés dans la photo d'origine, doivent tomber dedans.
    /// S'ils en sortent, le tirage rapporte du blanc — le défaut qui ne se voit qu'une
    /// fois la feuille sortie.
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(3.0)]
    [InlineData(-7.0)]
    [InlineData(15.0)]
    public void Le_recadrage_rendu_ne_mord_pas_sur_les_coins_du_redressement(double degres)
    {
        const int largeurPx = 6000, hauteurPx = 4000;
        var cadre = new FramedCrop(largeurPx, hauteurPx, 152, 102) { RotationDegrees = degres };
        cadre.ZoomIn();
        cadre.Move(10_000, 10_000); // poussée dans un coin, là où ça casse

        var crop = cadre.ToCropSpec();

        // le canevas que le rendu obtient en tournant l'image
        var radians = degres * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var canevasW = largeurPx * cos + hauteurPx * sin;
        var canevasH = largeurPx * sin + hauteurPx * cos;

        double[] xs = [crop.X * canevasW, (crop.X + crop.Width) * canevasW];
        double[] ys = [crop.Y * canevasH, (crop.Y + crop.Height) * canevasH];

        foreach (var x in xs)
            foreach (var y in ys)
            {
                // rotation inverse autour du centre du canevas, qui est celui de l'image
                var dx = x - canevasW / 2;
                var dy = y - canevasH / 2;
                var px = largeurPx / 2.0 + dx * Math.Cos(-radians) - dy * Math.Sin(-radians);
                var py = hauteurPx / 2.0 + dx * Math.Sin(-radians) + dy * Math.Cos(-radians);

                Assert.InRange(px, -1, largeurPx + 1);
                Assert.InRange(py, -1, hauteurPx + 1);
            }
    }

    /// <summary>Le format du tirage survit aussi à ce passage par le canevas.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(6.0)]
    [InlineData(7.0)]
    [InlineData(-11.0)]
    public void Le_format_survit_au_passage_par_le_canevas(double degres)
    {
        var cadre = new FramedCrop(6000, 4000, 152, 102) { RotationDegrees = degres };
        cadre.ZoomIn();
        cadre.Move(-30, 20);

        var radians = degres * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var canevasW = 6000 * cos + 4000 * sin;
        var canevasH = 6000 * sin + 4000 * cos;

        var crop = cadre.ToCropSpec();
        var obtenu = crop.Width * canevasW / (crop.Height * canevasH);

        Assert.Equal(152 / 102.0, obtenu, 3);
    }

    /// <summary>Redressée aussi, une photo rouverte doit se retrouver où on l'a laissée.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(4.0)]
    [InlineData(-9.0)]
    public void Un_recadrage_redresse_se_relit_a_l_identique(double degres)
    {
        var cadre = new FramedCrop(6000, 4000, 152, 102) { RotationDegrees = degres };
        cadre.ZoomIn();
        cadre.Move(-30, -20);
        var enregistre = cadre.ToCropSpec();

        var rouvert = new FramedCrop(6000, 4000, 152, 102) { RotationDegrees = degres };
        rouvert.SetFromCropSpec(enregistre);
        var relu = rouvert.ToCropSpec();

        Assert.Equal(enregistre.X, relu.X, 3);
        Assert.Equal(enregistre.Y, relu.Y, 3);
        Assert.Equal(enregistre.Width, relu.Width, 3);
        Assert.Equal(enregistre.Height, relu.Height, 3);
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

    // — la molette de la surface : un cran, un pixel —

    /// <summary>
    /// Le pas fixe est ce qui supprime l'escalier. Un pas multiplicatif grandit avec la
    /// photo : à 10 %, le premier cran vaut quinze pixels et le dixième en vaut quarante,
    /// et l'on voit le cadrage sauter. Ici le cran vaut la même chose du début à la fin.
    /// </summary>
    [Fact]
    public void Un_cran_de_molette_vaut_toujours_le_meme_ecart()
    {
        var cadre = Paysage();

        var avant = cadre.Width;
        cadre.WidenBy(1);
        var premier = cadre.Width - avant;

        for (var i = 0; i < 300; i++) cadre.WidenBy(1);

        avant = cadre.Width;
        cadre.WidenBy(1);
        var dernier = cadre.Width - avant;

        Assert.Equal(1, premier, 9);
        Assert.Equal(premier, dernier, 9);
    }

    /// <summary>La molette doit revenir exactement d'où elle est partie.</summary>
    [Fact]
    public void La_molette_est_reversible()
    {
        var cadre = Paysage();
        cadre.ZoomIn();   // de la marge, pour ne pas buter sur le minimum
        var depart = cadre.Width;

        for (var i = 0; i < 50; i++) cadre.WidenBy(0.4);
        for (var i = 0; i < 50; i++) cadre.WidenBy(-0.4);

        Assert.Equal(depart, cadre.Width, 9);
    }

    /// <summary>
    /// Même au pixel, la molette ne peut pas faire descendre la photo sous la taille qui
    /// couvre le cadre : le tirage sortirait avec une bande blanche.
    /// </summary>
    [Fact]
    public void La_molette_ne_decouvre_jamais_le_cadre()
    {
        var cadre = Paysage();

        for (var i = 0; i < 5000; i++) cadre.WidenBy(-1);

        Assert.True(cadre.X <= 0.001, $"la photo découvre le cadre à gauche (X = {cadre.X})");
        Assert.True(cadre.Y <= 0.001, $"la photo découvre le cadre en haut (Y = {cadre.Y})");
        Assert.True(cadre.X + cadre.Width >= cadre.FrameWidth - 0.001, "…à droite");
        Assert.True(cadre.Y + cadre.Height >= cadre.FrameHeight - 0.001, "…en bas");
    }

    // — les poignées, qui sont sur le CADRE —

    /// <summary>
    /// Le geste refait tel que la surface le calcule : la poignée est sur le cadre, on
    /// demande un rectangle <paramref name="part"/> fois plus grand que lui, et la photo
    /// est mise à l'échelle qui fait retomber ce rectangle dans le cadre.
    /// </summary>
    private static void TirerPoignee(FramedCrop cadre, double largeurAuDepart,
                                     double part, double ancreX, double ancreY) =>
        cadre.ResizeAnchored(largeurAuDepart / part, ancreX, ancreY);

    /// <summary>
    /// Le sens du geste, qui a valu deux allers-retours : tirer une poignée vers
    /// l'EXTÉRIEUR doit montrer PLUS de la photo. Avec les poignées sur la photo, cela
    /// faisait l'inverse — on agrandissait la photo, donc on gardait moins.
    /// </summary>
    [Fact]
    public void Tirer_une_poignee_vers_l_exterieur_montre_plus_de_photo()
    {
        var cadre = Paysage();
        for (var i = 0; i < 4; i++) cadre.ZoomIn();   // de la marge avant la butée

        var avant = cadre.ToCropSpec();

        // coin haut-gauche tiré vers l'extérieur : on demande un rectangle 1,25 fois le
        // cadre, ancré sur le coin bas-droit du cadre
        TirerPoignee(cadre, cadre.Width, 1.25, cadre.FrameWidth, cadre.FrameHeight);

        var apres = cadre.ToCropSpec();

        Assert.True(apres.Width > avant.Width,
            $"le cadrage doit s'élargir ({avant.Width:0.000} → {apres.Width:0.000})");
    }

    /// <summary>Et vers l'intérieur, l'inverse : on serre.</summary>
    [Fact]
    public void Tirer_une_poignee_vers_l_interieur_serre_le_cadrage()
    {
        var cadre = Paysage();
        var avant = cadre.ToCropSpec();

        TirerPoignee(cadre, cadre.Width, 0.8, cadre.FrameWidth, cadre.FrameHeight);

        var apres = cadre.ToCropSpec();

        Assert.True(apres.Width < avant.Width,
            $"le cadrage doit se resserrer ({avant.Width:0.000} → {apres.Width:0.000})");
    }

    /// <summary>
    /// Le geste se compte depuis son DÉBUT, jamais depuis l'image précédente.
    ///
    /// Sinon le même écart demandé à chaque pixel de souris s'appliquerait encore et
    /// encore : la photo fondrait en deux centimètres de curseur. Refaire deux fois le
    /// même geste depuis la même largeur de départ doit donc tomber au même endroit.
    /// </summary>
    [Fact]
    public void Le_geste_de_poignee_ne_s_accumule_pas()
    {
        var cadre = Paysage();
        cadre.ZoomIn();

        var depart = cadre.Width;
        var ancreX = cadre.FrameWidth;
        var ancreY = cadre.FrameHeight;

        TirerPoignee(cadre, depart, 1.2, ancreX, ancreY);
        var uneFois = cadre.ToCropSpec();

        // la souris a bougé, mais revient à la même distance : le résultat doit être le même
        TirerPoignee(cadre, depart, 1.5, ancreX, ancreY);
        TirerPoignee(cadre, depart, 1.2, ancreX, ancreY);
        var deRetour = cadre.ToCropSpec();

        Assert.Equal(uneFois.X, deRetour.X, 6);
        Assert.Equal(uneFois.Y, deRetour.Y, 6);
        Assert.Equal(uneFois.Width, deRetour.Width, 6);
    }

    /// <summary>
    /// Tirer un coin ne doit rien déranger du côté opposé : le point du cadre qui sert
    /// d'ancre garde la même place sur la photo.
    ///
    /// La photo est largement agrandie au départ, et le geste modeste : c'est le seul
    /// moyen de vérifier l'ancrage SEUL. Un geste ample viendrait buter sur l'interdiction
    /// de découvrir le cadre, qui recentre la photo — et l'on ne saurait plus si l'ancre a
    /// tenu ou si c'est la butée qui a parlé.
    /// </summary>
    [Fact]
    public void Le_coin_oppose_ne_bouge_pas()
    {
        var cadre = Paysage();
        for (var i = 0; i < 4; i++) cadre.ZoomIn();

        // où tombe le coin bas-droit du cadre sur la photo, en fraction de celle-ci
        double SurLaPhoto() => (cadre.FrameWidth - cadre.X) / cadre.Width;
        var avant = SurLaPhoto();

        TirerPoignee(cadre, cadre.Width, 1.05, cadre.FrameWidth, cadre.FrameHeight);

        Assert.Equal(avant, SurLaPhoto(), 6);
    }

    // — les encoches de côté —

    /// <summary>
    /// Tirer l'encoche du HAUT ne doit pas déplacer le bas : c'est ce qui distingue une
    /// encoche de côté d'un coin, et ce qui permet de recouper le ciel sans redescendre
    /// chercher le sol.
    ///
    /// L'ancre est le milieu du côté opposé, c'est-à-dire le point que la surface passe à
    /// <see cref="FramedCrop.ResizeAnchored"/> quand on saisit cette encoche.
    /// </summary>
    [Fact]
    public void L_encoche_du_haut_laisse_le_bas_en_place()
    {
        var cadre = Paysage();
        cadre.ZoomIn();

        var bas = cadre.Y + cadre.Height;
        var milieuX = cadre.X + cadre.Width / 2;

        cadre.ResizeAnchored(cadre.Width * 1.3, milieuX, bas);

        Assert.Equal(bas, cadre.Y + cadre.Height, 6);
        Assert.Equal(milieuX, cadre.X + cadre.Width / 2, 6);
    }

    /// <summary>
    /// Tirer l'encoche de GAUCHE laisse la droite où elle est, et garde la photo centrée
    /// verticalement — l'axe ne doit pas dériver d'un geste horizontal.
    /// </summary>
    [Fact]
    public void L_encoche_de_gauche_laisse_la_droite_en_place()
    {
        var cadre = Paysage();
        cadre.ZoomIn();

        var droite = cadre.X + cadre.Width;
        var milieuY = cadre.Y + cadre.Height / 2;

        cadre.ResizeAnchored(cadre.Width * 1.4, droite, milieuY);

        Assert.Equal(droite, cadre.X + cadre.Width, 6);
        Assert.Equal(milieuY, cadre.Y + cadre.Height / 2, 6);
    }

    // — mode « photo entière » : des marges blanches, et rien de coupé —

    /// <summary>
    /// Le mode ne changeait RIEN : le cadre forçait la photo à couvrir le format quoi
    /// qu'il arrive, donc l'écran montrait un recadrage là où le tirage allait poser des
    /// marges blanches (signalé le 01/08/2026). La photo doit désormais tenir entière
    /// dans le cadre, et y laisser du vide.
    /// </summary>
    [Fact]
    public void Le_mode_photo_entiere_laisse_la_photo_entiere_dans_le_cadre()
    {
        // une photo 3:2 dans un cadre 10×15 tourné en portrait : c'est le cas qui coupe
        // le plus, donc celui où les marges se voient le mieux
        var cadre = new FramedCrop(6000, 4000, 102, 152) { AllowsWhiteMargins = true };
        cadre.Reset();

        Assert.True(cadre.Width <= cadre.FrameWidth + 0.001, "la photo déborde en largeur");
        Assert.True(cadre.Height <= cadre.FrameHeight + 0.001, "la photo déborde en hauteur");

        // et elle est bien centrée : le tirage la centre aussi
        Assert.Equal(cadre.FrameWidth / 2, cadre.X + cadre.Width / 2, 6);
        Assert.Equal(cadre.FrameHeight / 2, cadre.Y + cadre.Height / 2, 6);

        // du vide sur au moins un axe, sinon il n'y aurait pas de marge à montrer
        Assert.True(cadre.Height < cadre.FrameHeight - 1, "aucune marge : le mode ne sert à rien");
    }

    /// <summary>
    /// En « photo entière », le recadrage rendu au tirage doit être l'image COMPLÈTE :
    /// c'est le rendu qui pose les marges blanches, pas le cadre.
    /// </summary>
    [Fact]
    public void Le_mode_photo_entiere_ne_coupe_rien()
    {
        var cadre = new FramedCrop(6000, 4000, 102, 152) { AllowsWhiteMargins = true };
        cadre.Reset();

        var crop = cadre.ToCropSpec();

        Assert.Equal(0, crop.X, 6);
        Assert.Equal(0, crop.Y, 6);
        Assert.Equal(1, crop.Width, 6);
        Assert.Equal(1, crop.Height, 6);
    }

    /// <summary>En « remplir le format », rien ne change : la photo couvre toujours le cadre.</summary>
    [Fact]
    public void Le_mode_remplir_couvre_toujours_le_cadre()
    {
        var cadre = new FramedCrop(6000, 4000, 102, 152);

        Assert.True(cadre.Width >= cadre.FrameWidth - 0.001, "le cadre n'est pas couvert en largeur");
        Assert.True(cadre.Height >= cadre.FrameHeight - 0.001, "le cadre n'est pas couvert en hauteur");
    }

    /// <summary>Une encoche ne dispense pas du format : le rapport du tirage tient bon.</summary>
    [Fact]
    public void Une_encoche_garde_le_format_du_tirage()
    {
        var cadre = Paysage();
        var attendu = 152 / 102.0;

        cadre.ResizeAnchored(cadre.Width * 1.6, cadre.X + cadre.Width / 2, cadre.Y + cadre.Height);
        cadre.ResizeAnchored(cadre.Width * 1.2, cadre.X + cadre.Width, cadre.Y + cadre.Height / 2);

        var crop = cadre.ToCropSpec();
        Assert.Equal(attendu, crop.Width * 6000 / (crop.Height * 4000), 3);
    }
}
