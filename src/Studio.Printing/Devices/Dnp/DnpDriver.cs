using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Studio.Printing.Devices.Dnp;

/// <summary>
/// Interrogation et réglage des imprimantes à sublimation DNP (DS620, DS820, QW410)
/// via <c>cspstat.dll</c>.
///
/// À n'instancier que dans un processus 32 bits — la DLL est en x86.
///
/// Le tirage lui-même ne passe pas par ici : il emprunte le pilote Windows
/// (<c>BitmapPrinter</c> + <c>DevMode</c>). Cette classe sert à savoir si la machine
/// peut imprimer, combien de tirages restent sur le rouleau, et à appliquer finition,
/// découpe et vitesse avant l'envoi.
/// </summary>
public sealed class DnpDriver
{
    private const int ValueCapacity = 256;

    /// <summary>Imprimantes que la découverte peut rendre — la douzaine que prévoit DiLand.</summary>
    private const int MaxPrinters = 12;

    /// <summary>Type et identifiant d'unité : deux octets par machine dans le tampon.</summary>
    private const int OctetsParImprimante = 2;

    /// <summary>
    /// La bibliothèque du SDK DNP. Voir <see cref="CspStatInterop"/> : le poste porte
    /// aussi un <c>CPPCtrl32.dll</c> aux mêmes noms de fonctions, qui ne découvre AUCUNE
    /// imprimante à sublimation — elle sert aux imprimantes à cartes. DiLand appelle
    /// celle-ci.
    /// </summary>
    private const string SdkFileName = "cspstat.dll";

    /// <summary>Déclare où trouver le SDK DNP.</summary>
    public static void UseSdkFrom(string directory) => NativeSdkResolver.Register(SdkFileName, directory);

    /// <summary>
    /// Cherche le SDK DNP et le déclare s'il est trouvé. Comme celui de Fuji, il est
    /// livré avec DiLand et non avec Studio.
    /// </summary>
    public static string? LocateSdk() => NativeSdkResolver.Locate(SdkFileName, "STUDIO_DNP_SDK");

    /// <summary>Vrai si le SDK DNP est chargeable depuis ce poste.</summary>
    public static bool IsSdkInstalled()
    {
        if (NativeSdkResolver.DirectoryOf(SdkFileName) is not null)
            return NativeSdkResolver.Exists(SdkFileName);

        var loaded = NativeLibrary.TryLoad(SdkFileName, out var lib);
        if (loaded) NativeLibrary.Free(lib);
        return loaded;
    }

    /// <summary>
    /// Numéros de port des imprimantes DNP branchées.
    ///
    /// Le « numéro de port » attendu par toutes les autres fonctions est en réalité le
    /// RANG de la machine dans la découverte (0, 1, 2…), et non une valeur lue dans le
    /// tampon : celui-ci ne contient que le type et l'identifiant d'unité, deux octets par
    /// imprimante. C'est ainsi que DiLand procède (<c>DnpHelper.GetPrinters</c>).
    ///
    /// Corrigé le 03/08/2026 : on passait un <c>int[]</c> et une taille en éléments, et on
    /// prenait le CONTENU du tampon pour des numéros de port. La fonction rendait 0 — la
    /// DS620 restait invisible même DiLand fermé, ce qu'on mettait sur le compte du port
    /// USB tenu.
    /// </summary>
    public IReadOnlyList<int> ListPorts()
    {
        // DiLand n'appelle PAS SetPrinterFilter avant la découverte : on s'en tient à son
        // enchaînement, seul éprouvé sur cette machine.
        var taille = MaxPrinters * OctetsParImprimante;
        var tampon = Marshal.AllocHGlobal(taille);
        try
        {
            var trouvees = CspStatInterop.GetPrinterPortNum(tampon, taille);
            if (trouvees <= 0) return [];

            return Enumerable.Range(0, Math.Min(trouvees, MaxPrinters)).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(tampon);
        }
    }

    /// <summary>État courant d'une imprimante.</summary>
    public DnpStatus GetStatus(int portNumber) => new(CspStatInterop.GetStatus(portNumber));

