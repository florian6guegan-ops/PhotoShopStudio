using Studio.Core.Domain;

namespace Studio.Imaging.Geometry;

/// <summary>
/// Le cadre du PORTRAIT, déduit de celui de l'identité.
///
/// Une planche de rentrée porte deux cadrages de la même photo : la case normée, serrée sur
/// le visage comme l'exige le guichet, et le portrait, qui doit montrer les épaules — c'est
/// une photo qu'on met sur un buffet, pas dans un passeport. Demander deux cadrages à
/// l'opérateur pour chaque enfant, un jour de rentrée, c'est doubler le temps du comptoir.
///
/// On part donc du cadre d'identité, qui est déjà posé sur le visage — à la main ou par le
/// cadrage automatique —, et on l'ÉLARGIT autour du regard. L'opérateur garde la main : le
/// cadre proposé s'ouvre dans l'éditeur de recadrage ordinaire s'il veut le reprendre.
///
/// <b>Rien n'est déduit du fichier ici.</b> Ce sont des fractions d'image et un rapport de
/// cases : la fonction est pure, et se vérifie sans photo.
/// </summary>
public static class CadrageElargi
{
    /// <summary>
    /// De combien ouvrir le cadre, en hauteur.
    ///
    /// Une case d'identité française cadre 45 mm de haut sur un visage de 34 : le buste n'y
    /// est pas. À 1,6, le même cadre couvre la hauteur d'un demi-buste — épaules comprises,
    /// un peu d'air au-dessus de la tête —, ce qui est le portrait scolaire. Au-delà, la
    /// tête devient petite et la photo se met à raconter le mur du fond.
    /// </summary>
    public const double FacteurParDefaut = 1.6;

    /// <summary>
    /// Où tombe le regard dans le cadre d'identité, en fraction de sa hauteur.
    ///
    /// Les normes placent la tête haut dans la case — menton près du bas, un peu d'air sur
    /// le crâne : les yeux se retrouvent autour du milieu haut. C'est ce point-là qu'il faut
    /// tenir en élargissant, et non le centre du cadre : ancrer sur le centre ferait
    /// descendre le visage et l'on gagnerait du plafond au lieu des épaules.
    /// </summary>
    private const double RegardDansLIdentite = 0.42;

