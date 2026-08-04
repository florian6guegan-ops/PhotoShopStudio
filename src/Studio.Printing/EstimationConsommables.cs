using System.Text.Json;
using Studio.Printing.Devices.Fuji;

namespace Studio.Printing;

/// <summary>Ce qui arrêtera la machine en premier.</summary>
public enum Limite
{
    /// <summary>Rien ne manque à court terme.</summary>
    Aucune,

    /// <summary>La longueur de rouleau restante.</summary>
    Papier,

    /// <summary>Une encre, celle qui descend le plus vite.</summary>
    Encre,

    /// <summary>Le bac de récupération, qui se remplit au lieu de se vider.</summary>
    BacDeMaintenance,
}

/// <summary>
/// Combien de tirages d'un format donné la machine peut encore sortir, et ce qui
/// l'arrêtera.
/// </summary>
/// <param name="Tirages">Le nombre annoncé : le plus petit des trois comptes.</param>
/// <param name="Limite">Ce qui l'arrêtera en premier.</param>
/// <param name="Detail">De quoi il s'agit — « magenta 15 % », « 44,1 m » — pour l'affichage.</param>
/// <param name="ParLePapier">Ce que le papier seul permettrait.</param>
/// <param name="ParLEncre">Ce que les encres seules permettraient ; -1 si on ne sait pas.</param>
/// <param name="ParLeBac">Ce que le bac de maintenance seul permettrait ; -1 si on ne sait pas.</param>
/// <param name="Approximative">
/// Vrai tant que la consommation n'a pas été observée sur cette machine : les comptes
/// d'encre et de bac reposent alors sur des valeurs par défaut, et non sur ses tirages à
/// elle.
/// </param>
public sealed record EstimationRestante(
    int Tirages,
    Limite Limite,
    string Detail,
    int ParLePapier,
    int ParLEncre,
    int ParLeBac,
    bool Approximative)
{
    /// <summary>Ce qu'on écrit dans le bandeau : le compte, puis ce qui limite.</summary>
    public string Resume(string nomDuFormat)
    {
        var tilde = Approximative ? "~" : "";
        var compte = $"{tilde}{Tirages} × {nomDuFormat}";

        return Limite switch
        {
            Limite.Aucune => compte,
            _ => $"{compte} · {Detail}",
        };
    }
}

/// <summary>
/// Ce qu'on a observé de la consommation d'une machine, pour estimer ce qu'il lui reste.
/// </summary>
/// <param name="TiragesParPourcentDEncre">
/// Tirages sortis pour un point de pourcentage d'encre consommé, sur l'encre qui descend le
/// plus vite. Zéro = jamais observé.
/// </param>
/// <param name="TiragesParPourcentDeBac">
/// Tirages sortis pour un point de remplissage du bac de maintenance. Zéro = jamais observé.
/// </param>
/// <param name="Compteur">Compteur de la machine au dernier relevé.</param>
/// <param name="EncreLaPlusBasse">Niveau de l'encre la plus basse au dernier relevé.</param>
/// <param name="Bac">Remplissage du bac au dernier relevé.</param>
/// <param name="Le">Date du dernier relevé.</param>
public sealed record ObservationMachine(
    double TiragesParPourcentDEncre,
    double TiragesParPourcentDeBac,
    long Compteur,
    int EncreLaPlusBasse,
    int Bac,
    DateTimeOffset Le)
{
    public static ObservationMachine Vide => new(0, 0, 0, 0, 0, DateTimeOffset.MinValue);

    /// <summary>Vrai quand la consommation a été observée sur cette machine.</summary>
    public bool Calibree => TiragesParPourcentDEncre > 0;
}

