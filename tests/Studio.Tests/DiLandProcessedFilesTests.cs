using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// La marque « _p » que DiLand ajoute aux fichiers d'une commande qu'il a traitée.
///
/// <c>Order.xml</c> devient <c>Order.xml_p</c>, et chaque photo <c>F\xxx.jpg</c> devient
/// <c>F\xxx.jpg_p</c> — mais la base garde le nom d'origine. Sans rapprochement, toute
/// commande déjà passée chez DiLand devient inouvrable : le 01/08/2026, huit des neuf
/// commandes en attente répondaient « aucune photo n'a pu être récupérée », la seule qui
/// s'ouvrait étant celle que DiLand n'avait pas encore touchée.
/// </summary>
public class DiLandProcessedFilesTests : IDisposable
{
    private readonly string _racine = Path.Combine(
        Path.GetTempPath(), "diland-p-" + Guid.NewGuid().ToString("N"));

    public DiLandProcessedFilesTests() => Directory.CreateDirectory(_racine);

    public void Dispose()
    {
        try { Directory.Delete(_racine, recursive: true); } catch (IOException) { }
    }

    private DiLandRepository Depot() =>
        new(_racine, Path.Combine(_racine, "travail"));

    /// <summary>Crée le dossier d'une commande et y dépose un fichier photo.</summary>
    private string Deposer(string dossierCommande, string nomFichier)
    {
        var f = Path.Combine(_racine, "Orders", dossierCommande, "F");
        Directory.CreateDirectory(f);
        var chemin = Path.Combine(f, nomFichier);
        File.WriteAllBytes(chemin, [0xFF, 0xD8]);
        return chemin;
    }

    private static DiLandOrder Commande(string dossier) =>
        new(Oid: 1, Number: 42, DailyNumber: "01-001", Date: DateTime.Now,
            DirectoryName: dossier, EndUserName: "", PhotoCount: 1);

    private static DiLandOrderPhoto Photo(string nom) =>
        new(FileName: nom, OriginalFileName: nom, Quantity: 1,
            ApplyCrop: false, CropX: 0, CropY: 0, CropWidth: 1, CropHeight: 1, Angle: 0);

    [Fact]
    public void Une_photo_non_marquee_se_trouve_sous_son_nom()
    {
        var attendu = Deposer("20260801-1006-abcd.COM", "photo.jpg");

        var trouve = Depot().PhotoPath(Commande("20260801-1006-abcd.COM"), Photo("photo.jpg"));

        Assert.Equal(attendu, trouve);
        Assert.True(File.Exists(trouve));
    }

    /// <summary>Le cas qui cassait tout : la base dit « photo.jpg », le disque a « photo.jpg_p ».</summary>
    [Fact]
    public void Une_photo_marquee_par_DiLand_se_retrouve_quand_meme()
    {
        var attendu = Deposer("20260731-1509-efgh.COM", "photo.jpg_p");

        var trouve = Depot().PhotoPath(Commande("20260731-1509-efgh.COM"), Photo("photo.jpg"));

        Assert.Equal(attendu, trouve);
        Assert.True(File.Exists(trouve));
    }

    /// <summary>Le nom de la base l'emporte quand les deux existent : c'est l'original.</summary>
    [Fact]
    public void Le_fichier_non_marque_est_prefere_s_il_existe()
    {
        var original = Deposer("20260801-1200-ijkl.COM", "photo.jpg");
        Deposer("20260801-1200-ijkl.COM", "photo.jpg_p");

        var trouve = Depot().PhotoPath(Commande("20260801-1200-ijkl.COM"), Photo("photo.jpg"));

        Assert.Equal(original, trouve);
    }

    /// <summary>Aucun des deux : on rend le chemin nu, l'appelant constate le manque.</summary>
    [Fact]
    public void Une_photo_vraiment_absente_reste_absente()
    {
        Directory.CreateDirectory(Path.Combine(_racine, "Orders", "20260801-1300-mnop.COM", "F"));

        var trouve = Depot().PhotoPath(Commande("20260801-1300-mnop.COM"), Photo("photo.jpg"));

        Assert.False(File.Exists(trouve));
        Assert.EndsWith("photo.jpg", trouve);
    }

    [Theory]
    [InlineData("photo.jpg_p", "photo.jpg")]
    [InlineData("photo.jpg", "photo.jpg")]
    [InlineData("Order.xml_p", "Order.xml")]
    public void Le_nom_de_recopie_perd_la_marque(string surLeDisque, string attendu) =>
        Assert.Equal(attendu, DiLandRepository.CleanFileName(surLeDisque));

