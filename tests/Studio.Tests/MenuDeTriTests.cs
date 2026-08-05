using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// Le classement des photos. Le bouton « trier » BASCULAIT entre deux ordres sans dire
/// lequel s'appliquait ; il en propose maintenant cinq, et c'est ce code qui les rend.
/// </summary>
public class MenuDeTriTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "StudioTri-" + Guid.NewGuid().ToString("N"));

    /// <summary>Une photo : son chemin, son poids et sa date.</summary>
    private sealed record Photo(string Chemin, string Nom);

    public MenuDeTriTests() => Directory.CreateDirectory(_dossier);

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    /// <summary>Fabrique un fichier d'un poids et d'une date donnés.</summary>
    private Photo Fichier(string nom, int octets, DateTime date)
    {
        var chemin = Path.Combine(_dossier, nom);
        File.WriteAllBytes(chemin, new byte[octets]);
        File.SetLastWriteTime(chemin, date);
        File.SetCreationTime(chemin, date);
        return new Photo(chemin, nom);
    }

    private List<Photo> TroisPhotos() =>
    [
        Fichier("bravo.jpg", 300, new DateTime(2026, 8, 2, 10, 0, 0)),
        Fichier("alpha.jpg", 100, new DateTime(2026, 8, 1, 10, 0, 0)),
        Fichier("charlie.jpg", 200, new DateTime(2026, 8, 3, 10, 0, 0)),
    ];

    private static List<string> Noms(IEnumerable<Photo> photos) => photos.Select(p => p.Nom).ToList();

    private static List<Photo> Trier(List<Photo> photos, CritereDeTri critere) =>
        MenuDeTri.Appliquer(photos, critere, p => p.Chemin, p => p.Nom);

    [Fact]
    public void Nom_croissant_puis_decroissant()
    {
        var photos = TroisPhotos();

        Assert.Equal(["alpha.jpg", "bravo.jpg", "charlie.jpg"],
            Noms(Trier(photos, CritereDeTri.NomCroissant)));
        Assert.Equal(["charlie.jpg", "bravo.jpg", "alpha.jpg"],
            Noms(Trier(photos, CritereDeTri.NomDecroissant)));
    }

    [Fact]
    public void Date_la_plus_recente_d_abord_puis_l_inverse()
    {
        var photos = TroisPhotos();

        Assert.Equal(["charlie.jpg", "bravo.jpg", "alpha.jpg"],
            Noms(Trier(photos, CritereDeTri.DateRecente)));
        Assert.Equal(["alpha.jpg", "bravo.jpg", "charlie.jpg"],
            Noms(Trier(photos, CritereDeTri.DateAncienne)));
    }

    [Fact]
    public void Poids_du_fichier_la_plus_lourde_d_abord()
    {
        var photos = TroisPhotos();

        Assert.Equal(["bravo.jpg", "charlie.jpg", "alpha.jpg"],
            Noms(Trier(photos, CritereDeTri.TailleDecroissante)));
    }

    /// <summary>
    /// Le bouton « Dupliquer » met la MÊME photo deux fois dans la planche, en deux
    /// formats. Un dictionnaire direct lèverait sur la clé en double.
    /// </summary>
    [Fact]
    public void Une_photo_presente_deux_fois_ne_fait_pas_echouer_le_tri()
    {
        var photos = TroisPhotos();
        photos.Add(photos[0]);

        foreach (var critere in Enum.GetValues<CritereDeTri>())
            Assert.Equal(4, Trier(photos, critere).Count);
    }

    [Fact]
    public void Un_fichier_disparu_part_en_fin_de_liste_sans_lever()
    {
        var photos = TroisPhotos();
        photos.Add(new Photo(Path.Combine(_dossier, "jamais-cree.jpg"), "zzz-disparue.jpg"));

        var parPoids = Trier(photos, CritereDeTri.TailleDecroissante);
        Assert.Equal("zzz-disparue.jpg", parPoids[^1].Nom);

        var parDate = Trier(photos, CritereDeTri.DateRecente);
        Assert.Equal("zzz-disparue.jpg", parDate[^1].Nom);
    }

    [Fact]
    public void Chaque_classement_porte_un_libelle_lisible()
    {
        foreach (var critere in Enum.GetValues<CritereDeTri>())
            Assert.False(string.IsNullOrWhiteSpace(MenuDeTri.Libelle(critere)));
    }
}
