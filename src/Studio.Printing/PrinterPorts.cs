using System.Management;

namespace Studio.Printing;

/// <summary>
/// Port Windows d'une file d'impression. Sert à détecter les files « puits » :
/// une imprimante branchée sur le port `nul` accepte les travaux et les jette
/// silencieusement — c'est le cas des files DE100 installées par DiLand, qui
/// pilote le minilab par le SDK Fuji et non par le spouleur Windows.
/// Imprimer là-dessus donne un succès apparent et aucun tirage.
/// </summary>
public static class PrinterPorts
{
    /// <summary>Nom du port de la file, ou null si la file est introuvable.</summary>
    public static string? GetPort(string printerName)
    {
        try
        {
            var escaped = printerName.Replace("\\", "\\\\").Replace("'", "\\'");
            using var searcher = new ManagementObjectSearcher(
                $"SELECT PortName FROM Win32_Printer WHERE Name = '{escaped}'");
            foreach (var printer in searcher.Get())
                return printer["PortName"] as string;
        }
        catch
        {
            // WMI indisponible : on ne bloque pas l'impression pour autant
        }
        return null;
    }

    /// <summary>Vrai si la file jette les travaux (port `nul`).</summary>
    public static bool IsNullPort(string printerName) =>
        string.Equals(GetPort(printerName), "nul", StringComparison.OrdinalIgnoreCase);
}
