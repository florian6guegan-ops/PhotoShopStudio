using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Studio.Printing.Devices.Dnp;

/// <summary>
/// Envoie une image à une imprimante DNP <b>sans passer par le pilote Windows</b>, comme
/// le fait DiLand.
///
/// <b>Pourquoi ce chemin existe.</b> Les tirages sortaient avec un fantôme coloré décalé :
/// aléatoire, sur plusieurs postes et plusieurs DS620, et JAMAIS depuis DiLand. Tous les
/// réglages du pilote y sont passés — vitesse, tampon, ICM, spoule, EMF — sans rien
/// changer, et DNP n'a pas publié de pilote depuis 2017. Mesuré le 06/08/2026 : DiLand
/// imprime <b>sans que le spouleur Windows en sache rien</b> (compteur machine +1, aucun
/// travail dans la file, aucune entrée dans le journal du spouleur, pourtant activé). Il
/// passe par <c>cspstat.dll</c>. Le premier tirage envoyé par ce chemin depuis Studio est
/// sorti sans fantôme.
///
/// <b>Trois conventions, chacune payée d'une feuille.</b>
///
/// 1. <b>L'image part en MIROIR gauche-droite.</b> Le premier essai est sorti retourné :
///    le SDK ne parcourt pas la trame dans le sens où GDI+ la rend.
/// 2. <b>Les lignes sont compactées à largeur × 3.</b> <see cref="Bitmap.LockBits"/> aligne
///    chaque ligne sur quatre octets ; sur une largeur qui n'est pas multiple de quatre, ce
///    bourrage décale chaque ligne d'un octet de plus que la précédente — les canaux de
///    couleur partent en biais, ce qui ressemble beaucoup au défaut qu'on cherchait.
/// 3. <b>La découverte est obligatoire</b> avant tout appel, dans le processus courant :
///    voir <see cref="CspStatInterop.GetPrinterPortNum"/>.
///
/// À n'utiliser que dans un processus 32 BITS — donc le relais, jamais l'application.
/// </summary>
public static class DnpEnvoiDirect
{
    /// <summary>Ce que <c>SendImageData</c> rend quand le travail est accepté.</summary>
    private const int Accepte = 1;

    /// <summary>
    /// Prépare la trame telle que la machine l'attend : 24 bits par pixel, en miroir,
    /// lignes compactées.
    ///
    /// Séparée de l'envoi pour être vérifiable : aucun poste de développement n'a de DS620
    /// branchée, et c'est ici que vivent les trois conventions qui coûtent une feuille
    /// chacune quand on se trompe.
    /// </summary>
    /// <param name="image">L'image à tirer, déjà à la taille de la trame.</param>
    /// <returns>Les octets à remettre au SDK, et les dimensions correspondantes.</returns>
    public static (byte[] Octets, int Largeur, int Hauteur) Preparer(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // On travaille sur une COPIE : le miroir modifie l'image, et l'appelant garde
        // souvent la sienne pour les copies suivantes du même tirage.
        using var copie = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(copie))
        {
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(image, 0, 0, image.Width, image.Height);
        }

        copie.RotateFlip(RotateFlipType.RotateNoneFlipX);

        var parLigne = copie.Width * 3;
        var octets = new byte[parLigne * copie.Height];

        var verrou = copie.LockBits(
            new Rectangle(0, 0, copie.Width, copie.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < copie.Height; y++)
                Marshal.Copy(verrou.Scan0 + y * verrou.Stride, octets, y * parLigne, parLigne);
        }
        finally
        {
            copie.UnlockBits(verrou);
        }

        return (octets, copie.Width, copie.Height);
    }

    /// <summary>
    /// Envoie une image à l'imprimante et rend vrai si elle l'a acceptée.
    ///
    /// N'attend PAS la sortie du papier : la machine prend le travail en mémoire et le tire
    /// ensuite. Pour savoir où elle en est, interroger <c>GetPQTY</c> et <c>GetStatus</c>.
    /// </summary>
    /// <param name="portNumber">Rang de la machine dans la découverte.</param>
    /// <param name="image">L'image, déjà à la taille de la trame de la machine.</param>
    /// <param name="finition">Finition de surface appliquée à ce tirage.</param>
    public static bool Envoyer(int portNumber, Bitmap image, DnpOvercoat finition)
    {
        var (octets, largeur, hauteur) = Preparer(image);

        CspStatInterop.SetOvercoatFinish(portNumber, (int)finition);

        var epingle = GCHandle.Alloc(octets, GCHandleType.Pinned);
        try
        {
            var rendu = CspStatInterop.SendImageData(
                portNumber, epingle.AddrOfPinnedObject(), 0, 0, largeur, hauteur);

            return rendu == Accepte;
        }
        finally
        {
            epingle.Free();
        }
    }
}
