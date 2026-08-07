using ImageMagick;

namespace Studio.Logo;

/// <summary>
/// Le curseur d'attente de l'application : le diaphragme du logo qui tourne dans sa
/// monture, à la place du cercle bleu de Windows.
///
/// <b>Pourquoi l'écrire octet par octet.</b> Un curseur animé Windows est un fichier RIFF
/// (<c>.ani</c>) qui contient une suite de curseurs (<c>.cur</c>), lesquels sont des
/// bitmaps DIB avec un masque. Ni .NET ni ImageMagick ne savent en produire : GDI+ lit les
/// curseurs, il n'en écrit pas. Les deux formats sont simples et figés depuis trente ans,
/// et tout tient dans ce fichier.
///
/// <b>Six lames, donc soixante degrés.</b> Le diaphragme a une symétrie d'ordre six : au
/// bout de 60° il est revenu sur lui-même. C'est ce qui permet une boucle parfaitement
/// continue avec douze images seulement — l'œil ne voit aucun saut au raccord.
/// </summary>
public static class CurseurAttente
{
    /// <summary>Durée d'affichage d'une image, en soixantièmes de seconde (unité du format).</summary>
    private const int Cadence = 5;

    /// <summary>
    /// Écrit le curseur animé.
    /// </summary>
    /// <param name="chemin">Fichier <c>.ani</c> à écrire.</param>
    /// <param name="cote">
    /// Côté du curseur en pixels. 32 est la taille que Windows attend ; il redimensionne
    /// lui-même pour les affichages agrandis.
    /// </param>
    /// <param name="images">Nombre d'images du cycle.</param>
    public static void Ecrire(string chemin, uint cote = 32, int images = 12)
    {
        var curseurs = new List<byte[]>(images);

        for (var i = 0; i < images; i++)
        {
            // dessiné huit fois trop grand puis réduit : à 32 px, les entailles du
            // diaphragme tracées directement disparaissent — même raison que pour l'icône
            using var grande = Logo.Dessiner(cote * 8, 60.0 * i / images, avecTuile: false);
            grande.Resize(cote, cote);

            curseurs.Add(ConstruireCurseur(grande, cote));
        }

        File.WriteAllBytes(chemin, ConstruireAni(curseurs, cote));
    }

    /// <summary>
    /// Écrit une bande des images du cycle, agrandies, pour juger le mouvement sans avoir
    /// à installer le curseur : un <c>.ani</c> ne se relit pas, il se subit.
    /// </summary>
    public static void EcrireApercu(string chemin, uint cote = 96, int images = 12)
    {
        using var bande = new MagickImageCollection();

        for (var i = 0; i < images; i++)
        {
            var image = Logo.Dessiner(cote * 4, 60.0 * i / images, avecTuile: false);
            image.Resize(cote, cote);
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
            bande.Add(image);
        }

        using var planche = bande.AppendHorizontally();
        planche.Write(chemin);
    }

