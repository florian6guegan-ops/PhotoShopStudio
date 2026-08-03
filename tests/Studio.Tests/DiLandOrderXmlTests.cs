using Studio.Store.DiLand;

namespace Studio.Tests;

/// <summary>
/// Lecture d'une commande de borne dans son <c>Order.xml</c>, sans base et sans DiLand.
///
/// Le fichier d'essai reprend la structure et les valeurs d'une vraie commande de la
/// boutique — <c>20260803-1648-ommcdsbz.COM</c>, 8x10, image 1536 × 2048 recadrée à
/// <c>0/44/1536/1958</c> — parce que c'est là que les pièges se trouvent : l'unité des
/// recadrages, le nom du produit caché dans <c>Sys_Product_Alias</c>, et la date écrite à
/// l'américaine.
/// </summary>
public class DiLandOrderXmlTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "DiLandXml-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    private string CreerCommande(string nomDuDossier, string xml)
    {
        var dossier = Path.Combine(_root, nomDuDossier);
        Directory.CreateDirectory(Path.Combine(dossier, "F"));
        File.WriteAllBytes(Path.Combine(dossier, "F", "photo.jpg"), [0xFF, 0xD8, 0xFF, 0xE0]);
        File.WriteAllText(Path.Combine(dossier, DiLandOrderXml.FileName), xml);
        return dossier;
    }

    /// <summary>Une commande à l'image des vraies, attributs superflus retirés.</summary>
    private const string XmlReel = """
        <?xml version="1.0" encoding="utf-8"?>
        <Order Sys_GlobalUniqueId="1996178c-e181-428e-b93c-7471a1d19165"
               Number="6878" DailyNumber="03-001" Date="08/03/2026 16:48:12"
               EndUserName="YU" Price="22.55">
          <Lines>
            <OrderLine Sys_Product_Alias="8x10" Price="22.55" Quantity="1">
              <Images>
                <OrderImageOrderLineImage FileName="photo.jpg"
                    OriginalFileName="IMG_0143.jpeg" Quantity="2" ApplyCrop="True"
                    CropX="0" CropY="44" CropWidth="1536" CropHeight="1958"
                    Angle="90" FineRotationAngle="-2" Width="1536" Height="2048"/>
              </Images>
            </OrderLine>
          </Lines>
        </Order>
        """;

    [Fact]
    public void Une_commande_se_lit_entierement_sans_la_base()
    {
        var contenu = DiLandOrderXml.Lire(CreerCommande("20260803-1648-ommcdsbz.COM", XmlReel));

        Assert.NotNull(contenu);
        Assert.Equal(6878, contenu!.Order.Number);
        Assert.Equal("03-001", contenu.Order.DailyNumber);
        Assert.Equal("YU", contenu.Order.EndUserName);
        Assert.True(contenu.Order.IsFromKiosk);
        Assert.True(contenu.Order.IsComplete);
    }

    /// <summary>
    /// Le nom du produit vit dans <c>Sys_Product_Alias</c> : le XML ne porte pas la table
    /// des produits, seulement leur identifiant. Sans cet attribut, aucune ligne ne
    /// pourrait être rapprochée du catalogue Studio.
    /// </summary>
    [Fact]
    public void Le_produit_vient_de_l_alias()
    {
        var contenu = DiLandOrderXml.Lire(CreerCommande("20260803-1648-x.COM", XmlReel))!;

        Assert.Equal("8x10", Assert.Single(contenu.Lines).ProductName);
    }

    /// <summary>Le recadrage est en pixels dans le fichier, en fractions à la sortie.</summary>
    [Fact]
    public void Le_recadrage_est_ramene_en_fractions()
    {
        var contenu = DiLandOrderXml.Lire(CreerCommande("20260803-1648-x.COM", XmlReel))!;
        var photo = Assert.Single(Assert.Single(contenu.Lines).Photos);

        Assert.True(photo.ApplyCrop);
        Assert.Equal(44.0 / 2048, photo.CropY, 4);
        Assert.Equal(1.0, photo.CropWidth, 4);
        Assert.Equal(1958.0 / 2048, photo.CropHeight, 4);
    }

    [Fact]
    public void Rotation_redressement_et_quantite_sont_repris()
    {
        var contenu = DiLandOrderXml.Lire(CreerCommande("20260803-1648-x.COM", XmlReel))!;
        var photo = Assert.Single(Assert.Single(contenu.Lines).Photos);

        Assert.Equal(90, photo.Angle, 3);
        Assert.Equal(-2, photo.FineRotationDegrees, 3);
        Assert.Equal(2, photo.Quantity);
        Assert.Equal("IMG_0143.jpeg", photo.DisplayName);
    }

    /// <summary>
    /// La date vient du NOM DU DOSSIER, sans ambiguïté, et non de l'attribut écrit à
    /// l'américaine : <c>08/03/2026</c> lu à la française donnerait le 8 mars au lieu du
    /// 3 août — cinq mois d'écart, et une commande classée n'importe où dans la liste.
    /// </summary>
    [Fact]
    public void La_date_est_celle_du_dossier_et_non_du_format_americain()
    {
        var contenu = DiLandOrderXml.Lire(CreerCommande("20260803-1648-ommcdsbz.COM", XmlReel))!;

        Assert.Equal(new DateTime(2026, 8, 3, 16, 48, 0), contenu.Order.Date);
    }

    /// <summary>
    /// La clé du journal doit être la même à chaque lecture, et survivre au redémarrage :
    /// <c>string.GetHashCode()</c> est randomisé par processus, et une clé qui change
    /// ferait resurgir toutes les commandes déjà traitées au démarrage suivant.
    /// </summary>
    [Fact]
    public void La_cle_de_journal_est_deterministe()
    {
        var a = DiLandOrderXml.CleDe("1996178c-e181-428e-b93c-7471a1d19165", "peu importe");
        var b = DiLandOrderXml.CleDe("1996178c-e181-428e-b93c-7471a1d19165", "autre chose");

        Assert.Equal(a, b);
        Assert.True(a > 0, "la clé doit rester positive pour être lisible dans le journal");
        Assert.NotEqual(a, DiLandOrderXml.CleDe("un-autre-identifiant", ""));
    }

    /// <summary>
    /// Un paquet arrivé à moitié ne doit pas faire tomber la lecture des autres : l'écran
    /// balaie un dossier entier, et une commande abîmée n'a pas à en cacher dix bonnes.
    /// </summary>
    [Fact]
    public void Un_fichier_abime_est_ignore_sans_lever()
    {
        var dossier = CreerCommande("20260803-1648-abime.COM", "<Order Number=\"1\"");

        Assert.Null(DiLandOrderXml.Lire(dossier));
    }

    [Fact]
    public void Un_dossier_sans_fichier_ne_porte_rien()
    {
        var dossier = Path.Combine(_root, "vide.COM");
        Directory.CreateDirectory(dossier);

        Assert.False(DiLandOrderXml.Porte(dossier));
        Assert.Null(DiLandOrderXml.Lire(dossier));
    }
}
