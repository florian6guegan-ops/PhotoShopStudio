using System.Net;
using Studio.Core.Cloud;

namespace Studio.Tests;

/// <summary>
/// L'installation des modèles de détourage, publiés à part des versions de Studio.
///
/// Ils s'installaient à la main : l'écran des réglages indiquait un dossier et un nom de
/// fichier, et c'était tout. Personne n'y a rien posé — le poste de Créteil a tourné sans
/// détourage depuis son installation, le réglage restant sur la méthode par couleur, ce
/// qui ressemble à un choix et n'en était pas un.
/// </summary>
public class ModelesDetourageTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "StudioModeles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// <b>L'étiquette est fixe et ne suit pas les versions.</b> Rattacher les modèles à la
    /// dernière publication obligerait à renvoyer 109 Mo à chaque parution, et un oubli les
    /// rendrait introuvables pour tout le parc.
    /// </summary>
    [Fact]
    public void L_adresse_vise_la_publication_des_modeles()
    {
        var adresse = ModelesDetourage.Adresse("birefnet-lite-fp16.onnx");

        Assert.Equal(
            "https://github.com/florian6guegan-ops/PhotoShopStudio/releases/download/" +
            "modeles-v1/birefnet-lite-fp16.onnx",
            adresse);
    }

    /// <summary>L'étiquette ne doit pas dériver : c'est elle qui a été publiée.</summary>
    [Fact]
    public void L_etiquette_est_celle_qui_a_ete_publiee()
    {
        Assert.Equal("modeles-v1", ModelesDetourage.Etiquette);
    }

    [Fact]
    public async Task Le_modele_telecharge_est_ecrit_sous_son_nom()
    {
        var client = ClientQuiRend(HttpStatusCode.OK, [1, 2, 3, 4, 5]);

        var chemin = await ModelesDetourage.TelechargerAsync(client, "modele.onnx", _dossier);

        Assert.Equal(Path.Combine(_dossier, "modele.onnx"), chemin);
        Assert.Equal([1, 2, 3, 4, 5], await File.ReadAllBytesAsync(chemin));
    }

    /// <summary>
    /// <b>Un téléchargement qui échoue ne laisse rien sous le nom du modèle.</b> Sinon le
    /// moteur chargerait un fichier tronqué, échouerait, et retomberait sur la méthode par
    /// couleur sans que rien n'explique pourquoi — le pire des états, puisque l'écran
    /// annoncerait le modèle « installé ».
    /// </summary>
    [Fact]
    public async Task Un_echec_ne_laisse_pas_de_modele_tronque()
    {
        var client = ClientQuiRend(HttpStatusCode.NotFound, []);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => ModelesDetourage.TelechargerAsync(client, "modele.onnx", _dossier));

        Assert.False(File.Exists(Path.Combine(_dossier, "modele.onnx")));
    }

    /// <summary>Le dossier des modèles n'existe pas sur un poste neuf : on le crée.</summary>
    [Fact]
    public async Task Le_dossier_des_modeles_est_cree_au_besoin()
    {
        var neuf = Path.Combine(_dossier, "jamais", "vu");
        var client = ClientQuiRend(HttpStatusCode.OK, [7]);

        await ModelesDetourage.TelechargerAsync(client, "modele.onnx", neuf);

        Assert.True(File.Exists(Path.Combine(neuf, "modele.onnx")));
    }

    /// <summary>L'avancement est rapporté quand le serveur annonce la taille.</summary>
    [Fact]
    public async Task L_avancement_est_rapporte()
    {
        var client = ClientQuiRend(HttpStatusCode.OK, [1, 2, 3, 4, 5, 6, 7, 8]);
        var vus = new List<double>();

        await ModelesDetourage.TelechargerAsync(
            client, "modele.onnx", _dossier, new Progress<double>(vus.Add));

        // Progress<T> rejoue sur le contexte : on laisse le temps aux rappels d'arriver
        await Task.Delay(200);

        Assert.NotEmpty(vus);
        Assert.All(vus, f => Assert.InRange(f, 0, 1));
        Assert.Equal(1, vus[^1], 3);
    }

    // ————— outils —————

    private static HttpClient ClientQuiRend(HttpStatusCode code, byte[] contenu) =>
        new(new ReponseFigee(code, contenu));

    /// <summary>Un serveur en dur : ces essais portent sur l'écriture, pas sur le réseau.</summary>
    private sealed class ReponseFigee(HttpStatusCode code, byte[] contenu) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var reponse = new HttpResponseMessage(code)
            {
                Content = new ByteArrayContent(contenu),
            };
            reponse.Content.Headers.ContentLength = contenu.Length;
            return Task.FromResult(reponse);
        }
    }
}
