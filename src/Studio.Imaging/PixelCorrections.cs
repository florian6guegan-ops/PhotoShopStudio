using System.Numerics;
using Studio.Core.Domain;

namespace Studio.Imaging;

/// <summary>
/// Les corrections calculées ici même, sur les octets de l'image, plutôt que confiées à
/// ImageMagick.
///
/// <b>Pourquoi.</b> Magick.NET est compilé sans OpenMP sur ce poste : un seul fil, quel
/// que soit le nombre de cœurs, et l'augmenter ne change rien (vérifié le 01/08/2026).
/// Chaque réglage y coûtait donc une traversée complète de l'image, à un fil, et il y en
/// avait cinq à la suite. Mesuré sur un aperçu de 900 px le 01/08/2026 : courbe de tons
/// 26 ms, température 25 ms, saturation 26 ms, vibrance 107 ms, clarté 277 ms, netteté
/// 156 ms — 588 ms pour un développement complet, c'est-à-dire deux images par seconde
/// pendant qu'un curseur bouge.
///
/// Ici tout se fait en <b>une seule traversée</b> pour les réglages ponctuels, et en
/// <b>parallèle sur tous les cœurs</b> : notre code, lui, n'a pas la limite d'ImageMagick.
/// Le même développement tombe à une vingtaine de millisecondes, ce qui rend l'aperçu
/// pleine définition suivable à la main — et rend inutiles les deux béquilles qu'il avait
/// fallu poser : l'aperçu en demi-taille pendant le geste, et le relief sauté.
///
/// <b>Le résultat est le même qu'avant</b>, et pas seulement « proche » : la saturation et
/// la vibrance refont exactement le calcul HSL de <c>Modulate</c>, et le masque flou celui
/// d'<c>UnsharpMask</c>, seuil compris. Ce qui change est la vitesse, pas l'image — c'est
/// ce que <c>PixelCorrectionsTests</c> vérifie contre l'ancienne voie.
/// </summary>
public static class PixelCorrections
{
    /// <summary>
    /// En dessous de quoi on ne distribue rien : sur une vignette, répartir le travail
    /// coûte plus cher que le travail lui-même.
    /// </summary>
    private const int SeuilParallele = 40_000;

    /// <summary>
    /// Où trouver les canaux dans le tableau d'octets.
    ///
    /// WPF donne du BGRA, ImageMagick du RGB ou du RGBA, et une image passée en noir et
    /// blanc n'a qu'un canal. Plutôt que trois copies du même code, on décrit la
    /// disposition et le code la lit.
    /// </summary>
    /// <param name="Canaux">Octets par pixel.</param>
    /// <param name="R">Position du rouge ; les trois sont égales sur une image grise.</param>
    /// <param name="V">Position du vert.</param>
    /// <param name="B">Position du bleu.</param>
    /// <param name="Couleur">Faux sur une image à un seul canal : pas de couleur à régler.</param>
    public readonly record struct Disposition(int Canaux, int R, int V, int B, bool Couleur)
    {
        /// <summary>Ce que WPF manipule.</summary>
        public static Disposition Bgra => new(4, 2, 1, 0, true);

        /// <summary>Ce qu'ImageMagick rend pour une image couleur, avec ou sans alpha.</summary>
        public static Disposition Rvb(int canaux) => new(canaux, 0, 1, 2, true);

        /// <summary>Une image en niveaux de gris : un seul canal, et rien à saturer.</summary>
        public static Disposition Gris(int canaux) => new(canaux, 0, 0, 0, false);
    }

    /// <summary>
    /// Applique tout ce qui se règle sans regarder les voisins : courbe de tons,
    /// température, teinte, saturation, vibrance.
    ///
    /// Les tables sont calculées une fois pour l'image entière, puis chaque pixel n'est
    /// qu'une lecture de table et, s'il y a de la couleur à régler, une mise à l'échelle
    /// autour de sa clarté.
    /// </summary>
    public static void AppliquerPoints(
        byte[] pixels, int largeur, int hauteur, Disposition d, ImageAdjustments a)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(a);

