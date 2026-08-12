using ImageMagick;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le masque du sujet se réutilise d'une TAILLE à l'autre.
///
/// <b>Le récapitulatif des planches était très long, et c'est pour ça.</b> Le masque était
/// mémorisé sous une clé qui portait les dimensions : l'aperçu du cadrage et la planche
/// rendue à la taille d'impression n'avaient donc pas la même, et le réseau repassait —
/// <b>14 495 ms pour une seule photo</b>, relevés à Créteil le 12/08/2026. C'est aussi ce
/// second passage qui mettait la carte graphique en panne de mémoire.
///
/// Or BiRefNet travaille sur une entrée FIGÉE à 1024 × 1024 et ne remet à l'échelle qu'à la
/// fin : deux tailles de sortie donnent le même masque, à un redimensionnement près.
/// </summary>
[Collection(DetourageStatiqueCollection.Nom)]
public class MasqueReutiliseTests : IDisposable
{
    private readonly bool _actifAvant = BiRefNetMatting.Actif;

    public MasqueReutiliseTests()
    {
        // On veut mesurer le CACHE, pas le réseau : la méthode par couleur suffit, elle est
        // déterministe et n'a besoin d'aucune carte graphique.
        BiRefNetMatting.Actif = false;
        MasqueSujet.Oublier();
    }

    public void Dispose()
    {
        BiRefNetMatting.Actif = _actifAvant;
        MasqueSujet.Oublier();
    }

    /// <summary>Un sujet sombre sur fond blanc : ce que la méthode par couleur sait traiter.</summary>
    private static MagickImage Photo(uint largeur, uint hauteur)
    {
        var image = new MagickImage(MagickColors.White, largeur, hauteur);
        using var sujet = new MagickImage(MagickColors.SaddleBrown, largeur / 2, hauteur / 2);
        image.Composite(sujet, (int)largeur / 4, (int)hauteur / 4, CompositeOperator.Over);
        return image;
    }

    /// <summary>
    /// LE test de la lenteur : la même photo à une AUTRE taille ne redemande pas de calcul.
    /// </summary>
    [Fact]
    public void Le_masque_d_un_apercu_sert_a_la_planche_pleine_resolution()
    {
        using var apercu = Photo(400, 600);
        using var premier = MasqueSujet.Calculer(apercu, 0, 0, cle: "photo-001");

        Assert.NotNull(premier);
        Assert.True(MasqueSujet.DejaEnMemoire("photo-001", 400, 600));

        // la MÊME photo, rendue à la taille d'impression : plus rien à calculer
        Assert.True(MasqueSujet.DejaEnMemoire("photo-001", 1844, 1240),
            "le masque doit servir quelle que soit la taille demandée");
    }

    /// <summary>
    /// Et il en sort aux BONNES dimensions : un masque de l'aperçu appliqué tel quel à une
    /// planche pleine résolution ne recouvrirait qu'un coin de l'image.
    /// </summary>
    [Fact]
    public void Le_masque_reutilise_sort_a_la_taille_demandee()
    {
        using var apercu = Photo(400, 600);
        using var petit = MasqueSujet.Calculer(apercu, 0, 0, cle: "photo-002");
        Assert.NotNull(petit);
        Assert.Equal(400u, petit!.Width);
        Assert.Equal(600u, petit.Height);

        using var grande = Photo(800, 1200);
        using var grand = MasqueSujet.Calculer(grande, 0, 0, cle: "photo-002");

        Assert.NotNull(grand);
        Assert.Equal(800u, grand!.Width);
        Assert.Equal(1200u, grand.Height);
    }

    /// <summary>
    /// Deux photos différentes ne partagent jamais un masque, quelle que soit leur taille.
    /// C'est la garantie que la clé sert encore à quelque chose.
    /// </summary>
    [Fact]
    public void Deux_photos_differentes_gardent_des_masques_distincts()
    {
        using var a = Photo(400, 600);
        using var b = Photo(400, 600);

        Assert.NotNull(MasqueSujet.Calculer(a, 0, 0, cle: "photo-A"));

        Assert.True(MasqueSujet.DejaEnMemoire("photo-A", 400, 600));
        Assert.False(MasqueSujet.DejaEnMemoire("photo-B", 400, 600),
            "une photo qu'on n'a jamais détourée ne doit pas passer pour connue");

        Assert.NotNull(MasqueSujet.Calculer(b, 0, 0, cle: "photo-B"));
        Assert.True(MasqueSujet.DejaEnMemoire("photo-B", 400, 600));
    }

