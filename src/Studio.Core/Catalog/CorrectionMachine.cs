using System.Text.Json;
using Studio.Core.Domain;

namespace Studio.Core.Catalog;

/// <summary>
/// La correction propre à une MACHINE : ce qu'il faut ajouter au rendu pour que le papier
/// ressemble à l'écran.
///
/// <b>Pourquoi elle existe.</b> <see cref="Product.PrintExposure"/> répondait déjà à ce
/// besoin, mais en une seule valeur — l'exposition — et par PRODUIT, donc à retoucher
/// produit par produit dans le Catalogue, écran que Studio Photo Identité n'a pas. Or
/// l'écart ne vient pas du produit : il vient de la machine et de son papier. La DS620
/// d'une boutique sort plus dense que l'écran du comptoir, et la même correction vaut alors
/// pour la planche d'identité, l'E-Photo et le 10×15. Demandé le 18/08/2026 : « les photos
/// sont légèrement plus foncées », et « je voudrais un profil manuel que l'on peut modifier
/// à sa guise et désactiver ».
///
/// <b>⚠ ELLE NE TOUCHE PAS L'APERÇU</b>, et c'est tout l'objet du réglage — même règle que
/// <see cref="Product.PrintExposure"/>, point 3. Elle compense l'écart entre ce que l'écran
/// montre et ce que la machine sort : l'appliquer aussi à l'aperçu éclaircirait les DEUX et
/// laisserait l'écart intact. C'est exactement le défaut corrigé le 18/08/2026 dans
/// l'aperçu des planches d'identité.
///
/// <b>Elle s'AJOUTE aux réglages de l'opérateur</b>, elle ne les remplace pas : un portrait
/// déjà éclairci à la main le reste, et reçoit la compensation par-dessus.
/// </summary>
public sealed class CorrectionMachine
{
    /// <summary>
    /// L'interrupteur. Éteint, la machine reçoit exactement ce que l'écran montrait —
    /// c'est l'état de départ, et celui où l'on revient pour juger d'un tirage sans se
    /// demander ce qui a bien pu s'ajouter en route.
    /// </summary>
    public bool Actif { get; set; }

    /// <summary>Exposition en diaphragmes (IL), l'échelle de la photographie : +1 double la lumière.</summary>
    public double Exposition { get; set; }

    /// <summary>Contraste, −100..100.</summary>
    public double Contraste { get; set; }

    /// <summary>Hautes lumières, −100..100 : négatif retient un front brûlé par le flash.</summary>
    public double HautesLumieres { get; set; }

    /// <summary>Ombres, −100..100 : positif rouvre un visage bouché par la machine.</summary>
    public double Ombres { get; set; }

    /// <summary>Température, −100 (froid, bleu) .. 100 (chaud, orangé).</summary>
    public double Temperature { get; set; }

    /// <summary>Teinte, −100 (vert) .. 100 (magenta).</summary>
    public double Teinte { get; set; }

    /// <summary>Saturation, −100..100.</summary>
    public double Saturation { get; set; }

    /// <summary>Netteté ajoutée, 0..100 : la sublimation adoucit toujours un peu le détail.</summary>
    public double Nettete { get; set; }

    /// <summary>Bornes de saisie, pour que l'écran et le fichier disent la même chose.</summary>
    public const double ExpositionMax = 2;

    /// <summary>Vraie quand il n'y a rien à ajouter — active ou non.</summary>
    public bool EstNeutre =>
        Exposition == 0 && Contraste == 0 && HautesLumieres == 0 && Ombres == 0 &&
        Temperature == 0 && Teinte == 0 && Saturation == 0 && Nettete == 0;

    /// <summary>
    /// Les réglages du tirage : ceux de l'opérateur, plus cette correction.
    ///
    /// <b>L'objet reçu n'est JAMAIS modifié</b> : il appartient à la commande enregistrée,
    /// et l'y ajouter ferait s'empiler la correction à chaque réimpression — la troisième
    /// sortirait délavée. C'est le piège déjà rencontré sur <c>PrintExposure</c>, et il
    /// vaut ici pour les huit valeurs.
    ///
    /// Rien à ajouter : on rend l'objet TEL QUEL, sans copie. Les milliers de tirages du
    /// minilab, qui n'ont aucune correction, n'en paient pas une.
    /// </summary>
    public ImageAdjustments Appliquer(ImageAdjustments reglages)
    {
        ArgumentNullException.ThrowIfNull(reglages);

        if (!Actif || EstNeutre) return reglages;

        var corriges = reglages.Clone();

        corriges.Exposure = Borner(corriges.Exposure + Exposition, ExpositionMax);
        corriges.Contrast = Borner(corriges.Contrast + Contraste, 100);
        corriges.Highlights = Borner(corriges.Highlights + HautesLumieres, 100);
        corriges.Shadows = Borner(corriges.Shadows + Ombres, 100);
        corriges.Temperature = Borner(corriges.Temperature + Temperature, 100);
        corriges.Tint = Borner(corriges.Tint + Teinte, 100);
        corriges.Saturation = Borner(corriges.Saturation + Saturation, 100);

        // La netteté ne va que de 0 à 100 : une somme qui dépasserait ne veut rien dire
        // pour le pipeline, qui la lit comme un rayon.
        corriges.Sharpness = Math.Clamp(corriges.Sharpness + Nettete, 0, 100);

        return corriges;
    }

    /// <summary>
    /// Somme bornée. <b>On borne la SOMME, pas la correction</b> : une photo déjà poussée à
    /// +100 de contraste par l'opérateur ne doit pas sortir à +130 parce que la machine en
    /// demandait 30 — le pipeline n'a pas de sens au-delà, et le tirage part en carton.
    /// </summary>
    private static double Borner(double valeur, double max) => Math.Clamp(valeur, -max, max);

