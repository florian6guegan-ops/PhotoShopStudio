using Studio.App.Infrastructure;
using Studio.Core.Domain;

namespace Studio.Tests;

/// <summary>
/// Les dossiers épinglés dans les boîtes de fichiers de Windows et sur l'écran « d'où
/// viennent les photos ? ».
/// </summary>
public class DossiersFavorisTests : IDisposable
{
    private readonly FavorisSettings _avant = DossiersFavoris.Reglage;
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "StudioFavoris-" + Guid.NewGuid().ToString("N"));

    public DossiersFavorisTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        DossiersFavoris.Reglage = _avant;
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Un_poste_sans_reglage_recoit_les_trois_favoris_par_defaut()
    {
        var vide = new FavorisSettings();

        Assert.Equal(3, vide.Effectifs.Count);
        Assert.Collection(vide.Effectifs.Select(f => f.Cle),
            c => Assert.Equal(DossierFavori.Bureau, c),
            c => Assert.Equal(DossierFavori.Telechargements, c),
            c => Assert.Equal(DossierFavori.WeTransfer, c));
    }

    /// <summary>
    /// Les chemins par défaut sont VIDES : ils se résolvent sur le poste. Les figer dans le
    /// fichier de configuration ferait un réglage qui ne survit pas au premier changement de
    /// session ou de poste.
    /// </summary>
    [Fact]
    public void Les_favoris_par_defaut_ne_figent_aucun_chemin()
    {
        Assert.All(FavorisSettings.ParDefaut(), f => Assert.Equal("", f.Chemin));
    }

    [Fact]
    public void Un_reglage_renseigne_remplace_les_favoris_par_defaut()
    {
        var reglage = new FavorisSettings
        {
            Dossiers = [new DossierFavori("Photos du labo", _dossier)],
        };

        Assert.Single(reglage.Effectifs);
        Assert.Equal("Photos du labo", reglage.Effectifs[0].Libelle);
    }

    [Fact]
    public void Un_favori_decoche_n_est_pas_propose()
    {
        DossiersFavoris.Reglage = new FavorisSettings
        {
            Dossiers =
            [
                new DossierFavori("Gardé", _dossier),
                new DossierFavori("Mis de côté", _dossier + "-bis", Actif: false),
            ],
        };

        Assert.Single(DossiersFavoris.Actifs());
        Assert.Equal("Gardé", DossiersFavoris.Actifs()[0].Libelle);
    }

    /// <summary>
    /// Un favori qui ne mène nulle part ferait une entrée morte dans le volet de Windows —
    /// et Windows refuse d'ailleurs de l'y mettre.
    /// </summary>
    [Fact]
    public void Un_dossier_absent_n_est_jamais_propose()
    {
        DossiersFavoris.Reglage = new FavorisSettings
        {
            Dossiers = [new DossierFavori("Disparu", Path.Combine(_dossier, "jamais-cree"))],
        };

        Assert.Empty(DossiersFavoris.Actifs());
    }

    [Fact]
    public void Deux_favoris_sur_le_meme_dossier_n_en_font_qu_un()
    {
        DossiersFavoris.Reglage = new FavorisSettings
        {
            Dossiers =
            [
                new DossierFavori("WeTransfer", _dossier),
                new DossierFavori("Le même", _dossier + Path.DirectorySeparatorChar),
            ],
        };

        Assert.Single(DossiersFavoris.Actifs());
    }

    /// <summary>
    /// Le Bureau et les Téléchargements se trouvent tout seuls : ce sont les deux favoris
    /// qui doivent marcher sur un poste où personne n'a rien réglé.
    /// </summary>
    [Fact]
    public void Le_bureau_et_les_telechargements_se_resolvent_sans_reglage()
    {
        Assert.NotNull(DossiersUtilisateur.Resoudre(
            new DossierFavori("Bureau", Cle: DossierFavori.Bureau)));
        Assert.NotNull(DossiersUtilisateur.Resoudre(
            new DossierFavori("Téléchargements", Cle: DossierFavori.Telechargements)));
    }

    [Fact]
    public void Un_chemin_ecrit_a_la_main_l_emporte_sur_la_cle()
    {
        var resolu = DossiersUtilisateur.Resoudre(
            new DossierFavori("Bureau", _dossier, DossierFavori.Bureau));

        Assert.Equal(_dossier, resolu);
    }

    [Fact]
    public void Les_raccourcis_portent_un_picto_et_le_libelle()
    {
        DossiersFavoris.Reglage = new FavorisSettings
        {
            Dossiers = [new DossierFavori("WeTransfer", _dossier)],
        };

        var raccourci = Assert.Single(DossiersFavoris.Raccourcis());
        Assert.EndsWith("WeTransfer", raccourci.Libelle, StringComparison.Ordinal);
        Assert.Equal(_dossier, raccourci.Chemin);
    }
}
