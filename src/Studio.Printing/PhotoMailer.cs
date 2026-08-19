using System.Net;
using System.Net.Mail;
using ImageMagick;
using Studio.Core.Domain;
using Studio.Core.Mail;
using Studio.Imaging;

namespace Studio.Printing;

/// <summary>Les trois fichiers préparés pour le client.</summary>
/// <param name="NonRecadree">La photo d'origine, entière — le client peut tout refaire.</param>
/// <param name="BasseDefinition">Le cadrage retenu, léger : formulaires en ligne, courriel.</param>
/// <param name="HauteDefinition">Le cadrage retenu, pleine résolution : pour faire tirer ailleurs.</param>
public sealed record PhotosDuClient(string NonRecadree, string BasseDefinition, string HauteDefinition)
{
    public IReadOnlyList<string> Tous => [NonRecadree, BasseDefinition, HauteDefinition];
}

/// <summary>
/// Envoie au client ses photos par courriel.
///
/// Trois fichiers, parce qu'ils ne servent pas à la même chose : l'original entier pour
/// qu'il puisse recadrer autrement, une version légère pour les téléversements en ligne
/// (les sites d'administration refusent presque tous au-delà de quelques centaines de
/// kilo-octets), et le cadrage en pleine résolution s'il veut faire tirer ailleurs.
///
/// Les deux versions recadrées gardent LE RATIO DU CADRAGE, sans être reposées dans un
/// canevas au format du papier : le client reçoit sa photo, pas une planche.
/// </summary>
public static class PhotoMailer
{
    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>Grand côté de la version légère, en pixels.</summary>
    private const int GrandCoteBasseDefinition = 1200;

