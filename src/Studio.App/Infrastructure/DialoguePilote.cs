using System.Threading.Tasks;
using System.Windows;
using Studio.Printing;
using Studio.Printing.Devices.Dnp;

namespace Studio.App.Infrastructure;

/// <summary>
/// Ouvre le dialogue de réglages d'un pilote d'imprimante, en prévenant de ce qui peut mal
/// se passer.
///
/// <b>Pourquoi cet intermédiaire existe.</b> Le dialogue du pilote DS620 interroge la
/// machine pour se remplir. DiLand tient son port USB en exclusif et tourne en permanence :
/// le dialogue s'ouvre alors et ne répond plus. Il ne bloque plus l'application — il part
/// sur son propre fil (voir <see cref="DevMode.ShowDriverDialogAsync"/>) — mais un dialogue
/// figé reste un dialogue figé, et l'opérateur qui l'attend ne sait pas pourquoi. Autant le
/// lui dire AVANT, avec ce qu'il faut faire.
/// </summary>
internal static class DialoguePilote
{
    /// <summary>
    /// Demande les réglages du pilote pour une file, ou rend null si l'opérateur renonce.
    /// </summary>
    public static async Task<byte[]?> OuvrirAsync(string imprimante, byte[]? actuel)
    {
        if (!Prevenir(imprimante)) return null;

        try
        {
            return await DevMode.ShowDriverDialogAsync(imprimante, actuel);
        }
        catch (System.Exception ex)
        {
            FileLog.Write($"Dialogue du pilote « {imprimante} » impossible", ex);

            MessageBox.Show(
                $"Impossible d'ouvrir les réglages du pilote : {ex.Message}\n\n" +
                $"Vérifiez que l'imprimante « {imprimante} » est installée et allumée.",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);

            return null;
        }
    }

    /// <summary>
    /// Prévient quand le dialogue va probablement se figer, et laisse le dernier mot à
    /// l'opérateur : la machine répond parfois quand même, et c'est à lui de juger s'il
    /// peut fermer DiLand maintenant.
    /// </summary>
    /// <returns>Faux si l'opérateur préfère renoncer.</returns>
    private static bool Prevenir(string imprimante)
    {
        if (!EstUneDnp(imprimante) || !DiLandPresence.IsRunning()) return true;

        var reponse = MessageBox.Show(
            $"DiLand est ouvert, et il tient le port USB de « {imprimante} ».\n\n" +
            "Le dialogue du pilote interroge la machine pour s'afficher : il risque de " +
            "rester figé sur « Ne répond pas ». Fermez DiLand le temps de régler, puis " +
            "rouvrez-le.\n\n" +
            "Ouvrir quand même le dialogue ?",
            "Studio Photo", MessageBoxButton.YesNo, MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (reponse != MessageBoxResult.Yes)
        {
            FileLog.Write($"Réglages pilote de « {imprimante} » : abandonnés, DiLand est ouvert");
            return false;
        }

        FileLog.Write($"Réglages pilote de « {imprimante} » : ouverts malgré DiLand");
        return true;
    }

    /// <summary>Même règle de reconnaissance que <c>DiLandPresence.VuesParWindows</c>.</summary>
    private static bool EstUneDnp(string nomDeFile) =>
        nomDeFile.StartsWith("DP-DS", System.StringComparison.OrdinalIgnoreCase)
        || nomDeFile.StartsWith("DS6", System.StringComparison.OrdinalIgnoreCase)
        || nomDeFile.StartsWith("DS8", System.StringComparison.OrdinalIgnoreCase)
        || nomDeFile.StartsWith("QW", System.StringComparison.OrdinalIgnoreCase);
}
