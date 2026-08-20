using ImageMagick;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Les masques de détourage SURVIVENT AU LANCEMENT.
///
/// <b>Ce que ça règle.</b> La mémoire n'en garde que quatre, et elle meurt avec
/// l'application. Rouvrir le lendemain une photo à fond blanc — c'est tout l'objet de
/// l'historique des trente jours — repayait donc un passage complet du réseau : plusieurs
/// secondes, et surtout un SECOND passage, celui qui manque de mémoire vidéo sur les cartes
/// des boutiques et rend un fond dégradé. Demande de l'exploitant, 19/08/2026.
///
/// Ces essais tiennent trois choses :
///
/// - le masque calculé est écrit sur le disque ;
/// - après un « redémarrage » (mémoire vidée, disque intact), il revient <b>sans être
///   recalculé</b> ;
/// - <b>chaque méthode a son dossier</b> : un masque calculé par couleur ne doit jamais
///   ressortir quand le réseau est allumé, sans quoi changer de modèle dans les réglages
///   n'aurait aucun effet visible — et personne ne comprendrait pourquoi.
/// </summary>
[Collection(DetourageStatiqueCollection.Nom)]
public class MasqueSurDisqueTests : IDisposable
{
    private readonly string _cache =
        Path.Combine(Path.GetTempPath(), "Masques-" + Guid.NewGuid().ToString("N"));

    private readonly string? _dossierAvant = MasqueSujet.Dossier;
    private readonly bool _actifAvant = BiRefNetMatting.Actif;

    public MasqueSurDisqueTests()
    {
        MasqueSujet.OublierLaMemoire();
        MasqueSujet.Dossier = _cache;

        // la méthode par couleur : elle marche partout, sans modèle de 109 Mo
        BiRefNetMatting.Actif = false;
    }

    public void Dispose()
    {
        MasqueSujet.OublierLaMemoire();
        MasqueSujet.Dossier = _dossierAvant;
        BiRefNetMatting.Actif = _actifAvant;

        try { Directory.Delete(_cache, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>Un fond de studio uni, et un sujet sombre au milieu : ce que le comptoir voit.</summary>
    private static MagickImage Portrait()
    {
        var image = new MagickImage(new MagickColor("#F2F2F2"), 300, 400);
        using var sujet = new MagickImage(new MagickColor("#303030"), 120, 220);
        image.Composite(sujet, 90, 150, CompositeOperator.Over);
        return image;
    }

    private IEnumerable<string> MasquesEcrits() =>
        Directory.Exists(_cache)
            ? Directory.EnumerateFiles(_cache, "*.png", SearchOption.AllDirectories)
            : [];

    [Fact]
    public void Le_masque_calcule_est_ecrit_sur_le_disque()
    {
        using var image = Portrait();
        using var masque = MasqueSujet.Nu(image, cle: "photo-du-client");

        Assert.NotNull(masque);

        var ecrit = Assert.Single(MasquesEcrits());

        // le dossier porte la méthode : c'est ce qui fait qu'un changement de modèle
        // change ce qui sort
        Assert.Equal("couleur", Path.GetFileName(Path.GetDirectoryName(ecrit)));
    }

    [Fact]
    public void Apres_un_redemarrage_le_masque_revient_du_disque()
    {
        using (var image = Portrait())
        using (var premier = MasqueSujet.Nu(image, cle: "photo-du-client"))
        {
            Assert.NotNull(premier);
        }

        // l'application se ferme et se rouvre : la mémoire est vide, le disque non
        MasqueSujet.OublierLaMemoire();

        // et l'écran n'annonce PAS d'attente : il n'y a rien à attendre
        Assert.True(MasqueSujet.DejaEnMemoire("photo-du-client", 300, 400));

        // ⚠ une image UNIE, sur laquelle aucune méthode ne trouverait de sujet : si un
        // masque revient, il ne peut venir que du disque
        using var blanche = new MagickImage(MagickColors.White, 300, 400);
        using var repris = MasqueSujet.Nu(blanche, cle: "photo-du-client");

        Assert.NotNull(repris);
        Assert.Equal(300u, repris.Width);
        Assert.Equal(400u, repris.Height);
    }

    [Fact]
    public void Le_masque_dune_methode_ne_ressort_pas_pour_une_autre()
    {
        using (var image = Portrait())
        using (var parCouleur = MasqueSujet.Nu(image, cle: "photo-du-client"))
        {
            Assert.NotNull(parCouleur);
        }

        MasqueSujet.OublierLaMemoire();

        // l'exploitant allume le réseau dans les réglages : le masque d'hier ne vaut plus
        BiRefNetMatting.Actif = true;

        Assert.False(MasqueSujet.DejaEnMemoire("photo-du-client", 300, 400));
    }

    [Fact]
    public void Un_fichier_abime_est_efface_et_ne_fait_pas_echouer_le_rendu()
    {
        using (var image = Portrait())
        using (var premier = MasqueSujet.Nu(image, cle: "photo-du-client"))
        {
            Assert.NotNull(premier);
        }

        MasqueSujet.OublierLaMemoire();

        // coupure de courant pendant l'écriture : le fichier n'est plus un PNG
        var ecrit = MasquesEcrits().Single();
        File.WriteAllText(ecrit, "ceci n'est pas un PNG");

        using var blanche = new MagickImage(MagickColors.White, 300, 400);
        var repris = MasqueSujet.Nu(blanche, cle: "photo-du-client");
        repris?.Dispose();

        // le fichier abîmé est parti : il ne sera pas relu à chaque photo
        Assert.False(File.Exists(ecrit));
    }

    [Fact]
    public void Sans_dossier_regle_rien_nest_ecrit()
    {
        MasqueSujet.Dossier = null;

        using var image = Portrait();
        using var masque = MasqueSujet.Nu(image, cle: "photo-du-client");

        Assert.NotNull(masque);
        Assert.Empty(MasquesEcrits());
    }
}
