using QRCoder;

namespace Studio.Web;

/// <summary>Code QR en PNG, généré localement (aucun service externe).</summary>
public static class QrPng
{
    public static byte[] For(string url, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
