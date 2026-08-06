using Studio.App;

namespace Studio.Tests;

/// <summary>
/// Où l'application va chercher ses données au démarrage.
///
/// Ce choix décide si Studio s'ouvre ou affiche « Impossible de démarrer » : le chemin de
/// la boutique était écrit en dur sur <c>D:</c>, et un poste sans ce disque ne passait pas
/// la création des sous-dossiers.
/// </summary>
public class RacineDonneesTests
{
    [Fact]
    public void La_variable_d_environnement_l_emporte_sur_tout()
    {
        var voulu = Path.Combine(Path.GetTempPath(), "studio-racine-" + Guid.NewGuid().ToString("N"));
        var avant = Environment.GetEnvironmentVariable("STUDIO_DATA");

        try
        {
            Environment.SetEnvironmentVariable("STUDIO_DATA", voulu);
            Assert.Equal(voulu, AppServices.RacineDonneesParDefaut());
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUDIO_DATA", avant);
        }
    }

    [Fact]
    public void Sans_variable_la_racine_est_utilisable_en_ecriture()
    {
        var avant = Environment.GetEnvironmentVariable("STUDIO_DATA");

        try
        {
            Environment.SetEnvironmentVariable("STUDIO_DATA", null);

            var racine = AppServices.RacineDonneesParDefaut();

            // Le point qui compte n'est pas QUEL dossier est choisi — il dépend des disques
            // du poste — mais qu'on puisse y écrire. C'est précisément ce qui manquait.
            Assert.False(string.IsNullOrWhiteSpace(racine));
            Assert.True(Directory.Exists(racine), $"racine inexistante : {racine}");

            var temoin = Path.Combine(racine, $"essai-{Guid.NewGuid():N}.txt");
            File.WriteAllText(temoin, "essai");
            File.Delete(temoin);
        }
        finally
        {
            Environment.SetEnvironmentVariable("STUDIO_DATA", avant);
        }
    }
}
