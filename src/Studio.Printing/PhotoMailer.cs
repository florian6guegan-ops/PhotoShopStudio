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
        using (var entiere = new MagickImage(sourcePath))
        {
            entiere.AutoOrient();
            MagickInit.Write(entiere, original);
        }

        // Le cadrage passe par le pipeline de rendu : rotation, redressement, recadrage et
        // corrections y sont appliqués dans le bon ordre, et le client reçoit donc
        // exactement ce que l'opérateur a vu à l'écran.
        RendreLeCadrage(sourcePath, crop, rotationQuarterTurns, fineRotationDegrees,
            adjustments, haute);

        // la version légère se tire de la haute définition plutôt que d'un second rendu :
        // deux rendus d'une photo de 24 Mpx coûteraient le double pour un résultat
        // identique au rééchantillonnage près
        using (var legere = new MagickImage(haute))
        {
            legere.Resize(new MagickGeometry((uint)GrandCoteBasseDefinition, (uint)GrandCoteBasseDefinition));
            MagickInit.Write(legere, basse);
        }

        return new PhotosDuClient(original, basse, haute);
    }

    /// <summary>
    /// Rend le cadrage à sa taille naturelle, sans le reposer dans un format de papier :
    /// le client reçoit sa photo, pas une planche.
    ///
    /// La cible en pixels est celle du cadrage lui-même, donc le fichier garde LE RATIO DU
    /// CADRAGE et toute la résolution que la photo d'origine permet.
    /// </summary>
    private static void RendreLeCadrage(
        string sourcePath, CropSpec crop, int rotationQuarterTurns,
        double fineRotationDegrees, ImageAdjustments adjustments, string sortie)
    {
        using var mesure = new MagickImage(sourcePath);
        mesure.AutoOrient();

        var largeur = Math.Max(1, (int)Math.Round(crop.Width * mesure.Width));
        var hauteur = Math.Max(1, (int)Math.Round(crop.Height * mesure.Height));

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
    /// </summary>
    private static void Expedier(
        MailSettings reglages, MailMessage message, string destinataire, int fichiers)
    {
        using var client = new SmtpClient(reglages.Serveur, reglages.Port)
        {
            EnableSsl = true,   // STARTTLS sur le port 587, ce qu'attend Gmail
            Credentials = new NetworkCredential(reglages.Expediteur, reglages.MotDePasseApplication),
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = (int)TimeSpan.FromMinutes(2).TotalMilliseconds,
        };

        try
        {
            client.Send(message);
            Log?.Invoke(fichiers > 0
                ? $"Photos envoyées à {destinataire} ({fichiers} fichiers)."
                : $"Message envoyé à {destinataire} (sans pièce jointe).");
        }
        catch (SmtpException ex)
        {
            Log?.Invoke($"Envoi à {destinataire} impossible : {ex.StatusCode} — {ex.Message}");
            throw new InvalidOperationException(Expliquer(ex), ex);
        }
    }

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
