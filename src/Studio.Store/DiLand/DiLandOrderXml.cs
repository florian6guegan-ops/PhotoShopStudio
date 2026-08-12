using System.Globalization;
using System.Xml;
using Studio.Core.Domain;

namespace Studio.Store.DiLand;

/// <summary>
/// Lecture d'une commande de borne dans le fichier <c>Order.xml</c> de son dossier —
/// <b>sans la base de DiLand, et sans que DiLand tourne</b>.
///
/// <b>Pourquoi ce second lecteur.</b> Une commande de borne n'entre dans la base qu'une
/// fois que DiLand l'a intégrée, et il ne le fait que s'il tourne. Or il tombe en panne de
/// mémoire presque tous les jours (voir le journal de la boutique) : entre le moment où la
/// borne dépose et celui où il se relève, la commande existe sur le disque et nulle part
/// ailleurs. Elle était alors invisible pour tout le monde.
///
/// Le dossier, lui, se suffit : <c>Order.xml</c> + <c>Files.txt</c> + <c>F\</c>. Vérifié
/// sur les commandes réelles de la boutique — le XML porte tout ce que la base porte, y
/// compris les recadrages, les redressements et les quantités.
///
/// <b>Ce que cela ne règle pas.</b> Les bornes parlent à DiLand par .NET Remoting sur le
/// port 19200, et c'est le processus de DiLand qui écoute. Fermé, il n'arrive RIEN sur le
/// disque. Ce lecteur rend Studio indépendant de DiLand pour tout ce qui suit l'arrivée,
/// pas pour l'arrivée elle-même.
/// </summary>
public static class DiLandOrderXml
{
    /// <summary>Nom du fichier, dans le dossier de la commande.</summary>
    public const string FileName = "Order.xml";

    /// <summary>Une commande lue sur le disque, avec son contenu.</summary>
    public sealed record Contenu(DiLandOrder Order, IReadOnlyList<DiLandOrderLine> Lines);

    /// <summary>Vrai si ce dossier porte une commande lisible.</summary>
    public static bool Porte(string directory) =>
        File.Exists(Path.Combine(directory, FileName));

