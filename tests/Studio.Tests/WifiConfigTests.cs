using Studio.Web;

namespace Studio.Tests;

/// <summary>
/// Le réseau saisi dans <c>config/wifi.json</c>.
///
/// Ce fichier existe parce que le poste de l'atelier n'a pas de carte sans fil : la lecture
/// automatique du profil Windows ne rend rien et ne rendra jamais rien. C'est donc lui, et
/// non netsh, qui fait vivre le code QR en boutique.
/// </summary>
public class WifiConfigTests
{
    [Fact]
    public void Un_fichier_vide_ne_donne_aucun_reseau()
    {
        Assert.Null(new WifiConfig().Network());
    }

    [Fact]
    public void Un_ssid_fait_de_blancs_ne_compte_pas()
    {
        Assert.Null(new WifiConfig { Ssid = "   " }.Network());
    }

    [Fact]
    public void Le_reseau_saisi_est_rendu_tel_quel()
    {
        var reseau = new WifiConfig
        {
            Ssid = "  PHOTOCONCEPT  ",
            Password = "bonjour2026",
        }.Network();

        Assert.NotNull(reseau);
        Assert.Equal("PHOTOCONCEPT", reseau!.Ssid); // les blancs de saisie sont retirés
        Assert.Equal("bonjour2026", reseau.Password);
        Assert.Equal("WPA", reseau.Security);
    }

    /// <summary>
    /// « WPA2 » et « WPA3 » se ramènent à « WPA », seule valeur que la spécification du code
    /// connaisse. Une faute de frappe aussi : un réseau protégé annoncé « ouvert » ferait
    /// échouer la connexion sans rien expliquer.
    /// </summary>
    [Theory]
    [InlineData("WPA")]
    [InlineData("WPA2")]
    [InlineData("wpa3")]
    [InlineData("nimportequoi")]
    public void Un_reseau_avec_cle_est_toujours_annonce_protege(string saisi)
    {
        var reseau = new WifiConfig { Ssid = "Studio", Password = "cle", Security = saisi }.Network();

        Assert.Equal("WPA", reseau!.Security);
    }

    [Fact]
    public void Un_reseau_sans_cle_est_annonce_ouvert()
    {
        var reseau = new WifiConfig { Ssid = "Invites", Password = "" }.Network();

        Assert.Equal("nopass", reseau!.Security);
        Assert.Equal("WIFI:T:nopass;S:Invites;;", WifiQr.Payload(reseau));
    }

    [Fact]
    public void Le_WEP_demande_explicitement_est_respecte()
    {
        var reseau = new WifiConfig { Ssid = "Vieux", Password = "cle", Security = "WEP" }.Network();

        Assert.Equal("WEP", reseau!.Security);
    }

    [Fact]
    public void Un_reseau_ouvert_demande_explicitement_le_reste()
    {
        var reseau = new WifiConfig { Ssid = "Libre", Password = "inutile", Security = "nopass" }.Network();

        Assert.Equal("nopass", reseau!.Security);
    }

    [Fact]
    public void Le_ssid_masque_est_transmis()
    {
        var reseau = new WifiConfig { Ssid = "Cache", Password = "cle", Hidden = true }.Network();

        Assert.True(reseau!.Hidden);
        Assert.Contains("H:true;", WifiQr.Payload(reseau));
    }
}