    /// <summary>Instantané complet d'une imprimante : identité, état, média, compteurs.</summary>
    public DnpPrinterInfo GetPrinterInfo(int portNumber)
    {
        var capacite = CspStatInterop.GetInitialMediaCount(portNumber);

        return new DnpPrinterInfo(
            PortNumber: portNumber,
            SerialNumber: ReadString(sb => CspStatInterop.GetSerialNo(portNumber, sb)),
            FirmwareVersion: ReadString(sb => CspStatInterop.GetFirmwVersion(portNumber, sb)),
            Status: GetStatus(portNumber),
            MediaRemaining: CspStatInterop.GetMediaCounter(portNumber),
            MediaInitialCount: capacite,
            MediaSize: LireLeFormat(ReadString(sb => CspStatInterop.GetMedia(portNumber, sb)), capacite),
            MediaClass: ParseMediaClass(ReadString(sb => CspStatInterop.GetRfidMediaClass(portNumber, sb))),
            QueuedPrints: CspStatInterop.GetPQTY(portNumber),
            LifetimePrints: CspStatInterop.GetCounterA(portNumber));
    }

    /// <summary>
    /// Ce qu'il faut réclamer à la machine pour tirer cette image sur ce rouleau.
    ///
    /// <b>Un 10×15 sur un rouleau 15×20 doit être COUPÉ, sinon la moitié de la feuille est
    /// perdue.</b> Studio envoyait l'image sans rien dire du format : la DS620 sortait une
    /// feuille de 15×20 entière avec la planche d'identité dans le bas et du blanc au-dessus
    /// (Créteil, 10/08/2026). DiLand, lui, réclame la découpe — ses propres compteurs le
    /// disent : 138 feuilles restantes annoncées comme 275 tirages 10×15, soit deux par
    /// feuille.
    ///
    /// <see cref="DnpMediaSize.Size6x4x2"/> est exactement cela : deux 6×4 sur un 6×8.
    ///
    /// Hors de ce cas, on réclame le rouleau lui-même : c'est ce que la machine faisait
    /// déjà par défaut, et Maisons-Alfort tire juste ainsi depuis toujours.
    /// </summary>
    /// <param name="rouleau">Le format chargé, tel que <see cref="LireLeFormat"/> le donne.</param>
    /// <param name="largeurPouces">Largeur de l'image à tirer.</param>
    /// <param name="hauteurPouces">Hauteur de l'image à tirer.</param>
    public static DnpMediaSize TailleDeTirage(
        DnpMediaSize rouleau, double largeurPouces, double hauteurPouces)
    {
        // <b>On réclame Size6x4, JAMAIS Size6x4x2.</b> Les deux existent et ne font pas la
        // même chose :
        //
        //   Size6x4    « fais-moi un 10×15 » — la machine coupe et garde le reste du rouleau
        //   Size6x4x2  « voici une PAIRE »   — elle attend la seconde image avant d'imprimer
        //
        // Le second a bloqué Créteil le 10/08/2026 : la machine ACCEPTE l'envoi —
        // SendImageData rend 1, le journal annonce un succès — puis ne sort rien, la
        // première moitié restant en mémoire.
        //
        // Que le premier suffise n'est pas une supposition : les compteurs de DiLand, sur
        // cette machine et ce rouleau, passent de 138 feuilles (276 tirages 10×15) à 275
        // après UNE planche. Un seul tirage consommé, une seule image envoyée, coupée.
        var grand = Math.Max(largeurPouces, hauteurPouces);
        var petit = Math.Min(largeurPouces, hauteurPouces);

        // Tolérance large : un 10×15 rendu à 300 ppp fait 6,15 × 4,13 pouces, et les
        // gabarits d'identité débordent volontairement de quelques dixièmes.
        var estUn6x4 = grand is >= 5.5 and <= 6.6 && petit is >= 3.6 and <= 4.6;

        return rouleau == DnpMediaSize.Size6x8 && estUn6x4
            ? DnpMediaSize.Size6x4
            : rouleau;
    }