    /// <summary>
    /// Prépare les trois fichiers dans <paramref name="dossier"/> et rend leurs chemins.
    /// </summary>
    /// <param name="sourcePath">Photo d'origine.</param>
    /// <param name="crop">Cadrage retenu par l'opérateur.</param>
    /// <param name="rotationQuarterTurns">Quarts de tour appliqués.</param>
    /// <param name="fineRotationDegrees">Redressement fin, en degrés.</param>
    /// <param name="adjustments">Corrections d'image, fond blanc compris.</param>
    /// <param name="dossier">Où déposer les fichiers.</param>
    /// <param name="nomDeBase">Racine des noms de fichiers, sans extension.</param>
    public static PhotosDuClient Preparer(
        string sourcePath,
        CropSpec crop,
        int rotationQuarterTurns,
        double fineRotationDegrees,
        ImageAdjustments adjustments,
        string dossier,
        string nomDeBase)
    {
        ArgumentNullException.ThrowIfNull(adjustments);
        Directory.CreateDirectory(dossier);

        var original = Path.Combine(dossier, $"{nomDeBase}-originale.jpg");
        var basse = Path.Combine(dossier, $"{nomDeBase}-recadree-web.jpg");
        var haute = Path.Combine(dossier, $"{nomDeBase}-recadree-hd.jpg");

        // L'original est réécrit plutôt que copié : on applique l'orientation EXIF, sans
        // quoi la photo arrive couchée chez les clients dont la visionneuse ignore la
        // balise — c'est le cas de plusieurs webmails.
        // par MagickInit : la source est la photo du client, donc souvent sa carte, et
        // c'est la projection en mémoire d'un support retiré qui tue le processus
        // (voir MagickInit.Lire)
        //
        // ⚠ SAUF QUAND IL N'Y A RIEN À REDRESSER, et c'est le cas le plus fréquent. Une
        // photo sans étiquette de rotation ressort de l'AutoOrient identique à
        // elle-même : on payait alors un décodage ET un réencodage complets de 24 Mpx pour
        // récrire le même dessin. La copie octet pour octet est plus rapide, et elle est
        // même MEILLEURE — le client reçoit son fichier d'origine intact, ce que « la photo
        // d'origine, entière » promet, au lieu d'un JPEG réencodé une génération plus loin.
        // ⚠ CHAQUE ÉTAPE EST CHRONOMÉTRÉE, ET C'EST CE QUI MANQUAIT.
        //
        // « L'envoi par courriel prend jusqu'à 1 min 30 » : le journal d'Arcueil disait bien
        // « 1 photo préparée en 76,6 s », mais rien de plus — impossible de savoir si le
        // temps partait dans le détourage, dans la lecture de la carte du client ou dans le
        // rendu. Trois lignes de plus, et la prochaine plainte se diagnostique sans se
        // déplacer.
        var chrono = System.Diagnostics.Stopwatch.StartNew();

        // ⚠ ET LE FOND DÉTOURÉ VAUT AUSSI POUR LA PHOTO ENTIÈRE.
        //
        // Arcueil, 19/08/2026, commande 19-002 : l'opérateur pose un fond blanc pour une
        // photo canadienne, l'envoie au client — et le client ouvre une pièce jointe où le
        // fond gris du studio est toujours là. Les deux fichiers RECADRÉS étaient bien
        // détourés ; celui-ci, non, parce qu'il prenait le raccourci de la copie octet pour
        // octet. « Il a fait un détourage fond blanc, qui n'a pas été gardé dans l'envoi. »
        //
        // Le raccourci reste bon quand il n'y a pas de fond à poser — c'est le cas courant,
        // et le client reçoit alors son fichier d'origine intact. Mais un fond demandé est
        // un fond demandé : il vaut pour les trois fichiers, sinon le client trie lui-même
        // et se demande lequel est le bon.
        //
        // Le masque, lui, ne coûte rien de plus : c'est celui que le cadrage vient
        // d'employer, repris en mémoire sous la même clé (voir MasqueSujet).
        var fondADeposer = adjustments.GrayBackground
            ? BackgroundRemoval.GrisIdentite
            : adjustments.WhiteBackground ? MagickColors.White : null;

        if (fondADeposer is null && SansRedressementAFaire(sourcePath))
            File.Copy(sourcePath, original, overwrite: true);
        else
            using (var entiere = MagickInit.Lire(sourcePath, 0))
            {
                entiere.AutoOrient();

                if (fondADeposer is not null
                    && !BackgroundRemoval.PoserUnFond(entiere, fondADeposer, adjustments.CleDeLaPhoto))
                    Log?.Invoke(
                        "Courriel : le fond n'a pas pu être posé sur la photo entière — elle " +
                        "part telle quelle. Les deux fichiers recadrés, eux, sont détourés.");

                MagickInit.Write(entiere, original);
            }

        var apresOriginal = Prendre(chrono);

        // Le cadrage passe par le pipeline de rendu : rotation, redressement, recadrage et
        // corrections y sont appliqués dans le bon ordre, et le client reçoit donc
        // exactement ce que l'opérateur a vu à l'écran.
        RendreLeCadrage(sourcePath, crop, rotationQuarterTurns, fineRotationDegrees,
            adjustments, haute);

        var apresHaute = Prendre(chrono);

        // la version légère se tire de la haute définition plutôt que d'un second rendu :
        // deux rendus d'une photo de 24 Mpx coûteraient le double pour un résultat
        // identique au rééchantillonnage près
        using (var legere = new MagickImage(haute))
        {
            legere.Resize(new MagickGeometry((uint)GrandCoteBasseDefinition, (uint)GrandCoteBasseDefinition));
            MagickInit.Write(legere, basse);
        }

        Log?.Invoke(
            $"Courriel · {Path.GetFileName(sourcePath)} : original {apresOriginal:0.0} s · " +
            $"cadrage haute définition {apresHaute:0.0} s · version légère {Prendre(chrono):0.0} s" +
            (adjustments.WhiteBackground || adjustments.GrayBackground
                ? " (fond détouré compris)"
                : ""));

        return new PhotosDuClient(original, basse, haute);
    }

    /// <summary>Le temps écoulé depuis la dernière prise, en secondes, et l'on repart de zéro.</summary>
    private static double Prendre(System.Diagnostics.Stopwatch chrono)
    {
        var ecoule = chrono.Elapsed.TotalSeconds;
        chrono.Restart();
        return ecoule;
    }

