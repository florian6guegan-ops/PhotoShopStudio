using System.Windows;
using System.Windows.Controls;
using Studio.App.Infrastructure;
using Studio.Core.Catalog;
using Studio.Core.Domain;

namespace Studio.App.Views;

/// <summary>
/// La compensation d'impression, machine par machine : ce qu'on ajoute au tirage pour que
/// le papier ressemble à l'écran.
///
/// <b>Pourquoi cet écran.</b> Le besoin était déjà là — <see cref="Product.PrintExposure"/>,
/// posé le 04/08/2026 — mais réglable seulement produit par produit, dans le Catalogue, en
/// tapant un nombre, et sur un écran que Studio Photo Identité n'a pas. Demandé le
/// 18/08/2026 : « un profil manuel que l'on peut modifier à sa guise et désactiver ».
///
/// <b>⚠ ON NE MONTRE PAS LE RÉSULTAT À L'ÉCRAN</b>, et c'est le cœur du réglage : il rattrape
/// l'écart entre l'écran et le papier. Un aperçu compensé éclaircirait les deux et laisserait
/// l'écart intact — le défaut exact corrigé le même jour dans l'aperçu des planches.
///
/// Le même écran pour les deux logiciels, comme le profil couleur : <b>les BOUTONS se
/// doublent, ce qu'ils font, non.</b> La règle qui décide de ce qui s'ajoute vit dans
/// <see cref="CorrectionMachine"/>, où elle se vérifie sans imprimante.
/// </summary>
public partial class ReglagesCorrectionImpressionView : UserControl
{
    /// <summary>
    /// Les corrections en cours d'édition — une COPIE. « Retour » doit laisser le poste
    /// exactement comme on l'a trouvé, y compris après avoir promené trois curseurs.
    /// </summary>
    private CorrectionsMachines _corrections = new();

    /// <summary>Les machines du catalogue, dans l'ordre où elles s'affichent.</summary>
    private IReadOnlyList<string> _machines = [];

    /// <summary>Vrai le temps de poser les curseurs : les gestionnaires doivent se taire.</summary>
    private bool _chargement;

    public ReglagesCorrectionImpressionView()
    {
        InitializeComponent();
        Loaded += (_, _) => Montrer();
    }

    private void Montrer()
    {
        _corrections = Copie(App.Services.Corrections);
        _machines = MachinesDuCatalogue();

        MachineCombo.ItemsSource = _machines;

        // Sans machine, l'écran n'a rien à régler : on le DIT, plutôt que de proposer une
        // liste vide et des curseurs qui n'iraient nulle part.
        if (_machines.Count == 0)
        {
            MachineCombo.IsEnabled = false;
            ActifCheck.IsEnabled = false;
            CurseursCard.IsEnabled = false;
            EtatText.Text = "Aucune machine dans le catalogue : activez un produit pour pouvoir régler sa compensation.";
            return;
        }

        MachineCombo.SelectedIndex = 0;
        PoserLesCurseurs();
    }

    /// <summary>
    /// Les machines des produits ACTIFS, sans doublon.
    ///
    /// Le catalogue en compte une trentaine pour trois machines : c'est la machine qu'on
    /// règle, pas le produit, et lister les produits ferait croire à un réglage par produit
    /// — celui-là même dont on vient de sortir.
    /// </summary>
    private static IReadOnlyList<string> MachinesDuCatalogue() =>
        App.Services.Catalog.Enabled
            .Select(CorrectionsMachines.CleDe)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    /// <summary>Copie indépendante : voir <see cref="_corrections"/>.</summary>
    private static CorrectionsMachines Copie(CorrectionsMachines source)
    {
        var copie = new CorrectionsMachines();
        foreach (var (machine, correction) in source.Machines)
            copie.Machines[machine] = correction.Clone();
        return copie;
    }

    private string? MachineChoisie => MachineCombo.SelectedItem as string;

    /// <summary>La correction de la machine affichée, neuve si elle n'en a pas encore.</summary>
    private CorrectionMachine CorrectionCourante =>
        MachineChoisie is { } machine
            ? _corrections.Pour(machine)?.Clone() ?? new CorrectionMachine()
            : new CorrectionMachine();

    // ----- affichage -----

    private void PoserLesCurseurs()
    {
        var correction = CorrectionCourante;

        _chargement = true;
        try
        {
            ActifCheck.IsChecked = correction.Actif;

            ExpositionSlider.Value = correction.Exposition;
            ContrasteSlider.Value = correction.Contraste;
            HautesLumieresSlider.Value = correction.HautesLumieres;
            OmbresSlider.Value = correction.Ombres;
            TemperatureSlider.Value = correction.Temperature;
            TeinteSlider.Value = correction.Teinte;
            SaturationSlider.Value = correction.Saturation;
            NetteteSlider.Value = correction.Nettete;
        }
        finally
        {
            _chargement = false;
        }

        DireOuEnEst();
    }