    /// <summary>
    /// La définition à retenir pour juger de la TAILLE PHYSIQUE d'un tirage, en points par
    /// pouce.
    ///
    /// <b>⚠ C'est cette mesure qui décide de la DÉCOUPE</b> (voir <see cref="TailleDeTirage"/>),
    /// et l'appelant y renonçait trop vite : quand <c>GetResolution</c> rendait zéro — machine
    /// qui sort de veille, port occupé, SDK muet — il se repliait sur « le format du
    /// rouleau », c'est-à-dire AUCUNE découpe. Une planche d'identité 6×4 sortait alors sur
    /// une feuille 6×8 entière, à moitié blanche, sans que rien ne le signale : le journal
    /// annonçait sobrement « format demandé Size6x8 ».
    ///
    /// Relevé sur kodakidpc le 17/08/2026, à quatre minutes d'intervalle et sur la MÊME image
    /// de 1844 × 1240 : à 16:17 « format demandé Size6x4 » (juste), à 16:22 « Size6x8 » — une
    /// demi-feuille perdue. Seule la réponse de la machine avait changé.
    ///
    /// <b>⚠ ET LE 17/08 ON A CRU QUE LA MACHINE SE TAISAIT. C'EST FAUX : ELLE RÉPOND UN
    /// RÉGLAGE.</b> <c>GetResolutionH/V</c> ne dit pas « je compose à tant de points par
    /// pouce » une fois pour toutes — elle rend la définition RÉGLÉE dans la machine pour le
    /// travail suivant (le SDK a le <c>SetResolution</c> qui va avec), et n'importe quel
    /// logiciel qui partage cette DS620 la change sous nos pieds. Sur kodakidpc, IDMaker
    /// tourne à côté de Studio sur la même machine.
    ///
    /// La preuve, relevée le 19/08/2026 : sur les journaux de kodakidpc, la ligne « la DNP
    /// n'annonce pas sa définition » ne paraît <b>pas une seule fois</b> — la machine a donc
    /// toujours répondu du plausible — et pourtant six planches 1844 × 1240 identiques sont
    /// parties, les unes en <c>Size6x4</c>, les autres en <c>Size6x8</c> le même après-midi.
    /// Une machine réglée à 600 ppp mesure notre trame 3,07 × 2,07 pouces : plus un 6×4, donc
    /// plus de découpe, donc une feuille 15 × 20 entière pour une planche 10 × 15.
    ///
    /// <b>Le FICHIER d'abord, donc.</b> Il ne bouge pas, LUI : Studio rend ses pages à la
    /// définition du produit et l'écrit dans le PNG, et cette trame est fabriquée pour la
    /// machine — sa taille physique, c'est celle-là. La machine ensuite, si le fichier ne
    /// porte rien de plausible ; 300 ppp en dernier ressort, définition de toutes les DNP de
    /// la boutique.
    /// </summary>
    /// <param name="machineH">Ce que rend <c>GetResolutionH</c>, ou 0 si elle se tait.</param>
    /// <param name="machineV">Ce que rend <c>GetResolutionV</c>, ou 0 si elle se tait.</param>
    /// <param name="fichierH">Définition horizontale inscrite dans l'image.</param>
    /// <param name="fichierV">Définition verticale inscrite dans l'image.</param>
    public static (double H, double V) DefinitionRetenue(
        double machineH, double machineV, double fichierH, double fichierV)
    {
        if (Plausible(fichierH) && Plausible(fichierV)) return (fichierH, fichierV);
        if (Plausible(machineH) && Plausible(machineV)) return (machineH, machineV);

        return (DefinitionParDefaut, DefinitionParDefaut);
    }

    /// <summary>Définition de repli : celle de toutes les DNP de la boutique.</summary>
    public const double DefinitionParDefaut = 300.0;

    /// <summary>
    /// Large, mais pas crédule : une DNP tire à 300 ou 600 ppp, jamais à 12 ni à 12 000.
    /// Le zéro d'une machine muette tombe évidemment hors bornes, et c'est le cas qui compte.
    /// </summary>
    private static bool Plausible(double ppp) => ppp is >= 100 and <= 1200;


