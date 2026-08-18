using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace Studio.Imaging;

/// <summary>
/// Détourage par le réseau BiRefNet, quand il est activé et qu'un modèle est installé.
///
/// C'est l'état de l'art en 2026 pour séparer un sujet de son fond, et il tient les
/// mèches de cheveux là où une règle de couleur laisse un halo. Il ne remplace pas
/// <see cref="BackgroundRemoval"/> : il passe DEVANT, et on retombe dessus dès que
/// quelque chose manque — modèle absent, carte graphique indisponible, sortie douteuse.
/// Une boutique ne peut pas dépendre d'un fichier de 490 Mo pour servir un client.
///
/// LICENCE : BiRefNet est sous licence MIT, donc utilisable en commerce. À ne pas
/// confondre avec BRIA RMBG-2.0, bâti sur la même architecture mais dont l'usage
/// commercial est payant — écarté pour cette raison le 03/08/2026.
///
/// Le modèle vit HORS du dépôt (<see cref="DossiersCherches"/>) : le dépôt est public,
/// et un demi-gigaoctet de poids n'a rien à y faire.
/// </summary>
public static class BiRefNetMatting
{
    /// <summary>
    /// Modèles acceptés, du plus léger au plus lourd — l'ordre de recherche par défaut.
    ///
    /// La variante « lite » (109 Mo) passe d'abord parce qu'elle tient dans les 4 Go de la
    /// Quadro P2000 photo APRÈS photo, là où la « portrait » (467 Mo) réussit la première
    /// et échoue la seconde faute de mémoire.
    ///
    /// Sur une machine mieux dotée, <see cref="ModelePrefere"/> renverse cet ordre : il
    /// n'est plus nécessaire de retirer un fichier du disque pour changer de modèle.
    /// </summary>
    public static IReadOnlyList<string> ModelesAcceptes { get; } =
    [
        "birefnet-lite-fp16.onnx",
        "birefnet-portrait-fp16.onnx",
    ];

    /// <summary>
    /// Modèle demandé par les réglages du poste (Paramètres → Détourage du fond blanc),
    /// ou null pour l'ordre par défaut.
    ///
    /// Il passe en TÊTE de la recherche, il ne l'épuise pas : absent du disque, on retombe
    /// sur l'autre — mais <b>en le disant au journal</b>. Un modèle silencieusement
    /// remplacé par un autre ferait conclure à un réglage sans effet, et on chercherait le
    /// défaut ailleurs.
    /// </summary>
    public static string? ModelePrefere { get; set; }

    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Le réseau est-il utilisé ? NON par défaut — décision de Florian le 03/08/2026.
    ///
    /// Mesuré sur ce poste (Quadro P2000, 4 Go) : 4,3 s sur l'aperçu et 9,5 s sur la photo
    /// pleine résolution, contre 1,2 s pour <see cref="BackgroundRemoval"/>. Le rendu est
    /// franchement meilleur — mèches de cheveux tenues, aucune ombre résiduelle — mais dix
    /// secondes devant un client, c'est trop long.
    ///
    /// <b>Posé par les réglages du poste</b> (<c>config\detourage.json</c>, écran
    /// Paramètres) depuis la 6ᵉ passe. Il ne l'était par personne jusque-là : le réseau ne
    /// s'exécutait donc jamais, et tout ce code était mort.
    /// </summary>
    public static bool Actif { get; set; }

    /// <summary>
    /// Oublie la session chargée, pour que le prochain détourage reprenne les réglages.
    ///
    /// La session est gardée pour la vie du processus — recharger un demi-gigaoctet à
    /// chaque photo bloquerait le comptoir. Mais sans ce point d'entrée, changer de modèle
    /// dans Paramètres ne se voyait qu'après un redémarrage de l'application, et le
    /// réglage passait pour inopérant.
    /// </summary>
    public static void Reinitialiser()
    {
        lock (Verrou)
        {
            _session?.Dispose();
            _session = null;
            _tentative = false;
            _modeleCharge = null;

            // Un modèle écarté faute de mémoire retrouve sa chance : l'exploitant vient de
            // toucher aux réglages, et le poste a pu changer de carte entre-temps.
            _modelesEcartes.Clear();
            DernierRepli = null;

            // Le prochain détourage repaiera le chargement : l'oublier ici ferait annoncer
            // une attente courte juste après un changement de modèle, c'est-à-dire au seul
            // moment où l'on sait qu'elle sera longue.
            DureeDuChargement = null;
        }
    }

