using System.Windows.Media;

namespace Studio.App.Infrastructure;

/// <summary>
/// Étale un zoom sur plusieurs images pour qu'il glisse au lieu de sauter (ressenti DiLand).
/// Les crans de molette s'accumulent dans un facteur restant, consommé à chaque frame par
/// lissage exponentiel : la molette reste franche, l'œil suit le mouvement.
/// </summary>
public sealed class SmoothZoomDriver
{
    /// <summary>Constante de temps du lissage : ~95 % du zoom absorbé en 150 ms.</summary>
    private const double TauSeconds = 0.05;

    /// <summary>En deçà, le reste à zoomer est invisible : on solde et on s'arrête.</summary>
    private const double MinRemainingLog = 1e-4;

    private readonly Action<double> _applyFactor;

    private double _pending = 1;   // facteur multiplicatif restant à appliquer
    private bool _running;
    private TimeSpan _lastTick;

    public SmoothZoomDriver(Action<double> applyFactor) => _applyFactor = applyFactor;

    /// <summary>Ajoute un zoom à absorber (1 = neutre, &lt;1 = cadre plus serré).</summary>
    public void Add(double factor)
    {
        if (factor <= 0) throw new ArgumentOutOfRangeException(nameof(factor));
        _pending *= factor;

        if (_running) return;
        _running = true;
        _lastTick = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Abandonne le zoom en cours (fermeture de la vue, geste tactile qui prend la main).</summary>
    public void Cancel()
    {
        _pending = 1;
        if (!_running) return;
        _running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Rendering peut se déclencher deux fois pour la même image : sans ce garde-fou,
        // dt vaudrait 0 et la seconde passe ne ferait qu'ajouter du bruit.
        if (e is not RenderingEventArgs args || args.RenderingTime == _lastTick) return;

        var dt = _lastTick == TimeSpan.Zero
            ? 1 / 60.0
            : (args.RenderingTime - _lastTick).TotalSeconds;
        _lastTick = args.RenderingTime;

        // Une frame sautée (GC, chargement) ne doit pas téléporter le cadrage.
        dt = Math.Clamp(dt, 0.001, 0.1);

        var remainingLog = Math.Log(_pending);
        if (Math.Abs(remainingLog) < MinRemainingLog)
        {
            var last = _pending;
            Cancel();
            _applyFactor(last);
            return;
        }

        var stepLog = remainingLog * (1 - Math.Exp(-dt / TauSeconds));
        _pending = Math.Exp(remainingLog - stepLog);
        _applyFactor(Math.Exp(stepLog));
    }
}
