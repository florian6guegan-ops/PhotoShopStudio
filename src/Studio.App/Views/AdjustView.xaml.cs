using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Studio.App.Infrastructure;
using Studio.Core.Domain;
using Studio.Imaging;

namespace Studio.App.Views;

/// <summary>
/// Corrections d'image, appliquées à toutes les photos cochées d'un coup.
///
/// DiLand permet déjà de corriger plusieurs photos ensemble ; c'est ce qui rend le travail
/// tenable sur une commande de soixante tirages, où les photos viennent du même appareil
/// et souffrent donc du même défaut. On garde ce geste, avec des réglages qui vont
/// au-delà de la luminosité et du contraste.
///
/// L'aperçu porte sur une seule photo — la première cochée — car afficher soixante
/// vignettes en direct ne dirait rien de plus et coûterait cher.
/// </summary>
public partial class AdjustView : UserControl
{
    private readonly IReadOnlyList<string> _cibles;
    private readonly Action<ImageAdjustments> _appliquer;
    private readonly ImageAdjustments _reglages;

    private PreviewRenderer? _apercu;

    /// <summary>Empêche les curseurs de déclencher un rendu pendant qu'on les initialise.</summary>
    private bool _initialise;

    /// <summary>
    /// Derniers réglages demandés mais pas encore rendus, et rendu en cours.
    ///
    /// C'est le nœud de la lenteur : bouger un curseur produit des dizaines de demandes
    /// par seconde, et l'ancienne version les lançait TOUTES. Un jeton d'annulation ne
    /// servait à rien — ImageMagick ne s'interrompt pas au milieu d'un masque flou : les
    /// rendus périmés s'exécutaient quand même, l'un après l'autre, et l'aperçu traînait
    /// derrière le curseur de tout le retard accumulé.
    ///
    /// Ici il n'y a jamais qu'un rendu en vol et un seul en attente : celui qui finit
    /// reprend le dernier réglage demandé et laisse tomber tous ceux du milieu, qui
    /// n'auraient de toute façon jamais été vus.
    /// </summary>
    private ImageAdjustments? _enAttente;

    private bool _rendEnCours;

    /// <param name="photos">Chemins des photos concernées ; la première sert d'aperçu.</param>
    /// <param name="depart">Réglages de départ, généralement ceux de la première photo.</param>
    /// <param name="appliquer">Appelé avec les réglages retenus, une fois validés.</param>
    public AdjustView(
        IReadOnlyList<string> photos,
        ImageAdjustments depart,
        Action<ImageAdjustments> appliquer)
    {
        InitializeComponent();

        _cibles = photos;
        _appliquer = appliquer;
        _reglages = depart.Clone();

        ScopeText.Text = photos.Count == 1
            ? "Les réglages portent sur cette photo."
            : $"Les réglages porteront sur les {photos.Count} photos cochées.";

        ApplyButton.Content = photos.Count == 1
            ? "Appliquer"
            : $"Appliquer aux {photos.Count} photos";

        Loaded += OnLoaded;
        Unloaded += (_, _) => Liberer();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ChargerCurseurs();

        if (_cibles.Count == 0 || !File.Exists(_cibles[0]))
        {
            PreviewHint.Text = "Aperçu indisponible.";
            return;
        }

        try
        {
            var source = _cibles[0];
            _apercu = await Task.Run(() => new PreviewRenderer(source));
            PreviewHint.Visibility = Visibility.Collapsed;
            await RafraichirApercu();
        }
        catch (Exception ex)
        {
            PreviewHint.Text = "Aperçu impossible pour cette photo.";
            FileLog.Write("Corrections : aperçu impossible", ex);
        }
    }

    private void ChargerCurseurs()
    {
        // l'exposition est en diaphragmes ; le curseur travaille en centièmes pour
        // rester entier, sans quoi la position n'accrocherait pas de valeur ronde
        ExposureSlider.Value = _reglages.Exposure * 100;
        ContrastSlider.Value = _reglages.Contrast;
        HighlightsSlider.Value = _reglages.Highlights;
        ShadowsSlider.Value = _reglages.Shadows;
        WhitesSlider.Value = _reglages.Whites;
        BlacksSlider.Value = _reglages.Blacks;
        TemperatureSlider.Value = _reglages.Temperature;
        TintSlider.Value = _reglages.Tint;
        VibranceSlider.Value = _reglages.Vibrance;
        SaturationSlider.Value = _reglages.Saturation;
        ClaritySlider.Value = _reglages.Clarity;
        SharpnessSlider.Value = _reglages.Sharpness;
        GrayscaleCheck.IsChecked = _reglages.Grayscale;

        _initialise = true;
        MettreLesEtiquettesAJour();
    }

    private void LireCurseurs()
    {
        _reglages.Exposure = Math.Round(ExposureSlider.Value / 100, 2);
        _reglages.Contrast = ContrastSlider.Value;
        _reglages.Highlights = HighlightsSlider.Value;
        _reglages.Shadows = ShadowsSlider.Value;
        _reglages.Whites = WhitesSlider.Value;
        _reglages.Blacks = BlacksSlider.Value;
        _reglages.Temperature = TemperatureSlider.Value;
        _reglages.Tint = TintSlider.Value;
        _reglages.Vibrance = VibranceSlider.Value;
        _reglages.Saturation = SaturationSlider.Value;
        _reglages.Clarity = ClaritySlider.Value;
        _reglages.Sharpness = SharpnessSlider.Value;
        _reglages.Grayscale = GrayscaleCheck.IsChecked == true;
    }