    public CorrectionMachine Clone() => (CorrectionMachine)MemberwiseClone();
}

/// <summary>
/// Les corrections de toutes les machines du poste, telles qu'elles vivent dans
/// <c>config/corrections-machines.json</c>.
///
/// <b>Rangées par FILE D'IMPRESSION</b> — le nom Windows que porte déjà le produit
/// (<see cref="Product.PrinterName"/>) — et non par produit : c'est le même choix que le
/// profil couleur de la DNP, pour la même raison. Trois produits sortent de la DS620 dans
/// ces boutiques, et la couleur du papier ne fait pas de différence entre eux.
///
/// <b>Dans le dossier de CONFIG, pas dans le catalogue.</b> Le catalogue se publie d'une
/// boutique à l'autre — formats, prix, canaux — alors qu'une compensation se mesure sur UNE
/// machine et son rouleau. La distribuer avec le catalogue l'appliquerait à des machines qui
/// n'en veulent pas, comme les DEVMODE avant elle.
/// </summary>
public sealed class CorrectionsMachines
{
    /// <summary>
    /// Par nom de file d'impression. Insensible à la casse : le spouleur écrit
    /// « DP-DS620 » là où un opérateur tape « dp-ds620 », et une correction qui ne
    /// s'appliquerait pas faute d'une majuscule serait introuvable.
    /// </summary>
    public Dictionary<string, CorrectionMachine> Machines { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>La correction de cette machine, ou null quand elle n'en a pas.</summary>
    public CorrectionMachine? Pour(string? imprimante) =>
        string.IsNullOrWhiteSpace(imprimante) ? null
            : Machines.TryGetValue(imprimante, out var correction) ? correction
            : null;

    /// <summary>
    /// Pose (ou remplace) la correction d'une machine. Une correction éteinte ET neutre est
    /// RETIRÉE : le fichier ne garde alors aucune ligne, et l'on voit d'un coup d'œil quelles
    /// machines sont corrigées.
    /// </summary>
    public void Poser(string imprimante, CorrectionMachine correction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imprimante);
        ArgumentNullException.ThrowIfNull(correction);

        if (!correction.Actif && correction.EstNeutre) Machines.Remove(imprimante);
        else Machines[imprimante] = correction;
    }

    /// <summary>
    /// Les réglages du tirage pour un produit : ceux de l'opérateur, plus la correction de
    /// la machine qui va le sortir. Sans correction, l'objet est rendu tel quel.
    /// </summary>
    public ImageAdjustments Appliquer(ImageAdjustments reglages, string? imprimante) =>
        Pour(imprimante)?.Appliquer(reglages) ?? reglages;

    /// <summary>
    /// La machine d'un produit, telle que ce réglage la nomme.
    ///
    /// <b>La file Windows quand il y en a une, le canal sinon.</b> Le minilab DE100 ne
    /// passe pas par le spouleur : son <see cref="Product.PrinterName"/> est vide, et s'en
    /// tenir à lui l'aurait rendu impossible à compenser. Or c'est précisément la
    /// comparaison entre les deux machines qui a fait naître ce réglage — « la DS620 sort
    /// plus sombre que le minilab, sur la même photo et le même fichier ».
    /// </summary>
    public static string CleDe(Product produit)
    {
        ArgumentNullException.ThrowIfNull(produit);

        return string.IsNullOrWhiteSpace(produit.PrinterName) ? produit.Channel : produit.PrinterName;
    }

    /// <summary>Les réglages du tirage pour ce produit — voir <see cref="CleDe"/>.</summary>
    public ImageAdjustments Appliquer(ImageAdjustments reglages, Product produit) =>
        Appliquer(reglages, CleDe(produit));

    /// <summary>Nom du fichier, dans le dossier config/.</summary>
    public const string FileName = "corrections-machines.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Charge les corrections du poste. Un fichier absent ou abîmé rend des corrections
    /// VIDES plutôt que de lever : une compensation illisible doit priver du réglage, jamais
    /// du tirage — c'est la règle déjà tenue par la marque et par les raccourcis d'identité.
    /// </summary>
    public static CorrectionsMachines Load(string configDir)
    {
        var chemin = Path.Combine(configDir, FileName);
        if (!File.Exists(chemin)) return new CorrectionsMachines();

        try
        {
            using var flux = File.OpenRead(chemin);
            var lues = JsonSerializer.Deserialize<CorrectionsMachines>(flux, Options);

            // Un fichier écrit sans la section, ou avec « Machines: null » : le dictionnaire
            // doit rester joignable, et surtout garder son comparateur insensible à la casse
            // — celui que la désérialisation ne rétablit pas.
            if (lues is null) return new CorrectionsMachines();

            var corrections = new CorrectionsMachines();
            foreach (var (machine, correction) in lues.Machines)
                if (!string.IsNullOrWhiteSpace(machine) && correction is not null)
                    corrections.Machines[machine] = correction;

            return corrections;
        }
        catch (Exception)
        {
            return new CorrectionsMachines();
        }
    }

    /// <summary>Enregistre les corrections, en écrivant à côté puis en remplaçant.</summary>
    public static void Save(string configDir, CorrectionsMachines corrections)
    {
        ArgumentNullException.ThrowIfNull(corrections);

        Directory.CreateDirectory(configDir);
        var chemin = Path.Combine(configDir, FileName);
        var json = JsonSerializer.Serialize(corrections, Options);

        var tmp = chemin + ".tmp";
        File.WriteAllText(tmp, json);
        if (File.Exists(chemin))
            File.Replace(tmp, chemin, chemin + ".bak", ignoreMetadataErrors: true);
        else
            File.Move(tmp, chemin);
    }
}