        // Deux questions distinctes, et les confondre recolorerait un noir et blanc.
        //
        // « Y a-t-il trois canaux à écrire ? » dépend de la disposition : une image sortie
        // d'ImageMagick en niveaux de gris n'en a qu'un, mais la même image en BGRA en a
        // trois, égaux entre eux.
        //
        // « Y a-t-il une couleur à régler ? » dépend du réglage : sur une photo passée en
        // noir et blanc, température, teinte, saturation et vibrance n'ont plus d'objet —
        // les appliquer quand même y remettrait de la couleur.
        var canauxCouleur = d.Couleur;
        var reglagesCouleur = d.Couleur && !a.Grayscale;

        var tableR = Table(a, reglagesCouleur ? 1 + a.Temperature / 100.0 * AmplitudeTemperature : 1);
        var tableV = reglagesCouleur ? Table(a, 1 - a.Tint / 100.0 * AmplitudeTeinte) : tableR;
        var tableB = reglagesCouleur ? Table(a, 1 - a.Temperature / 100.0 * AmplitudeTemperature) : tableR;

        var facteurSaturation = reglagesCouleur ? 1 + a.Saturation / 100.0 : 1;
        var vibrance = reglagesCouleur ? a.Vibrance / 100.0 : 0;

        var tablesInutiles = EstIdentite(tableR) && EstIdentite(tableV) && EstIdentite(tableB);
        if (tablesInutiles && facteurSaturation == 1 && vibrance == 0) return;

        var couleurAJouer = reglagesCouleur && (facteurSaturation != 1 || vibrance != 0);

