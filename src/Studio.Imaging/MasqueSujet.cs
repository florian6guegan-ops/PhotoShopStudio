using ImageMagick;
using Studio.Core.Domain;

namespace Studio.Imaging;

/// <summary>
/// Le masque du sujet : blanc là où l'on corrige, noir sur le fond qu'on épargne.
///
/// <b>Pourquoi une classe à part.</b> Le détourage lui-même vit déjà dans
/// <see cref="BiRefNetMatting"/>, et <see cref="BackgroundRemoval"/> s'en sert pour poser
/// les fonds blanc et gris. Ce qu'il manquait est tout autour : régler le contour,
/// adoucir la transition, et surtout NE PAS RECALCULER le réseau à chaque mouvement de
/// curseur. Un masque coûte plusieurs centaines de millisecondes ; un panneau de huit
/// réglages en demanderait un par frappe.
/// </summary>
public static class MasqueSujet
{
    /// <summary>Journal optionnel, partagé avec le reste du détourage.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Ce qu'a duré le dernier détourage sur ce poste — celui du sujet comme celui d'un
    /// fond blanc, c'est le même travail et il est mesuré au même endroit.
    /// </summary>
    public static TimeSpan? DerniereDuree => BackgroundRemoval.DerniereDuree;

    /// <summary>
    /// Durée à ANNONCER pour un détourage à venir : la médiane des dernières mesures.
    /// Voir <see cref="BackgroundRemoval.DureeTypique"/> — c'est elle que la barre d'attente
    /// doit suivre, et non la dernière mesure.
    /// </summary>
    public static TimeSpan? DureeTypique => BackgroundRemoval.DureeTypique;

    /// <summary>
    /// Le masque de cette image est-il déjà en mémoire ? Autrement dit : y a-t-il quelque
    /// chose à attendre ?
    ///
    /// L'écran s'en sert pour ne montrer sa barre d'attente que lorsqu'il y a vraiment une
    /// attente. Elle apparaîtrait sinon à chaque mouvement de curseur, pour cent
    /// millisecondes, et ce clignotement serait pire que pas de barre du tout.
    /// </summary>
    /// <param name="cle">La même que celle passée à <see cref="Calculer"/>.</param>
    /// <param name="largeur">Ignorés depuis le 12/08/2026 — voir ci-dessous.</param>
    /// <param name="hauteur">Ignorés depuis le 12/08/2026 — voir ci-dessous.</param>
    /// <remarks>
    /// <b>Les dimensions ne comptent plus.</b> Un masque calculé pour l'aperçu du cadrage
    /// sert aussi la planche pleine résolution, à un redimensionnement près — voir
    /// <see cref="Empreinte"/>. Les demander encore ferait afficher une barre d'attente au
    /// récapitulatif alors qu'il n'y a plus rien à attendre, ce qui est exactement le
    /// clignotement que cette méthode existe pour éviter.
    ///
    /// Elles restent dans la signature : les appelants les ont sous la main, et les retirer
    /// obligerait à toucher des écrans qui n'ont rien à voir avec ce changement.
    /// </remarks>
    public static bool DejaEnMemoire(string cle, uint largeur, uint hauteur)
    {
        ArgumentException.ThrowIfNullOrEmpty(cle);

        lock (Verrou)
            return Connus.ContainsKey(cle);
    }

    /// <summary>
    /// Masques déjà calculés, par empreinte de l'image d'origine.
    ///
    /// <b>Quatre entrées suffisent</b> : l'aperçu travaille sur UNE photo à la fois, et le
    /// tirage traverse les siennes une par une. La marge sert au va-et-vient entre les
    /// quelques poses d'une planche d'identité, où l'opérateur compare avant de choisir.
    /// </summary>
    private const int MemoireMaximale = 4;

    private static readonly object Verrou = new();

    /// <summary>
    /// Le calcul lui-même : un seul à la fois, et le suivant retrouve le résultat du
    /// premier dans la mémoire au lieu de le refaire.
    ///
    /// <b>Sans lui, la mémoire ci-dessus ne servait à rien pendant qu'on bouge un curseur</b>
    /// — le cas pour lequel elle a été écrite. Un glissement de souris produit des dizaines
    /// d'événements ; chacun lançait son calcul, et tous partaient AVANT que le premier
    /// n'ait rangé son résultat. Trente détourages menaient donc de front une opération qui
    /// en demande un seul, chacun réclamant sa place sur une carte graphique de 4 Go.
    /// </summary>
    private static readonly object VerrouCalcul = new();

