using Studio.App.Infrastructure;

namespace Studio.Tests;

/// <summary>
/// La retouche dans un logiciel extérieur — Photoshop, GIMP — et le nom de la copie qu'on
/// lui donne.
///
/// Ce qui se vérifie ici est ce qui ne demande ni Photoshop ni disque : le NOM de la copie.
/// C'est aussi ce qui casse en boutique — deux photos homonymes venues de deux cartes, un
/// aller-retour recommencé sur la même photo — et ce qu'aucun essai à la main ne rattrape,
/// parce que l'écrasement d'une retouche par une autre ne se voit qu'au tirage.
///
/// La recherche du logiciel, elle, lit le registre : elle ne se simule pas, et ce qu'elle
/// fait est trivial. Voir <see cref="RetoucheExterne"/>.
/// </summary>
public class RetoucheExterneTests
{
    /// <summary>Un dossier vide : la copie garde le nom exact de l'original.</summary>
    [Fact]
    public void La_copie_garde_le_nom_de_l_original() =>
        Assert.Equal("DSC_0042.jpg",
            RetoucheExterne.NomDeLaCopie(@"E:\DCIM\100CANON\DSC_0042.jpg", _ => false));

    /// <summary>
    /// L'EXTENSION est gardée telle quelle, et c'est ce qui rend le retour automatique
    /// possible : le logiciel de retouche enregistre dans le même format d'un Ctrl+S, sur
    /// le fichier que Studio surveille. Une copie en .psd ne reviendrait jamais.
    /// </summary>
    [Theory]
    [InlineData(@"C:\photos\mariage.JPG", "mariage.JPG")]
    [InlineData(@"C:\photos\portrait.tif", "portrait.tif")]
    [InlineData(@"C:\photos\scan.png", "scan.png")]
    public void L_extension_ne_change_pas(string original, string attendu) =>
        Assert.Equal(attendu, RetoucheExterne.NomDeLaCopie(original, _ => false));

    /// <summary>
    /// <b>Le cas qui compte.</b> Deux cartes clients portent toutes deux un
    /// « DSC_0001.jpg » : sans numéro, la seconde retouche écraserait la première, et
    /// c'est le premier client qui repartirait avec la photo du second.
    /// </summary>
    [Fact]
    public void Un_nom_deja_pris_est_numerote()
    {
        var pris = new HashSet<string> { "DSC_0001.jpg" };

        Assert.Equal("DSC_0001 (2).jpg",
            RetoucheExterne.NomDeLaCopie(@"E:\DSC_0001.jpg", pris.Contains));
    }

    /// <summary>
    /// Et le numéro monte tant qu'il le faut : l'opérateur qui repart deux fois dans
    /// Photoshop sur la même photo ne doit pas perdre sa première passe.
    /// </summary>
    [Fact]
    public void Le_numero_monte_jusqu_a_trouver_libre()
    {
        var pris = new HashSet<string> { "photo.jpg", "photo (2).jpg", "photo (3).jpg" };

        Assert.Equal("photo (4).jpg",
            RetoucheExterne.NomDeLaCopie(@"C:\photo.jpg", pris.Contains));
    }

    /// <summary>
    /// Les accents RESTENT : le nom sert à reconnaître la photo à l'écran des tirages et sur
    /// la planche index, et « Sance Dupont » n'y ressemblerait plus.
    /// </summary>
    [Fact]
    public void Les_accents_du_nom_sont_gardes() =>
        Assert.Equal("Séance été.jpg",
            RetoucheExterne.NomDeLaCopie(@"C:\photos\Séance été.jpg", _ => false));

    /// <summary>
    /// Un dossier saturé d'homonymes ne doit pas bloquer le comptoir : on rend un nom
    /// unique plutôt que de tourner en rond ou de lever.
    /// </summary>
    [Fact]
    public void Dix_mille_homonymes_ne_bloquent_pas()
    {
        var nom = RetoucheExterne.NomDeLaCopie(@"C:\photo.jpg", _ => true);

        Assert.StartsWith("photo (", nom);
        Assert.EndsWith(".jpg", nom);
    }
}