    /// <summary>
    /// Les libellés portent la VALEUR, comme le panneau de correction des photos : un
    /// curseur sans chiffre ne se retrouve pas d'un tirage à l'autre, et c'est justement ce
    /// qu'on vient comparer.
    /// </summary>
    private void DireOuEnEst()
    {
        ExpositionLabel.Text = $"Exposition : {ExpositionSlider.Value:+0.00;−0.00;0} IL";
        ContrasteLabel.Text = $"Contraste : {ContrasteSlider.Value:+0;−0;0}";
        HautesLumieresLabel.Text = $"Hautes lumières : {HautesLumieresSlider.Value:+0;−0;0}";
        OmbresLabel.Text = $"Ombres : {OmbresSlider.Value:+0;−0;0}";
        TemperatureLabel.Text = $"Température : {TemperatureSlider.Value:+0;−0;0}";
        TeinteLabel.Text = $"Teinte : {TeinteSlider.Value:+0;−0;0}";
        SaturationLabel.Text = $"Saturation : {SaturationSlider.Value:+0;−0;0}";
        NetteteLabel.Text = $"Netteté : {NetteteSlider.Value:0}";

        var correction = LireLesCurseurs();

        EtatText.Text = correction switch
        {
            { Actif: false } when correction.EstNeutre =>
                "Cette machine n'est pas compensée : elle reçoit ce que l'écran montre.",
            { Actif: false } =>
                "Correction réglée mais ÉTEINTE : la machine reçoit ce que l'écran montre. "
                + "Les valeurs sont conservées.",
            _ when correction.EstNeutre =>
                "Correction allumée, mais tous les curseurs sont à zéro : rien ne s'ajoute au tirage.",
            _ => $"Correction appliquée à tout ce qui sort de « {MachineChoisie} ».",
        };
    }

    /// <summary>Ce que les curseurs disent, sans rien enregistrer.</summary>
    private CorrectionMachine LireLesCurseurs() => new()
    {
        Actif = ActifCheck.IsChecked == true,
        Exposition = ExpositionSlider.Value,
        Contraste = ContrasteSlider.Value,
        HautesLumieres = HautesLumieresSlider.Value,
        Ombres = OmbresSlider.Value,
        Temperature = TemperatureSlider.Value,
        Teinte = TeinteSlider.Value,
        Saturation = SaturationSlider.Value,
        Nettete = NetteteSlider.Value,
    };

    /// <summary>
    /// Retient ce que les curseurs disent, dans la copie de travail.
    ///
    /// À CHAQUE mouvement, et non au seul « Enregistrer » : sans cela, changer de machine
    /// en cours de réglage perdrait tout ce qu'on venait de poser sur la précédente, sans
    /// un mot.
    /// </summary>
    private void Retenir()
    {
        if (MachineChoisie is { } machine) _corrections.Poser(machine, LireLesCurseurs());
    }

    // ----- gestes -----

    private void OnMachineChange(object sender, SelectionChangedEventArgs e)
    {
        if (_chargement) return;
        PoserLesCurseurs();
    }

    private void OnActifChange(object sender, RoutedEventArgs e)
    {
        if (_chargement) return;
        Retenir();
        DireOuEnEst();
    }

    private void OnCurseurChange(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_chargement) return;
        Retenir();
        DireOuEnEst();
    }

    /// <summary>
    /// Remet les curseurs à zéro SANS éteindre : on remet à plat pour repartir d'un tirage
    /// neutre, et l'interrupteur reste ce qu'il était.
    /// </summary>
    private void OnRemiseAZero(object sender, RoutedEventArgs e)
    {
        _chargement = true;
        try
        {
            ExpositionSlider.Value = 0;
            ContrasteSlider.Value = 0;
            HautesLumieresSlider.Value = 0;
            OmbresSlider.Value = 0;
            TemperatureSlider.Value = 0;
            TeinteSlider.Value = 0;
            SaturationSlider.Value = 0;
            NetteteSlider.Value = 0;
        }
        finally
        {
            _chargement = false;
        }

        Retenir();
        DireOuEnEst();
    }

    private void OnEnregistrer(object sender, RoutedEventArgs e)
    {
        Retenir();

        try
        {
            App.Services.SaveCorrections(_corrections);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible d'enregistrer la compensation :\n{ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var actives = _corrections.Machines.Count(m => m.Value.Actif && !m.Value.EstNeutre);
        FileLog.Write($"Compensation d'impression enregistrée : {actives} machine(s) compensée(s).");

        MessageBox.Show(
            actives == 0
                ? "Compensation enregistrée. Aucune machine n'est compensée : les tirages partent tels que l'écran les montre."
                : "Compensation enregistrée. Elle s'applique dès le prochain tirage, sans redémarrer.",
            "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Information);

        Navigator.Back();
    }

    private void OnRetour(object sender, RoutedEventArgs e) => Navigator.Back();

    /// <summary>
    /// La compensation en une phrase, pour les écrans qui portent la porte : Paramètres du
    /// Studio complet et Réglages du poste identité.
    ///
    /// <b>Elle dit surtout quand il n'y en a AUCUNE.</b> C'est ce qui manquait au profil
    /// couleur : un réglage muet se croit posé, et l'on cherche ailleurs pendant des
    /// semaines pourquoi le papier ne ressemble pas à l'écran.
    /// </summary>
    public static string Resume()
    {
        var compensees = App.Services.Corrections.Machines
            .Where(m => m.Value.Actif && !m.Value.EstNeutre)
            .Select(m => m.Key)
            .OrderBy(m => m, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (compensees.Count == 0)
            return "Aucune machine n'est compensée : les tirages partent exactement tels que l'écran les montre.";

        return $"Compensation appliquée à : {string.Join(", ", compensees)}. "
               + "Elle ne change pas l'aperçu, seulement le fichier envoyé à la machine.";
    }
}