    private static readonly Dictionary<string, byte[]> Connus = new(StringComparer.Ordinal);
    private static readonly List<string> Ordre = [];

    /// <summary>
    /// Le dernier masque RETOUCHÉ — contour élargi et bord adouci — avec la clé qui l'a
    /// produit.
    ///
    /// <b>Le masque nu ne suffisait pas.</b> Le mémoriser épargnait le réseau, mais chaque
    /// mouvement de curseur repayait tout de même le décodage du masque, la dilatation et
    /// le flou : 360 ms mesurés sur une vignette d'aperçu (11/08/2026), sur les 950 ms que
    /// coûtait un curseur. Or ces trois-là ne dépendent QUE du contour et de
    /// l'adoucissement, deux réglages que personne ne touche pendant qu'il règle une
    /// exposition.
    ///
    /// <b>Un seul masque gardé ne suffisait pas non plus.</b> « On règle une photo à la
    /// fois » est vrai d'un curseur, faux d'une planche : l'opérateur fait l'aller-retour
    /// entre les quelques poses avant de choisir — c'est exactement pour cela que
    /// <see cref="MemoireMaximale"/> en garde quatre côté masque NU. Côté retouché, revenir
    /// à la pose précédente jetait le seul emplacement, et les curseurs y repayaient les
    /// 360 ms. D'où « les curseurs sont de nouveau lents PARFOIS », signalé à Créteil le
    /// 12/08/2026 : parfois, c'est-à-dire chaque fois qu'on change de photo.
    ///
    /// Autant d'emplacements que de masques nus, donc — les deux mémoires suivent le même
    /// va-et-vient. Rendus par copie : celui d'ici ne doit pas mourir sous l'appelant.
    /// </summary>
    private static readonly Dictionary<string, MagickImage> Retouches = new(StringComparer.Ordinal);
    private static readonly List<string> OrdreRetouches = [];

    /// <summary>
    /// Vide la mémoire des masques. Sert quand le modèle de détourage change dans les
    /// réglages : les masques déjà calculés viennent de l'ancien.
    /// </summary>
    public static void Oublier()
    {
        lock (Verrou)
        {
            Connus.Clear();
            Ordre.Clear();

            foreach (var masque in Retouches.Values) masque.Dispose();
            Retouches.Clear();
            OrdreRetouches.Clear();
        }
    }

    /// <summary>
    /// Le masque du sujet de cette image, à sa taille, prêt à servir de couche alpha.
    /// <c>null</c> quand le détourage n'a rien pu dire — et l'appelant renonce alors à la
    /// correction plutôt que d'en inventer une sur un masque approximatif.
    ///
    /// <b>À appeler sur l'image D'ORIGINE, avant toute correction.</b> L'empreinte qui sert
    /// de clé est celle des pixels : corriger l'image avant de demander son masque le
    /// ferait recalculer à chaque mouvement de curseur, ce qui est précisément ce que cette
    /// classe existe pour éviter.
    /// </summary>
    /// <summary>
    /// Le grand côté auquel les réglages de contour s'entendent.
    ///
    /// <b>C'est la taille de l'aperçu de l'écran d'identité</b>, et c'est ce qui rend le
    /// tirage fidèle à ce que l'opérateur a vu : les mêmes « +2 px » appliqués tels quels à
    /// un original de 6000 px ne dilateraient qu'un tiers de ce qu'ils dilataient sur la
    /// vignette, et le liseré qu'il venait de faire disparaître reviendrait sur le papier.
    /// Les valeurs sont donc mises à l'échelle de l'image réellement traitée.
    /// </summary>
    private const double GrandCoteDeReference = 1600;

