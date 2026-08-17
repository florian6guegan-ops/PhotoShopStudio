using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Les réglages du poste identité survivent-ils à l'aller-retour par le disque ?
///
/// La question n'est pas théorique : « cocher ou décocher, il cadre quand même », signalé le
/// 18/08/2026. Un champ qui ne s'écrirait pas donnerait exactement ce symptôme — la case
/// paraît prise, le fichier n'en garde rien, et la valeur par défaut revient au chargement.
/// </summary>
public class ReglagesIdentiteTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "studio-reglages-id-" + Guid.NewGuid().ToString("N"));

    public ReglagesIdentiteTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Le cadrage automatique est VRAI par défaut : rien ne change pour qui n'y touche pas.
    /// </summary>
    [Fact]
    public void Le_cadrage_automatique_est_actif_par_defaut()
    {
        Assert.True(new ReglagesIdentite().CadrageAutomatique);
        Assert.True(ReglagesIdentite.Load(_dossier).CadrageAutomatique);
    }

    /// <summary>
    /// LE CAS SIGNALÉ : on décoche, et il faut que ça tienne. Un champ perdu à l'écriture
    /// rendrait la case parfaitement inopérante, sans rien dire.
    /// </summary>
    [Fact]
    public void Le_cadrage_decoche_survit_a_l_enregistrement()
    {
        ReglagesIdentite.Save(_dossier, new ReglagesIdentite(CadrageAutomatique: false));

        Assert.False(ReglagesIdentite.Load(_dossier).CadrageAutomatique);
    }

    /// <summary>Et il se recoche.</summary>
    [Fact]
    public void Le_cadrage_recoche_survit_aussi()
    {
        ReglagesIdentite.Save(_dossier, new ReglagesIdentite(CadrageAutomatique: false));
        ReglagesIdentite.Save(_dossier, new ReglagesIdentite(CadrageAutomatique: true));

        Assert.True(ReglagesIdentite.Load(_dossier).CadrageAutomatique);
    }

    /// <summary>
    /// Les trois réglages coexistent : en écrire un ne doit pas effacer les autres. C'est
    /// exactement ce que fait `with` côté écran, et il faut que le fichier suive.
    /// </summary>
    [Fact]
    public void Les_trois_reglages_coexistent()
    {
        ReglagesIdentite.Save(_dossier,
            new ReglagesIdentite(DossierPhotos: @"D:\photos", ModeSombre: true, CadrageAutomatique: false));

        var relu = ReglagesIdentite.Load(_dossier);

        Assert.Equal(@"D:\photos", relu.DossierPhotos);
        Assert.True(relu.ModeSombre);
        Assert.False(relu.CadrageAutomatique);
    }

    /// <summary>
    /// ⚠ Un fichier écrit AVANT que ce réglage existe — le cas de TOUS les postes en
    /// service — n'a pas le champ. Il doit se relire en « cadrage actif », c'est-à-dire
    /// comme avant, et non en faux.
    /// </summary>
    [Fact]
    public void Un_fichier_d_avant_le_reglage_garde_le_cadrage_actif()
    {
        File.WriteAllText(Path.Combine(_dossier, ReglagesIdentite.FileName),
            """
            {
              "DossierPhotos": "",
              "ModeSombre": false,
              "DossierFixeUtilisable": false
            }
            """);

        Assert.True(ReglagesIdentite.Load(_dossier).CadrageAutomatique);
    }
}