    /// <summary>
    /// Sans clé fournie, on retombe sur la signature des pixels — qui distingue d'elle-même
    /// deux tailles de la même photo. C'est le comportement d'avant, et il reste juste.
    /// </summary>
    [Fact]
    public void Sans_cle_le_masque_reste_propre_a_la_taille()
    {
        using var petite = Photo(400, 600);
        using var masque = MasqueSujet.Calculer(petite, 0, 0);

        Assert.NotNull(masque);
        Assert.Equal(400u, masque!.Width);
        Assert.Equal(600u, masque.Height);
    }

    /// <summary>Oublier vide vraiment : sinon un changement de modèle ne se verrait pas.</summary>
    [Fact]
    public void Oublier_vide_la_memoire_des_masques()
    {
        using var photo = Photo(400, 600);
        Assert.NotNull(MasqueSujet.Calculer(photo, 0, 0, cle: "photo-003"));
        Assert.True(MasqueSujet.DejaEnMemoire("photo-003", 400, 600));

        MasqueSujet.Oublier();

        Assert.False(MasqueSujet.DejaEnMemoire("photo-003", 400, 600));
    }

    // ————— les curseurs —————

    /// <summary>
    /// L'ALLER-RETOUR entre les poses d'une planche ne doit plus tout recalculer.
    ///
    /// Le masque retouché — contour dilaté, bord adouci — n'avait qu'UN emplacement.
    /// Revenir à la pose précédente le jetait, et chaque curseur y repayait les 360 ms de
    /// dilatation et de flou. « Les curseurs sont de nouveau lents PARFOIS », signalé à
    /// Créteil le 12/08/2026 : parfois, c'est-à-dire chaque fois qu'on change de photo.
    ///
    /// L'essai ne mesure pas un temps — ce serait fragile sur une machine chargée. Il
    /// vérifie ce dont le temps dépend : que les masques des quatre poses coexistent, et
    /// que le retour à la première ne redemande rien.
    /// </summary>
    [Fact]
    public void Quatre_poses_gardent_chacune_leur_masque_retouche()
    {
        var photos = new List<MagickImage>();
        try
        {
            for (var i = 1; i <= 4; i++)
            {
                var photo = Photo(400, 600);
                photos.Add(photo);
                using var masque = MasqueSujet.Calculer(photo, contourPx: 2, adoucissementPx: 3,
                    cle: $"pose-{i}");
                Assert.NotNull(masque);
            }

            // les quatre sont connues, la première comme la dernière
            for (var i = 1; i <= 4; i++)
                Assert.True(MasqueSujet.DejaEnMemoire($"pose-{i}", 400, 600),
                    $"la pose {i} doit être restée en mémoire");
        }
        finally
        {
            foreach (var p in photos) p.Dispose();
        }
    }

    /// <summary>
    /// Et la mémoire reste bornée : ces masques pèsent plusieurs mégaoctets, on n'en garde
    /// pas un par photo de la journée.
    /// </summary>
    [Fact]
    public void La_memoire_des_masques_reste_bornee()
    {
        var photos = new List<MagickImage>();
        try
        {
            for (var i = 1; i <= 7; i++)
            {
                var photo = Photo(400, 600);
                photos.Add(photo);
                using var masque = MasqueSujet.Calculer(photo, 0, 0, cle: $"beaucoup-{i}");
                Assert.NotNull(masque);
            }

            Assert.False(MasqueSujet.DejaEnMemoire("beaucoup-1", 400, 600),
                "la plus ancienne doit avoir été oubliée");
            Assert.True(MasqueSujet.DejaEnMemoire("beaucoup-7", 400, 600),
                "la plus récente doit être là");
        }
        finally
        {
            foreach (var p in photos) p.Dispose();
        }
    }
}