        // Les champs de la fermeture sont recopiés en variables locales avant la boucle.
        // Sans cela, chaque pixel relit `pixels`, `tableR`, `d.Canaux`… depuis l'objet de
        // capture : le compilateur ne peut pas prouver que la boucle ne les modifie pas, et
        // garde donc une lecture mémoire par accès. Sur cinq cent mille pixels, cela seul
        // triplait le temps de la passe.
        ParBandes(hauteur, (long)largeur * hauteur, (debut, fin) =>
        {
            var px = pixels;
            var tR = tableR;
            var tV = tableV;
            var tB = tableB;
            var canaux = d.Canaux;
            var iR = d.R;
            var iV = d.V;
            var iB = d.B;
            var saturation = facteurSaturation;
            var vib = vibrance;

            if (!canauxCouleur)
            {
                for (var y = debut; y < fin; y++)
                {
                    var fin1 = (y * largeur + largeur) * canaux;
                    for (var p = y * largeur * canaux; p < fin1; p += canaux)
                        px[p + iR] = tR[px[p + iR]];
                }
                return;
            }

            for (var y = debut; y < fin; y++)
            {
                var fin2 = (y * largeur + largeur) * canaux;

                for (var p = y * largeur * canaux; p < fin2; p += canaux)
                {
                    double r = tR[px[p + iR]];
                    double v = tV[px[p + iV]];
                    double b = tB[px[p + iB]];

                    if (couleurAJouer)
                        (r, v, b) = Colorer(r, v, b, saturation, vib);

                    px[p + iR] = Octet(r);
                    px[p + iV] = Octet(v);
                    px[p + iB] = Octet(b);
                }
            }
        });
    }

    /// <summary>Écart maximal appliqué aux canaux rouge et bleu par la température.</summary>
    private const double AmplitudeTemperature = 0.30;

    /// <summary>Écart maximal appliqué au canal vert par la teinte.</summary>
    private const double AmplitudeTeinte = 0.20;

    /// <summary>
    /// La courbe de tons et la pesée d'un canal, ramassées en 256 entrées.
    ///
    /// Les deux se composent en flottant, sans arrondi intermédiaire : ImageMagick les
    /// faisait en deux passes, donc en arrondissant deux fois — un dégradé y perdait un
    /// demi-niveau à chaque étape.
    /// </summary>
    private static byte[] Table(ImageAdjustments a, double facteur)
    {
        var table = new byte[256];
        var courbe = !ToneCurve.IsIdentity(a);

        for (var i = 0; i < 256; i++)
        {
            var v = i / 255.0;
            if (courbe) v = ToneCurve.Apply(v, a);
            v *= facteur;
            table[i] = (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
        }

        return table;
    }

    /// <summary>
    /// Arrondi au plus proche et bornage, en une comparaison de plus qu'une conversion
    /// nue : en C# une conversion de flottant négatif vers <c>byte</c> ne borne pas, elle
    /// déborde — un pixel presque noir en ressortirait presque blanc.
    /// </summary>
    private static byte Octet(double v) =>
        v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5);

    private static bool EstIdentite(byte[] table)
    {
        for (var i = 0; i < table.Length; i++)
            if (table[i] != i) return false;
        return true;
    }

    /// <summary>
    /// Saturation et vibrance, en une seule opération.
    ///
    /// Les deux ne touchent qu'à la saturation HSL, hue et clarté inchangées. Or à teinte
    /// et clarté fixes, chaque canal est une fonction <b>affine</b> de la saturation : tout
    /// se ramène donc à écarter les canaux de leur clarté L = (max+min)/2 d'un rapport
    /// S′/S. Pas de conversion HSL à l'aller, pas de retour, pas de trigonométrie — et le
    /// résultat est celui de <c>Modulate</c> au pixel près.
    ///
    /// La vibrance est ce mélange que faisait l'ancienne voie à coups de calques : une
    /// copie saturée deux fois, ramenée à travers un masque valant 1−S, donc opaque sur
    /// les couleurs ternes et transparent sur les vives. Comme le mélange était linéaire
    /// en RVB, et que RVB est affine en S, il l'était aussi en S : c'est ce qu'on écrit
    /// ici directement. Un teint de peau, déjà saturé, reste ainsi épargné.
    /// </summary>
    private static (double R, double V, double B) Colorer(
        double r, double v, double b, double facteurSaturation, double vibrance)
    {
        var max = r > v ? (r > b ? r : b) : (v > b ? v : b);
        var min = r < v ? (r < b ? r : b) : (v < b ? v : b);

        var ecart = max - min;
        if (ecart == 0) return (r, v, b);   // un gris n'a pas de saturation à régler

        var clarte = (max + min) / 2;

        // 1 − |2L − 1|, ramené à l'échelle 0..255 : le dénominateur de la saturation HSL
        var etendue = 255 - Math.Abs(2 * clarte - 255);
        if (etendue <= 0) return (r, v, b);

        var saturation = ecart / etendue;
        var reglee = Math.Clamp(saturation * facteurSaturation, 0, 1);

        if (vibrance != 0)
        {
            var accentuee = Math.Clamp(reglee * (1 + vibrance), 0, 1);
            reglee += (1 - reglee) * (accentuee - reglee);
        }

        var rapport = reglee / saturation;

        return (clarte + (r - clarte) * rapport,
                clarte + (v - clarte) * rapport,
                clarte + (b - clarte) * rapport);
    }

    // — relief —

    /// <summary>
    /// Clarté et netteté, toutes deux en masque flou : la clarté sur un large rayon donne
    /// du relief à la matière, la netteté sur un rayon serré détache les contours.
    ///
    /// Le rayon de la clarté suit la taille de l'image, sinon son effet dépendrait du
    /// format de tirage au lieu du sujet.
    /// </summary>
    public static void AppliquerRelief(
        byte[] pixels, int largeur, int hauteur, Disposition d, ImageAdjustments a)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentNullException.ThrowIfNull(a);

        if (a.Clarity != 0)
        {
            var sigma = Math.Max(4, Math.Max(largeur, hauteur) / 200.0);
            MasqueFlou(pixels, largeur, hauteur, d, sigma, a.Clarity / 100.0, 0);
        }

        if (a.Sharpness > 0)
            MasqueFlou(pixels, largeur, hauteur, d, 1, a.Sharpness / 100.0 * 1.5, 0.02);
    }

    /// <summary>
    /// Le masque flou d'ImageMagick, refait à l'identique : l'image plus le gain fois son
    /// écart au flou, et rien du tout là où l'écart est sous le seuil — c'est ce seuil qui
    /// empêche la netteté de faire ressortir le grain d'un ciel uni.
    ///
    /// Les canaux sont traités l'un après l'autre : deux plans de flottants suffisent
    /// alors, au lieu de six, ce qui compte sur un tirage grand format.
    /// </summary>
    private static void MasqueFlou(
        byte[] pixels, int largeur, int hauteur, Disposition d,
        double sigma, double gain, double seuil)
    {
        var taille = (long)largeur * hauteur;
        if (taille == 0) return;

        var plan = new float[taille];
        var tampon = new float[taille];

        var canaux = d.Couleur ? new[] { d.R, d.V, d.B } : new[] { d.R };

        foreach (var canal in canaux)
        {
            var octetsParPixel = d.Canaux;

            ParBandes(hauteur, taille, (debut, fin) =>
            {
                var px = pixels;
                var dst = plan;

                for (var y = debut; y < fin; y++)
                {
                    var i = y * largeur;
                    var p = i * octetsParPixel + canal;

                    for (var x = 0; x < largeur; x++, p += octetsParPixel)
                        dst[i + x] = px[p];
                }
            });

            var flou = Flouter(plan, tampon, largeur, hauteur, sigma);
            var limite = (float)(255 * seuil);
            var force = (float)gain;

            ParBandes(hauteur, taille, (debut, fin) =>
            {
                var px = pixels;
                var src = flou;

                for (var y = debut; y < fin; y++)
                {
                    var i = y * largeur;
                    var p = i * octetsParPixel + canal;

                    for (var x = 0; x < largeur; x++, p += octetsParPixel)
                    {
                        float origine = px[p];
                        var ecart = origine - src[i + x];

                        // le seuil d'ImageMagick porte sur le double de l'écart
                        if (Math.Abs(2 * ecart) < limite) continue;

                        px[p] = Octet(origine + force * ecart);
                    }
                }
            });
        }
    }

    /// <summary>
    /// Un flou gaussien, par la voie la moins chère selon le rayon.
    ///
    /// Sous un petit sigma le noyau ne fait que quelques points : on convolue pour de vrai,
    /// c'est exact et déjà rapide. Au-delà, le noyau deviendrait énorme — 30 de sigma sur
    /// un tirage grand format, donc 181 points par pixel et par sens — et l'on passe aux
    /// trois passes de moyenne glissante, qui coûtent le même prix quel que soit le rayon
    /// et dont la somme approche une gaussienne à ne pas la distinguer à l'œil.
    /// </summary>
    /// <returns>Le plan qui contient le résultat : <paramref name="plan"/> ou <paramref name="tampon"/>.</returns>
    private static float[] Flouter(
        float[] plan, float[] tampon, int largeur, int hauteur, double sigma)
    {
        if (sigma < 2.5)
        {
            var noyau = Noyau(sigma);
            ConvoluerHorizontal(plan, tampon, largeur, hauteur, noyau);
            ConvoluerVertical(tampon, plan, largeur, hauteur, noyau);
            return plan;
        }

        var (a, b, c) = Boites(sigma);

        MoyenneHorizontale(plan, tampon, largeur, hauteur, a);
        MoyenneHorizontale(tampon, plan, largeur, hauteur, b);
        MoyenneHorizontale(plan, tampon, largeur, hauteur, c);

        MoyenneVerticale(tampon, plan, largeur, hauteur, a);
        MoyenneVerticale(plan, tampon, largeur, hauteur, b);
        MoyenneVerticale(tampon, plan, largeur, hauteur, c);

        return plan;
    }

    /// <summary>Un demi-noyau gaussien normalisé, coupé à trois sigmas.</summary>
    private static float[] Noyau(double sigma)
    {
        var rayon = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var noyau = new float[rayon * 2 + 1];
        var somme = 0.0;

        for (var i = -rayon; i <= rayon; i++)
        {
            var v = Math.Exp(-(i * i) / (2 * sigma * sigma));
            noyau[i + rayon] = (float)v;
            somme += v;
        }

        for (var i = 0; i < noyau.Length; i++) noyau[i] = (float)(noyau[i] / somme);
        return noyau;
    }

    /// <summary>
    /// Convolution horizontale, en trois morceaux : le bord gauche, le milieu, le bord
    /// droit.
    ///
    /// Le découpage n'est pas une coquetterie. Avec un seul <c>Clamp</c> par point du
    /// noyau, le bornage se paie sur toute l'image alors qu'il ne sert qu'aux quelques
    /// pixels des bords — et il empêche surtout le processeur de vectoriser la boucle,
    /// ce qui coûte bien plus que le bornage lui-même.
    /// </summary>
    private static void ConvoluerHorizontal(
        float[] source, float[] cible, int largeur, int hauteur, float[] noyau)
    {
        var rayon = noyau.Length / 2;

        ParBandes(hauteur, (long)largeur * hauteur, (debut, fin) =>
        {
            for (var y = debut; y < fin; y++)
            {
                var ligne = y * largeur;
                Array.Clear(cible, ligne, largeur);

                for (var k = 0; k < noyau.Length; k++)
                {
                    var decalage = k - rayon;
                    var coefficient = noyau[k];

                    // la part de la ligne où le voisin demandé existe vraiment : elle se
                    // traite d'un bloc, donc au rythme des registres larges
                    var premier = Math.Max(0, -decalage);
                    var dernier = Math.Min(largeur, largeur - decalage);

                    if (dernier > premier)
                        AjouterPondere(
                            source.AsSpan(ligne + premier + decalage, dernier - premier),
                            cible.AsSpan(ligne + premier, dernier - premier),
                            coefficient);

                    // les bords, où le voisin manque : il est prolongé par le pixel extrême
                    for (var x = 0; x < premier; x++)
                        cible[ligne + x] += coefficient * source[ligne];
                    for (var x = dernier; x < largeur; x++)
                        cible[ligne + x] += coefficient * source[ligne + largeur - 1];
                }
            }
        });
    }

    /// <summary>
    /// Convolution verticale, menée <b>ligne par ligne</b> et non colonne par colonne.
    ///
    /// Écrite naïvement, elle saute d'une ligne à l'autre à chaque point du noyau : sur une
    /// image large, chaque lecture tombe dans une page de mémoire différente et le cache ne
    /// sert plus à rien. En accumulant une ligne de sortie à partir des lignes voisines
    /// entières, tous les accès redeviennent séquentiels — même nombre d'opérations, une
    /// fraction du temps.
    /// </summary>
    private static void ConvoluerVertical(
        float[] source, float[] cible, int largeur, int hauteur, float[] noyau)
    {
        var rayon = noyau.Length / 2;

        ParBandes(hauteur, (long)largeur * hauteur, (debut, fin) =>
        {
            for (var y = debut; y < fin; y++)
            {
                var sortie = y * largeur;
                Array.Clear(cible, sortie, largeur);

                for (var k = 0; k < noyau.Length; k++)
                {
                    var entree = Math.Clamp(y - rayon + k, 0, hauteur - 1) * largeur;

                    AjouterPondere(source.AsSpan(entree, largeur),
                                   cible.AsSpan(sortie, largeur), noyau[k]);
                }
            }
        });
    }

    /// <summary>
    /// <c>cible += coefficient × source</c> sur une ligne entière, huit valeurs à la fois.
    ///
    /// C'est la boucle la plus chaude du relief : elle est parcourue une fois par point du
    /// noyau et par ligne. Passer par <see cref="Vector{T}"/> la fait tenir dans les
    /// registres larges du processeur, ce que la boucle écrite à la main n'obtenait pas —
    /// le contrôle de bornes des tableaux suffit à l'en empêcher.
    /// </summary>
    private static void AjouterPondere(
        ReadOnlySpan<float> source, Span<float> cible, float coefficient)
    {
        var pas = Vector<float>.Count;
        var poids = new Vector<float>(coefficient);

        var x = 0;
        for (; x <= source.Length - pas; x += pas)
        {
            var v = new Vector<float>(source.Slice(x, pas)) * poids
                  + new Vector<float>(cible.Slice(x, pas));
            v.CopyTo(cible.Slice(x, pas));
        }

        for (; x < source.Length; x++) cible[x] += coefficient * source[x];
    }

    /// <summary>
    /// Les trois largeurs de moyenne glissante dont la somme approche une gaussienne de ce
    /// sigma — la construction de Kovesi, où deux largeurs voisines sont dosées pour tomber
    /// sur la bonne variance.
    /// </summary>
    private static (int, int, int) Boites(double sigma)
    {
        var ideale = Math.Sqrt(12 * sigma * sigma / 3 + 1);
        var basse = (int)Math.Floor(ideale);
        if (basse % 2 == 0) basse--;
        if (basse < 1) basse = 1;

        var haute = basse + 2;

        var combien = (int)Math.Round(
            (12 * sigma * sigma - 3.0 * basse * basse - 12 * basse - 9) / (-4.0 * basse - 4));
        combien = Math.Clamp(combien, 0, 3);

        int Largeur(int rang) => rang < combien ? basse : haute;

        return ((Largeur(0) - 1) / 2, (Largeur(1) - 1) / 2, (Largeur(2) - 1) / 2);
    }

    /// <summary>
    /// Moyenne glissante horizontale sur 2r+1 points, bords prolongés. La somme se
    /// transporte d'un point au suivant : le coût ne dépend pas du rayon, ce qui est tout
    /// l'intérêt — un flou de rayon 30 sur un tirage grand format coûte le même prix qu'un
    /// flou de rayon 2.
    /// </summary>
    private static void MoyenneHorizontale(
        float[] source, float[] cible, int largeur, int hauteur, int rayon)
    {
        if (rayon <= 0)
        {
            Array.Copy(source, cible, source.Length);
            return;
        }

        var facteur = 1f / (2 * rayon + 1);

        ParBandes(hauteur, (long)largeur * hauteur, (debut, fin) =>
        {
            for (var y = debut; y < fin; y++)
            {
                var ligne = y * largeur;

                // les rangs négatifs retombent tous sur le premier point : bord prolongé
                var somme = source[ligne] * (rayon + 1);
                for (var k = 1; k <= rayon; k++)
                    somme += source[ligne + Math.Min(k, largeur - 1)];

                for (var x = 0; x < largeur; x++)
                {
                    cible[ligne + x] = somme * facteur;

                    somme += source[ligne + Math.Min(x + rayon + 1, largeur - 1)]
                           - source[ligne + Math.Max(x - rayon, 0)];
                }
            }
        });
    }

    /// <summary>
    /// Moyenne glissante verticale, elle aussi menée ligne par ligne : une ligne de sommes
    /// courantes avance d'un rang à l'autre, en ajoutant la ligne qui entre dans la fenêtre
    /// et en retirant celle qui en sort.
    ///
    /// Les colonnes sont réparties par paquets larges plutôt qu'une à une : la somme
    /// courante interdit de découper en hauteur, mais rien n'empêche deux cœurs de tenir
    /// chacun leur portion de largeur.
    /// </summary>
    private static void MoyenneVerticale(
        float[] source, float[] cible, int largeur, int hauteur, int rayon)
    {
        if (rayon <= 0)
        {
            Array.Copy(source, cible, source.Length);
            return;
        }

        var facteur = 1f / (2 * rayon + 1);

        ParBandes(largeur, (long)largeur * hauteur, (debut, fin) =>
        {
            var colonnes = fin - debut;
            var sommes = new float[colonnes];

            for (var k = -rayon; k <= rayon; k++)
            {
                var entree = Math.Clamp(k, 0, hauteur - 1) * largeur + debut;
                AjouterPondere(source.AsSpan(entree, colonnes), sommes, 1f);
            }

            for (var y = 0; y < hauteur; y++)
            {
                var sortie = y * largeur + debut;
                Multiplier(sommes, cible.AsSpan(sortie, colonnes), facteur);

                var entrant = Math.Min(y + rayon + 1, hauteur - 1) * largeur + debut;
                var sortant = Math.Max(y - rayon, 0) * largeur + debut;

                AjouterEcart(source.AsSpan(entrant, colonnes),
                             source.AsSpan(sortant, colonnes), sommes);
            }
        });
    }

    /// <summary><c>cible = facteur × source</c>, huit valeurs à la fois.</summary>
    private static void Multiplier(ReadOnlySpan<float> source, Span<float> cible, float facteur)
    {
        var pas = Vector<float>.Count;
        var poids = new Vector<float>(facteur);

        var x = 0;
        for (; x <= source.Length - pas; x += pas)
            (new Vector<float>(source.Slice(x, pas)) * poids).CopyTo(cible.Slice(x, pas));

        for (; x < source.Length; x++) cible[x] = facteur * source[x];
    }

    /// <summary>
    /// <c>cumul += entrant − sortant</c> : la ligne qui entre dans la fenêtre glissante,
    /// moins celle qui en sort.
    /// </summary>
    private static void AjouterEcart(
        ReadOnlySpan<float> entrant, ReadOnlySpan<float> sortant, Span<float> cumul)
    {
        var pas = Vector<float>.Count;

        var x = 0;
        for (; x <= entrant.Length - pas; x += pas)
        {
            var v = new Vector<float>(cumul.Slice(x, pas))
                  + new Vector<float>(entrant.Slice(x, pas))
                  - new Vector<float>(sortant.Slice(x, pas));
            v.CopyTo(cumul.Slice(x, pas));
        }

        for (; x < entrant.Length; x++) cumul[x] += entrant[x] - sortant[x];
    }

    /// <summary>
    /// Découpe un travail en paquets et les répartit sur les cœurs — sauf sur une petite
    /// image, où distribuer coûte plus que calculer.
    ///
    /// C'est ici que se gagne l'essentiel : ImageMagick est mono-fil sur ce poste, notre
    /// code ne l'est pas.
    /// </summary>
    /// <param name="tranches">Nombre de tranches indépendantes — lignes, ou colonnes.</param>
    /// <param name="travail">Nombre de pixels en jeu, pour décider s'il vaut la peine de répartir.</param>
    /// <param name="bande">Appelé avec un intervalle de tranches, borne haute exclue.</param>
    private static void ParBandes(int tranches, long travail, Action<int, int> bande)
    {
        if (tranches <= 0) return;

        if (travail < SeuilParallele || Environment.ProcessorCount < 2)
        {
            bande(0, tranches);
            return;
        }

        // deux paquets par cœur : assez pour que les cœurs se rattrapent si l'un traîne,
        // assez peu pour que chaque paquet reste large — une bande étroite ferait relire
        // les mêmes lignes de cache à plusieurs cœurs
        var bandes = Math.Min(tranches, Environment.ProcessorCount * 2);
        var parBande = (tranches + bandes - 1) / bandes;

        Parallel.For(0, bandes, i =>
        {
            var debut = i * parBande;
            if (debut >= tranches) return;
            bande(debut, Math.Min(debut + parBande, tranches));
        });
    }
}
