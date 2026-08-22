using System.IO;

namespace Studio.App.Infrastructure;

/// <summary>
/// Regarde les copies de retouche changer sur le disque, et prévient quand l'une d'elles
/// vient d'être enregistrée.
///
/// <b>C'est tout le mécanisme du retour dans Studio</b> : on ne demande rien à Photoshop, on
/// constate. L'opérateur fait Ctrl+S, le fichier change, la photo se rafraîchit à l'écran
/// des tirages. Voir <see cref="RetoucheExterne"/>.
/// </summary>
internal sealed class SurveillantDeRetouche : IDisposable
{
    /// <summary>
    /// Le temps de calme après la dernière écriture avant de crier victoire.
    ///
    /// Un enregistrement n'est pas UN événement : le logiciel ouvre, écrit par blocs, ferme,
    /// et parfois écrit d'abord un fichier temporaire qu'il renomme ensuite. Prévenir au
    /// premier signal ferait relire une photo à moitié écrite — et Studio afficherait une
    /// vignette tronquée en croyant la retouche finie.
    /// </summary>
    private static readonly TimeSpan Calme = TimeSpan.FromSeconds(1.5);

    private readonly FileSystemWatcher _guetteur;
    private readonly Dictionary<string, System.Threading.Timer> _minuteries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _surveilles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Combien de fois on a déjà repoussé, faute d'avoir pu ouvrir le fichier.</summary>
    private readonly Dictionary<string, int> _reprises = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Au-delà, on renonce : environ trente secondes d'attente.</summary>
    private const int RepriseMax = 20;
    private readonly object _verrou = new();
    private bool _jete;

    /// <summary>Levé quand une copie surveillée vient d'être enregistrée. <b>Hors du fil d'interface.</b></summary>
    public event Action<string>? Enregistree;

    public SurveillantDeRetouche(string dossier)
    {
        Directory.CreateDirectory(dossier);

        _guetteur = new FileSystemWatcher(dossier)
        {
            // la taille ne suffit pas : une retouche peut rendre un fichier de même poids
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        _guetteur.Changed += (_, e) => Signaler(e.FullPath);
        _guetteur.Created += (_, e) => Signaler(e.FullPath);
        _guetteur.Renamed += (_, e) => Signaler(e.FullPath);
    }

    /// <summary>Ajoute une copie à surveiller. Les autres fichiers du dossier sont ignorés.</summary>
    public void Surveiller(string chemin)
    {
        lock (_verrou) _surveilles.Add(chemin);
    }

    /// <summary>
    /// Retire une copie de la surveillance — l'écran des tirages est quitté, ou la photo a
    /// été retirée de la commande.
    /// </summary>
    public void Oublier(string chemin)
    {
        lock (_verrou)
        {
            _surveilles.Remove(chemin);
            if (_minuteries.Remove(chemin, out var minuterie)) minuterie.Dispose();
        }
    }

    /// <summary>
    /// Repousse l'annonce : tant que le fichier bouge, on attend. C'est la minuterie qui
    /// finit par prévenir, une fois le calme revenu.
    /// </summary>
    private void Signaler(string chemin)
    {
        lock (_verrou)
        {
            if (_jete || !_surveilles.Contains(chemin)) return;

            if (_minuteries.TryGetValue(chemin, out var deja))
            {
                deja.Change(Calme, Timeout.InfiniteTimeSpan);
                return;
            }

            _minuteries[chemin] = new System.Threading.Timer(
                _ => Annoncer(chemin), null, Calme, Timeout.InfiniteTimeSpan);
        }
    }

    private void Annoncer(string chemin)
    {
        lock (_verrou)
        {
            if (_jete || !_surveilles.Contains(chemin)) return;

            if (_minuteries.Remove(chemin, out var minuterie)) minuterie.Dispose();
        }

        // Le calme ne prouve pas que le fichier soit refermé : on demande à l'ouvrir SEUL.
        // Tant que le logiciel de retouche le tient, on repousse plutôt que de lire une
        // image incomplète.
        if (!Lisible(chemin))
        {
            // ...mais pas indéfiniment : un fichier qu'un logiciel garde ouvert pour de bon
            // ferait tourner une minuterie jusqu'à la fermeture de Studio. On renonce en le
            // disant au journal, et le bouton « Reprendre la retouche » reste là.
            lock (_verrou)
            {
                _reprises.TryGetValue(chemin, out var faites);
                if (faites >= RepriseMax)
                {
                    _reprises.Remove(chemin);
                    FileLog.Write($"Retouche : « {Path.GetFileName(chemin)} » est resté occupé, " +
                                  "le retour dans Studio est abandonné pour cette fois.");
                    return;
                }

                _reprises[chemin] = faites + 1;
            }

            Signaler(chemin);
            return;
        }

        lock (_verrou) _reprises.Remove(chemin);

        Enregistree?.Invoke(chemin);
    }

    private static bool Lisible(string chemin)
    {
        try
        {
            using var flux = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.None);
            return flux.Length > 0;
        }
        catch (IOException)
        {
            return false;   // encore tenu par le logiciel de retouche
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        lock (_verrou)
        {
            if (_jete) return;
            _jete = true;

            foreach (var minuterie in _minuteries.Values) minuterie.Dispose();
            _minuteries.Clear();
            _surveilles.Clear();
        }

        _guetteur.EnableRaisingEvents = false;
        _guetteur.Dispose();
    }
}
