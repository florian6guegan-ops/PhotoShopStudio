using Studio.Core.Imaging;
using Studio.Imaging;

namespace Studio.Tests;

/// <summary>
/// Le détourage du fond blanc est réglable poste par poste.
///
/// <b>Ce que la 6ᵉ passe a trouvé.</b> <c>BiRefNetMatting.Actif</c> était un <c>bool</c>
/// statique à faux qu'aucune ligne du dépôt n'assignait : le réseau ne s'exécutait jamais,
/// et tout ce code était mort. Il l'est désormais par les réglages du poste.
///
/// Le défaut RESTE faux, et c'est délibéré — mesuré sur la Quadro P2000 de l'atelier,
/// 9,5 s par photo pleine résolution contre 1,2 s pour la méthode par couleur. Une mise à
/// jour ne doit pas allonger le détourage sans que personne l'ait demandé.
/// </summary>
public class DetourageSettingsTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "Detourage-" + Guid.NewGuid().ToString("N"));

    private readonly IReadOnlyList<string> _dossiersDorigine = BiRefNetMatting.DossiersCherches;
    private readonly string? _modeleDorigine = BiRefNetMatting.ModelePrefere;

    public DetourageSettingsTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        // état statique partagé : on le remet où on l'a trouvé, sinon les essais qui
        // suivent hériteraient d'un poste imaginaire
        BiRefNetMatting.DossiersCherches = _dossiersDorigine;
        BiRefNetMatting.ModelePrefere = _modeleDorigine;

        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
    }

    /// <summary>
    /// Le contrôle qui compte : sans fichier de configuration, rien ne change. C'est le cas
    /// de tous les postes le jour de la mise à jour.
    /// </summary>
    [Fact]
    public void Sans_fichier_le_comportement_est_celui_d_avant()
    {
        var reglages = DetourageSettings.Load(_dossier);

        Assert.False(reglages.Actif);
        Assert.False(reglages.ModelePuissant);
    }

    [Fact]
    public void Les_reglages_se_relisent_tels_qu_ils_ont_ete_ecrits()
    {
        DetourageSettings.Save(_dossier, new DetourageSettings(Actif: true, ModelePuissant: true));

        var relus = DetourageSettings.Load(_dossier);

        Assert.True(relus.Actif);
        Assert.True(relus.ModelePuissant);
    }

    /// <summary>
    /// Un fichier abîmé n'empêche pas de démarrer : on retombe sur les valeurs par défaut,
    /// donc sur la méthode par couleur, qui ne dépend de rien.
    /// </summary>
    [Fact]
    public void Un_fichier_abime_retombe_sur_les_valeurs_par_defaut()
    {
        File.WriteAllText(Path.Combine(_dossier, DetourageSettings.FileName), "{ pas du JSON");

        var reglages = DetourageSettings.Load(_dossier);

        Assert.False(reglages.Actif);
        Assert.False(reglages.ModelePuissant);
    }

    [Fact]
    public void Le_modele_demande_suit_la_case()
    {
        Assert.Equal(DetourageSettings.ModeleLeger,
            new DetourageSettings(Actif: true).ModeleDemande);

        Assert.Equal(DetourageSettings.ModelePuissantFichier,
            new DetourageSettings(Actif: true, ModelePuissant: true).ModeleDemande);
    }

    /// <summary>
    /// Le modèle demandé passe en TÊTE de la recherche.
    ///
    /// Avant, l'ordre était figé — « lite » d'abord — et changer de modèle demandait de
    /// retirer un fichier du disque.
    /// </summary>
    [Fact]
    public void Le_modele_demande_passe_devant()
    {
        PoserLesDeuxModeles();

        BiRefNetMatting.ModelePrefere = DetourageSettings.ModelePuissantFichier;
        Assert.Equal(DetourageSettings.ModelePuissantFichier,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));

        BiRefNetMatting.ModelePrefere = DetourageSettings.ModeleLeger;
        Assert.Equal(DetourageSettings.ModeleLeger,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));
    }

    /// <summary>
    /// Le modèle demandé mais absent ne fait pas échouer le détourage : on prend l'autre.
    ///
    /// C'est le cas du poste de l'atelier au 03/08/2026 — seul le « lite » y est installé,
    /// et cocher « modèle puissant » ne doit pas rendre les photos d'identité impossibles.
    /// </summary>
    [Fact]
    public void Un_modele_demande_mais_absent_retombe_sur_l_autre()
    {
        BiRefNetMatting.DossiersCherches = [_dossier];
        File.WriteAllText(Path.Combine(_dossier, DetourageSettings.ModeleLeger), "");

        BiRefNetMatting.ModelePrefere = DetourageSettings.ModelePuissantFichier;

        Assert.Equal(DetourageSettings.ModeleLeger,
            Path.GetFileName(BiRefNetMatting.ModeleRetenu));
    }

    /// <summary>Aucun modèle installé : rien n'est retenu, et l'appelant retombera sur la couleur.</summary>
    [Fact]
    public void Sans_modele_installe_rien_n_est_retenu()
    {
        BiRefNetMatting.DossiersCherches = [_dossier];
        BiRefNetMatting.ModelePrefere = DetourageSettings.ModeleLeger;

        Assert.Null(BiRefNetMatting.ModeleRetenu);
        Assert.False(BiRefNetMatting.EstInstalle);
    }

    private void PoserLesDeuxModeles()
    {
        BiRefNetMatting.DossiersCherches = [_dossier];
        File.WriteAllText(Path.Combine(_dossier, DetourageSettings.ModeleLeger), "");
        File.WriteAllText(Path.Combine(_dossier, DetourageSettings.ModelePuissantFichier), "");
    }
}