    /// <summary>
    /// Lit la commande d'un dossier, ou rend null si le fichier manque ou n'est pas
    /// exploitable.
    ///
    /// Ne lève jamais sur un fichier abîmé : un paquet arrivé à moitié ne doit pas
    /// empêcher les autres commandes de s'afficher. L'appelant balaie un dossier entier.
    /// </summary>
    public static Contenu? Lire(string directory)
    {
        var chemin = Path.Combine(directory, FileName);
        if (!File.Exists(chemin)) return null;

        try
        {
            var document = new XmlDocument();
            document.Load(chemin);

            var racine = document.DocumentElement;
            if (racine is null) return null;

            var nomDuDossier = Path.GetFileName(directory.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

            var commande = new DiLandOrder(
                Oid: CleDe(Texte(racine, "Sys_GlobalUniqueId"), nomDuDossier),
                Number: Entier(racine, "Number"),
                DailyNumber: Texte(racine, "DailyNumber"),
                Date: DateDe(racine, nomDuDossier),
                DirectoryName: nomDuDossier,
                EndUserName: Texte(racine, "EndUserName"),
                PhotoCount: CompterLesPhotos(directory));

            return new Contenu(commande, LignesDe(racine));
        }
        catch (Exception e) when (e is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Une clé numérique stable pour le journal, dérivée de l'identifiant global de la
    /// commande.
    ///
    /// Le journal est indexé sur l'<c>Oid</c> de la base — un entier. Le XML, lui, ne
    /// porte que des GUID. On en dérive donc un entier <b>déterministe</b> : la même
    /// commande doit retrouver la même clé à chaque lecture, sinon une commande traitée
    /// réapparaîtrait à chaque passage.
    ///
    /// Le bit de poids fort est écarté pour que la clé reste positive, et donc lisible dans
    /// le journal comme dans les journaux d'exécution. Le risque de collision entre deux
    /// commandes est théorique et sans conséquence pratique : les deux seraient dédoublonnées
    /// sur leur dossier bien avant (voir <see cref="DiLandImporter"/>).
    /// </summary>
    internal static long CleDe(string identifiantGlobal, string nomDuDossier)
    {
        var source = string.IsNullOrWhiteSpace(identifiantGlobal) ? nomDuDossier : identifiantGlobal;

        // FNV-1a 64 bits : court, stable d'une exécution à l'autre — contrairement à
        // string.GetHashCode(), que .NET randomise à chaque démarrage du processus. Une clé
        // qui change au redémarrage ferait resurgir toutes les commandes déjà traitées.
        const ulong depart = 14695981039346656037;
        const ulong facteur = 1099511628211;

        var empreinte = depart;
        foreach (var caractere in source)
        {
            empreinte ^= caractere;
            empreinte *= facteur;
        }

        return (long)(empreinte & 0x7FFF_FFFF_FFFF_FFFF);
    }

    private static IReadOnlyList<DiLandOrderLine> LignesDe(XmlElement racine)
    {
        var lignes = new List<DiLandOrderLine>();
        var numero = 0L;

        foreach (XmlElement ligne in racine.SelectNodes(".//OrderLine")!)
        {
            var photos = new List<DiLandOrderPhoto>();

            foreach (XmlElement image in ligne.SelectNodes(".//OrderImageOrderLineImage")!)
            {
                var nom = Texte(image, "FileName");
                if (string.IsNullOrWhiteSpace(nom)) continue;

                photos.Add(DiLandOrderPhoto.FromRaw(
                    fileName: nom,
                    originalFileName: Texte(image, "OriginalFileName"),
                    quantity: Math.Max(1, Entier(image, "Quantity")),
                    applyCrop: Booleen(image, "ApplyCrop"),
                    cropX: Reel(image, "CropX"),
                    cropY: Reel(image, "CropY"),
                    cropWidth: Reel(image, "CropWidth"),
                    cropHeight: Reel(image, "CropHeight"),
                    angle: Reel(image, "Angle"),
                    fineRotationDegrees: Reel(image, "FineRotationAngle"),
                    sourceWidth: Entier(image, "Width"),
                    sourceHeight: Entier(image, "Height")));
            }

            // Le nom du produit vit dans « Sys_Product_Alias » — « 8x10 », « 10x15 ». Le
            // XML ne porte pas la table des produits, seulement l'identifiant du produit et
            // cet alias : c'est donc lui, et lui seul, qui permet de retrouver l'article au
            // catalogue Studio.
            lignes.Add(new DiLandOrderLine(
                Oid: ++numero,
                ProductName: Texte(ligne, "Sys_Product_Alias"),
                Price: (decimal)Reel(ligne, "Price"),
                // Ici la finition s'écrit en toutes lettres — PaperType="Glossy" — là où
                // la base la range en code numérique. Les deux doivent rendre le même nom :
                // une commande ne peut pas changer de rouleau selon que DiLand tourne ou non.
                Finish: FinitionPapier.DepuisDiLand(Texte(ligne, "PaperType")),
                Photos: photos));
        }

        return lignes;
    }

    /// <summary>Photos réellement présentes, hors dérivés de DiLand (préfixe <c>O_</c>).</summary>
    private static int CompterLesPhotos(string directory)
    {
        var dossier = Path.Combine(directory, "F");
        if (!Directory.Exists(dossier)) return 0;

        return Directory.EnumerateFiles(dossier)
            .Count(f => !Path.GetFileName(f).StartsWith("O_", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// La date de la commande.
    ///
    /// <b>Le NOM DU DOSSIER passe en premier.</b> Il est en <c>aaaaMMjj-HHmm</c>, donc sans
    /// ambiguïté, là où l'attribut <c>Date</c> du XML est écrit à l'américaine
    /// (<c>08/03/2026</c> pour le 3 août) : lu avec la culture française, il donnerait le
    /// 8 mars — cinq mois d'écart, et une commande classée n'importe où dans la liste.
    /// </summary>
    private static DateTime DateDe(XmlElement racine, string nomDuDossier)
    {
        var horodatage = nomDuDossier.Split('-');
        if (horodatage.Length >= 2 &&
            DateTime.TryParseExact($"{horodatage[0]}-{horodatage[1]}", "yyyyMMdd-HHmm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var duDossier))
            return duDossier;

        var texte = Texte(racine, "Date");
        if (DateTime.TryParseExact(texte, "MM/dd/yyyy HH:mm:ss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var americaine))
            return americaine;

        return DateTime.TryParse(texte, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var quelconque) ? quelconque : DateTime.MinValue;
    }

    private static string Texte(XmlElement element, string attribut) =>
        element.GetAttribute(attribut) ?? "";

    private static int Entier(XmlElement element, string attribut) =>
        int.TryParse(Texte(element, attribut), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var valeur) ? valeur : 0;

    private static double Reel(XmlElement element, string attribut) =>
        double.TryParse(Texte(element, attribut), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var valeur) ? valeur : 0;

    private static bool Booleen(XmlElement element, string attribut) =>
        bool.TryParse(Texte(element, attribut), out var valeur) && valeur;
}