    /// <summary>
    /// Côté de l'image donnée au réseau.
    ///
    /// BiRefNet est entraîné en 1024, et c'est là qu'il donne son meilleur contour. Mais
    /// la Quadro P2000 de ce poste n'a que 4 Go : à 1024 la première photo passe et la
    /// SECONDE échoue faute de mémoire (« DmlFusedNode », constaté le 03/08/2026, y
    /// compris en désactivant le réutilisateur de mémoire). À 768 le réseau tient dans la
    /// carte photo après photo, et le contour reste très au-dessus de la méthode par
    /// couleur.
    ///
    /// Le modèle exporté a une entrée FIGÉE à 1024 : lui en donner une autre le fait
    /// refuser l'image (« invalid dimensions for input »). On ne peut donc pas alléger la
    /// carte par la taille — c'est le POIDS du modèle qu'il faut réduire, d'où la variante
    /// « lite ».
    /// </summary>
    private const int Cote = 1024;

    /// <summary>Normalisation ImageNet, celle avec laquelle le réseau a été entraîné.</summary>
    private static readonly float[] Moyenne = [0.485f, 0.456f, 0.406f];
    private static readonly float[] EcartType = [0.229f, 0.224f, 0.225f];

    private static readonly object Verrou = new();

    /// <summary>
    /// Un seul passage du réseau à la fois, tous appelants confondus.
    ///
    /// <b>Ce n'est pas une précaution de style.</b> Le commentaire de <see cref="Cote"/>
    /// raconte déjà qu'à 1024 la carte de l'atelier (Quadro P2000, 4 Go) réussit une photo
    /// et échoue la SUIVANTE faute de mémoire. Deux passages MENÉS EN MÊME TEMPS demandent
    /// donc à la carte exactement ce qu'elle ne sait pas faire — et l'échec ne se rattrape
    /// pas ici : un dépassement de mémoire dans DirectML se produit sous le code managé,
    /// emporte le processus sans lever d'exception, et met parfois le pilote graphique à
    /// terre avec lui (l'écran clignote, Windows le redémarre).
    ///
    /// L'aperçu d'identité en lançait un par mouvement de curseur. Le verrou fait la queue.
    /// </summary>
    private static readonly object VerrouExecution = new();

    private static InferenceSession? _session;
    private static bool _tentative;

    /// <summary>
    /// Ce qu'a duré le chargement du réseau sur ce poste, ou null tant qu'il n'a pas été
    /// chargé.
    ///
    /// <b>Il ne se confond pas avec un détourage</b>, et c'est pourquoi il se mesure à part :
    /// un modèle de plusieurs centaines de mégaoctets posé sur la carte graphique prend
    /// bien plus de temps qu'un masque, et il ne se paie qu'UNE fois par séance. Mêlé aux
    /// autres mesures, il rendait toutes les estimations fausses — trop longues ensuite,
    /// bien trop courtes la première fois.
    /// </summary>
    public static TimeSpan? DureeDuChargement { get; private set; }

    /// <summary>
    /// Vrai quand le réseau est déjà en mémoire : le prochain détourage ne paie plus le
    /// chargement. C'est ce que l'écran doit savoir pour annoncer une attente juste.
    ///
    /// Faux aussi quand le chargement a échoué — le détourage passe alors par la méthode
    /// couleur, qui est autrement plus rapide et n'a rien à charger.
    /// </summary>
    public static bool SessionPrete
    {
        get { lock (Verrou) return _session is not null; }
    }

    /// <summary>Le fichier de modèle réellement chargé dans <see cref="_session"/>.</summary>
    private static string? _modeleCharge;

