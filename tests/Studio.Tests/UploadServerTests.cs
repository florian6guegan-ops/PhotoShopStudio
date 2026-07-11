using System.Net;
using Studio.Web;

namespace Studio.Tests;

public class UploadServerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioUpload-" + Guid.NewGuid().ToString("N"));
    private UploadServer _server = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _server = new UploadServer(_root, port: 18123);
        await _server.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri("http://localhost:18123") };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _server.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Session_PageServed_FileUploaded_ForbiddenExtensionIgnored()
    {
        var (session, url) = _server.CreateSession();
        Assert.Contains(session.Token, url);
        Assert.True(Directory.Exists(session.Folder));

        // page d'envoi française
        var page = await _client.GetStringAsync($"/u/{session.Token}");
        Assert.Contains("Envoyer vos photos", page);

        // envoi multipart : un jpg accepté + un exécutable refusé silencieusement
        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 }), "files", "IMG_0001.JPG" },
            { new ByteArrayContent(new byte[] { 0x4D, 0x5A }), "files", "malware.exe" },
        };
        var response = await _client.PostAsync($"/u/{session.Token}", form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"saved\":1", await response.Content.ReadAsStringAsync());

        var files = Directory.GetFiles(session.Folder);
        Assert.Single(files);
        Assert.EndsWith(".jpg", files[0]); // nom neutre re-généré, jamais celui du téléphone
        Assert.DoesNotContain("IMG_0001", Path.GetFileName(files[0]));
    }

    [Fact]
    public async Task UnknownToken_GetsExpiredPage_PostRejected()
    {
        var page = await _client.GetStringAsync("/u/ZZZZZZ");
        Assert.Contains("plus valable", page);

        using var form = new MultipartFormDataContent
        {
            { new ByteArrayContent(new byte[] { 1 }), "files", "a.jpg" },
        };
        var response = await _client.PostAsync("/u/ZZZZZZ", form);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Tokens_AreUnique_AndUnambiguous()
    {
        var (s1, _) = _server.CreateSession();
        var (s2, _) = _server.CreateSession();
        Assert.NotEqual(s1.Token, s2.Token);
        Assert.All(s1.Token + s2.Token, c => Assert.DoesNotContain(c, "01OIL"));
    }
}