    /// <summary>
    /// Élargit le cadre d'identité au rapport du portrait.
    ///
    /// Le résultat est TOUJOURS dans la photo : s'il n'y a pas la place d'ouvrir autant, on
    /// ouvre autant qu'on peut, puis on ramène le cadre à l'intérieur des bords. Un cadre
    /// qui déborde donnerait un tirage à bords vides — c'est le défaut que l'écran
    /// d'identité signale déjà, et qu'il ne faut pas fabriquer soi-même.
    /// </summary>
    /// <param name="identite">Le cadre normé, en fractions de l'image ORIENTÉE.</param>
    /// <param name="imageLargeurPx">Largeur de l'image orientée (EXIF et quarts de tour appliqués).</param>
    /// <param name="imageHauteurPx">Hauteur de l'image orientée.</param>
    /// <param name="grandeLargeurMm">Largeur de la case du portrait sur la planche.</param>
    /// <param name="grandeHauteurMm">Hauteur de la case du portrait sur la planche.</param>
    /// <param name="facteur">Ouverture, en multiple de la hauteur du cadre d'identité.</param>
    /// <param name="redressementDegres">
    /// Le redressement fin appliqué à la photo, en degrés.
    ///
    /// <b>Sans lui, le portrait sort avec un coin blanc.</b> Voir <see cref="SurfaceUtile"/>.
    /// </param>
    public static CropSpec Depuis(
        CropSpec identite,
        double imageLargeurPx, double imageHauteurPx,
        double grandeLargeurMm, double grandeHauteurMm,
        double facteur = FacteurParDefaut,
        double redressementDegres = 0)
    {
        ArgumentNullException.ThrowIfNull(identite);

        // Sans dimensions ni cotes, il n'y a rien à calculer : on rend la photo entière,
        // que le rendu saura toujours poser (en Fill) dans la case.
        if (imageLargeurPx <= 0 || imageHauteurPx <= 0) return CropSpec.Full;
        if (grandeLargeurMm <= 0 || grandeHauteurMm <= 0) return CropSpec.Full;
        if (!identite.IsValid) return CropSpec.Full;

        // ⚠ TOUT SE COMPTE DANS LE REPÈRE DE L'IMAGE REDRESSÉE, pas dans celui du fichier.
        // Le rendu redresse AVANT de recadrer (voir ImagePipeline.AppliquerLaGeometrie), et
        // une rotation AGRANDIT l'image — elle pousse les coins. Le cadre d'identité posé à
        // l'écran est en fractions de cette image-là, et celui qu'on rend doit l'être aussi.
        var (imgW, imgH) = Encombrement(imageLargeurPx, imageHauteurPx, redressementDegres);

        var rapport = grandeLargeurMm / grandeHauteurMm;

        // TOUT SE CALCULE EN PIXELS, et c'est indispensable : un CropSpec est en fractions
        // de l'image, si bien qu'un cadre carré sur une photo 3:2 s'écrit 0,66 × 1. Élargir
        // sur les fractions déformerait le portrait d'autant.
        var hauteurId = identite.Height * imgH;
        var ancreX = (identite.X + identite.Width / 2) * imgW;
        var ancreY = (identite.Y + identite.Height * RegardDansLIdentite) * imgH;

        var hauteur = hauteurId * Math.Max(1, facteur);
        var largeur = hauteur * rapport;

        // Le cadre ne peut pas être plus grand que ce que la photo REDRESSÉE offre : on le
        // réduit AU RAPPORT, sans quoi le portrait sortirait étiré.
        var (maxLargeur, maxHauteur) = SurfaceUtile(
            imageLargeurPx, imageHauteurPx, rapport, redressementDegres);

        var trop = Math.Max(largeur / maxLargeur, hauteur / maxHauteur);
        if (trop > 1)
        {
            largeur /= trop;
            hauteur /= trop;
        }

        // le regard garde sa place dans le nouveau cadre : c'est la règle des tiers, déjà
        // celle du cadrage automatique des tirages
        var x = ancreX - largeur / 2;
        var y = ancreY - hauteur * CadrageAutomatique.HauteurDuRegard;

        (x, y) = RamenerDansLaPhoto(
            x, y, largeur, hauteur,
            imageLargeurPx, imageHauteurPx, imgW, imgH, redressementDegres);

        // ⚠ Les bornes sont PROTÉGÉES du zéro négatif, et il ne faut pas l'enlever : quand
        // le cadre a été ramené à la taille exacte de la photo, le passage par le repère de
        // celle-ci et le retour ne retombent pas sur zéro mais sur −2,8·10⁻¹⁷. Le CropSpec
        // qui en sort se déclare INVALIDE, et Math.Clamp lève quand son minimum dépasse son
        // maximum. C'est le cas d'une photo cadrée large sur laquelle on demande beaucoup
        // d'ouverture — le portrait prend alors toute l'image.
        //
        // Ce dernier serrage ne défait rien : l'encombrement contient la photo, donc un
        // cadre déjà dedans y est déjà.
        x = Math.Clamp(x, 0, Math.Max(0, imgW - largeur));
        y = Math.Clamp(y, 0, Math.Max(0, imgH - hauteur));

        return new CropSpec(x / imgW, y / imgH, largeur / imgW, hauteur / imgH);
    }

    /// <summary>
    /// Ce qu'occupe une photo une fois redressée : ses coins tournés poussent les bords.
    /// </summary>
    public static (double Largeur, double Hauteur) Encombrement(
        double largeurPx, double hauteurPx, double degres)
    {
        var (c, s) = CosSin(degres);
        return (largeurPx * c + hauteurPx * s, largeurPx * s + hauteurPx * c);
    }

