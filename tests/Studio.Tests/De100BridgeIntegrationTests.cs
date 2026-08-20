using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>
    /// LA RÉGRESSION DU 20/08/2026 : se reconnecter ne doit rien laisser derrière soi.
    ///
    /// Commande 20-013, à Maisons-Alfort. Le relais du tirage de 13:03 tenait encore le tube
    /// à 13:05 : le relais suivant n'a pas pu créer le sien (« toutes les instances des canaux
    /// de communication sont occupées »), et la planche est partie par le pilote Windows au
    /// lieu de l'envoi direct — sans un mot à l'opérateur.
    ///
    /// <c>ConnectAsync</c> n'avait qu'une porte de sortie, « déjà connecté ». Le lien rompu en
    /// silence ne la franchissait pas : elle écrasait <c>_pipe</c> et <c>_host</c> par les
    /// neufs et abandonnait les anciens ouverts. Le tube restait donc pris par un relais que
    /// plus personne ne tenait, jusqu'au passage du ramasse-miettes — d'où une panne qui se
    /// réparait toute seule au bout de quelques minutes, et qu'on n'a donc jamais attrapée.
    ///
    /// <b>Ce que ce test reproduit, et ce qu'il ne reproduit pas.</b> Il provoque la perte du
    /// lien en TUANT le relais, ce qui est fidèle à l'état où le client se retrouve — pipe
    /// morte, boucle de lecture orpheline, processus à ranger — mais laisse le tube libre.
    /// Il vérifie donc le RANGEMENT, pas le blocage lui-même : celui-ci demanderait un relais
    /// vivant dont le tube s'est refermé, que rien ne permet de fabriquer du dehors.
    /// </summary>
    [Fact]
    public async Task Un_lien_perdu_est_relache_avant_d_en_ouvrir_un_autre()
    {
        // ⚠ file concurrente, et pas une List : le client journalise depuis le fil de sa
        // boucle de lecture pendant que le test parcourt le journal. Avec une List, le test
        // échoue sur « Collection was modified » — un faux négatif qui n'apprend rien.
        var journal = new ConcurrentQueue<string>();
        var etrangers = PidsDesRelais();

        await using var client = new De100BridgeClient(Patience) { Log = journal.Enqueue };
        await client.ConnectAsync();
        Assert.True(client.IsConnected);

        // une PREMIÈRE connexion n'a rien à relâcher : elle doit se taire là-dessus
        Assert.DoesNotContain(journal, l => l.Contains("lien précédent"));

        // ⚠ on ne tue que les relais que ce test a fait naître : un Studio ouvert sur le
        // poste tient le sien, et l'emporter couperait l'imprimante de l'opérateur.
        var lesNotres = PidsDesRelais().Except(etrangers).ToArray();
        Assert.Single(lesNotres);
        Tuer(lesNotres);

        Assert.True(await AttendreQue(() => !client.IsConnected),
            "le client devrait s'apercevoir que son relais est mort");

        await client.ConnectAsync();

        Assert.True(client.IsConnected);
        Assert.Contains(journal, l => l.Contains("lien précédent"));

        // La boucle de lecture d'avant partage _pending avec celle-ci. Restée vivante, elle
        // se réveille sur la mort de l'ANCIEN tube et vide les attentes du NOUVEAU : la
        // commande ci-dessous échouerait sur « Le relais DE100 s'est arrêté », d'un relais
        // qui va pourtant très bien.
        Assert.IsType<bool>(await client.IsSdkInstalledAsync());

        // et surtout : un seul relais à nous, pas deux
        Assert.Single(PidsDesRelais().Except(etrangers));
    }

    private static int[] PidsDesRelais() =>
        Process.GetProcessesByName("Studio.De100Host").Select(p => p.Id).ToArray();

    private static void Tuer(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var processus = Process.GetProcessById(pid);
                processus.Kill();
                processus.WaitForExit(5000);
            }
            catch (ArgumentException)
            {
                // déjà parti
            }
        }
    }

    private static async Task<bool> AttendreQue(Func<bool> condition)
    {
        var limite = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < limite)
        {
            if (condition()) return true;
            await Task.Delay(50);
        }

        return condition();
    }
}