    /// <summary>
    /// Les cotes du format déclaré, en pouces : (largeur du ROULEAU, longueur tirée).
    ///
    /// Le nom de l'énumération les porte déjà — <c>Size6x4</c>, c'est six pouces de rouleau
    /// et quatre de longueur — mais on les écrit ici plutôt que de disséquer un nom : une
    /// valeur qu'on ne connaît pas doit rendre null et ne rien faire, pas produire un
    /// nombre inventé sur une machine qu'on n'a jamais vue.
    /// </summary>
    private static (double Rouleau, double Longueur)? CotesEnPouces(DnpMediaSize taille) =>
        taille switch
        {
            DnpMediaSize.Size5x3 => (5, 3),
            DnpMediaSize.Size5x5 => (5, 5),
            DnpMediaSize.Size5x7 => (5, 7),
            DnpMediaSize.Size6x4 => (6, 4),
            DnpMediaSize.Size6x4p5 => (6, 4.5),
            DnpMediaSize.Size6x6 => (6, 6),
            DnpMediaSize.Size6x8 => (6, 8),
            DnpMediaSize.Size6x9 => (6, 9),
            DnpMediaSize.Size8x4 => (8, 4),
            DnpMediaSize.Size8x5 => (8, 5),
            DnpMediaSize.Size8x6 => (8, 6),
            DnpMediaSize.Size8x8 => (8, 8),
            DnpMediaSize.Size8x10 => (8, 10),
            DnpMediaSize.Size8x12 => (8, 12),
            _ => null,
        };

    /// <summary>
    /// La trame doit-elle être pivotée d'un quart de tour avant d'être remise au SDK ?
    ///
    /// <b>⚠ LA MACHINE N'ORIENTE RIEN.</b> Elle attend une trame dont la LARGEUR est celle du
    /// rouleau : pour un 6×4, du 1844 × 1240 — six pouces en largeur, quatre en longueur. On
    /// lui envoyait l'image telle que le rendu l'avait faite, et pour un produit PORTRAIT
    /// c'est l'inverse : 1240 × 1844. La machine lit alors la trame en travers et coupe ce
    /// qui dépasse.
    ///
    /// Signalé le 18/08/2026, commande 18-006 : une E-Photo portrait sortie coupée en
    /// paysage. Le rendu, lui, était juste — 1240 × 1844 pour un produit de 105 × 156,1 mm,
    /// photo entière, aucun recadrage. Tout s'est joué à l'envoi.
    ///
    /// <b>Pourquoi personne ne l'avait vu.</b> Le seul produit DNP portrait de la boutique
    /// est l'E-Photo, et elle n'était pas joignable depuis le poste identité jusqu'au
    /// 17/08. Les planches d'identité, elles, sont en 156,1 × 105 — donc déjà dans le sens
    /// de la trame, et elles sortent juste depuis des semaines.
    ///
    /// Format inconnu : on ne pivote pas. Deviner sur une machine qu'on n'a jamais vue
    /// coûterait une feuille à chaque tirage.
    /// </summary>
    public static bool DoitPivoter(DnpMediaSize taille, int largeurImage, int hauteurImage)
    {
        if (largeurImage <= 0 || hauteurImage <= 0) return false;
        if (CotesEnPouces(taille) is not { } cotes) return false;

        // Un format carré n'a pas de sens à défendre, et une image carrée non plus.
        if (Math.Abs(cotes.Rouleau - cotes.Longueur) < 0.01) return false;
        if (largeurImage == hauteurImage) return false;

        var trameCouchee = cotes.Rouleau > cotes.Longueur;
        var imageCouchee = largeurImage > hauteurImage;

        return trameCouchee != imageCouchee;
    }
    /// <summary>Tirages restants sur le rouleau chargé.</summary>
    public int GetMediaRemaining(int portNumber) => CspStatInterop.GetMediaCounter(portNumber);

    /// <summary>Nombre de tirages en attente dans la mémoire de l'imprimante.</summary>
    public int GetQueuedPrints(int portNumber) => CspStatInterop.GetPQTY(portNumber);

    /// <summary>Mémoire libre de l'imprimante, en octets.</summary>
    public int GetFreeBuffer(int portNumber) => CspStatInterop.GetFreeBuffer(portNumber);

    /// <summary>Résolution de l'imprimante, en points par pouce (horizontale, verticale).</summary>
    public (int Horizontal, int Vertical) GetResolution(int portNumber) =>
        (CspStatInterop.GetResolutionH(portNumber), CspStatInterop.GetResolutionV(portNumber));

    /// <summary>Applique la finition de surface au(x) tirage(s) suivant(s).</summary>
    public void SetOvercoat(int portNumber, DnpOvercoat overcoat) =>
        CspStatInterop.SetOvercoatFinish(portNumber, (int)overcoat);

    /// <summary>Applique le mode de découpe.</summary>
    public void SetCutter(int portNumber, DnpCutter cutter) =>
        CspStatInterop.SetCutterMode(portNumber, (int)cutter);