    /// <summary>
    /// Le plus grand cadre AU RAPPORT DEMANDÉ qui tienne entièrement dans la photo
    /// redressée — c'est-à-dire sans mordre sur les coins blancs qu'ouvre la rotation.
    ///
    /// <b>C'est le défaut du 20/08/2026.</b> La planche de rentrée sortait avec un coin
    /// blanc en haut à droite du portrait, en biseau : six pixels de blanc en haut, plus
    /// rien à mi-hauteur. Le cadre large est calculé pour l'opérateur, qui ne le voit donc
    /// pas avant le papier ; il était borné à l'image ENTIÈRE, coins compris.
    ///
    /// Les quatre coins du cadre, ramenés dans le repère de la photo, doivent tenir dans
    /// ses bords. Pour un cadre de demi-largeur <c>a</c> et de demi-hauteur <c>a/r</c> :
    /// <c>a·(cos + sin/r) ≤ L/2</c> et <c>a·(sin + cos/r) ≤ H/2</c>. On prend le plus
    /// petit des deux — et à zéro degré, cela redonne exactement le plus grand cadre au
    /// rapport voulu dans l'image, ce qu'on faisait déjà.
    /// </summary>
    public static (double Largeur, double Hauteur) SurfaceUtile(
        double largeurPx, double hauteurPx, double rapport, double degres)
    {
        if (rapport <= 0) return (largeurPx, hauteurPx);

        var (c, s) = CosSin(degres);

        var demiLargeur = Math.Min(
            largeurPx / 2 / (c + s / rapport),
            hauteurPx / 2 / (s + c / rapport));

        return (2 * demiLargeur, 2 * demiLargeur / rapport);
    }

    /// <summary>
    /// Ramène un cadre à l'intérieur de la photo redressée, sans changer sa taille.
    ///
    /// <b>Le domaine est un rectangle PENCHÉ dans le repère de l'image redressée</b>, donc
    /// on ne peut pas y borner x et y séparément. Mais il redevient droit dans le repère de
    /// la PHOTO : là, le centre du cadre doit simplement tenir entre deux bornes sur chaque
    /// axe. On y passe, on borne, on revient — c'est exact, et non une approximation.
    /// </summary>
    private static (double X, double Y) RamenerDansLaPhoto(
        double x, double y, double largeur, double hauteur,
        double photoL, double photoH, double imgW, double imgH, double degres)
    {
        var (c, s) = CosSin(degres);
        var radians = degres * Math.PI / 180.0;
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);

        // centre du cadre, rapporté au centre de l'image redressée
        var mx = x + largeur / 2 - imgW / 2;
        var my = y + hauteur / 2 - imgH / 2;

        // ce dont le centre doit rester éloigné du bord de la photo pour qu'aucun des
        // quatre coins n'en sorte
        var margeL = largeur / 2 * c + hauteur / 2 * s;
        var margeH = largeur / 2 * s + hauteur / 2 * c;

        var bornéL = Math.Max(0, photoL / 2 - margeL);
        var bornéH = Math.Max(0, photoH / 2 - margeH);

        // dans le repère de la photo…
        var u = mx * cos + my * sin;
        var v = -mx * sin + my * cos;

        u = Math.Clamp(u, -bornéL, bornéL);
        v = Math.Clamp(v, -bornéH, bornéH);

        // …et retour
        mx = u * cos - v * sin;
        my = u * sin + v * cos;

        return (mx + imgW / 2 - largeur / 2, my + imgH / 2 - hauteur / 2);
    }

    /// <summary>Cosinus et sinus ABSOLUS de l'angle : seules les longueurs nous occupent.</summary>
    private static (double Cos, double Sin) CosSin(double degres)
    {
        var radians = Math.Abs(degres) * Math.PI / 180.0;
        return (Math.Abs(Math.Cos(radians)), Math.Abs(Math.Sin(radians)));
    }
}
