using System.IO;
using System.Runtime.InteropServices;
using Studio.Core.Domain;

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

    /// <summary>
    /// Le Bureau de l'utilisateur, ou null s'il n'existe pas.
    ///
    /// Celui-là, <see cref="Environment.SpecialFolder"/> le connaît : pas besoin de passer
    /// par les dossiers connus de Windows.
    /// </summary>
    public static string? Bureau()
    {
        var chemin = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return !string.IsNullOrEmpty(chemin) && Directory.Exists(chemin) ? chemin : null;
    }

    /// <summary>
    /// Le dossier où la boutique range ses envois WeTransfer, cherché aux endroits
    /// habituels — ou null.
    ///
    /// <b>Il n'a rien d'officiel</b> : WeTransfer dépose dans les Téléchargements comme
    /// n'importe quel navigateur, et le dossier dont on parle est celui que l'exploitant
    /// crée pour y ranger ce qu'il en sort. On regarde donc là où il aurait pu le mettre,
    /// et l'on rend null plutôt qu'un chemin inventé : un favori qui mène nulle part est
    /// pire que pas de favori du tout. L'écran des réglages permet de le désigner.
    /// </summary>
    public static string? WeTransfer()
    {
        var profil = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidats = new List<string>();
        if (Telechargement() is { } telechargements)
        {
            candidats.Add(Path.Combine(telechargements, "WeTransfer"));
            candidats.Add(Path.Combine(telechargements, "Wetransfer"));
        }
        if (!string.IsNullOrEmpty(profil))
        {
            candidats.Add(Path.Combine(profil, "WeTransfer"));
            if (Bureau() is { } bureau) candidats.Add(Path.Combine(bureau, "WeTransfer"));
        }

        return candidats.FirstOrDefault(Directory.Exists);
    }

    /// <summary>
    /// Où mène un favori : son chemin s'il en porte un, sinon le dossier que sa clé désigne.
    /// Null quand le dossier n'existe pas sur ce poste.
    /// </summary>
    public static string? Resoudre(DossierFavori favori)
    {
        ArgumentNullException.ThrowIfNull(favori);

        if (!string.IsNullOrWhiteSpace(favori.Chemin))
            return Directory.Exists(favori.Chemin) ? favori.Chemin : null;

        return favori.Cle switch
        {
            DossierFavori.Bureau => Bureau(),
            DossierFavori.Telechargements => Telechargement(),
            DossierFavori.WeTransfer => WeTransfer(),
            _ => null,
        };
    }

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
