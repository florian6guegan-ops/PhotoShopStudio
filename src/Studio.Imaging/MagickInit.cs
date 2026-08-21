using ImageMagick;

namespace Studio.Imaging;

public static class MagickInit
{
    /// <summary>
    /// Les limites sont posées UNE fois, et le premier arrivé attend qu'elles le soient.
    ///
    /// <b>Un simple drapeau ne suffisait pas.</b> <c>Configure</c> est appelé depuis les
    /// rendus menés en parallèle (<c>PrintOrchestrator.RenderEnvelope</c>) et depuis le
    /// chargement des vignettes, lui aussi parallèle. Avec <c>if (_done) return; _done =
    /// true;</c>, un second fil pouvait lire le drapeau déjà posé pendant que le premier
    /// n'avait pas encore écrit les limites — et décoder un fichier piégé SANS plafond,
    /// c'est-à-dire précisément le cas contre lequel cette méthode existe.
    ///
    /// <see cref="Lazy{T}"/> garantit les deux : une seule exécution, et tout appelant
    /// bloqué jusqu'à ce qu'elle soit terminée.
    /// </summary>
    private static readonly Lazy<bool> Limites = new(() =>
    {
        ResourceLimits.Memory = 2UL * 1024 * 1024 * 1024;      // 2 Go puis bascule sur disque
        ResourceLimits.Width = 60000;                           // ~15 m à 300 dpi : au-delà c'est un fichier piégé
        ResourceLimits.Height = 60000;
        return true;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Plafonne les ressources de Magick.NET : un fichier client corrompu ou
    /// démesuré ne doit jamais pouvoir mettre l'application à genoux (leçon DiLand).
    /// </summary>
    public static void Configure() => _ = Limites.Value;

    /// <summary>
    /// Fait décoder un JPEG à la taille dont on a besoin, et pas à celle du fichier.
    ///
    /// <b>C'est l'économie la moins chère du logiciel.</b> Le décodeur JPEG sait sauter des
    /// coefficients et rendre l'image au demi, au quart ou au huitième : c'est du
    /// sous-échantillonnage exact, pas une réduction après coup. Une photo de 24 Mpx dont on
    /// ne veut qu'un aperçu de 900 px se décode ainsi huit fois plus vite, et ne passe
    /// jamais entière par la mémoire.
    ///
    /// <b>On demande un CARRÉ</b>, du plus grand des deux côtés voulus : l'indication porte
    /// sur le FICHIER, dont l'orientation n'est connue qu'après lecture de l'EXIF. Demander
    /// 900 × 600 sur un fichier couché ferait décoder trop petit ; un carré est juste dans
    /// les deux sens, et coûte au pire un cran de décodage.
    ///
    /// Rend null pour tout ce qui n'est pas un JPEG — les autres formats n'ont pas de
    /// décodage progressif — et null, aussi, quand le besoin dépasse ce que le fichier
    /// contient : le décodeur, LUI, sait agrandir, et il ne faut surtout pas le lui
    /// demander. Voir le corps de la méthode.
    /// </summary>
    /// <param name="sourcePath">Fichier à lire.</param>
    /// <param name="cote">Côté voulu, en pixels ; zéro ou moins = pas d'indication.</param>
    public static MagickReadSettings? IndicationDeTaille(string sourcePath, int cote)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (cote <= 0) return null;
        if (Path.GetExtension(sourcePath).ToLowerInvariant() is not (".jpg" or ".jpeg")) return null;

        // ⚠ SI, LE DÉCODEUR SAIT AGRANDIR — et c'est ce qui rendait l'envoi par courriel
        // interminable.
        //
        // « jamais à la hausse : le décodeur ne sait pas agrandir » était faux. libjpeg
        // accepte un facteur d'échelle de 1/8 à 16/8 : demandé plus grand que le fichier,
        // il DÉCODE EN DOUBLE. Mesuré le 20/08/2026 sur une photo de 6016 × 4000 :
        // « jpeg:size 7823x7823 » rend du 12032 × 8000, soit 96 Mpx là où le fichier en
        // contient 24.
        //
        // L'impression n'en souffrait pas : elle vise 1795 px et l'indication réduit
        // vraiment. L'envoi par courriel, lui, vise LA DÉFINITION NATIVE du cadrage — la
        // photo entière, à quelques pour cent près — et l'indication devenait donc un
        // agrandissement systématique. Tout le pipeline travaillait ensuite sur quatre fois
        // trop de pixels, et le redressement fin, seul, y passait 46 s au lieu de 11.
        //
        // On pingue l'en-tête — deux nombres, aucun pixel décodé — et on ne pose
        // l'indication que quand elle DEMANDE MOINS que ce que le fichier contient.
        //
        // <b>Le PLUS PETIT côté commande</b>, et c'est là-dessus que la première version de
        // cette garde s'est trompée. On demande un CARRÉ — voir plus haut, l'orientation du
        // fichier n'est pas connue avant l'EXIF — et « size » veut dire « au moins autant
        // dans les deux sens » : sur un fichier de 1200 × 800, une indication de 900
        // agrandit déjà, pour amener la hauteur à 900. Comparer au grand côté laissait donc
        // passer tout un domaine d'agrandissements silencieux.
        try
        {
            Configure();

            using var entete = new MagickImage();
            entete.Ping(sourcePath);

            if (cote >= Math.Min(entete.Width, entete.Height)) return null;
        }
        catch (MagickException)
        {
            // en-tête illisible : on laisse le décodeur lire le fichier tel quel
            return null;
        }

        var settings = new MagickReadSettings();
        settings.SetDefine(MagickFormat.Jpeg, "size", $"{cote}x{cote}");
        return settings;
    }

    /// <summary>
    /// Ouvre une image en ne décodant que ce dont on a besoin. Voir
    /// <see cref="IndicationDeTaille"/>.
    /// </summary>
    /// <param name="cote">Côté voulu, en pixels ; zéro ou moins = décodage complet.</param>
    public static MagickImage Lire(string sourcePath, int cote)
    {
        Configure();

        var econome = IndicationDeTaille(sourcePath, cote);

        // SUR UNE CARTE, ON LIT LES OCTETS D'ABORD.
        //
        // ImageMagick ouvre un fichier en le PROJETANT en mémoire. Tant que le support
        // reste là, c'est ce qu'on peut faire de mieux : rien n'est copié. Mais si le
        // support disparaît pendant qu'on travaille — une carte retirée un peu vite au
        // comptoir, une clef arrachée, une prise USB qui bouge — l'accès à une page déjà
        // projetée lève STATUS_IN_PAGE_ERROR. Ce n'est pas une exception .NET : c'est une
        // faute au niveau du système, que le CLR ne sait pas rattraper. Le processus meurt
        // sur place, sans une ligne au journal.
        //
        // C'est ce qui a tué Studio deux fois le 07/08/2026, à 18:33 et 18:37, pendant que
        // Windows enregistrait 236 erreurs de lecture sur le lecteur de cartes. La carte,
        // vérifiée le lendemain, était saine : elle avait seulement perdu le contact.
        //
        // Lus en octets, les mêmes incidents donnent une IOException ordinaire, que
        // l'appelant intercepte et montre à l'opérateur. On paie la photo en mémoire — un
        // JPEG d'appareil pèse une vingtaine de mégaoctets — et seulement sur les supports
        // qui peuvent s'en aller. Les disques du poste gardent la projection.
        if (SurSupportQuiPeutDisparaitre(sourcePath))
        {
            var octets = File.ReadAllBytes(sourcePath);
            return econome is not null
                ? new MagickImage(octets, econome)
                : new MagickImage(octets);
        }

        return econome is not null
            ? new MagickImage(sourcePath, econome)
            : new MagickImage(sourcePath);
    }

    /// <summary>
    /// Le fichier est-il sur un support qui peut s'en aller sous nos pieds ?
    ///
    /// Les cartes et les clefs, bien sûr — mais aussi les partages réseau, qui tombent de
    /// la même façon et donnent la même faute, et les lecteurs optiques.
    ///
    /// Dans le doute, on rend faux : se tromper ici ne coûte qu'une projection en mémoire
    /// là où elle était sûre, alors que rendre vrai à tort ferait recopier chaque photo du
    /// disque de travail.
    /// </summary>
    public static bool SurSupportQuiPeutDisparaitre(string chemin)
    {
        if (string.IsNullOrWhiteSpace(chemin)) return false;

        try
        {
            // un chemin UNC (\\serveur\partage) n'a pas de lettre de lecteur : DriveInfo
            // n'en dira rien, et c'est pourtant le cas le plus fragile
            if (chemin.StartsWith(@"\\", StringComparison.Ordinal)) return true;

            var racine = Path.GetPathRoot(Path.GetFullPath(chemin));
            if (string.IsNullOrEmpty(racine)) return false;

            return new DriveInfo(racine).DriveType
                is DriveType.Removable or DriveType.Network or DriveType.CDRom;
        }
        catch (Exception)
        {
            // lecteur déjà parti, chemin malformé, droits refusés
            return false;
        }
    }

    /// <summary>
    /// Niveau de compression des PNG produits par l'atelier : 1, c'est-à-dire le plus
    /// rapide qui compresse encore.
    ///
    /// <b>Pourquoi ce n'est pas un détail.</b> Mesuré le 02/08/2026 sur un rendu 50×70 à
    /// 300 ppp (5906 × 8268 px, 48,8 Mpx), depuis un scan de 7518 × 5013 :
    ///
    /// | Étape | Durée |
    /// |---|---|
    /// | lecture du JPEG source | 710 ms |
    /// | redimensionnement | 4 837 ms |
    /// | **écriture PNG, réglage par défaut** | **32 415 ms** — 41,8 Mo |
    /// | écriture PNG, niveau 1 | 4 126 ms — 50,4 Mo |
    /// | écriture PNG, niveau 0 | 2 296 ms — 139,8 Mo |
    ///
    /// Le rendu complet passe de 42 s à ~10 s. Les 8 Mo de plus par fichier ne coûtent rien :
    /// ces rendus sont des fichiers de travail, effacés à l'archivage des commandes. C'était
    /// là, et non dans l'impression, que « Imprimer » paraissait interminable.
    ///
    /// Le niveau 0 va deux fois plus vite encore, mais triple la taille : 140 Mo par tirage
    /// à relire ensuite depuis le disque, ce qu'on paierait à l'ouverture de la boîte
    /// d'agrandissement.
    /// </summary>
    private const string CompressionPng = "1";

    /// <summary>
    /// Qualité JPEG des rendus d'agrandissement. 95 : la limite au-delà de laquelle le
    /// fichier grossit sans que l'œil y gagne, et bien au-dessus du 92 de la planche
    /// d'index. La source est elle-même un JPEG d'appareil ou de scanner — un ré-encodage
    /// à 95 après agrandissement ne retire rien de visible sur un tirage.
    /// </summary>
    private const int QualiteJpeg = 95;

    /// <summary>
    /// Écrit une image de l'atelier, au format que dit son extension.
    ///
    /// <b>Passer par ici et non par <c>image.Write</c></b> : les réglages par défaut de
    /// Magick.NET coûtent des dizaines de secondes sur les grandes images.
    ///
    /// <b>Pourquoi le format compte à ce point.</b> Mesuré le 02/08/2026 sur un rendu 40×50
    /// à 300 ppp (4724 × 5906 = 27,9 Mpx), depuis une photo de 3024 × 2005 :
    ///
    /// | Écriture | Durée | Taille |
    /// |---|---|---|
    /// | PNG, réglages par défaut | 15 228 ms | 14,7 Mo |
    /// | PNG, compression 1 | 12 531 ms | 16,3 Mo |
    /// | PNG, compression 0, sans filtre | 11 905 ms | 26,6 Mo |
    /// | **JPEG qualité 95** | **694 ms** | **8,9 Mo** |
    ///
    /// Le niveau de compression ne change presque rien : l'encodeur PNG de Magick.NET est
    /// lent en lui-même sur ces définitions, indépendamment de zlib. Seul le changement de
    /// format règle la question — et il divise le rendu par trois.
    ///
    /// <b>Le PNG reste pour les PLANCHES</b> (identité, personnalisées) : elles portent des
    /// contours de découpe de deux dixièmes de millimètre et de la date en petits
    /// caractères, autour desquels le JPEG laisse des franges. Elles sont aussi bien plus
    /// petites, donc le coût ne se voit pas.
    /// </summary>
    public static void Write(IMagickImage<byte> image, string path)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            image.Format = MagickFormat.Jpeg;
            image.Quality = QualiteJpeg;
        }
        else
        {
            image.Settings.SetDefine(MagickFormat.Png, "compression-level", CompressionPng);

            // une image née d'une couleur porte le pseudo-format « XC », que rien ne sait
            // écrire : on impose le format plutôt que de le laisser deviner par l'extension
            image.Format = MagickFormat.Png;
        }

