namespace Studio.Core.Domain;

/// <summary>
/// Un dossier épinglé dans les boîtes de fichiers de Windows et dans le choix du support.
/// </summary>
/// <param name="Libelle">Ce que l'opérateur lit — « Bureau », « WeTransfer »…</param>
/// <param name="Chemin">
/// Le dossier lui-même. Vide = à résoudre automatiquement d'après <see cref="Cle"/> :
/// « Bureau » et « Téléchargements » ne sont pas au même endroit d'un poste à l'autre, et
/// pas du tout sur un Windows anglais ou quand l'utilisateur les a déplacés.
/// </param>
/// <param name="Cle">
/// Ce que le dossier est, quand Windows sait le trouver seul : <see cref="Bureau"/>,
/// <see cref="Telechargements"/>, <see cref="WeTransfer"/>. Vide pour un dossier désigné à
/// la main, dont seul le chemin compte.
/// </param>
/// <param name="Actif">Faux pour le garder dans la liste sans le proposer.</param>
public sealed record DossierFavori(
    string Libelle, string Chemin = "", string Cle = "", bool Actif = true)
{
    public const string Bureau = "bureau";
    public const string Telechargements = "telechargements";

    /// <summary>
    /// Le dossier où atterrissent les envois WeTransfer. Il n'a rien d'officiel — c'est un
    /// dossier que l'exploitant crée — et il se cherche donc aux endroits habituels avant
    /// d'être demandé.
    /// </summary>
    public const string WeTransfer = "wetransfer";
}

/// <summary>
/// Les dossiers épinglés du poste.
///
/// <b>Pourquoi c'est un réglage et non une liste écrite en dur.</b> La boutique reçoit des
/// photos par trois chemins qui ne changent jamais — le Bureau, les Téléchargements du
/// navigateur, et le dossier où l'on range les envois WeTransfer — et l'opérateur les
/// retrouvait à chaque fois en naviguant depuis la racine du disque. Mais le troisième
/// n'existe que chez lui : un chemin en dur marcherait sur ce poste et sur aucun autre.
/// </summary>
public sealed class FavorisSettings
{
    /// <summary>
    /// Les dossiers proposés, dans l'ordre où ils apparaissent.
    ///
    /// Vide = les trois par défaut (voir <see cref="ParDefaut"/>). C'est ce que trouve un
    /// poste neuf, et c'est ce qu'on veut : les favoris doivent marcher sans que personne
    /// n'ait rien réglé.
    /// </summary>
    public List<DossierFavori> Dossiers { get; set; } = [];

    /// <summary>
    /// Les favoris d'un poste sur lequel personne n'a rien réglé.
    ///
    /// Les chemins sont laissés VIDES : ils se résolvent au démarrage, chacun là où Windows
    /// le range. Écrire « C:\Users\DELL\Desktop » dans le fichier de configuration ferait
    /// un réglage qui ne survit pas au premier changement de poste ou de session.
    /// </summary>
    public static List<DossierFavori> ParDefaut() =>
    [
        new("Bureau", Cle: DossierFavori.Bureau),
        new("Téléchargements", Cle: DossierFavori.Telechargements),
        new("WeTransfer", Cle: DossierFavori.WeTransfer),
    ];

    /// <summary>Les favoris tels qu'ils doivent être lus : ceux du réglage, ou ceux par défaut.</summary>
    public IReadOnlyList<DossierFavori> Effectifs =>
        Dossiers.Count == 0 ? ParDefaut() : Dossiers;
}
