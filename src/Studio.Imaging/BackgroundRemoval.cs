using OpenCvSharp;


namespace Studio.Imaging;

/// <summary>
/// Remplace le fond d'une photo d'identité par du blanc franc.
///
/// Les normes exigent un fond clair et uni. Le fond de studio ne l'est jamais tout à fait :
/// il grisonne (mesuré à 203,210,207 sur les tirages du 03/08/2026), il porte l'ombre du
/// sujet, et il change de teinte selon l'éclairage.
///
/// PRINCIPE : on ne cherche PAS à deviner le sujet, on reconnaît le FOND. Sa couleur est
/// relevée sur le pourtour de l'image, là où il n'y a jamais personne ; est effacé ce qui
/// lui ressemble ET qui communique avec le bord du cadre. Cette seconde condition fait
/// tout le travail : une chemise claire, un reflet dans les cheveux ou le blanc d'un œil
/// ont la couleur du fond sans en être, mais ils sont enfermés dans le sujet.
///
/// La prudence est le cœur du procédé — deux approches ont été écartées le 03/08/2026 :
/// GrabCut amorcé sur la tête mangeait les cheveux clairs et laissait un halo sous le
/// menton ; des zones de protection géométriques (ellipse sur la tête, rectangle sur le
/// buste) préservaient bien le sujet mais gardaient aussi le fond qu'elles couvraient, et
/// ces formes grises se voyaient sur le blanc. Une photo au crâne rogné se fait refuser
/// au guichet, alors qu'un fond resté un peu gris ne gêne personne : dans le doute, on garde.
/// </summary>
public static class BackgroundRemoval
{
    /// <summary>
    /// Côté maximal du masque calculé. Le calcul se fait petit puis se remet à l'échelle :
    /// travailler en pleine résolution coûtait des dizaines de secondes sur un 24 Mpx,
    /// pour un masque qu'on adoucit de toute façon.
    /// </summary>
    private const int TailleMasque = 900;

    /// <summary>Épaisseur du pourtour où l'on relève la couleur du fond, en fraction du côté.</summary>
    private const double BandeDeReference = 0.06;

    /// <summary>
    /// Écarts de couleur, en multiples de la dispersion du fond, entre « c'est le fond »
    /// et « c'est le sujet ». La zone intermédiaire donne une transparence progressive,
    /// qui préserve les mèches de cheveux là où un seuil net les hacherait.
    /// </summary>
    private const double SeuilFond = 2.5;
    private const double SeuilSujet = 7.0;

    /// <summary>Adoucissement final du masque, en fraction de sa taille.</summary>
    private const double AdoucissementRelatif = 0.004;

    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Détoure le sujet et pose du blanc derrière lui. L'image est modifiée sur place ;
    /// elle est laissée intacte si le fond n'est pas reconnaissable.
    /// </summary>
    /// <param name="image">Image à traiter, déjà redressée (EXIF appliqué).</param>
    /// <returns>Vrai si un fond blanc a été posé.</returns>
    public static bool PoserUnFondBlanc(ImageMagick.MagickImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        // Le réseau d'abord quand il est installé : il tient les mèches de cheveux là où
        // la règle de couleur ci-dessous laisse un halo. Il rend null dès que quelque
        // chose manque (modèle absent, carte indisponible, échec), et on retombe alors
        // sur la méthode par couleur — qui, elle, marche toujours et en une seconde.
        var masque = BiRefNetMatting.CalculerMasque(image) ?? CalculerMasqueSujet(image);
        if (masque is null) return false;

        using (masque)
        {
            // Le masque devient la transparence de la photo, puis on aplatit sur du blanc.
            // « Alpha(Remove) » compose sur BackgroundColor sans seconde image ni encodage
            // intermédiaire : sur un 4000 × 6016, l'aller-retour PNG coûtait plus cher que
            // tout le reste du détourage réuni.
            image.Composite(masque, ImageMagick.CompositeOperator.CopyAlpha);
            image.BackgroundColor = ImageMagick.MagickColors.White;
            image.Alpha(ImageMagick.AlphaOption.Remove);
        }

        return true;
    }