    /// <param name="contourPx">
    /// Élargit (positif) ou resserre (négatif) le sujet, en pixels rapportés à
    /// <see cref="GrandCoteDeReference"/>.
    /// </param>
    /// <param name="adoucissementPx">Rayon du fondu, à la même échelle.</param>
    /// <param name="cle">
    /// De quelle photo il s'agit, quand l'appelant le sait. Null pour laisser l'empreinte
    /// des pixels s'en charger — sûr, mais bien plus lent.
    /// </param>
    public static MagickImage? Calculer(
        MagickImage image, double contourPx, double adoucissementPx, string? cle = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        var empreinte = Empreinte(image, cle);

        // L'image, SA TAILLE, et la découpe qu'on lui demande.
        //
        // La taille a disparu de `empreinte` — le masque nu n'en dépend pas — mais elle doit
        // rester ICI : ce cache-là garde un masque DÉJÀ RETOUCHÉ et déjà à l'échelle, et le
        // rendre à une autre taille sortirait un masque aux mauvaises dimensions. Deux
        // contours différents sur la même photo ne donnent pas le même masque non plus.
        var cleRetouchee = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{empreinte}|{image.Width}x{image.Height}|{contourPx:R}|{adoucissementPx:R}");

        // Le masque tout prêt, quand ni le contour ni l'adoucissement n'ont bougé : c'est
        // le cas de TOUS les autres curseurs du panneau.
        lock (Verrou)
            if (Retouches.TryGetValue(cleRetouchee, out var pret))
            {
                // le plus récemment servi repasse en queue, comme pour les masques nus
                OrdreRetouches.Remove(cleRetouchee);
                OrdreRetouches.Add(cleRetouchee);
                return (MagickImage)pret.Clone();
            }

        var masque = MasqueALaTaille(image, empreinte);
        if (masque is null) return null;

        try
        {
            var echelle = Math.Max(image.Width, image.Height) / GrandCoteDeReference;
            Retoucher(masque, contourPx * echelle, adoucissementPx * echelle);
        }
        catch
        {
            masque.Dispose();
            throw;
        }

        lock (Verrou)
        {
            if (Retouches.TryGetValue(cleRetouchee, out var ancien)) ancien.Dispose();

            Retouches[cleRetouchee] = (MagickImage)masque.Clone();
            OrdreRetouches.Remove(cleRetouchee);
            OrdreRetouches.Add(cleRetouchee);

            // Ces masques pèsent plusieurs mégaoctets : on en garde autant que de masques
            // nus, pas davantage.
            while (OrdreRetouches.Count > MemoireMaximale)
            {
                if (Retouches.Remove(OrdreRetouches[0], out var vieux)) vieux.Dispose();
                OrdreRetouches.RemoveAt(0);
            }
        }

        return masque;
    }

    /// <summary>
    /// La clé d'une image : celle que l'appelant donne, ou à défaut l'empreinte de ses
    /// PIXELS.
    ///
    /// <b>L'empreinte n'est pas gratuite</b> : ImageMagick relit toute l'image pour la
    /// calculer — 176 ms sur une vignette d'aperçu (11/08/2026), plus que la correction
    /// qu'on venait demander. Elle reste le recours quand personne ne sait nommer l'image,
    /// et elle a le mérite de ne jamais confondre deux photos différentes.
    ///
    /// <b>LA TAILLE NE FAIT PLUS PARTIE DE LA CLÉ quand l'appelant nomme la photo</b>, et
    /// c'est tout le sujet. Elle en faisait partie, et le récapitulatif des planches — rendu
    /// à la taille d'impression, donc sous une autre clé que l'aperçu du cadrage — refaisait
    /// tourner le réseau pour rien : <b>14,5 secondes par photo</b> relevées à Créteil le
    /// 12/08/2026, et c'est ce SECOND passage qui mettait la carte graphique à genoux.
    ///
    /// Or le masque ne dépend pas de la taille demandée : BiRefNet travaille sur une entrée
    /// FIGÉE à 1024 × 1024 et ne remet à l'échelle qu'à la toute fin (voir
    /// <c>BiRefNetMatting.EnMasque</c>). Deux tailles de sortie donnent donc le même masque
    /// à un redimensionnement près — et redimensionner coûte des millisecondes là où le
    /// réseau coûte des secondes.
    ///
    /// Sans clé fournie, on retombe sur la signature des pixels, qui distingue d'elle-même
    /// deux tailles de la même photo : le comportement d'avant, et il reste juste.
    /// </summary>
    private static string Empreinte(MagickImage image, string? cle) =>
        cle ?? $"{image.Signature}|{image.Width}x{image.Height}";

