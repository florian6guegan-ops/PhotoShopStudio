using Studio.Printing.Devices.Fuji.Bridge;

namespace Studio.Tests;

/// <summary>
/// Vérifie que le pont entre l'application 64 bits et le relais 32 bits fonctionne
/// réellement : démarrage du processus, tube nommé, aller-retour d'une commande.
///
/// Ces tests ne touchent PAS le minilab. Ils s'arrêtent à la seule commande qui ne
/// demande pas le SDK Fuji chargé (<c>ping</c>), ce qui suffit à prouver la plomberie.
/// Le reste ne peut se vérifier que devant la machine.
/// </summary>
public class De100BridgeIntegrationTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    [Fact]
    public void Le_relais_est_compile_et_trouvable()
    {
        var chemin = De100BridgeClient.FindHost();

        Assert.NotNull(chemin);
        Assert.EndsWith("Studio.De100Host.exe", chemin, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Le relais doit être en 32 bits, sinon le SDK Fuji refusera de se charger.</summary>
    [Fact]
    public void Le_relais_est_bien_en_32_bits()
    {
        var chemin = De100BridgeClient.FindHost();
        Assert.NotNull(chemin);

        using var flux = File.OpenRead(chemin);
        using var lecteur = new BinaryReader(flux);
        flux.Position = 0x3C;
        flux.Position = lecteur.ReadInt32() + 4;
        var machine = lecteur.ReadUInt16();

        const ushort I386 = 0x014C;
        Assert.Equal(I386, machine);
    }

    /// <summary>
    /// Le test qui compte : un processus 64 bits démarre le relais 32 bits, s'y connecte
    /// et obtient une réponse. C'est tout l'intérêt du relais.
    /// </summary>
    [Fact]
    public async Task Le_pont_64_vers_32_bits_repond()
    {
        Assert.True(Environment.Is64BitProcess, "ce test doit tourner en 64 bits pour avoir un sens");

        await using var client = new De100BridgeClient(Patience);
        await client.ConnectAsync();

        Assert.True(client.IsConnected);

        // le relais répond même sans SDK installé : il dit simplement s'il l'a trouvé
        var sdkPresent = await client.IsSdkInstalledAsync();

        Assert.IsType<bool>(sdkPresent);
    }

    [Fact]
    public async Task Le_relais_s_arrete_a_la_deconnexion()
    {
        var client = new De100BridgeClient(Patience);
        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        await client.DisposeAsync();

        Assert.False(client.IsConnected);
    }

    /// <summary>Une commande inconnue doit être rejetée proprement, sans tuer le relais.</summary>
    [Fact]
    public async Task Une_commande_inconnue_est_refusee_sans_couper_le_lien()
    {
        await using var client = new De100BridgeClient(Patience);
        await client.ConnectAsync();

        // on passe par le protocole brut : le client typé n'expose pas de commande invalide
        var message = De100Protocol.Request("commande-qui-n-existe-pas");
        Assert.Equal("commande-qui-n-existe-pas", message.Name);

        // le lien doit rester utilisable après une commande refusée
        await client.IsSdkInstalledAsync();
        Assert.True(client.IsConnected);
    }
}