    /// <summary>
    /// Rend le cadrage à sa taille naturelle, sans le reposer dans un format de papier :
    /// le client reçoit sa photo, pas une planche.
    ///
    /// La cible en pixels est celle du cadrage lui-même, donc le fichier garde LE RATIO DU
    /// CADRAGE et toute la résolution que la photo d'origine permet.
    /// </summary>
    /// <summary>
    /// L'orientation EXIF de cette photo la laisse-t-elle telle quelle ?
    ///
    /// Seules <c>Undefined</c> et <c>TopLeft</c> ne demandent RIEN — les six autres valeurs
    /// tournent l'image d'un quart de tour, la retournent en miroir, ou les deux. Dans le
    /// doute (fichier illisible, format sans en-tête EXIF) on répond NON : redresser une
    /// photo qui n'en avait pas besoin ne coûte que du temps, tandis que copier telle quelle
    /// une photo couchée l'envoie couchée au client.
    /// </summary>
    private static bool SansRedressementAFaire(string sourcePath)
    {
        try
        {
            MagickInit.Configure();

            using var entete = new MagickImage();
            entete.Ping(sourcePath);

            return entete.Orientation is OrientationType.Undefined or OrientationType.TopLeft;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RendreLeCadrage(
        string sourcePath, CropSpec crop, int rotationQuarterTurns,
        double fineRotationDegrees, ImageAdjustments adjustments, string sortie)
    {
        // ⚠ ON PINGUE L'EN-TÊTE, ON NE DÉCODE PAS.
        //
        // Cette mesure-là ouvrait la photo EN ENTIER — `MagickInit.Lire(sourcePath, 0)` —
        // pour ne lire que deux nombres, puis la jetait ; `RenderToFile` la relisait juste
        // après pour de vrai. Sur un fichier d'appareil de 50 Mo c'est un décodage complet
        // de trop, et sur une CARTE MÉMOIRE c'est pire : `MagickInit.Lire` en copie d'abord
        // tous les octets en mémoire pour survivre à un retrait de carte. Or au comptoir la
        // photo du client est justement sur sa carte. « L'envoi par courriel est
        // extrêmement long », signalé le 17/08/2026.
        //
        // <b>Et la mesure était FAUSSE dès qu'on avait pivoté la photo</b> : elle appliquait
        // l'orientation EXIF mais pas les quarts de tour de l'opérateur, alors que le
        // cadrage, lui, se rapporte à l'image pivotée. Sur une photo tournée d'un quart de
        // tour, largeur et hauteur étaient donc échangées et le fichier envoyé au client
        // sortait à une définition qui n'était pas la sienne.
        //
        // GetOrientedSize répond aux deux : elle pingue les en-têtes, applique l'EXIF ET les
        // quarts de tour, sans décoder un seul pixel.
        var (mesureL, mesureH) = ImagePipeline.GetOrientedSize(sourcePath, rotationQuarterTurns);

        var largeur = Math.Max(1, (int)Math.Round(crop.Width * mesureL));
        var hauteur = Math.Max(1, (int)Math.Round(crop.Height * mesureH));

        var demande = new RenderRequest(
            sourcePath, largeur, hauteur, crop,
            rotationQuarterTurns, fineRotationDegrees,
            FitMode.Fill, 0, adjustments);

        ImagePipeline.RenderToFile(demande, sortie, dpi: 300);
    }

    /// <summary>
    /// Envoie les fichiers à <paramref name="destinataire"/>.
    ///
    /// Ne rattrape rien : un envoi qui échoue doit remonter jusqu'à l'écran, avec sa
    /// raison. Un client qui repart en croyant avoir reçu ses photos revient le lendemain.
    /// </summary>
    public static void Envoyer(
        MailSettings reglages,
        string destinataire,
        PhotosDuClient photos,
        string? motDuPhotographe = null) =>
        Envoyer(reglages, [destinataire], photos, motDuPhotographe);

    /// <summary>
    /// Envoie les fichiers à PLUSIEURS adresses.
    ///
    /// UN seul message part, et un seul envoi SMTP : les pièces jointes d'une photo
    /// d'identité pèsent plusieurs mégaoctets, et les téléverser une fois par adresse
    /// ferait attendre le comptoir pour rien.
    ///
    /// La première adresse est en <c>To</c>, les autres en <c>Cci</c>. Ce n'est pas une
    /// précaution abstraite : au comptoir, « envoyez-le aussi à ma fille » et « envoyez-le
    /// aussi au photographe du mariage » se tapent dans la même case, et rien à l'écran ne
    /// dit si les gens se connaissent. Le <c>Cci</c> ne dévoile aucune adresse ; le
    /// <c>Cc</c> les dévoilerait toutes, et cela ne se rattrape pas.
    /// </summary>
    public static void Envoyer(
        MailSettings reglages,
        IReadOnlyList<string> destinataires,
        PhotosDuClient photos,
        string? motDuPhotographe = null)
    {
        ArgumentNullException.ThrowIfNull(reglages);
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(destinataires);

        if (!reglages.EstUtilisable)
            throw new InvalidOperationException(
                "L'envoi par courriel n'est pas configuré : " + reglages.CeQuiManque() +
                ".\n\nOuvrez Paramètres → Envoi par courriel pour le renseigner.");

        var propres = destinataires
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();

        if (propres.Count == 0)
            throw new ArgumentException("Aucune adresse de destination.", nameof(destinataires));

        foreach (var fichier in photos.Tous)
            if (!File.Exists(fichier))
                throw new FileNotFoundException($"Fichier à envoyer introuvable : {fichier}", fichier);

        using var message = new MailMessage
        {
            From = new MailAddress(reglages.Expediteur, reglages.NomExpediteur),
            Subject = "Vos photos",
            Body = Corps(motDuPhotographe),
            IsBodyHtml = false,
        };

        message.To.Add(propres[0]);
        foreach (var autre in propres.Skip(1))
            message.Bcc.Add(autre);

        // les pièces jointes tiennent des flux ouverts jusqu'à l'envoi : on les libère
        // avec le message
        foreach (var fichier in photos.Tous)
            message.Attachments.Add(new Attachment(fichier));

        Expedier(reglages, message, string.Join(", ", propres), photos.Tous.Count);
    }

    /// <summary>
    /// Envoie PLUSIEURS lots de photos — un message par lot, mais UNE SEULE connexion.
    ///
    /// <b>Pourquoi cette méthode existe.</b> L'écran d'envoi appelait <see cref="Envoyer"/>
    /// dans une boucle, et chaque appel ouvrait son propre <see cref="SmtpClient"/> : une
    /// connexion TCP, une négociation TLS et une authentification Gmail PAR PHOTO. Sur
    /// trois photos, c'est trois fois le prix d'entrée avant même de téléverser le premier
    /// octet. « L'envoi des photos par mail est extrêmement long », 18/08/2026.
    ///
    /// <b>Un message par lot, et non un seul message pour tout.</b> Les trois fichiers
    /// d'une photo pèsent plusieurs mégaoctets ; tout réunir dépasserait vite les 25 Mo que
    /// Gmail accepte, et l'envoi entier serait refusé au lieu d'une photo. Le découpage
    /// protège donc le comptoir — c'est la connexion qu'il fallait mutualiser, pas le
    /// message.
    /// </summary>
    /// <param name="lots">Les fichiers préparés, une entrée par photo.</param>
    public static void EnvoyerPlusieurs(
        MailSettings reglages,
        IReadOnlyList<string> destinataires,
        IReadOnlyList<PhotosDuClient> lots,
        string? motDuPhotographe = null)
    {
        ArgumentNullException.ThrowIfNull(reglages);
        ArgumentNullException.ThrowIfNull(destinataires);
        ArgumentNullException.ThrowIfNull(lots);

        if (lots.Count == 0) return;

        // un seul lot : rien à mutualiser, on passe par le chemin ordinaire
        if (lots.Count == 1)
        {
            Envoyer(reglages, destinataires, lots[0], motDuPhotographe);
            return;
        }

        if (!reglages.EstUtilisable)
            throw new InvalidOperationException(
                "L'envoi par courriel n'est pas configuré : " + reglages.CeQuiManque() +
                ".\n\nOuvrez Paramètres → Envoi par courriel pour le renseigner.");

        var propres = destinataires
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToList();

        if (propres.Count == 0)
            throw new ArgumentException("Aucune adresse de destination.", nameof(destinataires));

        var chrono = System.Diagnostics.Stopwatch.StartNew();
        var octets = 0L;

        using var client = Client(reglages);

        for (var i = 0; i < lots.Count; i++)
        {
            var photos = lots[i];

            foreach (var fichier in photos.Tous)
                if (!File.Exists(fichier))
                    throw new FileNotFoundException($"Fichier à envoyer introuvable : {fichier}", fichier);

            using var message = new MailMessage
            {
                From = new MailAddress(reglages.Expediteur, reglages.NomExpediteur),
                // numéroté : trois messages « Vos photos » côte à côte dans une boîte de
                // réception ne se distinguent pas, et le client croit à un doublon
                Subject = $"Vos photos ({i + 1}/{lots.Count})",
                Body = Corps(motDuPhotographe),
                IsBodyHtml = false,
            };

            message.To.Add(propres[0]);
            foreach (var autre in propres.Skip(1))
                message.Bcc.Add(autre);

            foreach (var fichier in photos.Tous)
            {
                message.Attachments.Add(new Attachment(fichier));
                octets += new FileInfo(fichier).Length;
            }

            Expedier(reglages, message, string.Join(", ", propres), photos.Tous.Count, client);
        }

        Log?.Invoke(
            $"Courriel : {lots.Count} message(s), {octets / 1024 / 1024} Mo téléversés " +
            $"en {chrono.Elapsed.TotalSeconds:0.0} s sur UNE connexion.");
    }

    /// <summary>
    /// Prévient le client que sa commande est prête à être retirée en magasin.
    ///
    /// <b>Aucune pièce jointe</b>, et c'est tout le sujet : ce message ne livre pas les
    /// photos, il annonce qu'elles attendent au comptoir. Les joindre reviendrait à les
    /// donner sans les vendre — l'envoi des fichiers est une prestation à part, facturée,
    /// qui passe par <see cref="Envoyer(MailSettings, IReadOnlyList{string}, PhotosDuClient, string?)"/>.
    ///
    /// Il emprunte la même voie SMTP que le reste : un serveur qui accepte l'un accepte
    /// l'autre, et les refus se traduisent au même endroit.
    /// </summary>
    /// <param name="reglages">Configuration du poste.</param>
    /// <param name="destinataire">Adresse du client.</param>
    /// <param name="numero">Numéro de commande, tel que le client le lira sur son ticket.</param>
    /// <param name="quoi">Ce qui l'attend, en clair : « 24 tirages 10×15 ».</param>
    /// <param name="nomClient">Nom du client, pour l'en-tête ; vide = formule neutre.</param>
    /// <param name="mot">Mot libre ajouté par l'opérateur, ou null.</param>
    public static void PrevenirCommandePrete(
        MailSettings reglages,
        string destinataire,
        string numero,
        string quoi,
        string? nomClient = null,
        string? mot = null)
    {
        ArgumentNullException.ThrowIfNull(reglages);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinataire);

        if (!reglages.EstUtilisable)
            throw new InvalidOperationException(
                "L'envoi par courriel n'est pas configuré : " + reglages.CeQuiManque() +
                ".\n\nOuvrez Paramètres → Envoi par courriel pour le renseigner.");

        using var message = new MailMessage
        {
            From = new MailAddress(reglages.Expediteur, reglages.NomExpediteur),
            Subject = $"Votre commande {numero} est prête",
            Body = CorpsCommandePrete(numero, quoi, nomClient, mot, reglages.NomExpediteur),
            IsBodyHtml = false,
        };

        message.To.Add(destinataire.Trim());

        Expedier(reglages, message, destinataire.Trim(), fichiers: 0);
    }

    /// <summary>
    /// Envoie au client le lien de téléchargement de ses photos (voir <c>DropboxTransfer</c>).
    ///
    /// Sans pièce jointe, et c'est tout l'intérêt : un dossier de séance pèse des centaines
    /// de mégaoctets, qu'aucun serveur de courriel n'accepte. Le client reçoit une adresse,
    /// télécharge ce qu'il veut, et n'a pas de compte à créer.
    ///
    /// <b>Le mot de passe du lien n'est PAS dans le message</b>, et ce n'est pas un oubli :
    /// mettre la serrure et la clé dans la même enveloppe ne protège de rien. Il se donne
    /// de vive voix au comptoir, ou par un autre canal.
    /// </summary>
    /// <param name="destinataires">Adresses du client. Aucun ne voit celles des autres.</param>
    /// <param name="lien">L'adresse de téléchargement.</param>
    /// <param name="photos">Nombre de photos déposées, pour que le client sache quoi attendre.</param>
    /// <param name="joursDeValidite">
    /// Jours pendant lesquels le client pourra réellement télécharger. Null = on ne promet
    /// rien. Voir <see cref="CorpsDuLien"/> : ce n'est pas la seule expiration du lien, mais
    /// la première des deux échéances qui le rendra inutilisable.
    /// </param>
    /// <param name="protege">Vrai si le lien demande un mot de passe.</param>
    public static void EnvoyerLeLien(
        MailSettings reglages,
        IReadOnlyList<string> destinataires,
        string lien,
        int photos,
        string? nomClient = null,
        string? mot = null,
        int? joursDeValidite = null,
        bool protege = false)
    {
        ArgumentNullException.ThrowIfNull(reglages);
        ArgumentNullException.ThrowIfNull(destinataires);
        ArgumentException.ThrowIfNullOrWhiteSpace(lien);

        if (destinataires.Count == 0)
            throw new InvalidOperationException("Aucune adresse de destinataire.");

        if (!reglages.EstUtilisable)
            throw new InvalidOperationException(
                "L'envoi par courriel n'est pas configuré : " + reglages.CeQuiManque() +
                ".\n\nOuvrez Paramètres → Envoi par courriel pour le renseigner.");

        using var message = new MailMessage
        {
            From = new MailAddress(reglages.Expediteur, reglages.NomExpediteur),
            Subject = "Vos photos sont prêtes à télécharger",
            Body = CorpsDuLien(lien, photos, nomClient, mot, joursDeValidite, protege,
                reglages.NomExpediteur),
            IsBodyHtml = false,
        };

        // Le premier en destinataire, les autres en copie CACHÉE : deux clients d'une même
        // séance n'ont pas à voir l'adresse l'un de l'autre. Même règle que l'envoi des
        // photos elles-mêmes.
        message.To.Add(destinataires[0].Trim());
        foreach (var autre in destinataires.Skip(1))
            message.Bcc.Add(autre.Trim());

        Expedier(reglages, message, string.Join(", ", destinataires), fichiers: 0);
    }

    /// <summary>
    /// Le message tel que le client le lira, pour le montrer AVANT de l'envoyer.
    /// Voir <see cref="ApercuCommandePrete"/> pour la raison.
    /// </summary>
    public static string ApercuDuLien(
        string lien, int photos, string? nomClient, string? mot,
        int? joursDeValidite, bool protege, string magasin) =>
        CorpsDuLien(lien, photos, nomClient, mot, joursDeValidite, protege, magasin);

    /// <summary>
    /// Le message qui porte le lien.
    ///
    /// Il dit les trois choses qu'on cherche dans ce genre de courriel : où télécharger,
    /// combien de photos, et jusqu'à quand. Le reste ferait du remplissage.
    ///
    /// <b>La date limite est celle qui arrive EN PREMIER</b>, et c'est tout l'enjeu : deux
    /// échéances courent en parallèle, l'expiration du lien Dropbox — qui demande un compte
    /// payant — et le ménage automatique, qui supprime le dossier au bout de quelques jours
    /// sur toutes les offres. Annoncer la première en oubliant la seconde ferait perdre
    /// leurs photos aux clients qui attendent, et c'est le magasin qu'ils rappelleraient.
    /// Le calcul est fait par l'appelant, qui seul connaît les deux réglages.
    /// </summary>
    private static string CorpsDuLien(
        string lien, int photos, string? nomClient, string? mot,
        int? joursDeValidite, bool protege, string magasin)
    {
        var lignes = new List<string>
        {
            string.IsNullOrWhiteSpace(nomClient) ? "Bonjour," : $"Bonjour {nomClient.Trim()},",
            "",
            photos == 1
                ? "Votre photo est prête à télécharger :"
                : $"Vos {photos} photos sont prêtes à télécharger :",
            "",
            lien.Trim(),
            "",
        };

        if (joursDeValidite is > 0)
            lignes.Add(
                (joursDeValidite == 1
                    ? "Ce lien restera valable 1 jour"
                    : $"Ce lien restera valable {joursDeValidite} jours") +
                $" (jusqu'au {DateTime.Now.AddDays(joursDeValidite.Value):dd/MM/yyyy}). " +
                "Pensez à enregistrer vos photos avant cette date.");
        else
            lignes.Add("Pensez à enregistrer vos photos sur votre ordinateur ou votre téléphone.");

        if (protege)
            lignes.Add("Le téléchargement demande le mot de passe qui vous a été communiqué.");

        if (!string.IsNullOrWhiteSpace(mot))
        {
            lignes.Add("");
            lignes.Add(mot.Trim());
        }

        lignes.Add("");
        lignes.Add("À bientôt,");
        lignes.Add(string.IsNullOrWhiteSpace(magasin) ? "Le magasin" : magasin);

        return string.Join("\n", lignes);
    }

    /// <summary>
    /// Le message tel que le client le lira, pour le montrer AVANT de l'envoyer.
    ///
    /// La même méthode que l'envoi, et non une copie : deux textes entretenus séparément
    /// finiraient par différer, et l'aperçu montrerait autre chose que ce qui part.
    /// </summary>
    public static string ApercuCommandePrete(
        string numero, string quoi, string? nomClient, string? mot, string magasin) =>
        CorpsCommandePrete(numero, quoi, nomClient, mot, magasin);

    /// <summary>
    /// Le message de mise à disposition.
    ///
    /// Court, et il dit les trois choses qu'on cherche dans ce genre de courriel : ce qui
    /// est prêt, sous quel numéro le réclamer, et où. Le reste ferait du remplissage.
    /// </summary>
    private static string CorpsCommandePrete(
        string numero, string quoi, string? nomClient, string? mot, string magasin)
    {
        var lignes = new List<string>
        {
            string.IsNullOrWhiteSpace(nomClient) ? "Bonjour," : $"Bonjour {nomClient.Trim()},",
            "",
            $"Votre commande {numero} est prête : elle vous attend en magasin.",
        };

        if (!string.IsNullOrWhiteSpace(quoi)) lignes.Add($"Elle contient {quoi}.");

        if (!string.IsNullOrWhiteSpace(mot))
        {
            lignes.Add("");
            lignes.Add(mot.Trim());
        }

        lignes.Add("");
        lignes.Add("À bientôt,");
        lignes.Add(string.IsNullOrWhiteSpace(magasin) ? "Le magasin" : magasin);

        return string.Join("\n", lignes);
    }

    /// <summary>
    /// La remise au serveur, partagée par l'envoi réel et par l'essai des Paramètres.
    ///
    /// Une seule voie, pour que l'essai valide EXACTEMENT ce que fera l'envoi : deux
    /// clients SMTP configurés séparément finiraient par différer, et l'essai passerait
    /// pendant que l'envoi échouerait.
    ///
    /// <b>Le rapport de diagnostic passe par ici aussi</b> (<see cref="RapportDiagnostic"/>),
    /// et pour la même raison : un poste qui sait envoyer les photos d'un client sait
    /// envoyer son rapport, sans rien de plus à régler.
    /// </summary>
    /// <param name="partage">
    /// Client SMTP déjà ouvert, quand plusieurs messages se suivent — voir
    /// <see cref="EnvoyerPlusieurs"/>. Null : on en ouvre un pour ce seul message, et on
    /// le referme.
    /// </param>
    internal static void Expedier(
        MailSettings reglages, MailMessage message, string destinataire, int fichiers,
        SmtpClient? partage = null)
    {
        var client = partage ?? Client(reglages);

        try
        {
            var chrono = System.Diagnostics.Stopwatch.StartNew();
            client.Send(message);

            // La DURÉE est écrite, et c'est nouveau : « l'envoi est extrêmement long » ne
            // se vérifiait nulle part, faute d'un seul chiffre au journal. On sait
            // maintenant ce que coûte le téléversement, message par message.
            Log?.Invoke(fichiers > 0
                ? $"Photos envoyées à {destinataire} ({fichiers} fichiers) " +
                  $"en {chrono.Elapsed.TotalSeconds:0.0} s."
                : $"Message envoyé à {destinataire} (sans pièce jointe) " +
                  $"en {chrono.Elapsed.TotalSeconds:0.0} s.");
        }
        catch (SmtpException ex)
        {
            Log?.Invoke($"Envoi à {destinataire} impossible : {ex.StatusCode} — {ex.Message}");
            throw new InvalidOperationException(Expliquer(ex), ex);
        }
        finally
        {
            // le client PARTAGÉ appartient à l'appelant : il l'utilise pour les messages
            // suivants, et le referme lui-même
            if (partage is null) client.Dispose();
        }
    }

    /// <summary>
    /// Le client SMTP, réglé comme Gmail l'attend.
    ///
    /// Sorti d'<see cref="Expedier"/> pour être RÉUTILISÉ : ouvrir la connexion, négocier
    /// le TLS et s'authentifier coûte plusieurs secondes, et c'était payé une fois par
    /// photo. Voir <see cref="EnvoyerPlusieurs"/>.
    /// </summary>
    private static SmtpClient Client(MailSettings reglages) =>
        new(reglages.Serveur, reglages.Port)
        {
            EnableSsl = true,   // STARTTLS sur le port 587, ce qu'attend Gmail
            Credentials = new NetworkCredential(reglages.Expediteur, reglages.MotDePasseApplication),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)TimeSpan.FromMinutes(2).TotalMilliseconds,
        };