    /// <summary>Applique la vitesse d'impression.</summary>
    public void SetPrintSpeed(int portNumber, DnpPrintSpeed speed) =>
        CspStatInterop.SetPrintSpeed(portNumber, (int)speed);

    /// <summary>Déclare le format de média chargé.</summary>
    public void SetMediaSize(int portNumber, DnpMediaSize media) =>
        CspStatInterop.SetMediaSize(portNumber, (int)media);

    /// <summary>Active l'anti-tuilage du papier en sortie.</summary>
    public void SetDecurl(int portNumber, bool enabled) =>
        CspStatInterop.SetDecurlCtrl(portNumber, enabled ? 1 : 0);

    /// <summary>Délai d'attente USB, en millisecondes.</summary>
    public void SetUsbTimeout(int portNumber, int milliseconds) =>
        CspStatInterop.SetUSBTimeout(portNumber, milliseconds);

    private static string ReadString(Func<StringBuilder, int> read)
    {
        var buffer = new StringBuilder(ValueCapacity);
        read(buffer);
        return buffer.ToString().Trim();
    }

    /// <summary>
    /// Le format du rouleau chargé.
    ///
    /// <b>Le libellé de <c>GetMedia</c> n'est PAS un numéro de format.</b> C'est une
    /// référence de consommable — deux notions différentes, et c'est ce qui a fait échouer
    /// la première version. Elle retenait « les trois premiers chiffres portent le
    /// format », règle déduite du seul rouleau de Maisons-Alfort (« 00301 » → 003 →
    /// <see cref="DnpMediaSize.Size6x4"/>, juste par coïncidence). Créteil rend « 00310 »,
    /// qui donnait le même 003 : Studio y a cru à un 10×15 pendant que la machine tirait
    /// sur du 15×20, et les planches d'identité sortaient sur une demi-feuille (10/08/2026).
    ///
    /// On ne devine donc plus : une table des codes RELEVÉS sur les machines, et à défaut
    /// la CAPACITÉ du rouleau, qui tranche sans ambiguïté — une DS620 tire 400 fois sur un
    /// 10×15 et 200 fois sur un 15×20. C'est cette mesure qui a fini par départager les
    /// deux boutiques, quand le code média, l'étiquette du rouleau et le souvenir de
    /// l'exploitant se contredisaient tous les trois.
    /// </summary>
    /// <param name="codeMedia">Ce que rend <c>GetMedia</c>.</param>
    /// <param name="capaciteInitiale">Ce que rend <c>GetInitialMediaCount</c>.</param>
    internal static DnpMediaSize LireLeFormat(string? codeMedia, int capaciteInitiale)
    {
        var connu = (codeMedia ?? "").Trim() switch
        {
            "00301" => DnpMediaSize.Size6x4,   // Maisons-Alfort, 400 tirages
            "00310" => DnpMediaSize.Size6x8,   // Créteil, 200 tirages
            _ => DnpMediaSize.None,
        };

        if (connu != DnpMediaSize.None) return connu;

        // Repli sur la capacité. Les bornes sont larges : un rouleau entamé rend le nombre
        // RESTANT sur certaines machines, et mieux vaut ne rien affirmer que se tromper.
        return capaciteInitiale switch
        {
            >= 300 and <= 500 => DnpMediaSize.Size6x4,
            >= 150 and <= 250 => DnpMediaSize.Size6x8,
            _ => DnpMediaSize.None,
        };
    }

    /// <summary>
    /// La classe de média lue sur la puce RFID du rouleau.
    ///
    /// Elle sort en CHIFFRES, pas en lettres : « 0002 » sur le rouleau de la boutique. Les
    /// libellés RX / HQL / HDM sont acceptés en plus, sans preuve qu'une machine les rende
    /// un jour — ils ne coûtent rien et évitent d'avoir à y revenir.
    /// </summary>
    private static DnpMediaClass ParseMediaClass(string value)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            && Enum.IsDefined(typeof(DnpMediaClass), n))
            return (DnpMediaClass)n;

        return value.ToUpperInvariant() switch
        {
            "RX" => DnpMediaClass.Rx,
            "HQL" => DnpMediaClass.Hql,
            "HDM" => DnpMediaClass.Hdm,
            _ => DnpMediaClass.Unknown,
        };
    }
}