/// <summary>
/// L'estimation de ce qu'une machine peut encore sortir, TOUS consommables confondus.
///
/// <b>Pourquoi elle existe.</b> Le bandeau annonçait « ~576 × 10x15 » d'après le seul
/// papier restant. Sur la machine B du 04/08/2026 — magenta à 15 %, bac de maintenance à
/// 38 % — ce chiffre était un mensonge : l'encre s'arrêtera bien avant le rouleau. Un
/// opérateur qui lance une commande de trois cents tirages sur cette annonce se retrouve à
/// mi-parcours avec une machine à l'arrêt et un client devant lui.
///
/// <b>Trois comptes, et l'on retient le plus petit</b> : le papier, l'encre, le bac de
/// maintenance. Le résultat dit toujours LEQUEL limite, parce que c'est la seule chose qui
/// dise quoi préparer.
///
/// <b>La calibration s'apprend.</b> Convertir un pourcentage d'encre en tirages dépend de
/// la machine, du format et de ce qu'on imprime : un fond noir vide une cartouche bien plus
/// vite qu'un portrait sur fond blanc. Aucune valeur écrite dans le code ne serait juste.
/// On observe donc la machine — compteur et niveaux, relevés à chaque passage — et l'on en
/// déduit sa consommation réelle. Tant qu'on n'a pas deux relevés utilisables, l'estimation
/// est marquée APPROXIMATIVE et le bandeau l'annonce avec un tilde.
/// </summary>
public static class EstimationConsommables
{
    /// <summary>
    /// Valeur de départ, le temps d'observer la machine : tirages par point de pourcentage
    /// d'encre.
    ///
    /// Volontairement PRUDENTE. Une estimation trop basse fait changer une cartouche un peu
    /// tôt ; une estimation trop haute laisse une commande de trois cents tirages s'arrêter
    /// au milieu. Le premier défaut coûte quelques euros, le second coûte un client.
    /// </summary>
    public const double TiragesParPourcentParDefaut = 8;

    /// <summary>Idem pour le bac de maintenance, qui se remplit au lieu de se vider.</summary>
    public const double TiragesParPourcentDeBacParDefaut = 20;

    /// <summary>
    /// Sous ce niveau, une encre est annoncée même si elle n'est pas le facteur limitant :
    /// c'est le moment de commander la cartouche, pas celui de la changer.
    /// </summary>
    public const int SeuilEncreBasse = 20;

    /// <summary>
    /// L'estimation pour un format donné.
    /// </summary>
    /// <param name="format">Le format visé — celui qu'on s'apprête à tirer.</param>
    /// <param name="media">Le rouleau chargé.</param>
    /// <param name="supplies">Encres et bac ; null si la machine n'en dit rien.</param>
    /// <param name="observation">Ce qu'on a appris de cette machine ; null = rien encore.</param>
    public static EstimationRestante Pour(
        De100Format format,
        De100Media media,
        De100Supplies? supplies,
        ObservationMachine? observation = null)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(media);

        var parLePapier = De100Formats.EstimatePrints(format, media.PaperRemainingMm, media.PaperWidthMm);

        // sans lecture des consommables, on ne sait rien de plus que le papier
        if (supplies is null)
            return new(parLePapier, Limite.Papier, Decrire(media), parLePapier, -1, -1, false);

        var vue = observation ?? ObservationMachine.Vide;
        var parPourcentEncre = vue.TiragesParPourcentDEncre > 0
            ? vue.TiragesParPourcentDEncre
            : TiragesParPourcentParDefaut;
        var parPourcentBac = vue.TiragesParPourcentDeBac > 0
            ? vue.TiragesParPourcentDeBac
            : TiragesParPourcentDeBacParDefaut;

        var encreBasse = supplies.Inks.OrderBy(i => i.Level).First();
        var parLEncre = (int)(Math.Max(0, encreBasse.Level) * parPourcentEncre);

        // le bac se REMPLIT : ce qui reste, c'est ce qui manque pour arriver à 100
        var placeAuBac = Math.Max(0, 100 - supplies.MaintenanceTank.Level);
        var parLeBac = (int)(placeAuBac * parPourcentBac);

        var tirages = Math.Min(parLePapier, Math.Min(parLEncre, parLeBac));

        var (limite, detail) =
            tirages == parLEncre && parLEncre <= parLePapier && parLEncre <= parLeBac
                ? (Limite.Encre, $"{encreBasse.Name.ToLowerInvariant()} à {encreBasse.Level} %")
            : tirages == parLeBac && parLeBac <= parLePapier
                ? (Limite.BacDeMaintenance, $"bac de maintenance à {supplies.MaintenanceTank.Level} %")
            : (Limite.Papier, Decrire(media));

