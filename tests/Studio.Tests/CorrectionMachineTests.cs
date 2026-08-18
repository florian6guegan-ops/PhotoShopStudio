using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// La compensation d'impression : ce qu'on ajoute au rendu pour que le papier ressemble à
/// l'écran, machine par machine. Demandée le 18/08/2026 — « les photos sont légèrement plus
/// foncées », et « un profil manuel que l'on peut modifier à sa guise et désactiver ».
///
/// Les deux pièges verrouillés ici sont ceux déjà payés sur <c>Product.PrintExposure</c> :
/// une correction éteinte ne doit rien coûter, et les réglages de la COMMANDE ne doivent
/// jamais être touchés — sans quoi la troisième réimpression sort délavée.
/// </summary>
public class CorrectionMachineTests
{
    private static CorrectionMachine Machine(double exposition = 0.15) => new()
    {
        Actif = true,
        Exposition = exposition,
    };

    [Fact]
    public void La_correction_s_ajoute_aux_reglages_de_l_operateur()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5, Contrast = 10 };

        var corriges = new CorrectionMachine
        {
            Actif = true,
            Exposition = 0.15,
            Contraste = 5,
            Ombres = 20,
        }.Appliquer(reglages);

        Assert.Equal(0.65, corriges.Exposure, 3);
        Assert.Equal(15, corriges.Contrast, 3);
        Assert.Equal(20, corriges.Shadows, 3);
    }

    /// <summary>
    /// Éteinte, elle rend l'objet TEL QUEL — pas une copie. C'est ce qui permet de la
    /// traverser sur les milliers de tirages du minilab sans rien payer.
    /// </summary>
    [Fact]
    public void Eteinte_elle_rend_l_objet_tel_quel()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5 };
        var correction = Machine();
        correction.Actif = false;

        Assert.Same(reglages, correction.Appliquer(reglages));
    }

    /// <summary>Allumée mais sans une valeur posée : rien à ajouter, donc rien à copier.</summary>
    [Fact]
    public void Allumee_mais_neutre_elle_rend_l_objet_tel_quel()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5 };

        Assert.Same(reglages, new CorrectionMachine { Actif = true }.Appliquer(reglages));
    }

    /// <summary>
    /// ⚠ Les réglages appartiennent à la COMMANDE enregistrée. Les modifier ferait
    /// s'empiler la compensation à chaque réimpression.
    /// </summary>
    [Fact]
    public void Les_reglages_recus_ne_sont_jamais_modifies()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5 };
        var correction = Machine(0.15);

        correction.Appliquer(reglages);
        correction.Appliquer(reglages);
        var troisieme = correction.Appliquer(reglages);

        Assert.Equal(0.5, reglages.Exposure, 3);
        Assert.Equal(0.65, troisieme.Exposure, 3);
    }

    /// <summary>
    /// Le sujet est un objet à part : une copie de surface le partagerait, et la
    /// compensation d'une machine finirait par bouger les curseurs de l'écran.
    /// </summary>
    [Fact]
    public void Le_sujet_de_la_commande_n_est_pas_partage_avec_la_copie()
    {
        var reglages = new ImageAdjustments();
        reglages.Sujet.Actif = true;
        reglages.Sujet.Exposure = 0.25;

        var corriges = Machine().Appliquer(reglages);
        corriges.Sujet.Exposure = 1;

        Assert.Equal(0.25, reglages.Sujet.Exposure, 3);
    }

    /// <summary>
    /// On borne la SOMME, pas la correction : une photo déjà poussée au maximum par
    /// l'opérateur ne doit pas sortir au-delà de ce que le pipeline sait lire.
    /// </summary>
    [Fact]
    public void La_somme_est_bornee()
    {
        var reglages = new ImageAdjustments { Contrast = 100, Sharpness = 90, Exposure = 2 };

        var corriges = new CorrectionMachine
        {
            Actif = true,
            Contraste = 30,
            Nettete = 40,
            Exposition = 1,
        }.Appliquer(reglages);

        Assert.Equal(100, corriges.Contrast, 3);
        Assert.Equal(100, corriges.Sharpness, 3);
        Assert.Equal(CorrectionMachine.ExpositionMax, corriges.Exposure, 3);
    }

    /// <summary>
    /// Le spouleur écrit « DP-DS620 », l'opérateur tape « dp-ds620 » : une compensation
    /// qui ne s'appliquerait pas faute d'une majuscule serait introuvable.
    /// </summary>
    [Fact]
    public void La_machine_se_retrouve_quelle_que_soit_la_casse()
    {
        var corrections = new CorrectionsMachines();
        corrections.Poser("DP-DS620", Machine());

        Assert.NotNull(corrections.Pour("dp-ds620"));
        Assert.Null(corrections.Pour("Minilab DE100"));
        Assert.Null(corrections.Pour(null));
    }

    /// <summary>Une machine sans correction reçoit les réglages tels quels.</summary>
    [Fact]
    public void Une_machine_sans_correction_ne_change_rien()
    {
        var reglages = new ImageAdjustments { Exposure = 0.5 };
        var corrections = new CorrectionsMachines();
        corrections.Poser("DP-DS620", Machine());

        Assert.Same(reglages, corrections.Appliquer(reglages, "Minilab DE100"));
        Assert.NotSame(reglages, corrections.Appliquer(reglages, "DP-DS620"));
    }

    /// <summary>
    /// Éteinte ET remise à zéro, la ligne est RETIRÉE : le fichier ne garde que les
    /// machines réellement corrigées, et l'on voit d'un coup d'œil lesquelles.
    /// </summary>
    [Fact]
    public void Une_correction_eteinte_et_neutre_est_retiree()
    {
        var corrections = new CorrectionsMachines();
        corrections.Poser("DP-DS620", Machine());

        corrections.Poser("DP-DS620", new CorrectionMachine());

        Assert.Empty(corrections.Machines);
    }

    /// <summary>
    /// Une correction éteinte mais RÉGLÉE est gardée : on éteint pour comparer un tirage,
    /// et retrouver ses valeurs en rallumant est tout l'intérêt de l'interrupteur.
    /// </summary>
    [Fact]
    public void Une_correction_eteinte_mais_reglee_est_gardee()
    {
        var corrections = new CorrectionsMachines();
        var correction = Machine();
        correction.Actif = false;

        corrections.Poser("DP-DS620", correction);

        Assert.Single(corrections.Machines);
        Assert.Equal(0.15, corrections.Pour("DP-DS620")!.Exposition, 3);
    }

    [Fact]
    public void Le_fichier_fait_l_aller_retour_casse_comprise()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "studio-corrections-" + Guid.NewGuid().ToString("N"));

        try
        {
            var corrections = new CorrectionsMachines();
            corrections.Poser("DP-DS620", new CorrectionMachine
            {
                Actif = true,
                Exposition = 0.15,
                Ombres = 10,
            });

            CorrectionsMachines.Save(dossier, corrections);
            var relues = CorrectionsMachines.Load(dossier);

            var machine = relues.Pour("dp-ds620");
            Assert.NotNull(machine);
            Assert.True(machine.Actif);
            Assert.Equal(0.15, machine.Exposition, 3);
            Assert.Equal(10, machine.Ombres, 3);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }

    /// <summary>
    /// Fichier absent ou abîmé : des corrections vides, jamais une exception. Une
    /// compensation illisible doit priver du réglage, pas du tirage.
    /// </summary>
    [Fact]
    public void Un_fichier_absent_ou_abime_ne_prive_que_du_reglage()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "studio-corrections-" + Guid.NewGuid().ToString("N"));

        try
        {
            Assert.Empty(CorrectionsMachines.Load(dossier).Machines);

            Directory.CreateDirectory(dossier);
            File.WriteAllText(Path.Combine(dossier, CorrectionsMachines.FileName), "{ ceci n'est pas du JSON");

            Assert.Empty(CorrectionsMachines.Load(dossier).Machines);
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }
}
