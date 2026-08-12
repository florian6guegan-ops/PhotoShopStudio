using Studio.Core.Imaging;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Ce qui arrive quand la carte graphique manque de mémoire au milieu d'une séance.
///
/// <b>La panne de Créteil, le 12/08/2026.</b> Au cadrage, le fond était parfait ; au
/// récapitulatif des planches, il ne l'était plus. La planche est rendue à la taille
/// d'impression, donc sous une autre empreinte de cache que l'aperçu — le réseau repasse,
/// et c'est ce second passage qui tombait :
///
/// <code>
/// 13:23:39  BiRefNet : échec du détourage (... DmlFusedNode_0_0 ...) — repli sur la méthode par couleur.
/// 13:23:48  BiRefNet : échec du détourage ([ErrorCode:Fail] ) — repli ...
/// 13:23:55  BiRefNet : échec du détourage ([ErrorCode:Fail] ) — repli ...
/// 13:23:58  BiRefNet : échec du détourage ([ErrorCode:Fail] ) — repli ...
/// </code>
///
/// Deux défauts s'y lisent, et ce sont deux essais distincts ci-dessous :
///
/// 1. la session morte n'était pas jetée — d'où les <c>[ErrorCode:Fail]</c> en rafale, et
///    une séance ENTIÈRE détourée à la couleur jusqu'au redémarrage ;
/// 2. le repli allait au pire — la règle par couleur — alors que le modèle « lite » tient
///    sur cette carte et donne un contour sans commune mesure.
/// </summary>
[Collection(DetourageStatiqueCollection.Nom)]
public class RepliDetourageTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "Repli-" + Guid.NewGuid().ToString("N"));

    private readonly IReadOnlyList<string> _dossiersDorigine = BiRefNetMatting.DossiersCherches;
    private readonly string? _modeleDorigine = BiRefNetMatting.ModelePrefere;

    public RepliDetourageTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        BiRefNetMatting.DossiersCherches = _dossiersDorigine;
        BiRefNetMatting.ModelePrefere = _modeleDorigine;

        // état statique partagé : les modèles écartés et le message de repli ne doivent pas
        // survivre à cette classe
        BiRefNetMatting.Reinitialiser();

        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
    }

    /// <summary>
    /// LE défaut de Créteil. Le modèle puissant manque de mémoire : on ne retombe pas sur
    /// la couleur tant que le léger est là, et l'on redemande un tour.
    /// </summary>
    [Fact]
    public void Le_modele_puissant_a_court_de_memoire_cede_la_place_au_leger()
    {
        PoserLesDeuxModeles();
        BiRefNetMatting.ModelePrefere = DetourageSettings.ModelePuissantFichier;

        Assert.Equal(DetourageSettings.ModelePuissantFichier,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));

        var reessayer = BiRefNetMatting.EcarterEtReessayer(
            DetourageSettings.ModelePuissantFichier, PanneDeMemoire());

        Assert.True(reessayer, "il reste le modèle léger : on doit refaire un tour");
        Assert.Equal(DetourageSettings.ModeleLeger,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));
    }

    /// <summary>
    /// Le modèle écarté ne revient pas à la photo suivante : il rejouerait la même panne, et
    /// ferait attendre à chaque fois.
    /// </summary>
    [Fact]
    public void Un_modele_ecarte_ne_revient_pas_de_lui_meme()
    {
        PoserLesDeuxModeles();
        BiRefNetMatting.ModelePrefere = DetourageSettings.ModelePuissantFichier;

        BiRefNetMatting.EcarterEtReessayer(
            DetourageSettings.ModelePuissantFichier, PanneDeMemoire());

        // le réglage du poste demande TOUJOURS le puissant : c'est l'écart qui doit primer
        Assert.Equal(DetourageSettings.ModelePuissantFichier, BiRefNetMatting.ModelePrefere);
        Assert.Equal(DetourageSettings.ModeleLeger,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));
    }

    /// <summary>
    /// Quand il ne reste plus rien, on retombe sur la couleur — et l'on cesse de tourner.
    /// C'est ce qui borne la boucle de <c>CalculerMasque</c>.
    /// </summary>
    [Fact]
    public void Le_dernier_modele_ecarte_rend_la_main_a_la_methode_par_couleur()
    {
        PoserLesDeuxModeles();
        BiRefNetMatting.ModelePrefere = DetourageSettings.ModelePuissantFichier;

        Assert.True(BiRefNetMatting.EcarterEtReessayer(
            DetourageSettings.ModelePuissantFichier, PanneDeMemoire()));

        Assert.False(BiRefNetMatting.EcarterEtReessayer(
            DetourageSettings.ModeleLeger, PanneDeMemoire()),
            "plus aucun modèle : il n'y a plus de tour à faire");

        Assert.Null(BiRefNetMatting.ModeleRetenu);
    }

    /// <summary>
    /// L'opérateur doit l'apprendre. Le repli n'était visible que du journal — l'écran, lui,
    /// montrait une planche au fond moins propre sans un mot d'explication.
    /// </summary>
    [Fact]
    public void Le_repli_laisse_une_phrase_pour_l_operateur()
    {
        PoserLesDeuxModeles();
        BiRefNetMatting.ModelePrefere = DetourageSettings.ModelePuissantFichier;

        Assert.Null(BiRefNetMatting.DernierRepli);

        BiRefNetMatting.EcarterEtReessayer(
            DetourageSettings.ModelePuissantFichier, PanneDeMemoire());

        Assert.NotNull(BiRefNetMatting.DernierRepli);
        Assert.Contains("mémoire", BiRefNetMatting.DernierRepli!, StringComparison.OrdinalIgnoreCase);

        BiRefNetMatting.EcarterEtReessayer(DetourageSettings.ModeleLeger, PanneDeMemoire());

        Assert.NotNull(BiRefNetMatting.DernierRepli);
        Assert.Contains("couleur", BiRefNetMatting.DernierRepli!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Toucher aux réglages redonne sa chance au modèle écarté : le poste a pu changer de
    /// carte, et l'exploitant a le droit de réessayer sans redémarrer l'application.
    /// </summary>
    [Fact]
    public void Changer_les_reglages_redonne_sa_chance_au_modele_ecarte()
    {
        PoserLesDeuxModeles();
        BiRefNetMatting.ModelePrefere = DetourageSettings.ModelePuissantFichier;

        BiRefNetMatting.EcarterEtReessayer(
            DetourageSettings.ModelePuissantFichier, PanneDeMemoire());
        Assert.Equal(DetourageSettings.ModeleLeger,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));

        BiRefNetMatting.Reinitialiser();

        Assert.Equal(DetourageSettings.ModelePuissantFichier,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));
        Assert.Null(BiRefNetMatting.DernierRepli);
    }

    /// <summary>
    /// Le seuil de mémoire vidéo, relevé le 12/08/2026 : la GTX 1660 SUPER de Créteil
    /// annonce 6 Go tout ronds et a échoué. Un seuil à 6 la laissait passer, puisque la
    /// comparaison est stricte.
    /// </summary>
    [Fact]
    public void Six_gigaoctets_ne_suffisent_plus_au_modele_puissant()
    {
        const double creteil = 6;

        Assert.True(creteil < DetourageSettings.MemoireVideoMinimaleGo,
            "la carte de Créteil doit désormais être écartée : elle a échoué en boutique");
        Assert.True(DetourageSettings.MemoireVideoMinimaleGo
                    <= DetourageSettings.MemoireVideoRecommandeeGo,
            "on ne peut pas exiger plus que ce qu'on recommande");
    }

    private static Exception PanneDeMemoire() => new InvalidOperationException(
        "[ErrorCode:RuntimeException] Non-zero status code returned while running " +
        "DmlFusedNode_0_0 node. Name:'DmlFusedNode_0_0' Status Message: ");

    private void PoserLesDeuxModeles()
    {
        BiRefNetMatting.DossiersCherches = [_dossier];
        File.WriteAllText(Path.Combine(_dossier, DetourageSettings.ModeleLeger), "");
        File.WriteAllText(Path.Combine(_dossier, DetourageSettings.ModelePuissantFichier), "");
    }
}