    /// <summary>
    /// Modèles écartés pour la séance, faute de mémoire vidéo.
    ///
    /// Vidé par <see cref="Reinitialiser"/> : changer de réglage doit redonner sa chance à
    /// un modèle, ne serait-ce que parce que le poste a pu changer de carte.
    /// </summary>
    private static readonly HashSet<string> _modelesEcartes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Dossiers où l'on cherche le modèle, dans l'ordre.</summary>
    public static IReadOnlyList<string> DossiersCherches { get; set; } =
    [
        Path.Combine(@"D:\PhotoStudioData", "models"),
        Path.Combine(AppContext.BaseDirectory, "models"),
    ];

    /// <summary>Vrai si le modèle est présent sur ce poste.</summary>
    public static bool EstInstalle => TrouverLeModele() is not null;

    /// <summary>
    /// Le chemin d'un modèle donné sur ce poste, ou null s'il n'y est pas.
    ///
    /// Public parce que l'écran des réglages doit pouvoir dire lequel est installé et
    /// lequel manque — sinon cocher « modèle puissant » n'a aucun effet visible, et rien
    /// n'explique pourquoi.
    /// </summary>
    public static string? CheminDuModele(string nomDeFichier)
    {
        foreach (var dossier in DossiersCherches)
        {
            var chemin = Path.Combine(dossier, nomDeFichier);
            if (File.Exists(chemin)) return chemin;
        }

        return null;
    }

    /// <summary>
    /// L'ordre de recherche réel : le modèle demandé d'abord, puis les autres — et jamais
    /// ceux qu'une panne de mémoire a fait écarter pour la séance.
    /// </summary>
    /// <summary>
    /// Le rang d'un modèle dans <see cref="ModelesAcceptes"/>, du plus léger au plus lourd.
    /// Un modèle inconnu passe pour le plus lourd : on ne se replie jamais dessus.
    /// </summary>
    private static int IndexDuModele(string nomDeFichier)
    {
        for (var i = 0; i < ModelesAcceptes.Count; i++)
            if (ModelesAcceptes[i].Equals(nomDeFichier, StringComparison.OrdinalIgnoreCase))
                return i;

        return int.MaxValue;
    }

    /// <remarks>
    /// La liste est CONSTRUITE sous le verrou, et non rendue paresseusement : l'écran des
    /// réglages l'interroge depuis le fil de l'interface pendant qu'un détourage peut
    /// écarter un modèle sur un autre fil, et un <c>HashSet</c> lu en cours d'écriture lève.
    /// </remarks>
    private static List<string> OrdreDeRecherche()
    {
        var ordre = ModelePrefere is { Length: > 0 } demande
            ? new[] { demande }.Concat(ModelesAcceptes.Where(m =>
                !m.Equals(demande, StringComparison.OrdinalIgnoreCase)))
            : ModelesAcceptes.AsEnumerable();

        lock (Verrou)
            return ordre.Where(m => !_modelesEcartes.Contains(m)).ToList();
    }

    /// <summary>
    /// Le fichier de modèle qui sera réellement chargé, ou null si aucun n'est installé.
    ///
    /// Sans effet de bord : l'écran des réglages l'interroge à chaque frappe, et une
    /// écriture au journal par affichage noierait le reste. La substitution est signalée
    /// une fois, au chargement de la session.
    /// </summary>
    public static string? ModeleRetenu =>
        OrdreDeRecherche().Select(CheminDuModele).FirstOrDefault(c => c is not null);

    private static string? TrouverLeModele() => ModeleRetenu;

