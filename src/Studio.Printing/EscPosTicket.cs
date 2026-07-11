using System.Net.Sockets;
using System.Text;
using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.Printing;

/// <summary>Réglages du ticket de caisse (config/ticket.json).</summary>
public sealed class TicketConfig
{
    public bool Enabled { get; set; } = true;
    public string Host { get; set; } = "192.168.1.87";
    public int Port { get; set; } = 9100;
    public List<string> Header { get; set; } = new() { "PHOTOCONCEPT" };
    public List<string> Footer { get; set; } = new() { "Merci de votre visite !" };
}

/// <summary>
/// Ticket ESC/POS maison : construction pure (testable octet par octet) + envoi TCP brut.
/// CP858 pour les accents et le symbole €.
/// </summary>
public static class EscPosTicket
{
    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;
    private const int LineWidth = 42; // imprimante 80 mm, police A

    private static readonly Encoding Cp858 = CreateCp858();

    private static Encoding CreateCp858()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(858);
    }

    /// <summary>Construit le ticket complet d'une commande (init → coupe).</summary>
    public static byte[] Build(Order order, ProductCatalog catalog, TicketConfig config)
    {
        var ms = new MemoryStream();

        ms.Write(new byte[] { Esc, (byte)'@' });          // init
        ms.Write(new byte[] { Esc, (byte)'t', 19 });      // page de codes 858 (Epson : 19)

        Align(ms, center: true);
        foreach (var line in config.Header)
        {
            Bold(ms, true);
            WriteLine(ms, line);
            Bold(ms, false);
        }
        WriteLine(ms, $"{order.CreatedAt:dd/MM/yyyy HH:mm}");
        WriteLine(ms, "");

        // numéro client en double taille
        Size(ms, doubleSize: true);
        WriteLine(ms, $"N° {order.DisplayNumber}");
        Size(ms, doubleSize: false);
        WriteLine(ms, "");

        Align(ms, center: false);
        foreach (var line in order.Envelopes.SelectMany(e => e.Lines))
        {
            var name = catalog.Find(line.ProductCode)?.Name ?? line.ProductCode;
            WriteLine(ms, PriceLine($"{line.TotalPrints} x {Truncate(name, 26)}", line.Total));
        }

        WriteLine(ms, new string('-', LineWidth));
        Bold(ms, true);
        WriteLine(ms, PriceLine("TOTAL", order.Total));
        Bold(ms, false);
        WriteLine(ms, "");

        Align(ms, center: true);
        foreach (var line in config.Footer)
            WriteLine(ms, line);
        if (order.CustomerName is { Length: > 0 } customer)
            WriteLine(ms, customer);

        ms.Write(new byte[] { 0x0A, 0x0A, 0x0A });
        ms.Write(new byte[] { Gs, (byte)'V', 66, 0 });    // coupe partielle
        return ms.ToArray();
    }

    /// <summary>Envoi TCP brut vers l'imprimante thermique réseau.</summary>
    public static void Send(byte[] ticket, TicketConfig config, int timeoutMs = 5000)
    {
        using var client = new TcpClient();
        if (!client.ConnectAsync(config.Host, config.Port).Wait(timeoutMs))
            throw new TimeoutException($"Imprimante ticket injoignable ({config.Host}:{config.Port})");
        using var stream = client.GetStream();
        stream.WriteTimeout = timeoutMs;
        stream.Write(ticket);
        stream.Flush();
    }

    /// <summary>Libellé à gauche, prix aligné à droite sur la largeur du ticket.</summary>
    public static string PriceLine(string label, decimal amount)
    {
        var price = $"{amount:0.00} EUR";
        var pad = LineWidth - label.Length - price.Length;
        return pad >= 1 ? label + new string(' ', pad) + price : $"{label} {price}";
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "…";

    private static void WriteLine(MemoryStream ms, string text)
    {
        var bytes = Cp858.GetBytes(text);
        ms.Write(bytes);
        ms.WriteByte(0x0A);
    }

    private static void Align(MemoryStream ms, bool center) =>
        ms.Write(new byte[] { Esc, (byte)'a', center ? (byte)1 : (byte)0 });

    private static void Bold(MemoryStream ms, bool on) =>
        ms.Write(new byte[] { Esc, (byte)'E', on ? (byte)1 : (byte)0 });

    private static void Size(MemoryStream ms, bool doubleSize) =>
        ms.Write(new byte[] { Gs, (byte)'!', doubleSize ? (byte)0x11 : (byte)0x00 });
}
