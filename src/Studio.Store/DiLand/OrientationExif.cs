namespace Studio.Store.DiLand;

/// <summary>
/// Lit l'orientation EXIF d'un JPEG, et rien d'autre.
///
/// <b>Pourquoi un lecteur écrit à la main plutôt qu'une bibliothèque :</b> le seul
/// besoin est le tag 0x0112, sur des fichiers qu'on vient de recopier. Faire entrer
/// Magick.NET dans <c>Studio.Store</c> pour six octets amènerait aussi ONNX Runtime et
/// OpenCV — deux cents mégaoctets de dépendances dans le projet qui lit une base SQLite.
///
/// Toute anomalie rend <c>1</c> (« déjà droite ») : un fichier tronqué, un PNG, une photo
/// sans EXIF ne doivent pas empêcher une commande de s'ouvrir. C'est aussi exactement le
/// comportement d'avant ce lecteur, donc l'échec ne peut rien casser de plus.
/// </summary>
internal static class OrientationExif
{
    /// <summary>
    /// Quarts de tour HORAIRES qu'<c>AutoOrient</c> applique à ce fichier pour le
    /// redresser. Zéro si le fichier ne dit rien.
    ///
    /// Les orientations en miroir (2, 4, 5, 7) sont ramenées à leur seule composante de
    /// rotation : aucun appareil de la boutique n'en produit, et une photo retournée
    /// comme dans une glace se verrait au premier coup d'œil.
    /// </summary>
    public static int QuartsDeTour(string cheminFichier)
    {
        return Lire(cheminFichier) switch
        {
            3 or 4 => 2,
            5 or 6 => 1,
            7 or 8 => 3,
            _ => 0,
        };
    }

    /// <summary>Valeur brute du tag EXIF « Orientation » (1 à 8), ou 1 à défaut.</summary>
    public static int Lire(string cheminFichier)
    {
        try
        {
            using var flux = File.OpenRead(cheminFichier);
            return LireDansLeFlux(flux);
        }
        catch
        {
            // fichier absent, verrouillé, illisible : on ne redresse rien de plus
            return 1;
        }
    }

    /// <summary>
    /// Parcourt les segments JPEG jusqu'à l'APP1 « Exif », puis le premier IFD du TIFF
    /// qu'il contient.
    /// </summary>
    internal static int LireDansLeFlux(Stream flux)
    {
        var lecteur = new BinaryReader(flux);

        // en-tête JPEG : SOI
        if (lecteur.ReadByte() != 0xFF || lecteur.ReadByte() != 0xD8) return 1;

        while (true)
        {
            // les segments sont préfixés d'un ou plusieurs 0xFF de bourrage
            int octet = lecteur.ReadByte();
            if (octet != 0xFF) return 1;

            int marqueur;
            do { marqueur = lecteur.ReadByte(); } while (marqueur == 0xFF);

            // SOS (0xDA) : les données d'image commencent, il n'y a plus de métadonnée
            // à espérer. EOI (0xD9) de même.
            if (marqueur is 0xDA or 0xD9) return 1;

            // segments sans charge utile
            if (marqueur is >= 0xD0 and <= 0xD7 or 0x01) continue;

            var longueur = LireEntier16BigEndian(lecteur);
            if (longueur < 2) return 1;

            var charge = lecteur.ReadBytes(longueur - 2);
            if (charge.Length < longueur - 2) return 1;

            if (marqueur != 0xE1) continue; // APP1

            // « Exif\0\0 » — un APP1 peut aussi porter du XMP, qu'on ignore
            if (charge.Length < 6 ||
                charge[0] != 'E' || charge[1] != 'x' || charge[2] != 'i' || charge[3] != 'f' ||
                charge[4] != 0)
                continue;

            return LireDansLeTiff(charge, 6);
        }
    }

    /// <summary>
    /// Cherche le tag 0x0112 dans le premier IFD du bloc TIFF.
    /// </summary>
    /// <param name="debut">
    /// Position de l'en-tête TIFF dans le tableau. <b>Tous les décalages du TIFF sont
    /// comptés depuis là</b>, et non depuis le début du fichier — s'en écarter fait lire
    /// n'importe quoi.
    /// </param>
    private static int LireDansLeTiff(byte[] octets, int debut)
    {
        if (octets.Length < debut + 8) return 1;

        bool petitBoutiste;
        if (octets[debut] == 'I' && octets[debut + 1] == 'I') petitBoutiste = true;
        else if (octets[debut] == 'M' && octets[debut + 1] == 'M') petitBoutiste = false;
        else return 1;

        if (Entier16(octets, debut + 2, petitBoutiste) != 42) return 1;

        var decalageIfd = Entier32(octets, debut + 4, petitBoutiste);
        var ifd = debut + decalageIfd;
        if (ifd < 0 || ifd + 2 > octets.Length) return 1;

        var nombreEntrees = Entier16(octets, ifd, petitBoutiste);

        for (var i = 0; i < nombreEntrees; i++)
        {
            var entree = ifd + 2 + i * 12;
            if (entree + 12 > octets.Length) return 1;

            if (Entier16(octets, entree, petitBoutiste) != 0x0112) continue;

            // type 3 = SHORT ; la valeur tient dans les quatre octets du champ, donc
            // elle est écrite sur place et non au bout d'un décalage
            var valeur = Entier16(octets, entree + 8, petitBoutiste);
            return valeur is >= 1 and <= 8 ? valeur : 1;
        }

        return 1;
    }

    private static int LireEntier16BigEndian(BinaryReader lecteur)
    {
        var haut = lecteur.ReadByte();
        var bas = lecteur.ReadByte();
        return (haut << 8) | bas;
    }

    private static int Entier16(byte[] o, int i, bool petitBoutiste) =>
        petitBoutiste ? o[i] | (o[i + 1] << 8) : (o[i] << 8) | o[i + 1];

    private static int Entier32(byte[] o, int i, bool petitBoutiste) =>
        petitBoutiste
            ? o[i] | (o[i + 1] << 8) | (o[i + 2] << 16) | (o[i + 3] << 24)
            : (o[i] << 24) | (o[i + 1] << 16) | (o[i + 2] << 8) | o[i + 3];
}