    /// <summary>
    /// Le masque nu rendu par le réseau, mémorisé. Les octets d'un PNG plutôt que l'image :
    /// une <see cref="MagickImage"/> gardée dans un dictionnaire serait à la merci du
    /// premier appelant qui la libère.
    /// </summary>
    /// <summary>
    /// Le masque du sujet TEL QUEL, sans contour élargi ni bord adouci — mais MÉMORISÉ.
    ///
    /// <b>C'est ce qui manquait au fond.</b> <c>BackgroundRemoval.PoserUnFond</c> appelait
    /// le découpage en direct, hors de toute mémoire : chaque rendu qui pose un fond blanc
    /// ou gris repayait un passage complet du réseau. L'aperçu du cadrage, le récapitulatif
    /// de la planche, l'impression et le courriel faisaient donc QUATRE détourages de la
    /// même photo, et un lot de quatre poses en faisait seize.
    ///
    /// La correction du 12/08/2026 — sortir la taille de la clé — n'avait réglé que la
    /// correction du SUJET, seule à passer par <see cref="Calculer"/>. Le fond, lui, est
    /// resté au plein tarif jusqu'au 13/08/2026.
    ///
    /// Rendu NU et non retouché, pour que le résultat soit au pixel près celui d'avant :
    /// <see cref="Calculer"/> ajoute toujours un fondu d'au moins un demi-pixel, ce qui
    /// aurait changé tous les fonds déjà tirés.
    /// </summary>
    /// <param name="cle">
    /// De quelle photo il s'agit. Null pour laisser l'empreinte des pixels s'en charger —
    /// sûr, mais elle relit toute l'image (176 ms) et distingue les tailles entre elles,
    /// donc l'aperçu et la planche ne se partageraient rien.
    /// </param>
    public static MagickImage? Nu(MagickImage image, string? cle = null)
    {
        ArgumentNullException.ThrowIfNull(image);

        return MasqueALaTaille(image, Empreinte(image, cle));
    }

    /// <summary>
    /// Le masque mémorisé, décodé UNE fois et remis à la taille de l'image.
    ///
    /// <b>L'aller-retour PNG coûtait plus cher que le redimensionnement.</b> La mémoire
    /// range des octets PNG ; les rendre à la bonne taille demandait de les décoder, de
    /// redimensionner, de RÉENCODER en PNG — puis l'appelant décodait une troisième fois.
    /// Sur un masque de 1800 × 2400, ces deux encodages de trop pesaient l'essentiel du
    /// temps d'une reprise en mémoire : 1,85 s mesurées le 13/08/2026, là où le seul
    /// redimensionnement se compte en dizaines de millisecondes.
    /// </summary>
    private static MagickImage? MasqueALaTaille(MagickImage image, string empreinte)
    {
        var octets = Brut(image, empreinte);
        if (octets is null) return null;

        var masque = new MagickImage(octets);
        if (masque.Width != image.Width || masque.Height != image.Height)
            masque.Resize(new MagickGeometry(image.Width, image.Height) { IgnoreAspectRatio = true });

        return masque;
    }

    /// <summary>
    /// Les octets du masque, tels qu'ils ont été rangés — À LA TAILLE OÙ ILS ONT ÉTÉ
    /// CALCULÉS, et non à celle de l'image demandée. C'est à l'appelant de les remettre à
    /// l'échelle, ce que fait <see cref="MasqueALaTaille"/> sans repasser par un PNG.
    /// </summary>
    private static byte[]? Brut(MagickImage image, string empreinte)
    {
        if (Deja(empreinte) is { } connu) return connu;

        // Un seul calcul à la fois — et on redemande la mémoire une fois le tour venu :
        // pendant l'attente, celui qui passait devant a très probablement rangé LE masque
        // qu'on s'apprêtait à recalculer.
        lock (VerrouCalcul)
        {
            if (Deja(empreinte) is { } entretemps) return entretemps;

            // Le réseau d'abord quand il est allumé, la méthode par couleur ensuite —
            // le même ordre que pour poser un fond, et pour la même raison : la seconde
            // marche toujours, en une seconde, et une photo d'identité a précisément le
            // fond uni sur lequel elle est bonne. Sans ce repli, la correction du sujet ne
            // faisait rien du tout sur un poste où le réseau est éteint.
            // Le même découpage que pour poser un fond blanc, au même endroit : réseau
            // d'abord, méthode par couleur ensuite. Sans ce repli, la correction du sujet
            // ne faisait rien du tout sur un poste où le réseau est éteint.
            using var calcule = BackgroundRemoval.DecouperLeSujet(image);

            if (calcule is null)
            {
                Log?.Invoke("Sélection du sujet : le détourage n'a rien rendu — correction du sujet ignorée.");
                return null;
            }

            var octets = calcule.ToByteArray(MagickFormat.Png);

            lock (Verrou)
            {
                Connus[empreinte] = octets;
                Ordre.Remove(empreinte);
                Ordre.Add(empreinte);

                while (Ordre.Count > MemoireMaximale)
                {
                    Connus.Remove(Ordre[0]);
                    Ordre.RemoveAt(0);
                }
            }

            return octets;
        }
    }