    /// <summary>
    /// Un curseur <c>.cur</c> : en-tête ICONDIR, une entrée, puis le bitmap.
    ///
    /// Le point chaud est au CENTRE et non en haut à gauche : un curseur d'attente ne
    /// désigne rien, il occupe la place du pointeur — c'est ce que fait le sablier.
    /// </summary>
    private static byte[] ConstruireCurseur(IMagickImage<byte> image, uint cote)
    {
        using var pixels = image.GetPixels();
        var bgra = pixels.ToByteArray(PixelMapping.BGRA)
                   ?? throw new InvalidOperationException("Pixels illisibles.");

        var c = (int)cote;

        // Le masque AND se lit par mots de 32 bits, donc ses lignes font un multiple de
        // 4 octets. Il reste à zéro : la transparence est portée par l'alpha du bitmap
        // 32 bits, que Windows sait lire depuis XP.
        var octetsParLigneMasque = (c + 31) / 32 * 4;
        var tailleCouleurs = c * c * 4;
        var tailleMasque = octetsParLigneMasque * c;
        var tailleBitmap = 40 + tailleCouleurs + tailleMasque;

        using var flux = new MemoryStream();
        using var ecrire = new BinaryWriter(flux);

        // ICONDIR
        ecrire.Write((ushort)0);   // réservé
        ecrire.Write((ushort)2);   // 2 = curseur (1 = icône)
        ecrire.Write((ushort)1);   // une seule image

        // ICONDIRENTRY — sur un curseur, les deux champs du milieu portent le point chaud
        ecrire.Write((byte)c);
        ecrire.Write((byte)c);
        ecrire.Write((byte)0);     // pas de palette
        ecrire.Write((byte)0);     // réservé
        ecrire.Write((ushort)(c / 2));
        ecrire.Write((ushort)(c / 2));
        ecrire.Write((uint)tailleBitmap);
        ecrire.Write(22u);         // les données suivent l'en-tête (6 + 16)

        // BITMAPINFOHEADER — la hauteur est DOUBLE : couleurs puis masque
        ecrire.Write(40u);
        ecrire.Write(c);
        ecrire.Write(c * 2);
        ecrire.Write((ushort)1);
        ecrire.Write((ushort)32);
        ecrire.Write(0u);          // sans compression
        ecrire.Write((uint)tailleCouleurs);
        ecrire.Write(0);
        ecrire.Write(0);
        ecrire.Write(0u);
        ecrire.Write(0u);

        // les pixels, de bas en haut : c'est le sens d'un DIB
        for (var y = c - 1; y >= 0; y--)
            ecrire.Write(bgra, y * c * 4, c * 4);

        ecrire.Write(new byte[tailleMasque]);

        ecrire.Flush();
        return flux.ToArray();
    }

    /// <summary>
    /// Le fichier <c>.ani</c> : « RIFF … ACON », un en-tête <c>anih</c>, puis la liste des
    /// images. Chaque morceau RIFF est aligné sur deux octets.
    /// </summary>
    private static byte[] ConstruireAni(List<byte[]> curseurs, uint cote)
    {
        using var images = new MemoryStream();
        using (var ecrire = new BinaryWriter(images, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            foreach (var curseur in curseurs)
            {
                ecrire.Write("icon"u8.ToArray());
                ecrire.Write((uint)curseur.Length);
                ecrire.Write(curseur);
                if (curseur.Length % 2 == 1) ecrire.Write((byte)0);
            }
        }

        using var entete = new MemoryStream();
        using (var ecrire = new BinaryWriter(entete, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            ecrire.Write(36u);                     // taille de cette structure
            ecrire.Write((uint)curseurs.Count);    // images stockées
            ecrire.Write((uint)curseurs.Count);    // pas de la séquence
            ecrire.Write(cote);
            ecrire.Write(cote);
            ecrire.Write(32u);                     // bits par pixel
            ecrire.Write(1u);                      // plans
            ecrire.Write((uint)Cadence);
            ecrire.Write(1u);                      // les images sont des curseurs, pas des DIB nus
        }

        using var fichier = new MemoryStream();
        using (var ecrire = new BinaryWriter(fichier, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            var liste = 4 + images.Length;                 // « fram » + les images
            var total = 4 + (8 + 36) + (8 + liste);        // « ACON » + anih + LIST

            ecrire.Write("RIFF"u8.ToArray());
            ecrire.Write((uint)total);
            ecrire.Write("ACON"u8.ToArray());

            ecrire.Write("anih"u8.ToArray());
            ecrire.Write(36u);
            ecrire.Write(entete.ToArray());

            ecrire.Write("LIST"u8.ToArray());
            ecrire.Write((uint)liste);
            ecrire.Write("fram"u8.ToArray());
            ecrire.Write(images.ToArray());
        }

        return fichier.ToArray();
    }
}