    /// <summary>
    /// Envoie un message d'essai à l'adresse d'expédition elle-même.
    ///
    /// C'est le SEUL contrôle qui vaille : un serveur, un port et un mot de passe bien
    /// tapés ne disent rien de ce que le serveur acceptera. Une configuration fausse doit
    /// se découvrir à l'écran des Paramètres, pas devant un client dont on vient
    /// d'annoncer le prix.
    ///
    /// Il passe par le même chemin qu'un vrai envoi — mêmes réglages, même client SMTP,
    /// même traduction des refus — sinon il validerait autre chose que ce qu'on veut
    /// vérifier. Seule la pièce jointe change : une image d'un pixel, écrite dans
    /// <paramref name="dossier"/>, plutôt que les photos d'un client.
    /// </summary>
    public static void EnvoyerUnEssai(MailSettings reglages, string dossier)
    {
        ArgumentNullException.ThrowIfNull(reglages);

        if (!reglages.EstUtilisable)
            throw new InvalidOperationException(
                "L'envoi par courriel n'est pas configuré : " + reglages.CeQuiManque() + ".");

        Directory.CreateDirectory(dossier);
        var temoin = Path.Combine(dossier, "essai.jpg");

        using (var pixel = new MagickImage(MagickColors.White, 1, 1))
            MagickInit.Write(pixel, temoin);

        using var message = new MailMessage
        {
            From = new MailAddress(reglages.Expediteur, reglages.NomExpediteur),
            Subject = "Studio Photo — essai d'envoi",
            Body = "Ce message confirme que l'envoi des photos par courriel fonctionne " +
                   "depuis ce poste." + Environment.NewLine + Environment.NewLine +
                   $"Envoyé le {DateTime.Now:dd/MM/yyyy à HH:mm}.",
            IsBodyHtml = false,
        };
        message.To.Add(reglages.Expediteur);
        message.Attachments.Add(new Attachment(temoin));

        Expedier(reglages, message, reglages.Expediteur, 1);
    }