    /// <summary>
    /// Masque d'opacité du sujet, à la taille de l'image, ou null si le réseau n'a pas pu
    /// répondre. Un null n'est pas une erreur : c'est le signal de retomber sur
    /// <see cref="BackgroundRemoval"/>.
    /// </summary>
    public static ImageMagick.MagickImage? CalculerMasque(ImageMagick.MagickImage image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (!Actif) return null;

        // DEUX TOURS AU PLUS. Le premier peut tomber en panne de mémoire vidéo ; le second
        // repart sur un modèle plus léger, qui vaut infiniment mieux que la règle par
        // couleur. Au-delà il n'y a plus rien à tenter, et une boucle devant un client
        // serait pire que le repli.
        for (var essai = 0; essai < 2; essai++)
        {
            var session = Session();
            if (session is null) return null;

            // relevé AVANT l'exécution : c'est ce modèle-là qu'on écartera s'il échoue,
            // et la session peut avoir changé entre-temps
            var modele = _modeleCharge;

            try
            {
                lock (VerrouExecution)
                    return Executer(session, image);
            }
            catch (Exception ex)
            {
                if (!EcarterEtReessayer(modele, ex)) return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Range la session morte et dit s'il reste quelque chose à tenter.
    ///
    /// <b>La session ne survit pas à son échec</b>, et c'est tout l'objet de cette méthode.
    /// Elle était conservée telle quelle : une seule panne de mémoire condamnait alors la
    /// séance ENTIÈRE. Créteil l'a payé le 12/08/2026 — un premier échec à 13:23:39 sur un
    /// <c>DmlFusedNode</c>, puis quatre appels de suite rendus en <c>[ErrorCode:Fail]</c>
    /// sec, jusqu'au redémarrage de l'application. L'opérateur voyait un cadrage parfait
    /// puis une planche détourée à la couleur, sans rien pour l'expliquer.
    ///
    /// <b>Et l'on ne retombe pas sur la couleur tant qu'il reste un modèle.</b> Une panne de
    /// mémoire ne dit rien du réseau, elle dit que CE modèle est trop gros pour cette carte :
    /// le « lite » tient là où le « portrait » déborde, et son contour reste très au-dessus
    /// de la règle par couleur. Le modèle fautif est écarté pour la séance — le reprendre à
    /// la photo suivante rejouerait la même panne, en faisant attendre à chaque fois.
    /// </summary>
    /// <returns>Vrai s'il faut refaire un tour avec un autre modèle.</returns>
    /// <remarks>
    /// Interne plutôt que privée : la règle de démotion ne se vérifie autrement qu'en
    /// mettant une carte graphique à genoux devant un client, ce qui est précisément la
    /// situation qu'elle existe pour rattraper.
    /// </remarks>
    internal static bool EcarterEtReessayer(string? modele, Exception ex)
    {
        lock (Verrou)
        {
            _session?.Dispose();
            _session = null;
            _tentative = false;
            _modeleCharge = null;

            if (modele is not null) _modelesEcartes.Add(modele);

            // UN REPLI NE SE FAIT QUE VERS PLUS LÉGER, et cette précision a coûté une
            // journée à Créteil le 12/08/2026 : le « lite » y est tombé en panne de
            // mémoire, et le seul modèle restant était le « portrait », DEUX FOIS ET DEMIE
            // plus lourd. Le journal annonçait « on reprend avec un modèle plus léger » en
            // chargeant le plus gros — qui a échoué à son tour, en faisant attendre.
            //
            // ModelesAcceptes est rangé du plus léger au plus lourd : un repli valable est
            // donc un modèle de RANG INFÉRIEUR à celui qui vient d'échouer.
            var rangFautif = modele is null
                ? int.MaxValue
                : IndexDuModele(modele);

            var repli = OrdreDeRecherche()
                .Where(m => IndexDuModele(m) < rangFautif)
                .FirstOrDefault(m => CheminDuModele(m) is not null);

            if (repli is not null)
            {
                DernierRepli =
                    $"Le modèle « {modele} » a manqué de mémoire vidéo sur cette carte : " +
                    $"Studio est repassé à « {repli} » pour la suite de la séance.";
                Log?.Invoke($"BiRefNet : échec du détourage ({ex.Message}) — « {modele} » " +
                            $"écarté pour la séance, on reprend avec « {repli} », plus léger.");
                return true;
            }

            DernierRepli =
                "Le détourage par réseau n'a pas pu s'exécuter : Studio est repassé à la " +
                "méthode par couleur, dont le contour est moins fin.";
            Log?.Invoke($"BiRefNet : échec du détourage ({ex.Message}) — plus aucun modèle " +
                        "disponible, repli sur la méthode par couleur.");
            return false;
        }
    }

    /// <summary>
    /// Ce qu'il faudrait dire à l'opérateur, ou null s'il n'y a rien à signaler.
    ///
    /// <b>Le repli était visible du seul journal.</b> C'est la leçon déjà écrite pour les
    /// messages d'erreur : ce qui n'est écrit nulle part ne se diagnostique pas, et ici
    /// c'était pire — l'écran ne montrait aucune anomalie, seulement un fond moins propre
    /// qu'à l'écran précédent. Les écrans d'identité lisent cette phrase et l'affichent.
    /// </summary>
    public static string? DernierRepli { get; private set; }

    /// <summary>
    /// La session, chargée une seule fois. Un CHARGEMENT raté n'est pas retenté : recharger
    /// un modèle de 490 Mo à chaque photo pour échouer pareil bloquerait le comptoir.
    ///
    /// Une EXÉCUTION ratée, elle, se reprend — mais sur un autre modèle, jamais le même :
    /// c'est <see cref="EcarterEtReessayer"/> qui rouvre la porte en remettant
    /// <see cref="_tentative"/> à faux.
    /// </summary>
    private static InferenceSession? Session()
    {
        lock (Verrou)
        {
            if (_tentative) return _session;
            _tentative = true;

            var chemin = TrouverLeModele();
            if (chemin is null)
            {
                Log?.Invoke($"BiRefNet : aucun modèle trouvé ({string.Join(" ou ", ModelesAcceptes)}) " +
                            "— détourage par la méthode couleur.");
                return null;
            }

            // Le modèle demandé dans Paramètres n'est pas celui qu'on charge : c'est dit
            // ICI, une fois. Sans cette ligne, cocher « modèle puissant » sans avoir posé
            // le fichier n'avait aucun effet visible, et rien n'expliquait pourquoi.
            if (ModelePrefere is { Length: > 0 } demande &&
                !Path.GetFileName(chemin).Equals(demande, StringComparison.OrdinalIgnoreCase))
                Log?.Invoke($"BiRefNet : « {demande} » demandé mais absent du poste — " +
                            $"« {Path.GetFileName(chemin)} » utilisé à sa place.");

            // La carte graphique d'abord (DirectML : pas de CUDA à installer, marche sur
            // n'importe quelle carte DirectX 12). Sur processeur, ce réseau demande des
            // dizaines de secondes par photo — inutilisable au comptoir, d'où le repli.
            try
            {
                var options = new SessionOptions();

                // DirectML exige ces deux réglages pour qu'une session serve PLUSIEURS
                // fois : sans eux, la première photo passe et la seconde échoue sur un
                // « DmlFusedNode » (constaté le 03/08/2026 sur la Quadro P2000). Le
                // réutilisateur de mémoire d'ONNX Runtime n'est pas compatible avec ce
                // fournisseur, et la documentation demande de le désactiver.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;

                options.AppendExecutionProvider_DML(0);

                // ⚠ CE CHARGEMENT SE COMPTE EN SECONDES, et il tombe sur le PREMIER
                // détourage de la séance — celui que l'opérateur attend devant un client.
                // La barre l'annonçait comme un détourage ordinaire (4,3 s) parce que rien
                // ne le mesurait à part : « le temps de chargement n'est toujours pas
                // représentatif du vrai temps que ça prend », 18/08/2026. On le chronomètre
                // donc, et l'écran l'ajoute tant que la session n'est pas prête.
                var chrono = System.Diagnostics.Stopwatch.StartNew();
                _session = new InferenceSession(chemin, options);
                DureeDuChargement = chrono.Elapsed;

                _modeleCharge = Path.GetFileName(chemin);
                Log?.Invoke($"BiRefNet : « {_modeleCharge} » chargé sur la carte graphique (DirectML) " +
                            $"en {DureeDuChargement.Value.TotalSeconds:0.0} s.");
                return _session;
            }
            catch (Exception ex)
            {
                Log?.Invoke($"BiRefNet : carte graphique indisponible ({ex.Message}) — " +
                            "détourage par la méthode couleur, qui reste instantanée.");
                _session = null;
                return null;
            }
        }
    }

    private static ImageMagick.MagickImage Executer(InferenceSession session, ImageMagick.MagickImage image)
    {
        var entree = session.InputMetadata.First();
        var sortie = session.OutputMetadata.First().Key;

        using var reduite = (ImageMagick.MagickImage)image.Clone();
        reduite.Resize(new ImageMagick.MagickGeometry(Cote, Cote) { IgnoreAspectRatio = true });

        using var mat = Cv2.ImDecode(
            reduite.ToByteArray(ImageMagick.MagickFormat.Png), ImreadModes.Color);

        using var rgb = new Mat();
        Cv2.CvtColor(mat, rgb, ColorConversionCodes.BGR2RGB);

        var valeurs = Preparer(rgb);

        // le modèle est en demi-précision : lui donner des flottants simples le ferait
        // refuser l'entrée
        NamedOnnxValue tenseur = entree.Value.ElementType == typeof(Float16)
            ? NamedOnnxValue.CreateFromTensor(entree.Key, EnDemiPrecision(valeurs))
            : NamedOnnxValue.CreateFromTensor(entree.Key,
                new DenseTensor<float>(valeurs, [1, 3, Cote, Cote]));

        using var resultat = session.Run([tenseur], [sortie]);
        var brut = resultat.First().AsTensor<Float16>() is { } demi
            ? demi.Select(v => (float)v).ToArray()
            : resultat.First().AsTensor<float>().ToArray();

        return EnMasque(brut, image.Width, image.Height);
    }

    /// <summary>
    /// Normalise et range en NCHW, l'ordre attendu par le réseau.
    ///
    /// Les pixels sont lus d'un bloc : l'indexeur pixel par pixel d'OpenCvSharp marshale
    /// chaque accès, ce qui coûte une éternité sur le million de pixels d'un 1024 × 1024.
    /// </summary>
    private static float[] Preparer(Mat rgb)
    {
        rgb.GetArray(out Vec3b[] pixels);

        var valeurs = new float[3 * Cote * Cote];
        var plan = Cote * Cote;

        for (var rang = 0; rang < plan; rang++)
        {
            var p = pixels[rang];
            valeurs[rang] = (p.Item0 / 255f - Moyenne[0]) / EcartType[0];
            valeurs[plan + rang] = (p.Item1 / 255f - Moyenne[1]) / EcartType[1];
            valeurs[2 * plan + rang] = (p.Item2 / 255f - Moyenne[2]) / EcartType[2];
        }

        return valeurs;
    }

    private static DenseTensor<Float16> EnDemiPrecision(float[] valeurs)
    {
        var demi = new Float16[valeurs.Length];
        for (var i = 0; i < valeurs.Length; i++) demi[i] = (Float16)valeurs[i];
        return new DenseTensor<Float16>(demi, [1, 3, Cote, Cote]);
    }

    /// <summary>
    /// Transforme la sortie brute en masque : sigmoïde (le réseau rend des logits), puis
    /// remise à la taille de la photo.
    /// </summary>
    private static ImageMagick.MagickImage EnMasque(float[] brut, uint largeur, uint hauteur)
    {
        var opacites = new byte[Cote * Cote];
        for (var i = 0; i < opacites.Length; i++)
        {
            var opacite = 1.0 / (1.0 + Math.Exp(-brut[i]));
            opacites[i] = (byte)Math.Clamp(Math.Round(opacite * 255), 0, 255);
        }

        using var masque = Mat.FromPixelData(Cote, Cote, MatType.CV_8UC1, opacites);
        Cv2.ImEncode(".png", masque, out var octets);

        var resultat = new ImageMagick.MagickImage(octets);
        resultat.Resize(new ImageMagick.MagickGeometry(largeur, hauteur) { IgnoreAspectRatio = true });
        return resultat;
    }
}
