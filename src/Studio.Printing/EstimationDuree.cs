using System.Text.Json;

namespace Studio.Printing;

/// <summary>
/// Ce qu'on a mesuré du débit d'une machine sur un format donné.
/// </summary>
/// <param name="SecondesParTirage">Temps moyen par photo, maintenances comprises.</param>
/// <param name="TiragesMesures">Sur combien de tirages la moyenne repose.</param>
public sealed record DebitMesure(double SecondesParTirage, int TiragesMesures)
{
    /// <summary>Vrai quand la mesure repose sur assez de tirages pour valoir mieux qu'un défaut.</summary>
    public bool Fiable => TiragesMesures >= EstimationDuree.TiragesPourEtreFiable;
}

/// <summary>
/// Combien de temps une commande va encore prendre.
///
/// <b>Pourquoi.</b> Le bandeau disait « 12 / 24 photos sorties » et rien d'autre.
/// L'opérateur qui a un client devant lui veut savoir s'il a le temps de servir quelqu'un
/// d'autre, et cette question-là n'a qu'une réponse : une durée.
///
/// <b>En fonction du FORMAT.</b> Un A4 ne sort pas à la même cadence qu'un 10×15 : le
/// papier défile plus longtemps et la tête a plus de surface à couvrir. Une moyenne toutes
/// tailles confondues annoncerait n'importe quoi sur une commande d'agrandissements.
///
/// <b>Maintenances comprises</b>, et c'est voulu : le DE100 s'interrompt pour nettoyer sa
/// tête, sécher, avancer son papier. Ces pauses font partie du temps que l'opérateur
/// attend — les exclure donnerait une estimation toujours trop courte, c'est-à-dire la
/// pire des deux. On ne les modélise donc pas séparément : le débit est mesuré de bout en
/// bout, elles y sont dedans.
///
/// <b>Le débit s'apprend.</b> Comme pour les consommables, aucune constante écrite dans le
/// code ne vaudrait pour toutes les machines. On chronomètre les commandes réelles.
/// </summary>
public static class EstimationDuree
{
    /// <summary>
    /// Débit de départ, le temps de mesurer : secondes par tirage, selon la longueur de
    /// papier que le format consomme.
    ///
    /// Ces valeurs viennent des commandes du 04/08/2026 — un 10×15 sort en quelques
    /// secondes, un A4 en une vingtaine. Elles ne servent qu'au premier tirage : dès qu'une
    /// commande a été chronométrée sur un format, c'est la mesure qui parle.
    /// </summary>
    public static double SecondesParDefaut(int longueurMm) => longueurMm switch
    {
        <= 0 => 6,
        <= 120 => 5,     // 10×10, 9×13
        <= 160 => 6,     // 10×15, 13×15
        <= 220 => 9,     // 15×20, 13×18
        <= 320 => 14,    // 20×30, 15×30
        _ => 20,         // A4 et au-delà
    };

    /// <summary>
    /// En deçà, une mesure ne vaut pas mieux qu'un défaut : sur deux ou trois tirages, le
    /// réveil de la machine ou une maintenance qui tombe là fausse tout.
    /// </summary>
    public const int TiragesPourEtreFiable = 10;

    /// <summary>
    /// Le temps qu'il reste à attendre.
    /// </summary>
    /// <param name="restants">Photos qui ne sont pas encore sorties.</param>
    /// <param name="longueurMm">Longueur de papier que consomme UN tirage de ce format.</param>
    /// <param name="mesure">Ce qu'on a chronométré sur ce format ; null = jamais mesuré.</param>
    public static TimeSpan Restant(int restants, int longueurMm, DebitMesure? mesure = null)
    {
        if (restants <= 0) return TimeSpan.Zero;

        var secondes = mesure is { SecondesParTirage: > 0 }
            ? mesure.SecondesParTirage
            : SecondesParDefaut(longueurMm);

        return TimeSpan.FromSeconds(restants * secondes);
    }

