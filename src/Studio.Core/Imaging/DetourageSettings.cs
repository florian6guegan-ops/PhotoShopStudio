using System.Text.Json;

namespace Studio.Core.Imaging;

/// <summary>
/// Comment le fond blanc des photos d'identité est détouré, sur CE poste.
///
/// Deux méthodes existent dans le code, et le choix dépend de la machine — d'où un réglage
/// et non une constante :
///
/// - <b>par couleur</b> (<c>BackgroundRemoval</c>) : on reconnaît le fond de studio et on
///   efface ce qui lui ressemble ET communique avec le bord. Environ une seconde, aucune
///   exigence matérielle, et ça marche toujours ;
/// - <b>par réseau de neurones</b> (<c>BiRefNetMatting</c>) : contour nettement meilleur —
///   les mèches de cheveux sont tenues, aucune ombre ne subsiste — mais il faut une carte
///   graphique, et le temps se compte en secondes.
///
/// <b>Deux drapeaux et non un</b>, parce que ce sont deux questions différentes : se servir
/// du réseau, et lequel. L'entrée du modèle est figée à 1024 — on ne peut pas soulager la
/// carte en réduisant la taille d'entrée, c'est le POIDS du modèle qu'il faut changer.
///
/// Les réglages vivent dans les DONNÉES du poste (<c>config\detourage.json</c>) : un poste
/// à Quadro P2000 et un poste mieux doté n'ont pas le même réglage, et cela ne regarde pas
/// le dépôt.
/// </summary>
/// <param name="Actif">
/// Se servir du réseau de neurones. <b>Faux par défaut</b>, et c'est délibéré : mesuré sur
/// la Quadro P2000 de l'atelier, 4,3 s sur l'aperçu et 9,5 s sur la photo pleine
/// résolution, contre ~1,2 s pour la méthode par couleur. Dix secondes devant un client,
/// c'est trop long — décision de l'exploitant, 03/08/2026. Une mise à jour ne doit pas
/// allonger le détourage sans que personne l'ait demandé.
/// </param>
/// <param name="ModelePuissant">
/// Préférer <c>birefnet-portrait-fp16.onnx</c> (467 Mo) à <c>birefnet-lite-fp16.onnx</c>
/// (109 Mio). Le contour est meilleur, mais il lui faut de la mémoire vidéo : sur 4 Go, il
/// réussit la première photo puis échoue sur la seconde (<c>DmlFusedNode</c>, constaté le
/// 03/08/2026). Voir <see cref="MemoireVideoRecommandeeGo"/>.
/// </param>
/// <param name="Carte">
/// Numéro DirectML de la carte graphique retenue, ou null tant qu'aucune mesure n'a été
/// faite sur ce poste.
///
/// <b>Le zéro en dur choisissait parfois la mauvaise.</b> Sur le poste d'Arcueil, une Quadro
/// K600 de 2013 (1 Go, sans demi-précision matérielle) cohabite avec une Intel UHD 630 —
/// et rien de lisible ne dit laquelle va le plus vite : la K600 annonce huit fois plus de
/// mémoire dédiée, et perd. On mesure donc, une fois, et on retient (voir
/// <c>BiRefNetMatting.ChoisirLaMeilleureCarte</c>).
///
/// Effacer ce champ refait la mesure au prochain démarrage : c'est ce qu'il faut faire après
/// un changement de carte ou de pilote.
/// </param>
/// <param name="CarteNom">
/// Le nom de la carte retenue, tel que le pilote le déclare. Il ne sert à RIEN au code — il
/// sert à l'exploitant, qui relit son fichier de réglages et doit pouvoir vérifier que le
/// numéro désigne encore la même carte.
/// </param>
public sealed record DetourageSettings(
    bool Actif = false,
    bool ModelePuissant = false,
    int? Carte = null,
    string? CarteNom = null)
{
    /// <summary>Nom du fichier, dans le dossier de configuration.</summary>
    public const string FileName = "detourage.json";

    /// <summary>Fichier du modèle léger, celui qui tient sur la carte de l'atelier.</summary>
    public const string ModeleLeger = "birefnet-lite-fp16.onnx";

    /// <summary>Fichier du modèle le plus fin, et le plus gourmand.</summary>
    public const string ModelePuissantFichier = "birefnet-portrait-fp16.onnx";

    /// <summary>
    /// Mémoire vidéo au-dessous de laquelle le modèle puissant est déconseillé, en
    /// gigaoctets.
    ///
    /// Ce n'est pas une estimation, c'est un relevé — et il a été CORRIGÉ VERS LE HAUT le
    /// 12/08/2026 :
    ///
    /// <list type="bullet">
    /// <item>Quadro P2000 de l'atelier, <b>5 Go</b> : le modèle puissant passe une fois puis
    /// échoue (03/08/2026). D'où un seuil posé à 6, juste au-dessus.</item>
    /// <item>GTX 1660 SUPER de Créteil, <b>6 Go</b> exactement (<c>qwMemorySize</c> =
    /// 6 442 450 944) : elle satisfaisait donc le seuil — <c>6 &lt; 6</c> est faux — et le
    /// modèle puissant y a été offert, choisi… puis il a échoué de la même façon.</item>
    /// </list>
    ///
    /// Six gigaoctets ne suffisent pas : le seuil rejoint la valeur recommandée. Le poste de
    /// Créteil a passé une matinée à sortir des planches détourées à la couleur pendant que
    /// le cadrage, lui, restait parfait — le premier passage réussissait, le second non.
    ///
    /// Cela reste un avertissement doublé d'un choix grisé, jamais une panne : le repli
    /// existe, et il tombe désormais sur le modèle léger plutôt que sur la couleur.
    /// </summary>
    public const double MemoireVideoMinimaleGo = MemoireVideoRecommandeeGo - MargeDeMesureGo;

    /// <summary>Ce qu'on conseille réellement d'avoir pour le modèle puissant.</summary>
    public const double MemoireVideoRecommandeeGo = 8;

    /// <summary>
    /// L'écart entre la capacité qu'une carte porte sur sa boîte et celle qu'elle DÉCLARE.
    ///
    /// ⚠ <b>Sans cette marge, un seuil rond écarte les cartes qu'il visait.</b> Relevé le
    /// 20/08/2026 sur la RTX 5060 du Kremlin-Bicêtre : <c>qwMemorySize</c> annonce
    /// <b>7,96 Go</b> pour une carte de 8 Go. Comparée à 8, elle échoue — et avec elle
    /// TOUTE carte grand public de 8 Go, puisqu'elles réservent toutes un peu de mémoire
    /// avant de la déclarer. Le modèle puissant devenait donc inatteignable en pratique.
    ///
    /// C'est le même piège que la GTX 1660 SUPER de Créteil (<c>6 &lt; 6</c> est faux),
    /// retourné : <b>une valeur mesurée ne se compare pas à un chiffre commercial.</b>
    ///
    /// Un demi-gigaoctet suffit à rattraper l'écart de déclaration sans rien laisser passer
    /// d'autre : les 6 Go de Créteil et les 5 Go de l'atelier restent écartés, et il n'existe
    /// pas de carte vendue entre 6 et 8 Go.
    /// </summary>
    public const double MargeDeMesureGo = 0.5;

    /// <summary>
    /// Cette carte peut-elle porter le modèle puissant ?
    ///
    /// <b>La règle vit ICI et nulle part ailleurs.</b> Elle était recopiée à trois endroits
    /// — le choix du modèle au démarrage, l'avertissement des réglages, et le grisage du
    /// bouton — ce qui est précisément ce qui laisse un seuil diverger d'une copie à l'autre.
    ///
    /// Une carte qui n'annonce pas sa mémoire passe pour capable : on ne retire pas un choix
    /// à quelqu'un sur un doute, et le repli rattrape de toute façon un modèle qui n'aurait
    /// pas tenu.
    /// </summary>
    /// <param name="memoireGo">Le relevé de la carte, ou null si elle ne le donne pas.</param>
    public static bool AssezDeMemoirePourLeModelePuissant(double? memoireGo) =>
        memoireGo is not { } go || go >= MemoireVideoMinimaleGo;

    /// <summary>Le fichier de modèle demandé par ces réglages.</summary>
    public string ModeleDemande => ModelePuissant ? ModelePuissantFichier : ModeleLeger;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge les réglages. Un fichier absent ou abîmé rend les valeurs par défaut plutôt
    /// que de lever : le détourage doit rester possible, et la méthode par couleur ne
    /// dépend de rien.
    /// </summary>
    public static DetourageSettings Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return new DetourageSettings();

        try
        {
            using var flux = File.OpenRead(chemin);
            return JsonSerializer.Deserialize<DetourageSettings>(flux, Options) ?? new DetourageSettings();
        }
        catch (Exception)
        {
            return new DetourageSettings();
        }
    }

    /// <summary>Enregistre les réglages, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, DetourageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(configDir);
        var chemin = Path.Combine(configDir, FileName);
        var json = JsonSerializer.Serialize(settings, Options);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