    /// <summary>Le masque déjà en mémoire pour cette empreinte, ou null.</summary>
    private static byte[]? Deja(string empreinte)
    {
        lock (Verrou)
        {
            if (!Connus.TryGetValue(empreinte, out var connu)) return null;

            // le plus récemment servi repasse en queue : c'est ce qui doit survivre
            Ordre.Remove(empreinte);
            Ordre.Add(empreinte);
            return connu;
        }
    }

    /// <summary>
    /// Le contour et le fondu, dans cet ordre : élargir APRÈS avoir adouci reviendrait à
    /// étaler un bord déjà flou, et le sujet gagnerait un halo au lieu de quelques pixels.
    /// </summary>
    private static void Retoucher(MagickImage masque, double contourPx, double adoucissementPx)
    {
        // Le masque n'a qu'une information par pixel — combien de sujet il y a là. On la
        // porte en RVB plutôt qu'en niveaux de gris : c'est sous cette forme que
        // <see cref="Fondre"/> le relit, et la conversion coûte 24 ms si elle attend le
        // dernier moment, à chaque mouvement de curseur, au lieu d'une fois ici.
        masque.Alpha(AlphaOption.Off);
        masque.ColorSpace = ColorSpace.Gray;
        masque.ColorSpace = ColorSpace.sRGB;

        var contour = (int)Math.Round(contourPx);
        if (contour != 0)
        {
            masque.Morphology(new MorphologySettings
            {
                Method = contour > 0 ? MorphologyMethod.Dilate : MorphologyMethod.Erode,
                Kernel = Kernel.Disk,
                KernelArguments = Math.Abs(contour)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        // Un fondu, TOUJOURS, même quand l'opérateur l'a mis à zéro : un bord au pixel près
        // se voit comme un découpage aux ciseaux, et c'est la bordure qui trahit une
        // retouche bien avant la correction elle-même.
        var rayon = Math.Max(0.5, adoucissementPx);
        masque.Blur(0, rayon);
    }

    /// <summary>
    /// Applique à une image les corrections d'un sujet, en épargnant le fond.
    ///
    /// <b>Le principe.</b> On corrige une COPIE entière de l'image — le calcul est le même
    /// que pour une correction ordinaire, et il n'y a donc pas deux mathématiques à tenir
    /// d'accord — puis on ne laisse voir cette copie qu'à travers le masque. Le fond de
    /// l'image d'origine ressort intact là où le masque est noir.
    ///
    /// Rend faux quand rien n'a été fait : détourage indisponible, ou aucun réglage.
    /// </summary>
    /// <param name="image">L'image à corriger, modifiée sur place.</param>
    /// <param name="sujet">Les réglages du sujet.</param>
    /// <param name="masque">
    /// Le masque, calculé par <see cref="Calculer"/> sur l'image AVANT ses autres
    /// corrections. Fourni par l'appelant plutôt que calculé ici : au moment où le sujet se
    /// corrige, l'image porte déjà le fond reposé et les réglages globaux, et son empreinte
    /// n'est plus celle qui sert de clé au cache.
    /// </param>
    public static bool Appliquer(MagickImage image, CorrectionsSujet sujet, MagickImage? masque)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(sujet);

        if (sujet.IsNeutral || masque is null) return false;

        // Les mêmes réglages que partout ailleurs, portés par le même objet : le sujet n'a
        // pas sa propre arithmétique, seulement sa propre portée.
        var reglages = new ImageAdjustments
        {
            Exposure = sujet.Exposure,
            Contrast = sujet.Contrast,
            Shadows = sujet.Shadows,
            Highlights = sujet.Highlights,
            Saturation = sujet.Saturation,
            Vibrance = sujet.Vibrance,
            Clarity = sujet.Clarity,
            Sharpness = sujet.Sharpness,
        };

        using var corrige = (MagickImage)image.Clone();
        ImageAdjuster.Apply(corrige, reglages);

        using var alpha = (MagickImage)masque.Clone();

        // le masque a pu être calculé sur une autre taille — l'aperçu travaille en 1600 px,
        // le tirage en pleine résolution
        if (alpha.Width != image.Width || alpha.Height != image.Height)
            alpha.Resize(image.Width, image.Height);

        Fondre(image, corrige, alpha);
        return true;
    }

    /// <summary>
    /// Mêle l'image corrigée à l'originale, dose par dose, selon le masque.
    ///
    /// <b>Pourquoi à la main plutôt qu'en composant deux images.</b> La version d'avant
    /// posait le masque en couche alpha puis superposait — deux compositions ImageMagick,
    /// mesurées à 125 ms et 71 ms sur une vignette d'aperçu (11/08/2026), pour un travail
    /// qui tient en une multiplication par pixel. Sur les octets, en parallèle, il en reste
    /// une quinzaine. C'est le même choix que <see cref="PixelCorrections"/>, et pour la
    /// même raison : Magick.NET est mono-fil sur ce poste.
    ///
    /// Le résultat est identique au pixel près — <c>Over</c> d'une source d'opacité m sur
    /// un fond opaque, c'est exactement <c>fond + (source − fond) × m</c>.
    /// </summary>
    private static void Fondre(MagickImage image, MagickImage corrige, MagickImage masque)
    {
        // Pas d'alpha, donc trois octets par pixel de part et d'autre : les tableaux se
        // parcourent du même pas, et la relecture rend ce qu'on lui a donné.
        image.Alpha(AlphaOption.Off);
        corrige.Alpha(AlphaOption.Off);

        // Retoucher l'a déjà rendu en RVB sans alpha ; on n'y touche que s'il vient
        // d'ailleurs — un masque redimensionné, ou fourni par un appelant.
        if (masque.HasAlpha) masque.Alpha(AlphaOption.Off);
        if (masque.ColorSpace != ColorSpace.sRGB) masque.ColorSpace = ColorSpace.sRGB;

        using var pixelsFond = image.GetPixels();
        using var pixelsCorrige = corrige.GetPixels();
        using var pixelsMasque = masque.GetPixels();

        var fond = pixelsFond.ToByteArray(PixelMapping.RGB);
        var dessus = pixelsCorrige.ToByteArray(PixelMapping.RGB);
        var dose = pixelsMasque.ToByteArray(PixelMapping.RGB);

        if (fond is null || dessus is null || dose is null ||
            dessus.Length != fond.Length || dose.Length != fond.Length)
        {
            // relecture impossible : on retombe sur la composition d'ImageMagick, plus lente
            // mais qui ne dépend d'aucune hypothèse sur la disposition des canaux
            corrige.Alpha(AlphaOption.Set);
            corrige.Composite(masque, CompositeOperator.CopyAlpha);
            image.Composite(corrige, CompositeOperator.Over);
            return;
        }

        var lignes = (int)image.Height;
        var parLigne = (int)image.Width * 3;

        Parallel.For(0, lignes, ligne =>
        {
            var debut = ligne * parLigne;

            for (var i = debut; i < debut + parLigne; i += 3)
            {
                var m = dose[i];
                if (m == 0) continue;          // du fond pur : rien à faire

                if (m == 255)                  // du sujet pur : la version corrigée, telle quelle
                {
                    fond[i] = dessus[i];
                    fond[i + 1] = dessus[i + 1];
                    fond[i + 2] = dessus[i + 2];
                    continue;
                }

                // La moyenne pondérée en entier, et non le fond plus un écart : un écart
                // négatif, divisé par 255, serait tronqué VERS ZÉRO par C# et un bord
                // adouci s'éclaircirait d'un demi-niveau sur toute sa longueur. Ici les
                // deux termes sont positifs et le + 127 arrondit au plus proche.
                var reste = 255 - m;
                fond[i] = (byte)((fond[i] * reste + dessus[i] * m + 127) / 255);
                fond[i + 1] = (byte)((fond[i + 1] * reste + dessus[i + 1] * m + 127) / 255);
                fond[i + 2] = (byte)((fond[i + 2] * reste + dessus[i + 2] * m + 127) / 255);
            }
        });

        pixelsFond.SetPixels(fond);
    }
}