    /// <summary>
    /// Masque d'opacité du sujet, à la taille de l'image. Null si le fond n'est pas
    /// assez uni pour être reconnu — on préfère ne rien faire qu'abîmer la photo.
    /// </summary>
    private static ImageMagick.MagickImage? CalculerMasqueSujet(ImageMagick.MagickImage image)
    {
        if (image.Width < 60 || image.Height < 60) return null;

        var echelle = Math.Min(1.0, (double)TailleMasque / Math.Max(image.Width, image.Height));
        var w = Math.Max(30, (int)Math.Round(image.Width * echelle));
        var h = Math.Max(30, (int)Math.Round(image.Height * echelle));

        using var reduite = (ImageMagick.MagickImage)image.Clone();
        reduite.Resize(new ImageMagick.MagickGeometry((uint)w, (uint)h) { IgnoreAspectRatio = true });

        using var couleur = Cv2.ImDecode(
            reduite.ToByteArray(ImageMagick.MagickFormat.Png), ImreadModes.Color);
        if (couleur.Empty()) return null;

        // Lab : les écarts y correspondent à ce que l'œil perçoit, là où le RVB déclare
        // proches deux teintes que tout le monde distingue
        using var lab = new Mat();
        Cv2.CvtColor(couleur, lab, ColorConversionCodes.BGR2Lab);
        using var labF = new Mat();
        lab.ConvertTo(labF, MatType.CV_32FC3);

        if (!MesurerLeFond(labF, w, h, out var fond, out var dispersion)) return null;

        // distance au fond, pixel par pixel
        using var ecart = new Mat(h, w, MatType.CV_32FC1);
        using var diff = new Mat();
        Cv2.Subtract(labF, fond, diff);
        using var carres = new Mat();
        Cv2.Multiply(diff, diff, carres);

        var canaux = Cv2.Split(carres);
        try
        {
            Cv2.Add(canaux[0], canaux[1], ecart);
            Cv2.Add(ecart, canaux[2], ecart);
            Cv2.Sqrt(ecart, ecart);
        }
        finally
        {
            foreach (var c in canaux) c.Dispose();
        }

        // transparence progressive entre « fond » et « sujet »
        var bas = Math.Max(1.0, dispersion * SeuilFond);
        var haut = Math.Max(bas + 1.0, dispersion * SeuilSujet);

        using var alpha = new Mat();
        Cv2.Subtract(ecart, new Scalar(bas), alpha);
        Cv2.Divide(alpha, new Scalar(haut - bas), alpha);
        Cv2.Threshold(alpha, alpha, 1.0, 1.0, ThresholdTypes.Trunc);
        Cv2.Threshold(alpha, alpha, 0.0, 0.0, ThresholdTypes.Tozero);

        RestreindreAuFondConnecte(alpha, w, h);

        using var octets8 = new Mat();
        alpha.ConvertTo(octets8, MatType.CV_8UC1, 255.0);

        Cv2.ImEncode(".png", octets8, out var octets);
        var resultat = new ImageMagick.MagickImage(octets);

        // adouci À LA TAILLE DU MASQUE, jamais en pleine résolution : le flou d'un masque
        // 4000 × 6016 coûtait 28 s ici (Magick.NET est mono-fil sur ce poste, mesuré le
        // 03/08/2026). L'agrandissement qui suit lisse encore.
        resultat.Blur(0, Math.Max(1.0, TailleMasque * AdoucissementRelatif));
        resultat.Resize(new ImageMagick.MagickGeometry(image.Width, image.Height)
            { IgnoreAspectRatio = true });

        return resultat;
    }