        // une encre basse se dit même quand elle ne limite pas encore : c'est le moment de
        // commander la cartouche
        if (limite == Limite.Papier && encreBasse.Level <= SeuilEncreBasse)
            detail += $", {encreBasse.Name.ToLowerInvariant()} à {encreBasse.Level} %";

        return new(tirages, limite, detail, parLePapier, parLEncre, parLeBac,
                   Approximative: !vue.Calibree);
    }

    private static string Decrire(De100Media media) => $"{media.PaperRemainingMm / 1000:0.0} m";

    /// <summary>
    /// Apprend la consommation de la machine en comparant deux relevés.
    ///
    /// <b>Ce qui rend l'observation utilisable</b>, et qu'il ne faut pas assouplir :
    ///
    /// 1. le compteur doit avoir AVANCÉ d'assez de tirages — sur dix tirages, un point de
    ///    pourcentage de plus ou de moins fausse tout d'un facteur deux ;
    /// 2. le niveau doit avoir BAISSÉ — une cartouche qu'on vient de changer remonte, et
    ///    l'écart n'a alors aucun sens ;
    /// 3. le compteur ne doit pas avoir reculé — machine remplacée, ou relevé d'une autre.
    ///
    /// Quand un relevé n'apprend rien, on garde la calibration précédente et l'on met
    /// seulement le point de repère à jour.
    /// </summary>
    /// <param name="precedent">Le dernier relevé retenu.</param>
    /// <param name="compteur">Compteur de la machine, maintenant.</param>
    /// <param name="supplies">Consommables, maintenant.</param>
    /// <param name="maintenant">Horodatage.</param>
    public static ObservationMachine Apprendre(
        ObservationMachine? precedent, long compteur, De100Supplies? supplies,
        DateTimeOffset maintenant)
    {
        if (supplies is null) return precedent ?? ObservationMachine.Vide;

        var encre = supplies.Inks.Min(i => i.Level);
        var bac = supplies.MaintenanceTank.Level;

        // premier relevé : on note le point de départ, rien de plus
        if (precedent is null || precedent.Compteur <= 0)
            return new ObservationMachine(0, 0, compteur, encre, bac, maintenant);

        var tirages = compteur - precedent.Compteur;
        if (tirages < TiragesMinimumPourApprendre)
            return precedent with { Compteur = compteur, EncreLaPlusBasse = encre, Bac = bac, Le = maintenant };

        var baisseEncre = precedent.EncreLaPlusBasse - encre;
        var hausseBac = bac - precedent.Bac;

        var parEncre = baisseEncre > 0 ? tirages / (double)baisseEncre : precedent.TiragesParPourcentDEncre;
        var parBac = hausseBac > 0 ? tirages / (double)hausseBac : precedent.TiragesParPourcentDeBac;

        return new ObservationMachine(parEncre, parBac, compteur, encre, bac, maintenant);
    }

    /// <summary>
    /// En deçà, deux relevés n'apprennent rien : le pourcentage d'encre est un entier, et
    /// sur dix tirages son arrondi fausserait le calcul d'un facteur deux.
    /// </summary>
    public const int TiragesMinimumPourApprendre = 50;

    // ————— persistance —————

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Les observations, par machine, dans les données du poste.
    ///
    /// Elles vivent hors du dépôt : ce sont des mesures de CES machines-là, elles n'ont
    /// aucun sens ailleurs, et elles se refont toutes seules sur une nouvelle installation.
    /// </summary>
    public static Dictionary<string, ObservationMachine> Charger(string chemin)
    {
        try
        {
            if (!File.Exists(chemin)) return [];

            return JsonSerializer.Deserialize<Dictionary<string, ObservationMachine>>(
                File.ReadAllText(chemin), Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException)
        {
            // fichier abîmé : on repart d'observations vides, elles se referont
            return [];
        }
    }

    /// <summary>Enregistre les observations. N'échoue jamais : c'est du confort, pas une commande.</summary>
    public static void Enregistrer(string chemin, Dictionary<string, ObservationMachine> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin, JsonSerializer.Serialize(observations, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // tant pis : l'estimation repartira des valeurs par défaut
        }
    }
}