        EcrirePuisPoser(image, path);
    }

    /// <summary>
    /// Écrit à côté, puis met en place d'un seul geste.
    ///
    /// <b>Ce n'est pas une précaution théorique.</b> Un rendu qui existe déjà n'est jamais
    /// refait : <c>PrintOrchestrator.RenderEnvelope</c> saute le calcul dès que
    /// <c>File.Exists(output)</c> — c'est ce qui rend une commande rejouable après un
    /// incident sans tout recalculer. Mais un tirage 50×70 met une dizaine de secondes à
    /// s'écrire : une coupure de courant, un arrêt forcé ou une saturation du disque
    /// pendant ces secondes laissait un PNG TRONQUÉ à l'emplacement final. À la reprise, il
    /// était donc réutilisé tel quel — le minilab recevait une image incomplète, ou
    /// <c>new Bitmap(page.Path)</c> levait sur une commande qu'on croyait rattrapée.
    ///
    /// Le fichier temporaire porte l'identifiant du processus et du fil : deux rendus
    /// menés en parallèle (ils le sont) ne peuvent pas se marcher dessus.
    ///
    /// <c>File.Move</c> est atomique sur un même volume — et le temporaire est écrit dans
    /// le dossier de destination, donc c'est toujours le cas.
    /// </summary>
    private static void EcrirePuisPoser(IMagickImage<byte> image, string path)
    {
        var dossier = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);

        var temporaire = $"{path}.{Environment.ProcessId}-{Environment.CurrentManagedThreadId}.part";

        try
        {
            image.Write(temporaire);
            File.Move(temporaire, path, overwrite: true);
        }
        catch
        {
            // un temporaire abandonné ne doit pas s'accumuler dans le dossier des rendus,
            // ni surtout passer pour un rendu valide au prochain passage
            try { File.Delete(temporaire); } catch (IOException) { }
            throw;
        }
    }
}
