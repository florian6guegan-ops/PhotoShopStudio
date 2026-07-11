using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Studio.Web;

/// <summary>Session d'envoi téléphone : un jeton court = un dossier de réception.</summary>
public sealed record UploadSession(string Token, string Folder, DateTimeOffset CreatedAt)
{
    public bool Expired => DateTimeOffset.Now - CreatedAt > TimeSpan.FromHours(4);
}

/// <summary>
/// Serveur Kestrel embarqué dans l'app opérateur : page d'envoi de photos pour
/// téléphone (via QR), sans application ni compte. Les fichiers arrivent dans
/// incoming/&lt;jeton&gt;/ ; le reste du pipeline (HEIC compris) est inchangé.
/// </summary>
public sealed class UploadServer : IAsyncDisposable
{
    // alphabet sans caractères ambigus (pas de 0/O, 1/I/L…)
    private const string TokenAlphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    private const int TokenLength = 6;
    private const long MaxFileBytes = 200 * 1024 * 1024;   // vidéo/rafale iPhone généreuse
    private const long MaxRequestBytes = 2L * 1024 * 1024 * 1024;

    private static readonly string[] AllowedExtensions =
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".bmp", ".tif", ".tiff", ".webp", ".gif", ".dng" };

    private readonly string _incomingRoot;
    private readonly int _port;
    private readonly ConcurrentDictionary<string, UploadSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private WebApplication? _app;

    /// <summary>Réception des commandes borne ; null = API /api/orders désactivée.</summary>
    public KioskOrderReceiver? KioskOrders { get; set; }

    public UploadServer(string incomingRoot, int port = 8123)
    {
        _incomingRoot = incomingRoot;
        _port = port;
        Directory.CreateDirectory(incomingRoot);
    }

    public int Port => _port;

    /// <summary>Adresse IPv4 locale non-boucle la plus plausible pour le réseau de la boutique.</summary>
    public static IPAddress? LocalIPv4()
    {
        try
        {
            // « connexion » UDP fictive : n'envoie rien, mais force le choix de l'interface de sortie
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.Connect("8.8.8.8", 65530);
            return (socket.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        }
    }

    /// <summary>Crée une session : dossier de réception + URL à mettre dans le QR.</summary>
    public (UploadSession Session, string Url) CreateSession()
    {
        string token;
        do
        {
            token = string.Concat(Enumerable.Range(0, TokenLength)
                .Select(_ => TokenAlphabet[RandomNumberGenerator.GetInt32(TokenAlphabet.Length)]));
        } while (_sessions.ContainsKey(token));

        var folder = Path.Combine(_incomingRoot, $"{DateTime.Now:yyyyMMdd-HHmm}-{token}");
        Directory.CreateDirectory(folder);
        var session = new UploadSession(token, folder, DateTimeOffset.Now);
        _sessions[token] = session;

        var host = LocalIPv4()?.ToString() ?? "localhost";
        return (session, $"http://{host}:{_port}/u/{token}");
    }

    public UploadSession? FindSession(string token) =>
        _sessions.TryGetValue(token, out var s) && !s.Expired ? s : null;

    public async Task StartAsync()
    {
        if (_app is not null) return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.ListenAnyIP(_port);
            k.Limits.MaxRequestBodySize = MaxRequestBytes;
        });

        var app = builder.Build();

        app.MapGet("/u/{token}", (string token) =>
        {
            var session = FindSession(token);
            return session is null
                ? Results.Content(Pages.Expired, "text/html; charset=utf-8")
                : Results.Content(Pages.Upload(token), "text/html; charset=utf-8");
        });

        app.MapPost("/u/{token}", async (string token, HttpRequest request) =>
        {
            var session = FindSession(token);
            if (session is null) return Results.NotFound();
            if (!request.HasFormContentType) return Results.BadRequest();

            var form = await request.ReadFormAsync();
            var saved = 0;
            foreach (var file in form.Files)
            {
                if (file.Length is 0 or > MaxFileBytes) continue;
                var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext)) continue;

                // nom neutre et sûr : jamais le chemin fourni par le téléphone
                var target = Path.Combine(session.Folder,
                    $"tel-{DateTime.Now:HHmmss}-{Guid.NewGuid().ToString("N")[..6]}{ext}");
                await using var output = File.Create(target);
                await file.CopyToAsync(output);
                saved++;
            }
            return Results.Json(new { saved });
        });

        app.MapGet("/api/ping", () => Results.Json(new { ok = true }));

        app.MapPost("/api/orders", async (HttpRequest request) =>
        {
            var receiver = KioskOrders;
            if (receiver is null) return Results.NotFound();
            if (!request.HasFormContentType) return Results.BadRequest();

            var form = await request.ReadFormAsync();
            var orderJson = form["order"].ToString();
            if (string.IsNullOrEmpty(orderJson)) return Results.BadRequest();

            KioskOrderDto dto;
            try
            {
                dto = KioskOrderReceiver.ParseDto(orderJson);
            }
            catch (Exception)
            {
                return Results.BadRequest();
            }

            // les photos de la commande arrivent dans un dossier dédié au GUID :
            // un retry ré-écrit les mêmes fichiers, jamais de doublon de commande
            var folder = Path.Combine(_incomingRoot, $"borne-{dto.Id:N}");
            Directory.CreateDirectory(folder);
            foreach (var file in form.Files)
            {
                if (file.Length is 0 or > MaxFileBytes) continue;
                var name = Path.GetFileName(file.FileName);
                var ext = Path.GetExtension(name).ToLowerInvariant();
                if (!AllowedExtensions.Contains(ext)) continue;
                await using var output = File.Create(Path.Combine(folder, name));
                await file.CopyToAsync(output);
            }

            try
            {
                var ack = receiver.Submit(dto, folder);
                return Results.Json(ack);
            }
            catch (KeyNotFoundException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });

        _app = app;
        await app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
        _app = null;
    }
}