    /// <summary>La valeur est affichée à côté du nom : un curseur seul ne dit pas où il en est.</summary>
    private void MettreLesEtiquettesAJour()
    {
        ExposureLabel.Text = $"Exposition   {_reglages.Exposure:+0.00;-0.00;0} IL";
        ContrastLabel.Text = Etiquette("Contraste", _reglages.Contrast);
        HighlightsLabel.Text = Etiquette("Hautes lumières", _reglages.Highlights);
        ShadowsLabel.Text = Etiquette("Ombres", _reglages.Shadows);
        WhitesLabel.Text = Etiquette("Blancs", _reglages.Whites);
        BlacksLabel.Text = Etiquette("Noirs", _reglages.Blacks);
        TemperatureLabel.Text = Etiquette("Température", _reglages.Temperature);
        TintLabel.Text = Etiquette("Teinte", _reglages.Tint);
        VibranceLabel.Text = Etiquette("Vibrance", _reglages.Vibrance);
        SaturationLabel.Text = Etiquette("Saturation", _reglages.Saturation);
        ClarityLabel.Text = Etiquette("Clarté", _reglages.Clarity);
        SharpnessLabel.Text = Etiquette("Netteté", _reglages.Sharpness);
    }

    private static string Etiquette(string nom, double valeur) =>
        $"{nom}   {valeur.ToString("+0;-0;0", CultureInfo.CurrentCulture)}";

    private async void OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialise) return;

        LireCurseurs();
        MettreLesEtiquettesAJour();
        await RafraichirApercu();
    }

    /// <summary>
    /// Recalcule l'aperçu hors du fil d'affichage, en ne gardant que la dernière demande.
    ///
    /// Voir <see cref="_enAttente"/> : tant qu'un rendu est en vol, les mouvements de
    /// curseur ne font qu'écraser les réglages en attente. Celui qui finit enchaîne sur
    /// le plus récent, et s'arrête quand plus rien n'a bougé.
    ///
    /// <b>Un seul rendu, en pleine définition et tout compris.</b> Il y en avait deux : un
    /// brouillon en demi-taille et sans relief pendant le geste, puis l'image définitive à
    /// l'arrêt. C'était l'aveu qu'un aperçu coûtait 590 ms. Depuis
    /// <c>PixelCorrections</c> il en coûte une quinzaine : montrer autre chose que l'image
    /// juste n'aurait plus d'excuse, et le clignotement du brouillon remplacé par le
    /// définitif disparaît avec lui.
    /// </summary>
    private async Task RafraichirApercu()
    {
        if (_apercu is null) return;

        _enAttente = _reglages.Clone();
        if (_rendEnCours) return;

        _rendEnCours = true;
        try
        {
            while (_enAttente is { } reglages)
            {
                _enAttente = null;

                if (_apercu is not { } rendu) return;   // l'écran s'est refermé

                var image = await Task.Run(() => Composer(rendu, reglages));
                if (image is not null) PreviewImage.Source = image;
            }
        }
        finally
        {
            _rendEnCours = false;
        }
    }

    /// <summary>
    /// Le rendu lui-même, entièrement hors du fil d'affichage — l'image est gelée avant
    /// de revenir, ce qui la rend utilisable telle quelle par l'interface.
    /// </summary>
    private static BitmapSource? Composer(PreviewRenderer rendu, ImageAdjustments reglages)
    {
        try
        {
            var apercu = rendu.RenderPixels(reglages);

            var image = BitmapSource.Create(
                apercu.Width, apercu.Height, 96, 96, PixelFormats.Bgra32, null,
                apercu.Bgra, apercu.Width * 4);
            image.Freeze();
            return image;
        }
        catch (ObjectDisposedException)
        {
            return null;   // l'écran s'est refermé pendant le rendu
        }
        catch (Exception ex)
        {
            FileLog.Write("Corrections : rendu de l'aperçu", ex);
            return null;
        }
    }

    private async void OnReset(object sender, RoutedEventArgs e)
    {
        _initialise = false;

        foreach (var curseur in new[]
                 {
                     ExposureSlider, ContrastSlider, HighlightsSlider, ShadowsSlider,
                     WhitesSlider, BlacksSlider, TemperatureSlider, TintSlider,
                     VibranceSlider, SaturationSlider, ClaritySlider, SharpnessSlider,
                 })
            curseur.Value = 0;

        GrayscaleCheck.IsChecked = false;

        _initialise = true;
        LireCurseurs();
        MettreLesEtiquettesAJour();
        await RafraichirApercu();
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        LireCurseurs();
        _appliquer(_reglages.Clone());
        Liberer();
        Navigator.Back();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Liberer();
        Navigator.Back();
    }

    private void Liberer()
    {
        _enAttente = null;
        _apercu?.Dispose();
        _apercu = null;
    }
}
