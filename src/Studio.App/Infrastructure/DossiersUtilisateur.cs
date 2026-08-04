using System.IO;
using System.Runtime.InteropServices;

namespace Studio.App.Infrastructure;

/// <summary>
/// Les dossiers de l'utilisateur Windows qu'un écran peut proposer en raccourci.
///
/// « Téléchargements » n'est PAS dans <see cref="Environment.SpecialFolder"/> : c'est un
/// dossier « connu » d'après Vista, et il faut passer par <c>SHGetKnownFolderPath</c>.
/// Le composer à la main depuis le profil marche sur un poste français mais pas sur un
/// Windows anglais, et pas du tout quand l'utilisateur l'a déplacé — ce que fait tout le
/// monde quand le disque système est petit.
/// </summary>
public static class DossiersUtilisateur
{
    /// <summary>GUID du dossier connu « Downloads ».</summary>
    private static readonly Guid Telechargements = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    /// <summary>
    /// Le dossier « Téléchargements » de l'utilisateur, ou null s'il n'existe pas.
    ///
    /// Null plutôt qu'un chemin inventé : un raccourci qui mène à un dossier absent est
    /// pire que pas de raccourci du tout — l'opérateur clique et ne comprend pas.
    /// </summary>
    public static string? Telechargement()
    {
        var chemin = ParDossierConnu() ?? ParProfil();
        return chemin is not null && Directory.Exists(chemin) ? chemin : null;
    }

    private static string? ParDossierConnu()
    {
        var ptr = IntPtr.Zero;
        try
        {
            return SHGetKnownFolderPath(Telechargements, 0, IntPtr.Zero, out ptr) == 0
                ? Marshal.PtrToStringUni(ptr)
                : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
        }
    }

    /// <summary>Repli : l'emplacement par défaut, sous le profil.</summary>
    private static string? ParProfil()
    {
        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profil) ? null : Path.Combine(profil, "Downloads");
    }
}