    /// <summary>
    /// La durée en toutes lettres, arrondie à ce qui se dit à voix haute.
    ///
    /// Personne n'annonce « 4 minutes 37 » à un client : on dit « environ 5 minutes ». La
    /// précision affichée doit correspondre à la précision réelle, sans quoi elle promet
    /// ce qu'elle ne peut pas tenir.
    /// </summary>
    /// <param name="approximatif">Ajoute « environ » tant que le débit n'est pas mesuré.</param>
    public static string Ecrire(TimeSpan duree, bool approximatif = true)
    {
        if (duree <= TimeSpan.Zero) return "";

        var environ = approximatif ? "environ " : "";

        if (duree.TotalSeconds < 45) return "moins d'une minute";
        if (duree.TotalMinutes < 2) return $"{environ}1 minute";

        if (duree.TotalMinutes < 10)
            return $"{environ}{Math.Round(duree.TotalMinutes):0} minutes";

        // au-delà de dix minutes, le quart d'heure suffit : annoncer « 23 minutes » sur une
        // estimation à ±20 % serait un faux-semblant de précision
        if (duree.TotalHours < 1)
            return $"{environ}{Math.Round(duree.TotalMinutes / 5) * 5:0} minutes";

        var heures = (int)duree.TotalHours;
        var minutes = (int)Math.Round((duree.TotalMinutes - heures * 60) / 15) * 15;
        if (minutes >= 60) { heures++; minutes = 0; }

        return minutes == 0
            ? $"{environ}{heures} h"
            : $"{environ}{heures} h {minutes:00}";
    }

    /// <summary>
    /// Apprend le débit d'un format à partir d'une commande chronométrée.
    ///
    /// Moyenne PONDÉRÉE par le nombre de tirages : une commande de soixante photos pèse
    /// plus qu'une commande d'une seule, et c'est juste — la seconde est presque
    /// entièrement faite du réveil de la machine.
    /// </summary>
    /// <param name="precedent">Ce qu'on savait de ce format ; null = rien encore.</param>
    /// <param name="tirages">Photos réellement sorties.</param>
    /// <param name="duree">Temps écoulé du premier au dernier tirage.</param>
    public static DebitMesure? Apprendre(DebitMesure? precedent, int tirages, TimeSpan duree)
    {
        // une commande d'un seul tirage n'apprend rien : elle est presque entièrement faite
        // du réveil de la machine, qui ne se reproduira pas sur les suivantes
        if (tirages < 2 || duree <= TimeSpan.Zero) return precedent;

        var secondes = duree.TotalSeconds / tirages;

        // une valeur aberrante — machine en panne au milieu, opérateur parti déjeuner — ne
        // doit pas empoisonner la moyenne pour toujours
        if (secondes is <= 0 or > SecondesMaximumRetenues) return precedent;

        if (precedent is null) return new DebitMesure(secondes, tirages);

        var total = precedent.TiragesMesures + tirages;
        var moyenne = (precedent.SecondesParTirage * precedent.TiragesMesures
                       + secondes * tirages) / total;

        // le compte est borné : au-delà, la moyenne devient insensible et une machine qui
        // ralentit — tête encrassée, papier plus épais — ne serait plus jamais suivie
        return new DebitMesure(moyenne, Math.Min(total, TiragesRetenus));
    }

    /// <summary>Au-delà, la mesure est jugée aberrante et rejetée.</summary>
    public const double SecondesMaximumRetenues = 300;

    /// <summary>
    /// Plafond du compte de tirages retenus dans la moyenne. Il fait de la moyenne une
    /// moyenne GLISSANTE : la machine peut ralentir avec le temps, et l'estimation doit
    /// suivre.
    /// </summary>
    public const int TiragesRetenus = 500;

    // ————— persistance —————

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Les débits mesurés, par format, dans les données du poste.</summary>
    public static Dictionary<string, DebitMesure> Charger(string chemin)
    {
        try
        {
            if (!File.Exists(chemin)) return [];

            return JsonSerializer.Deserialize<Dictionary<string, DebitMesure>>(
                File.ReadAllText(chemin), Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>Enregistre les débits. N'échoue jamais : c'est du confort.</summary>
    public static void Enregistrer(string chemin, Dictionary<string, DebitMesure> debits)
    {
        ArgumentNullException.ThrowIfNull(debits);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            File.WriteAllText(chemin, JsonSerializer.Serialize(debits, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // tant pis : l'estimation repartira des valeurs par défaut
        }
    }
}
