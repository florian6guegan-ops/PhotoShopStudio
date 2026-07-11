using System.Net.Http;
using System.Net.Http.Json;

namespace Studio.Web;

/// <summary>
/// Côté borne : envoi de la commande au poste opérateur. Le GUID de la commande
/// rend les retries sûrs — en cas de doute (timeout), on renvoie tel quel.
/// </summary>
public sealed class KioskClient : IDisposable
{
    private readonly HttpClient _http;

    public KioskClient(string operatorBaseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(operatorBaseUrl),
            Timeout = TimeSpan.FromMinutes(5), // cartes SD lentes + WiFi de boutique
        };
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("/api/ping", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <param name="photoPaths">Chemins locaux, dans le même ordre que dto.Items (item.File = nom envoyé).</param>
    public async Task<KioskAck> SubmitAsync(KioskOrderDto dto, IReadOnlyList<string> photoPaths, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(KioskOrderReceiver.SerializeDto(dto)), "order");

        for (var i = 0; i < dto.Items.Count; i++)
        {
            var stream = File.OpenRead(photoPaths[i]); // libéré avec le form
            form.Add(new StreamContent(stream), "files", dto.Items[i].File);
        }

        using var response = await _http.PostAsync("/api/orders", form, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<KioskAck>(cancellationToken: ct)
               ?? throw new InvalidDataException("Réponse du poste opérateur illisible");
    }

    public void Dispose() => _http.Dispose();
}
