using System.Text.Json;
using System.Text.Json.Serialization;
using Studio.Core.Domain;

namespace Studio.Store;

/// <summary>
/// Une photo d'une commande mise en attente, telle que l'opérateur l'avait réglée.
///
/// <b>La photo est désignée par son NOM DE FICHIER, jamais par son rang dans la grille.</b>
/// Un fichier illisible est écarté au chargement : les rangs se décaleraient d'une
/// ouverture à l'autre, et on reprendrait le cadrage du voisin sans que rien ne le dise.
/// </summary>
public sealed class PhotoEnAttente
{
    public string FileName { get; set; } = "";

    /// <summary>Cochée pour l'impression.</summary>
    public bool Selected { get; set; }

    public int Quantity { get; set; } = 1;

    /// <summary>Code catalogue du produit, ou vide si l'opérateur n'en avait pas encore posé.</summary>
    public string? ProductCode { get; set; }

    /// <summary>Finition choisie (voir <c>Product.Finishes</c>) ; null = celle du produit.</summary>
    public string? Finish { get; set; }

    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; } = 1;
    public double CropHeight { get; set; } = 1;

    public int RotationQuarterTurns { get; set; }
    public double FineRotationDegrees { get; set; }

    /// <summary>« Remplir le format » / « photo entière » imposé ; null = celui du produit.</summary>
    public FitMode? Fit { get; set; }

    public bool CutBorder { get; set; }

    public ImageAdjustments Adjustments { get; set; } = new();

    [JsonIgnore]
    public CropSpec Crop => new(CropX, CropY, CropWidth, CropHeight);
}

/// <summary>
/// Une photo d'identité en préparation, avec le travail de l'opérateur.
///
/// Elle a sa propre classe plutôt que d'allonger <see cref="PhotoEnAttente"/> : une photo
/// d'identité ne porte ni produit, ni finition, ni découpe, et porte en revanche des
/// repères de visage qui n'ont aucun sens sur un tirage. Les mêler ferait un objet dont la
/// moitié des champs serait toujours vide.
/// </summary>
public sealed class PhotoIdentiteEnAttente
{
    /// <summary>Nom du fichier, jamais le rang : voir <see cref="PhotoEnAttente"/>.</summary>
    public string FileName { get; set; } = "";

    public bool Selected { get; set; }
    public int Quantity { get; set; } = 1;

    /// <summary>Photos sur la planche.</summary>
    public int Copies { get; set; }

    /// <summary>
    /// Les repères ont déjà été posés. Sans ce drapeau, une photo rouverte relancerait la
    /// détection de visage et écraserait le placement manuel qu'on vient de corriger.
    /// </summary>
    public bool Prete { get; set; }

    public double CropX { get; set; }
    public double CropY { get; set; }
    public double CropWidth { get; set; } = 1;
    public double CropHeight { get; set; } = 1;

    /// <summary>Sommet du crâne, en fraction de l'image ; null = pas encore posé.</summary>
    public double? CrownX { get; set; }
    public double? CrownY { get; set; }

    /// <summary>Bas du menton, en fraction de l'image ; null = pas encore posé.</summary>
    public double? ChinX { get; set; }
    public double? ChinY { get; set; }

    /// <summary>Visage détecté, en fractions de l'image ; null = aucune détection retenue.</summary>
    public double? HeadX { get; set; }
    public double? HeadY { get; set; }
    public double? HeadWidth { get; set; }
    public double? HeadHeight { get; set; }

    /// <summary>Axe vertical du visage, en fraction de la largeur.</summary>
    public double AxeVisage { get; set; } = 0.5;

    /// <summary>Redressement en degrés — le « Tilt ».</summary>
    public double Redressement { get; set; }

    public bool NoirEtBlanc { get; set; }
    public bool FondBlanc { get; set; }

    /// <summary>Le même détourage, posé sur du gris clair. Exclusif de <see cref="FondBlanc"/>.</summary>
    public bool FondGris { get; set; }

    public ImageAdjustments Corrections { get; set; } = new();
}

