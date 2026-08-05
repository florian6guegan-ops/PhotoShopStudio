using QRCoder;

namespace Studio.Core;

/// <summary>
/// Code QR en PNG, généré localement (aucun service externe).
///
/// Il vit dans le noyau parce que trois étages s'en servent et qu'aucun d'eux ne dépend des
/// autres : l'écran du téléversement par téléphone, le partage du réseau Wi-Fi, et la bande
/// basse des planches identité — celle-ci étant composée par l'imagerie et par l'atelier
/// d'impression, qui ignorent tout du serveur web où le code vivait d'abord.
/// </summary>
public static class QrPng
{
    public static byte[] For(string url, int pixelsPerModule = 12)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
