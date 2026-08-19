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

    /// <summary>
    /// La part de l'image qu'un détourage par couleur doit AU MOINS garder pour qu'on y
    /// croie, en opacité moyenne du masque (0 = tout est fond, 1 = rien n'est fond).
    ///
    /// Un sujet d'identité occupe la moitié du cadre ou davantage ; un portrait en pied,
    /// nettement moins. <b>Un dixième</b> est donc très bas exprès : on ne veut refuser que
    /// l'absurde — la photo entière prise pour du fond —, pas arbitrer un cadrage.
    /// </summary>
    private const double PartMinimaleDuSujet = 0.10;

    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Ce qu'a duré le dernier découpage réellement calculé sur ce poste, ou null tant
    /// qu'aucun ne l'a été.
    ///
    /// L'écran s'en sert pour annoncer une attente : c'est la seule opération de la photo
    /// d'identité qui se compte en secondes. Une mesure vaut mieux qu'une constante — la
    /// durée dépend de la carte du poste, du modèle installé et de la taille traitée.
    /// </summary>
    public static TimeSpan? DerniereDuree { get; private set; }

    /// <summary>
    /// Les dernières durées mesurées, pour en tirer une estimation qui ne saute pas.
    ///
    /// <b>La DERNIÈRE durée est un mauvais prédicteur</b>, et l'écran s'en servait seul :
    /// un aperçu de cadrage et une planche pleine résolution ne demandent pas le même
    /// travail, le premier passage d'une séance paie le chargement du réseau, et une photo
    /// mal contrastée traîne. La barre annonçait donc « encore 4 s » puis restait plantée,
    /// ou l'inverse — signalé depuis la boutique le 13/08/2026.
    ///
    /// Huit mesures : assez pour que la médiane ait un sens, assez peu pour suivre un
    /// changement de modèle ou de taille dans la même séance.
    /// </summary>
    private static readonly Queue<Mesure> Dernieres = new();

    private const int MesuresRetenues = 8;

    /// <summary>
    /// Une durée mesurée, AVEC la taille sur laquelle elle a été mesurée.
    ///
    /// <b>La taille manquait, et c'est ce qui faisait mentir la barre.</b> Le même écran
    /// détoure tantôt une vignette d'aperçu (1 600 px de côté, moins de 3 Mpx) tantôt la
    /// photo d'origine pour la planche (24 Mpx) : entre les deux, le réseau coûte la même
    /// chose — son entrée est figée à 1024 × 1024 — mais les deux redimensionnements qui
    /// l'encadrent, eux, suivent la taille. Une médiane qui mélangeait les deux populations
    /// n'était juste pour aucune. Signalé le 18/08/2026 : « le temps de chargement n'est
    /// toujours pas représentatif du vrai temps que ça prend ».
    /// </summary>
    private readonly record struct Mesure(TimeSpan Duree, double Megapixels);

    /// <summary>
    /// Ce que coûte une image de <paramref name="megapixels"/>, en unités arbitraires.
    ///
    /// Un coût FIXE — le réseau, toujours en 1024 × 1024 — plus une part proportionnelle à
    /// la taille : les deux redimensionnements et la lecture des pixels. Le rapport entre
    /// les deux vient des mesures de l'atelier du 03/08/2026 : environ 4 s pour un aperçu
    /// de 2 Mpx, environ 6 s pour une photo de 24 Mpx sur la même carte.
    /// </summary>
    private static double Cout(double megapixels) => 1 + Math.Max(0, megapixels) / 12.0;

    /// <summary>
    /// Durée à annoncer : la MÉDIANE des dernières mesures, ou null tant qu'aucune n'existe.
    ///
    /// La médiane et non la moyenne : un seul détourage anormalement long — le premier de
    /// la séance, celui qui charge le réseau — tirerait la moyenne vers le haut pendant
    /// tout le reste de la journée.
    /// </summary>
    public static TimeSpan? DureeTypique => DureeTypiquePour(null);

    /// <summary>
    /// Durée à annoncer pour une image de <paramref name="megapixels"/> mégapixels.
    ///
    /// <b>On ramène chaque mesure à son coût, puis on l'étend à celui qu'on attend.</b> Les
    /// mesures ne portent pas toutes sur la même taille — un aperçu de cadrage et une
    /// planche pleine résolution se suivent dans la même séance —, et la médiane brute de
    /// durées n'était donc juste pour aucune des deux. La médiane des durées RAPPORTÉES au
    /// coût, elle, est la même grandeur pour toutes : c'est la vitesse du poste.
    ///
    /// <paramref name="megapixels"/> null : on rend la durée typique telle quelle, pour les
    /// appelants qui ne savent pas encore sur quoi ils vont travailler.
    ///
    /// Null tant qu'aucune mesure n'existe — l'écran a ses ordres de grandeur pour ce cas.
    /// </summary>
    public static TimeSpan? DureeTypiquePour(double? megapixels)
    {
        lock (Dernieres)
        {
            if (Dernieres.Count == 0) return null;

            if (megapixels is not { } voulus)
            {
                var brutes = Dernieres.Select(m => m.Duree).OrderBy(d => d).ToList();
                return brutes[brutes.Count / 2];
            }

            // secondes par unité de coût, sur chaque mesure — puis la médiane
            var vitesses = Dernieres
                .Select(m => m.Duree.TotalSeconds / Cout(m.Megapixels))
                .OrderBy(v => v)
                .ToList();

            return TimeSpan.FromSeconds(vitesses[vitesses.Count / 2] * Cout(voulus));
        }
    }

    /// <summary>
    /// Le masque du sujet, par le meilleur moyen dont ce poste dispose.
    ///
    /// <b>C'est LE point de passage du détourage</b>, celui que partagent le fond blanc et
    /// la correction du sujet. Le réseau d'abord quand il est allumé — il tient les mèches
    /// de cheveux —, la méthode par couleur ensuite, qui marche toujours et en une seconde.
    /// Les deux appelants écrivaient la même ligne chacun de leur côté ; ils la partagent
    /// désormais, ce qui donne aussi un seul endroit où mesurer le temps que ça prend.
    ///
    /// Null quand rien n'a pu être découpé : l'appelant renonce alors plutôt que d'abîmer
    /// la photo sur un masque inventé.
    /// </summary>
    internal static ImageMagick.MagickImage? DecouperLeSujet(ImageMagick.MagickImage image)
    {
        // Relevé AVANT l'appel : c'est ce qui permet de savoir si CE détourage-ci a payé le
        // chargement du réseau. Après coup, la session est prête dans les deux cas.
        var reseauDejaCharge = BiRefNetMatting.SessionPrete;

        var chrono = System.Diagnostics.Stopwatch.StartNew();

        var masque = BiRefNetMatting.CalculerMasque(image) ?? CalculerMasqueSujet(image);
        if (masque is null) return null;

        DerniereDuree = chrono.Elapsed;

        // ⚠ LE CHARGEMENT DU RÉSEAU N'EST PAS UN DÉTOURAGE. Le premier appel de la séance
        // le paie — plusieurs secondes pour poser le modèle sur la carte —, et le retenir
        // ici gonflerait toutes les estimations suivantes d'un temps qui ne se repaiera
        // jamais. Il est mesuré à part, et l'écran l'ajoute tant que la session n'est pas
        // prête (voir BiRefNetMatting.DureeDuChargement).
        var duree = chrono.Elapsed;
        if (!reseauDejaCharge
            && BiRefNetMatting.DureeDuChargement is { } chargement
            && duree > chargement)
            duree -= chargement;

        lock (Dernieres)
        {
            Dernieres.Enqueue(new Mesure(duree, image.Width * (double)image.Height / 1_000_000.0));
            while (Dernieres.Count > MesuresRetenues) Dernieres.Dequeue();
        }

        return masque;
    }

    /// <summary>
    /// Le gris clair des photos d'identité : 210 sur les trois canaux, soit 82 % de blanc.
    ///
    /// <b>Pourquoi un gris et pas du blanc.</b> Les textes demandent un fond « uni, de
    /// couleur claire » — gris clair ou bleu clair — et le blanc franc y est mal vu : une
    /// chemise blanche, des cheveux blancs ou une peau très claire s'y fondent, et la
    /// silhouette ne se détache plus. Le gris règle cela sans assombrir le tirage.
    ///
    /// 210 est la valeur des labos. Elle se distingue du blanc à l'œil tout en restant
    /// franchement claire, là où un gris plus soutenu paraît sale sur un tirage déjà dense.
    /// </summary>
    public static readonly ImageMagick.MagickColor GrisIdentite =
        ImageMagick.MagickColor.FromRgb(210, 210, 210);

    /// <summary>
    /// Détoure le sujet et pose du blanc derrière lui. L'image est modifiée sur place ;
    /// elle est laissée intacte si le fond n'est pas reconnaissable.
    /// </summary>
    /// <param name="image">Image à traiter, déjà redressée (EXIF appliqué).</param>
    /// <returns>Vrai si un fond blanc a été posé.</returns>
    public static bool PoserUnFondBlanc(ImageMagick.MagickImage image) =>
        PoserUnFond(image, ImageMagick.MagickColors.White);

    /// <summary>
    /// Détoure le sujet et pose la couleur demandée derrière lui.
    ///
    /// Le détourage est le même quelle que soit la couleur : seul l'aplat final change.
    /// C'est pourquoi le gris n'a rien coûté à ajouter — tout le travail difficile, celui
    /// qui distingue une mèche de cheveux d'un fond de studio, était déjà fait.
    /// </summary>
    /// <param name="image">Image à traiter, déjà redressée (EXIF appliqué).</param>
    /// <param name="fond">Couleur à poser derrière le sujet.</param>
    /// <param name="cle">
    /// De quelle photo il s'agit, quand l'appelant le sait — voir <see cref="MasqueSujet.Nu"/>.
    ///
    /// <b>Sans elle, le fond repayait le réseau à CHAQUE rendu.</b> Ce découpage partait en
    /// direct, hors de toute mémoire : l'aperçu, le récapitulatif, l'impression et le
    /// courriel détouraient la même photo chacun de leur côté. Avec la clé, le premier
    /// paie et les suivants reprennent son masque, remis à l'échelle en quelques
    /// millisecondes — BiRefNet travaille de toute façon sur une entrée figée à 1024×1024,
    /// le second passage n'apportait donc rien.
    ///
    /// Null retombe sur l'empreinte des pixels : sûr, mais elle distingue les tailles, donc
    /// l'aperçu et la planche ne se partagent rien.
    /// </param>
    /// <returns>Vrai si un fond a été posé.</returns>
    public static bool PoserUnFond(
        ImageMagick.MagickImage image, ImageMagick.MagickColor fond, string? cle = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(fond);

        var masque = MasqueSujet.Nu(image, cle);
        if (masque is null) return false;

        using (masque) return PoserUnFond(image, fond, masque);
    }

    /// <summary>
    /// Pose le fond en se servant d'un masque QUE L'APPELANT FOURNIT, sans passer par le
    /// cache.
    ///
    /// <b>Pour le masque que le pipeline a transporté à travers la géométrie.</b> La surcharge
    /// à clé ci-dessus demande au cache le masque « de ce fichier », qui est celui de la photo
    /// ENTIÈRE ; quand l'image a été recadrée depuis, il ne lui va plus. Le pipeline, lui, sait
    /// quel masque va avec son image — il lui a fait subir le même recadrage et la même mise à
    /// l'échelle. Voir <c>ImagePipeline.RenderInto</c>.
    ///
    /// Le masque n'est PAS libéré : il appartient à l'appelant, qui peut encore en avoir besoin
    /// pour la correction du sujet.
    /// </summary>
    /// <returns>Vrai si un fond a été posé.</returns>
    public static bool PoserUnFond(
        ImageMagick.MagickImage image, ImageMagick.MagickColor fond, ImageMagick.MagickImage masque)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(fond);
        ArgumentNullException.ThrowIfNull(masque);

        // Le masque devient la transparence de la photo, puis on aplatit sur la couleur
        // demandée. « Alpha(Remove) » compose sur BackgroundColor sans seconde image ni
        // encodage intermédiaire : sur un 4000 × 6016, l'aller-retour PNG coûtait plus
        // cher que tout le reste du détourage réuni.
        image.Composite(masque, ImageMagick.CompositeOperator.CopyAlpha);
        image.BackgroundColor = fond;
        image.Alpha(ImageMagick.AlphaOption.Remove);

        return true;
    }

    /// <summary>
    /// Masque d'opacité du sujet, à la taille de l'image. Null si le fond n'est pas
    /// assez uni pour être reconnu — on préfère ne rien faire qu'abîmer la photo.
    ///
    /// Ouvert au reste du module pour servir de repli à <see cref="MasqueSujet"/> : la
    /// correction du sujet a besoin de la même découpe, et sans ce repli elle ne faisait
    /// RIEN dès que le réseau était éteint dans les réglages — ce qu'il est par défaut.
    /// </summary>
    internal static ImageMagick.MagickImage? CalculerMasqueSujet(ImageMagick.MagickImage image)
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

        // ⚠ UN MASQUE QUI NE GARDE PRESQUE RIEN N'EST PAS UN DÉTOURAGE.
        //
        // Arcueil, 14/08/2026 : « le fond blanc fait blanc sur toute la photo ». Le modèle
        // de détourage n'était pas installé sur ce poste, on se rabattait donc sur cette
        // découpe par COULEUR — et sur une photo dont le fond n'est pas uni, elle prend
        // toute l'image pour du fond. Le client repart avec un rectangle blanc.
        //
        // Rien ne le rattrapait ensuite : PoserUnFond fait confiance au masque qu'on lui
        // rend. On préfère NE RIEN FAIRE — c'est déjà la règle affichée de cette méthode,
        // « on préfère ne rien faire qu'abîmer la photo », elle manquait simplement de ce
        // dernier contrôle.
        var garde = Cv2.Mean(alpha).Val0;
        if (garde < PartMinimaleDuSujet)
        {
            Log?.Invoke(
                $"Détourage par couleur abandonné : il ne resterait que {garde * 100:0.#} % de " +
                "l'image, le fond n'est pas assez uni pour être reconnu. La photo est laissée " +
                "telle quelle — installez le modèle de détourage dans Paramètres pour ces photos-là.");
            return null;
        }

        using var octets8 = new Mat();
        alpha.ConvertTo(octets8, MatType.CV_8UC1, 255.0);

        Cv2.ImEncode(".png", octets8, out var octets);
        var resultat = new ImageMagick.MagickImage(octets);

        // adouci À LA TAILLE DU MASQUE, jamais en pleine résolution : le flou d'un masque
        // 4000 × 6016 coûtait 28 s ici (Magick.NET est mono-fil sur ce poste, mesuré le
        // 03/08/2026). L'agrandissement qui suit lisse encore.
        resultat.Blur(0, Math.Max(1.0, TailleMasque * AdoucissementRelatif));

        // AU RAPPORT DE LA PHOTO, PAS À SA TAILLE — la même règle que pour le réseau, et
        // pour la même raison : ce masque a été calculé sur 900 px de côté, l'étirer à
        // 24 Mpx ici ne fait qu'ajouter de l'interpolation à encoder, à ranger et à
        // repromener. Voir MasqueSujet.TailleDeCalcul.
        var (largeur, hauteur) = MasqueSujet.TailleDeCalcul(image.Width, image.Height);
        resultat.Resize(new ImageMagick.MagickGeometry(largeur, hauteur)
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