/// <summary>
/// Une planche de photos d'identité mise de côté.
///
/// La NORME est enregistrée à plat, et non par son nom : le référentiel compte 274
/// documents et peut être rechargé entre-temps, alors que les cotes, elles, ne bougent
/// pas. C'est aussi ce qui permet de reprendre une planche dont la norme aurait disparu
/// du référentiel.
/// </summary>
public sealed class IdentiteEnAttente
{
    public string Country { get; set; } = "";
    public string Document { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public double HeadMinMm { get; set; }
    public double HeadMaxMm { get; set; }
    public double? CrownMarginMm { get; set; }
    public double? TargetHeadOverrideMm { get; set; }

    /// <summary>
    /// Les photos imposées par l'écran de sélection, dans SON ordre ; vide si l'écran
    /// scannait un dossier. Ce sont des chemins complets : la sélection peut piocher
    /// ailleurs que sous <see cref="TravailEnAttente.PhotosDirectory"/>.
    /// </summary>
    public List<string> Chemins { get; set; } = [];

    /// <summary>Nom du fichier affiché au moment de la mise de côté, pour y revenir.</summary>
    public string? PhotoCourante { get; set; }

    public List<PhotoIdentiteEnAttente> Photos { get; set; } = [];
}

/// <summary>
/// Une commande qu'on prépare et qu'on met de côté pour servir quelqu'un d'autre.
///
/// C'est le geste du comptoir : un client hésite ou s'absente, un autre attend derrière.
/// On met en attente, on sert, on reprend là où on en était.
///
/// <b>Toute commande en préparation, quelle qu'en soit l'origine</b> — une clé USB, un
/// téléphone, une borne. La première version ne valait que pour les bornes ; or c'est
/// justement en préparant une commande au comptoir qu'on a besoin de faire autre chose,
/// et l'origine des photos n'y est pour rien.
///
/// Elle vit dans les données du poste (<c>attente\&lt;id&gt;.json</c>) et se conserve
/// <see cref="Retention"/>, comme le reste : une commande mise de côté et jamais reprise
/// finit par ne plus rien vouloir dire.
/// </summary>
public sealed class TravailEnAttente
{
    /// <summary>Identifiant propre, indépendant de toute commande de borne.</summary>
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Dossier des photos, tel que l'écran l'a ouvert.</summary>
    public string PhotosDirectory { get; set; } = "";

    /// <summary>Descendre ou non sous ce dossier — l'écran ne le devine pas.</summary>
    public bool AvecSousDossiers { get; set; } = true;

    /// <summary>Produit présélectionné dans la barre, ou vide.</summary>
    public string? ProduitParDefaut { get; set; }

    /// <summary>
    /// La commande de borne à l'origine des photos, s'il y en a une.
    ///
    /// Elle doit survivre à la mise en attente : sans elle, l'impression ne ferait plus
    /// basculer la commande de borne dans l'historique, et elle resterait affichée pour
    /// toujours dans la liste du jour.
    /// </summary>
    public long? KioskOid { get; set; }

    /// <summary>Titre de l'écran, pour rouvrir sur le même intitulé.</summary>
    public string Titre { get; set; } = "";

    /// <summary>Ce qu'on affiche sur l'accueil : « 12 photo(s) · 7 cochée(s) · 8,40 € ».</summary>
    public string Resume { get; set; } = "";

    /// <summary>
    /// Taille personnalisée en cours, en millimètres, ou 0 au format du catalogue.
    ///
    /// Reprise telle quelle : rouvrir au format commandé un travail fait en 5,5 × 8
    /// remettrait tous les cadres au centre, au mauvais rapport.
    /// </summary>
    public double CustomWidthMm { get; set; }
    public double CustomHeightMm { get; set; }

    /// <summary>Papier imposé en taille personnalisée, ou vide pour « automatique ».</summary>
    public string? PaperCode { get; set; }

    /// <summary>
    /// Feuille de montage retenue pour les agrandissements, ou vide.
    ///
    /// Elle doit survivre à la mise en attente pour la même raison que le papier imposé :
    /// c'est un choix que l'opérateur a fait à un écran qu'il ne reverra pas en reprenant, et
    /// le perdre en silence ferait ressortir une commande en un fichier par tirage — donc sur
    /// deux fois plus de papier — sans que rien ne le signale.
    /// </summary>
    public string? MontageSheetCode { get; set; }

    public List<PhotoEnAttente> Photos { get; set; } = [];

    /// <summary>
    /// La planche d'identité en préparation, ou null s'il s'agit de tirages.
    ///
    /// Une planche se reprend dans l'écran d'IDENTITÉ, avec ses repères de crâne et de
    /// menton — la rouvrir dans la grille des tirages donnerait un cadre libre, c'est-à-dire
    /// précisément ce qui ne permet PAS de refaire une photo d'identité.
    /// </summary>
    public IdentiteEnAttente? Identite { get; set; }