    /// <summary>
    /// Relève la couleur du fond sur le pourtour, et sa dispersion.
    ///
    /// Les bords gauche, droit et haut seulement : en bas, ce sont les épaules. On refuse
    /// de continuer si le pourtour n'est pas uni — c'est qu'on n'est pas devant une photo
    /// de studio, et effacer « ce qui ressemble au fond » y ferait n'importe quoi.
    /// </summary>
    private static bool MesurerLeFond(Mat labF, int w, int h, out Scalar fond, out double dispersion)
    {
        fond = default;
        dispersion = 0;

        var bande = Math.Max(2, (int)(Math.Min(w, h) * BandeDeReference));

        // Les montants gauche et droit ne sont relevés que sur la MOITIÉ HAUTE : plus bas,
        // les épaules touchent les bords du cadre. Les y inclure faisait entrer le vêtement
        // dans la mesure, la dispersion dépassait le seuil, et le fond était déclaré non
        // uniforme — l'outil ne faisait alors rien du tout, sans le dire (03/08/2026).
        var hautSeul = Math.Max(bande + 1, h / 2);

        using var zone = new Mat(h, w, MatType.CV_8UC1, Scalar.All(0));
        zone.RowRange(0, bande).SetTo(Scalar.All(255));
        zone.SubMat(new Rect(0, 0, bande, hautSeul)).SetTo(Scalar.All(255));
        zone.SubMat(new Rect(w - bande, 0, bande, hautSeul)).SetTo(Scalar.All(255));

        Cv2.MeanStdDev(labF, out var moyenne, out var ecartType, zone);

        fond = moyenne;
        dispersion = Math.Sqrt(
            ecartType.Val0 * ecartType.Val0 +
            ecartType.Val1 * ecartType.Val1 +
            ecartType.Val2 * ecartType.Val2);

        // un pourtour trop bariolé n'est pas un fond de studio
        const double DispersionMax = 18.0;

        var uni = dispersion <= DispersionMax;
        Log?.Invoke($"Fond blanc : dispersion du pourtour {dispersion:0.0} " +
                    $"(seuil {DispersionMax:0.0}) — {(uni ? "fond retenu" : "jugé non uniforme, photo laissée intacte")}");

        return uni;
    }

    /// <summary>
    /// N'efface que le fond qui COMMUNIQUE AVEC LE BORD de l'image ; tout le reste
    /// redevient opaque.
    ///
    /// Ressembler au fond ne suffit pas à en être : une chemise blanche, un reflet clair
    /// dans les cheveux ou le blanc d'un œil ont la couleur du fond sans en faire partie.
    /// Le vrai fond, lui, entoure le sujet et touche forcément le cadre.
    ///
    /// C'est ce qui remplace les zones de protection géométriques du premier essai
    /// (03/08/2026) : une ellipse autour de la tête et un rectangle sur le buste
    /// protégeaient bien le sujet, mais conservaient aussi le fond qu'ils recouvraient —
    /// ces formes grises se voyaient comme le nez au milieu de la figure sur le blanc.
    /// La connexité protège autant, sans rien laisser paraître.
    /// </summary>
    private static void RestreindreAuFondConnecte(Mat alpha, int w, int h)
    {
        // « franchement transparent » = candidat fond
        using var candidats = new Mat();
        Cv2.Threshold(alpha, candidats, 0.5, 255, ThresholdTypes.BinaryInv);
        using var candidats8 = new Mat();
        candidats.ConvertTo(candidats8, MatType.CV_8UC1);

        // On propage depuis le pourtour : chaque région candidate qui touche le bord est
        // repeinte, les autres restent telles quelles. Un remplissage couvre toute une
        // région d'un coup, donc les amorces suivantes tombent déjà repeintes et ne
        // coûtent rien.
        const byte MarqueFond = 128;

        using var travail = candidats8.Clone();

        void Amorcer(int x, int y)
        {
            if (travail.At<byte>(y, x) != 255) return;
            Cv2.FloodFill(travail, new Point(x, y), new Scalar(MarqueFond));
        }

        for (var x = 0; x < w; x++)
        {
            Amorcer(x, 0);
            Amorcer(x, h - 1);
        }
        for (var y = 0; y < h; y++)
        {
            Amorcer(0, y);
            Amorcer(w - 1, y);
        }

        // fond retenu = ce qui a été atteint depuis le bord ; hors de lui, tout redevient opaque
        using var fondRetenu = new Mat();
        Cv2.InRange(travail, new Scalar(MarqueFond), new Scalar(MarqueFond), fondRetenu);

        using var horsFond = new Mat();
        Cv2.BitwiseNot(fondRetenu, horsFond);
        alpha.SetTo(Scalar.All(1.0), horsFond);
    }
}
