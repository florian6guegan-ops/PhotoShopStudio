using Studio.Web;

namespace Studio.Tests;

/// <summary>
/// La chaîne du code QR WiFi. Elle ne se vérifie pas à l'œil : un caractère mal échappé
/// donne un code qui se scanne, se lit, et échoue à la connexion sans rien dire.
/// </summary>
public class WifiQrTests
{
    [Fact]
    public void Un_reseau_ordinaire_donne_la_chaine_attendue()
    {
        var payload = WifiQr.Payload(new WifiNetwork("StudioPhoto", "monmotdepasse", "WPA"));

        Assert.Equal("WIFI:T:WPA;S:StudioPhoto;P:monmotdepasse;;", payload);
    }

    /// <summary>
    /// Un point-virgule dans la clé couperait la chaîne en deux : le téléphone lirait un mot
    /// de passe tronqué et refuserait la connexion, sans que rien ne le signale.
    /// </summary>
    [Theory]
    [InlineData("a;b", "a\\;b")]
    [InlineData("a:b", "a\\:b")]
    [InlineData("a,b", "a\\,b")]
    [InlineData("a\\b", "a\\\\b")]
    [InlineData("a\"b", "a\\\"b")]
    public void Les_caracteres_de_structure_sont_echappes(string clair, string echappe)
    {
        var payload = WifiQr.Payload(new WifiNetwork("Studio", clair, "WPA"));

        Assert.Contains($"P:{echappe};", payload);
    }

    [Fact]
    public void Le_ssid_est_echappe_lui_aussi()
    {
        var payload = WifiQr.Payload(new WifiNetwork("Studio;Photo", "x", "WPA"));

        Assert.Contains("S:Studio\\;Photo;", payload);
    }

    /// <summary>Réseau ouvert : pas de champ mot de passe du tout, et non un champ vide.</summary>
    [Fact]
    public void Un_reseau_ouvert_n_annonce_aucune_cle()
    {
        var payload = WifiQr.Payload(new WifiNetwork("Invites", "", "nopass"));

        Assert.Equal("WIFI:T:nopass;S:Invites;;", payload);
        Assert.DoesNotContain("P:", payload);
    }

    [Fact]
    public void Un_ssid_masque_est_signale()
    {
        var payload = WifiQr.Payload(new WifiNetwork("Cache", "cle", "WPA", Hidden: true));

        Assert.Contains("H:true;", payload);
    }

    [Fact]
    public void Un_ssid_visible_ne_porte_pas_le_champ_masque()
    {
        Assert.DoesNotContain("H:", WifiQr.Payload(new WifiNetwork("Studio", "cle", "WPA")));
    }

    /// <summary>
    /// WPA, WPA2 et WPA3 se déclarent tous « WPA » : les téléphones ne distinguent pas,
    /// ils négocient. C'est ce que dit la spécification du code.
    /// </summary>
    [Theory]
    [InlineData("WPA2PSK", "WPA")]
    [InlineData("WPAPSK", "WPA")]
    [InlineData("WPA3SAE", "WPA")]
    [InlineData("open", "nopass")]
    [InlineData("shared", "WEP")]
    public void Le_type_de_securite_suit_le_profil_windows(string authentification, string attendu)
    {
        Assert.Equal(attendu, WifiQr.Securite(authentification, cle: ""));
    }

    [Fact]
    public void Un_profil_ouvert_mais_avec_cle_reste_protege()
    {
        // authentification inconnue et clé présente : mieux vaut annoncer WPA que « nopass »,
        // qui ferait tenter une connexion sans mot de passe
        Assert.Equal("WPA", WifiQr.Securite("inconnu", cle: "quelquechose"));
    }

    [Fact]
    public void Le_png_du_code_est_produit()
    {
        var png = WifiQr.Png(new WifiNetwork("StudioPhoto", "motdepasse", "WPA"));

        Assert.NotEmpty(png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }
}