    [JsonIgnore]
    public bool EnTaillePersonnalisee => CustomWidthMm > 0 && CustomHeightMm > 0;

    /// <summary>Depuis quand elle attend, en clair.</summary>
    [JsonIgnore]
    public string Depuis
    {
        get
        {
            var local = SavedAt.LocalDateTime;
            return local.Date == DateTime.Today
                ? $"en attente depuis {local:HH:mm}"
                : $"en attente depuis le {local:dd/MM} à {local:HH:mm}";
        }
    }
}

/// <summary>
/// Les commandes mises en attente, sur le disque : un fichier par commande.
///
/// Un fichier par commande, et non un journal unique : deux commandes mises de côté le
/// même jour n'ont alors aucune raison de se gêner, et un fichier abîmé n'emporte pas les
/// autres. C'est aussi ce qui rend l'effacement trivial.
/// </summary>
public sealed class AttenteStore
{
    /// <summary>Durée au-delà de laquelle une commande jamais reprise s'efface d'elle-même.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dossier;

    public AttenteStore(string dossier) => _dossier = dossier;

    private string Chemin(Guid id) => Path.Combine(_dossier, $"{id:N}.json");

    /// <summary>
    /// Les commandes en attente, la plus récemment mise de côté d'abord — c'est celle
    /// qu'on reprend le plus souvent.
    ///
    /// Les périmées sont effacées au passage : la liste est relue à chaque affichage de
    /// l'accueil, c'est le seul endroit où la purge coûte zéro.
    /// </summary>
    public IReadOnlyList<TravailEnAttente> Lister()
    {
        if (!Directory.Exists(_dossier)) return [];

        var vivantes = new List<TravailEnAttente>();
        var plancher = DateTimeOffset.Now - Retention;

        foreach (var chemin in Directory.EnumerateFiles(_dossier, "*.json"))
        {
            var travail = LireLeFichier(chemin);

            // fichier illisible, ou trop vieux : il part, et il ne bloque personne
            if (travail is null || travail.SavedAt < plancher)
            {
                Supprimer(chemin);
                continue;
            }

            vivantes.Add(travail);
        }

        return vivantes.OrderByDescending(t => t.SavedAt).ToList();
    }

    /// <summary>Une commande en attente, ou null si elle n'existe plus.</summary>
    public TravailEnAttente? Lire(Guid id) => LireLeFichier(Chemin(id));

    /// <summary>
    /// La commande en attente issue de CETTE commande de borne, ou null.
    ///
    /// Sert aux deux listes de bornes : une commande dont le travail est de côté doit
    /// l'annoncer, sans quoi on la rouvrirait à neuf sans savoir qu'on écrase quelque chose.
    /// </summary>
    public TravailEnAttente? PourLaBorne(long oid) =>
        Lister().FirstOrDefault(t => t.KioskOid == oid);

    /// <summary>Met une commande de côté, en écrivant à côté puis en remplaçant.</summary>
    public void Enregistrer(TravailEnAttente travail)
    {
        ArgumentNullException.ThrowIfNull(travail);

        Directory.CreateDirectory(_dossier);
        AtomicFile.WriteAllText(Chemin(travail.Id),
            JsonSerializer.Serialize(travail, JsonOptions));
    }

    /// <summary>
    /// Efface une commande en attente. Ne lève jamais : appelé après l'impression et
    /// depuis la purge, où l'échec ne doit rien arrêter.
    /// </summary>
    public void Effacer(Guid id) => Supprimer(Chemin(id));

    /// <summary>
    /// Efface ce qui attend au nom d'une commande de borne — elle vient d'être tirée, ou
    /// retirée de la liste. Le travail de côté n'a plus d'objet.
    /// </summary>
    public void EffacerPourBorne(long oid)
    {
        foreach (var travail in Lister().Where(t => t.KioskOid == oid))
            Effacer(travail.Id);
    }

    private static TravailEnAttente? LireLeFichier(string chemin)
    {
        if (!File.Exists(chemin)) return null;

        try
        {
            return JsonSerializer.Deserialize<TravailEnAttente>(File.ReadAllText(chemin), JsonOptions);
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException)
        {
            // fichier abîmé : la commande doit pouvoir se rouvrir à neuf plutôt que de
            // bloquer le comptoir sur un fichier de confort
            return null;
        }
    }

    private static void Supprimer(string chemin)
    {
        try
        {
            if (File.Exists(chemin)) File.Delete(chemin);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // fichier verrouillé : il repartira au prochain passage
        }
    }
}