    [Theory]
    [InlineData("photo.jpg_p", true)]
    [InlineData("photo.jpg", false)]
    public void La_marque_se_reconnait(string nom, bool marque) =>
        Assert.Equal(marque, DiLandRepository.IsProcessedName(nom));

    // — le brouillage du contenu —

    /// <summary>Une photo factice : en-tête JPEG, puis du remplissage reconnaissable.</summary>
    private static byte[] PhotoOriginale(int taille = 4000)
    {
        var octets = new byte[taille];
        octets[0] = 0xFF; octets[1] = 0xD8; octets[2] = 0xFF; octets[3] = 0xE0;
        for (var i = 4; i < taille; i++) octets[i] = (byte)(i % 251);
        octets[^2] = 0xFF; octets[^1] = 0xD9;
        return octets;
    }

    /// <summary>Ce que DiLand écrit sur le disque : les 1024 premiers octets au XOR 0x07.</summary>
    private static byte[] Brouiller(byte[] clair)
    {
        var brouille = (byte[])clair.Clone();
        for (var i = 0; i < Math.Min(1024, brouille.Length); i++) brouille[i] ^= 0x07;
        return brouille;
    }

    /// <summary>
    /// Le cas complet : nom marqué ET contenu brouillé. Sans la remise en clair, la photo
    /// s'ouvrait comme fichier mais aucun décodeur n'en voulait.
    /// </summary>
    [Fact]
    public void Une_photo_brouillee_est_remise_en_clair_a_la_recopie()
    {
        var clair = PhotoOriginale();
        var f = Path.Combine(_racine, "Orders", "20260731-1620-abcd.COM", "F");
        Directory.CreateDirectory(f);
        File.WriteAllBytes(Path.Combine(f, "photo.jpg_p"), Brouiller(clair));

        var cible = Path.Combine(_racine, "sortie.jpg");
        Depot().CopyPhotoTo(Commande("20260731-1620-abcd.COM"), Photo("photo.jpg"), cible);

        Assert.Equal(clair, File.ReadAllBytes(cible));
    }

    /// <summary>Un fichier non marqué est recopié tel quel : pas de brouillage à défaire.</summary>
    [Fact]
    public void Une_photo_normale_est_recopiee_telle_quelle()
    {
        var clair = PhotoOriginale();
        var f = Path.Combine(_racine, "Orders", "20260801-1006-efgh.COM", "F");
        Directory.CreateDirectory(f);
        File.WriteAllBytes(Path.Combine(f, "photo.jpg"), clair);

        var cible = Path.Combine(_racine, "sortie2.jpg");
        Depot().CopyPhotoTo(Commande("20260801-1006-efgh.COM"), Photo("photo.jpg"), cible);

        Assert.Equal(clair, File.ReadAllBytes(cible));
    }

    /// <summary>
    /// Une copie abîmée par une version antérieure doit être refaite : sinon la commande
    /// resterait inouvrable alors que le défaut est corrigé.
    /// </summary>
    [Fact]
    public void Une_copie_abimee_est_refaite()
    {
        var clair = PhotoOriginale();
        var f = Path.Combine(_racine, "Orders", "20260731-1509-ijkl.COM", "F");
        Directory.CreateDirectory(f);
        File.WriteAllBytes(Path.Combine(f, "photo.jpg_p"), Brouiller(clair));

        var cible = Path.Combine(_racine, "sortie3.jpg");
        File.WriteAllBytes(cible, Brouiller(clair));   // la copie ratée d'avant

        Depot().CopyPhotoTo(Commande("20260731-1509-ijkl.COM"), Photo("photo.jpg"), cible);

        Assert.Equal(clair, File.ReadAllBytes(cible));
    }

    /// <summary>Une photo plus courte que le préfixe brouillé ne doit pas déborder.</summary>
    [Fact]
    public void Une_photo_plus_courte_que_le_prefixe_se_decode_entierement()
    {
        var clair = PhotoOriginale(300);
        var f = Path.Combine(_racine, "Orders", "20260731-1700-mnop.COM", "F");
        Directory.CreateDirectory(f);
        File.WriteAllBytes(Path.Combine(f, "petite.jpg_p"), Brouiller(clair));

        var cible = Path.Combine(_racine, "sortie4.jpg");
        Depot().CopyPhotoTo(Commande("20260731-1700-mnop.COM"), Photo("petite.jpg"), cible);

        Assert.Equal(clair, File.ReadAllBytes(cible));
    }
}