    /// <summary>
    /// Traduit les refus du serveur en quelque chose d'actionnable. « 5.7.0 Authentication
    /// Required » ne dit rien à un opérateur ; « le mot de passe d'application n'est plus
    /// valable » lui dit quoi faire.
    /// </summary>
    private static string Expliquer(SmtpException ex) => ex.StatusCode switch
    {
        SmtpStatusCode.MailboxBusy or SmtpStatusCode.MailboxUnavailable =>
            "La boîte du destinataire refuse le message. Vérifiez l'adresse.",

        SmtpStatusCode.ClientNotPermitted or SmtpStatusCode.MustIssueStartTlsFirst =>
            "Gmail a refusé la connexion. Le mot de passe d'application est peut-être révoqué : " +
            "regénérez-en un sur le compte, puis reportez-le dans Paramètres → Envoi par courriel.",

        _ => "Envoi impossible : " + ex.Message +
             "\n\nLes fichiers sont préparés et restent disponibles ; vous pouvez réessayer.",
    };

    private static string Corps(string? motDuPhotographe)
    {
        var corps = new List<string>
        {
            "Bonjour,",
            "",
            "Vous trouverez ci-joint vos photos, en trois versions :",
            "  • la photo entière, non recadrée ;",
            "  • le cadrage retenu, en résolution légère — pour les démarches en ligne ;",
            "  • le cadrage retenu, en pleine résolution — si vous souhaitez la faire tirer.",
        };

        if (!string.IsNullOrWhiteSpace(motDuPhotographe))
        {
            corps.Add("");
            corps.Add(motDuPhotographe.Trim());
        }

        corps.Add("");
        corps.Add("Bonne journée.");
        return string.Join(Environment.NewLine, corps);
    }
}
