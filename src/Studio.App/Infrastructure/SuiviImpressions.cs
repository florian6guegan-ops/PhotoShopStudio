using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Studio.Core.Domain;
using Studio.Printing;

namespace Studio.App.Infrastructure;

/// <summary>
/// Une commande en train de s'imprimer : où elle en est, sur quelle machine, et de quoi
/// l'arrêter.
///
/// C'est le remplaçant du simple compteur de numéros : le bandeau affichait
/// « Impression en cours — commande 01-014 » et rien d'autre. Sur trente tirages,
/// impossible de savoir s'il restait dix secondes ou trois minutes, ni sur quelle machine
/// regarder, ni comment arrêter.
/// </summary>
public sealed class TravailImpression : ObservableObject
{
    private readonly CancellationTokenSource _arret = new();
    private string _etape = "Préparation…";
    private int _faits;
    private int _total;
    private string? _machine;
    private bool _arretDemande;

    public TravailImpression(string numero) => Numero = numero;

    /// <summary>Numéro affiché de la commande, celui que l'opérateur lit sur le ticket.</summary>
    public string Numero { get; }

    public CancellationToken Jeton => _arret.Token;

    public string Etape
    {
        get => _etape;
        private set => Set(ref _etape, value);
    }

    public int Faits
    {
        get => _faits;
        private set
        {
            if (!Set(ref _faits, value)) return;
            OnPropertyChanged(nameof(Fraction));
            OnPropertyChanged(nameof(Detail));
        }
    }

    public int Total
    {
        get => _total;
        private set
        {
            if (!Set(ref _total, value)) return;
            OnPropertyChanged(nameof(Fraction));
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>Tuile du bandeau visée (« A », « B », « D »), ou null si on ne sait pas.</summary>
    public string? Machine
    {
        get => _machine;
        private set => Set(ref _machine, value);
    }

    /// <summary>Avancement de l'étape en cours, entre 0 et 1.</summary>
    public double Fraction => _total <= 0 ? 0 : Math.Clamp(_faits / (double)_total, 0, 1);

    /// <summary>« 12 / 30 » — le compte brut, celui qu'on cherche des yeux.</summary>
    public string Detail => _total <= 0 ? "" : $"{_faits} / {_total}";

    /// <summary>Vrai dès que l'arrêt a été demandé : le bouton ne doit plus répondre.</summary>
    public bool ArretDemande
    {
        get => _arretDemande;
        private set => Set(ref _arretDemande, value);
    }

    // ----- ce qui est réellement SORTI de la machine -----

    private int _sortis;
    private int _rates;
    private int _attendus;

    /// <summary>
    /// Photos effectivement tirées, telles que le minilab les confirme.
    ///
    /// À ne pas confondre avec l'envoi : envoyer trente tirages au DE100 prend quelques
    /// secondes, les tirer prend plusieurs minutes. Le suivi s'arrêtait à l'envoi, si
    /// bien que la commande semblait finie alors que la machine n'avait encore rien sorti.
    /// </summary>
    public int Sortis
    {
        get => _sortis;
        private set
        {
            if (!Set(ref _sortis, value)) return;
            OnPropertyChanged(nameof(Fraction));
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>Tirages que la machine a refusés, annulés, ou laissés sans réponse.</summary>
    public int Rates
    {
        get => _rates;
        private set => Set(ref _rates, value);
    }

    /// <summary>Vrai quand la machine a rendu son verdict sur tous les tirages envoyés.</summary>
    public bool TirageTermine => _attendus > 0 && Sortis + Rates >= _attendus;

    /// <summary>
    /// Passe de l'envoi au tirage : tout est parti, la machine travaille. C'est ici que
    /// commence l'attente qui compte pour l'opérateur — celle du papier qui sort.
    /// </summary>
    internal void CommencerLeTirage(int photosEnvoyees)
    {
        _attendus = photosEnvoyees;
        Sortis = 0;
        Rates = 0;
        Total = photosEnvoyees;
        Faits = 0;
        Etape = "Tirage en cours";
    }

    /// <summary>La machine a rendu son verdict sur une photo.</summary>
    internal void NoterTirage(bool reussi)
    {
        if (reussi) Sortis++;
        else Rates++;

        Faits = Sortis + Rates;
        Etape = Rates == 0
            ? "Tirage en cours"
            : $"Tirage en cours — {Rates} en échec";
    }

    /// <summary>
    /// Demande l'arrêt. Il sera pris en compte entre deux tirages — jamais au milieu d'un
    /// envoi, ce qui laisserait une commande à moitié transmise dans la file du minilab.
    /// </summary>
    public void Annuler()
    {
        if (ArretDemande) return;
        ArretDemande = true;
        Etape = "Arrêt demandé…";
        FileLog.Write($"Impression : arrêt demandé pour la commande {Numero}");
        _arret.Cancel();
    }

    /// <summary>Nombre de photos parties au minilab, retenu pour attendre leur sortie.</summary>
    internal int PhotosEnvoyees { get; private set; }

    internal void Avancer(PrintProgress avancement)
    {
        Etape = avancement.Etape;
        Total = avancement.Total;
        Faits = avancement.Faits;
        if (avancement.Machine is not null) Machine = avancement.Machine;

        if (avancement.Etape == PrintProgress.Envoi) PhotosEnvoyees = avancement.Faits;
    }

    internal void Liberer() => _arret.Dispose();
}

/// <summary>
/// Les impressions en cours, menées en tâche de fond.
///
/// Une commande de trente-deux photos, c'est trente-deux rendus ImageMagick puis autant
/// d'envois au minilab : plusieurs minutes. L'écran restait bloqué jusqu'au bout, une
/// boîte de dialogue s'ouvrait à la fin, et le poste était inutilisable pendant ce
/// temps-là — impossible de servir le client suivant (signalé le 01/08/2026).
///
/// Désormais la commande est créée, l'opérateur reprend la main immédiatement, et
/// l'impression se poursuit derrière. L'avancement se lit dans le bandeau des machines,
/// sur la tuile de la machine concernée, avec de quoi l'arrêter.
///
/// <b>Rien n'est rejoué tout seul.</b> Si l'impression échoue, la commande reste dans
/// « Commandes du jour » et c'est l'opérateur qui décide de la relancer — c'est la règle
/// posée depuis les tempêtes de réémission de DiLand.
/// </summary>
public sealed class SuiviImpressions : ObservableObject
{
    private readonly ObservableCollection<TravailImpression> _travaux = [];
    private string? _note;
    private Action? _surAcquittement;

    /// <summary>
    /// Les commandes en train de s'imprimer, dans l'ordre où elles sont parties.
    ///
    /// Construite une fois pour toutes : une enveloppe recréée à chaque lecture perdrait
    /// ses abonnements, et le bandeau ne verrait plus rien bouger.
    /// </summary>
    public ReadOnlyObservableCollection<TravailImpression> Travaux { get; }

    public SuiviImpressions() => Travaux = new ReadOnlyObservableCollection<TravailImpression>(_travaux);

    /// <summary>Vrai tant qu'au moins une commande est en train de s'imprimer.</summary>
    public bool Actif => _travaux.Count > 0;

    /// <summary>Le travail qui vise cette tuile du bandeau, s'il y en a un.</summary>
    public TravailImpression? PourMachine(string lettre) =>
        _travaux.FirstOrDefault(t => string.Equals(t.Machine, lettre, StringComparison.OrdinalIgnoreCase));

    /// <summary>Les travaux dont on ne sait pas encore sur quelle machine ils sortiront.</summary>
    public IReadOnlyList<TravailImpression> SansMachine =>
        _travaux.Where(t => t.Machine is null).ToList();

    /// <summary>Ce que le bandeau affiche ; vide quand il n'y a rien à dire.</summary>
    public string Message
    {
        get
        {
            if (_travaux.Count > 0)
            {
                var numeros = string.Join(", ", _travaux.Select(t => t.Numero));
                return _travaux.Count == 1
                    ? $"Impression en cours — commande {numeros}"
                    : $"Impressions en cours — commandes {numeros}";
            }

            return _note ?? "";
        }
    }

    public Visibility Visibilite => Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Vrai quand le bandeau porte un avertissement : il doit alors se voir autrement.</summary>
    public bool EnAlerte => _travaux.Count == 0 && _note is not null;

    /// <summary>
    /// Pose un avertissement dans le bandeau, au lieu d'ouvrir une boîte de dialogue.
    ///
    /// Une boîte modale au démarrage barre l'écran avant même qu'on ait pu travailler, et
    /// force une réponse tout de suite — c'est ainsi qu'on a renvoyé deux fois les mêmes
    /// 29 tirages à un minilab qui les avait déjà (01/08/2026). Le bandeau, lui, attend.
    /// </summary>
    public void Informer(string message, Action? surAcquittement = null)
    {
        _note = message;
        _surAcquittement = surAcquittement;
        Prevenir();
    }

    /// <summary>Efface le message, une fois que l'opérateur l'a lu, et exécute sa suite s'il y en a une.</summary>
    public void Acquitter()
    {
        var suite = _surAcquittement;

        _note = null;
        _surAcquittement = null;
        Prevenir();

        suite?.Invoke();
    }

    /// <summary>
    /// Imprime une commande en tâche de fond et rend la main tout de suite.
    /// </summary>
    /// <param name="commande">La commande déjà créée et persistée.</param>
    /// <param name="imprimer">
    /// Le travail long : rendu des photos et envoi aux machines. Il reçoit de quoi
    /// rapporter son avancement et de quoi savoir qu'on lui demande de s'arrêter.
    /// </param>
    /// <param name="apresSucces">Appelé sur le fil de l'interface si tout est parti.</param>
    public async void Lancer(
        Order commande,
        Action<IProgress<PrintProgress>, CancellationToken> imprimer,
        Action? apresSucces = null)
    {
        ArgumentNullException.ThrowIfNull(commande);
        ArgumentNullException.ThrowIfNull(imprimer);

        var travail = new TravailImpression(commande.DisplayNumber);

        _travaux.Add(travail);
        _note = null;
        _surAcquittement = null;
        Prevenir();

        FileLog.Write($"Impression : commande {travail.Numero} lancée en tâche de fond");

        // Progress<T> rejoue sur le fil qui l'a créé — ici celui de l'interface — donc
        // les propriétés notifiées ne traversent jamais de frontière de thread
        var avancement = new Progress<PrintProgress>(travail.Avancer);

        try
        {
            var jeton = travail.Jeton;
            await Task.Run(() => imprimer(avancement, jeton), CancellationToken.None);

            FileLog.Write($"Impression : commande {travail.Numero} envoyée " +
                          $"({travail.PhotosEnvoyees} photo(s))");
            apresSucces?.Invoke();

            // Tout est PARTI, rien n'est encore SORTI. Sur le minilab, l'attente qui
            // compte pour l'opérateur commence maintenant : on reste affiché tant que la
            // machine n'a pas rendu son verdict sur chaque photo.
            await AttendreLesTirages(travail);
        }
        catch (PrintCanceledException arret)
        {
            // arrêt VOULU : ce n'est pas un échec, et le message dit exactement ce qui
            // est parti et ce qui a été rappelé
            _note = arret.Message;
            FileLog.Write($"Impression : commande {travail.Numero} arrêtée — {arret.Message}");
        }
        catch (OperationCanceledException)
        {
            _note = $"Commande {travail.Numero} arrêtée.";
            FileLog.Write($"Impression : commande {travail.Numero} arrêtée");
        }
        catch (Exception ex)
        {
            // pas de boîte de dialogue : l'opérateur est peut-être en train de servir
            // quelqu'un. Le bandeau le dit, et la commande reste réimprimable.
            _note = $"Commande {travail.Numero} : impression échouée — {ex.Message}. " +
                    "Elle reste réimprimable depuis « Commandes du jour ».";
            FileLog.Write($"Impression : commande {travail.Numero} en échec", ex);
        }
        finally
        {
            _travaux.Remove(travail);
            travail.Liberer();
            Prevenir();
        }
    }

    /// <summary>Arrête tout ce qui est en cours — le geste de panique, un seul bouton.</summary>
    public void ToutArreter()
    {
        foreach (var travail in _travaux.ToList()) travail.Annuler();
    }

    /// <summary>Attentes en cours, par numéro de commande.</summary>
    private readonly Dictionary<string, TaskCompletionSource> _attentes = new();

    /// <summary>
    /// Reste affiché jusqu'à ce que la machine ait rendu son verdict sur chaque photo.
    ///
    /// Seul le minilab sait dire ce qui est sorti — il rappelle chaque commande par son
    /// identifiant. Le spouleur Windows, lui, ne rend jamais compte : sur ce circuit on
    /// s'arrête à l'envoi, faute de mieux, et on le dit.
    ///
    /// Le délai de garde n'est pas une politesse : sans lui, une machine qui reste muette
    /// laisserait la commande affichée indéfiniment. Le pilote borne déjà chaque tirage à
    /// trente minutes et finit par rendre un verdict — celui-ci est la ceinture.
    /// </summary>
    private async Task AttendreLesTirages(TravailImpression travail)
    {
        if (travail.PhotosEnvoyees <= 0) return;          // circuit sans retour machine
        if (travail.Machine is null or "D") return;       // DNP : pas d'accusé de sortie

        travail.CommencerLeTirage(travail.PhotosEnvoyees);

        var attente = new TaskCompletionSource();
        _attentes[travail.Numero] = attente;
        try
        {
            await attente.Task.WaitAsync(TimeSpan.FromMinutes(35));
        }
        catch (TimeoutException)
        {
            _note = $"Commande {travail.Numero} : {travail.Sortis} photo(s) sorties sur " +
                    $"{travail.Total}, la machine n'a rien dit des autres. À vérifier sur le minilab.";
            FileLog.Write($"Impression : commande {travail.Numero} — attente des tirages expirée " +
                          $"({travail.Sortis}/{travail.Total})");
        }
        finally
        {
            _attentes.Remove(travail.Numero);
        }

        if (travail.Rates > 0)
            _note = $"Commande {travail.Numero} : {travail.Sortis} photo(s) sorties, " +
                    $"{travail.Rates} en échec. Réimpression depuis « Commandes du jour ».";
    }

    /// <summary>
    /// Le minilab a rendu son verdict sur un tirage.
    ///
    /// L'identifiant est celui que l'orchestrateur a fabriqué à l'envoi :
    /// <c>{numéro}-{enveloppe}-{rang}</c>, où le numéro contient lui-même un tiret
    /// (« 01-016 »). On retire donc les DEUX derniers segments pour retrouver la commande.
    /// </summary>
    public void TirageTermine(string jobId, bool reussi)
    {
        var numero = PrintOrchestrator.OrderNumberOf(jobId);
        if (numero is null) return;

        var travail = _travaux.FirstOrDefault(t => t.Numero == numero);
        if (travail is null) return;

        travail.NoterTirage(reussi);
        Prevenir();

        if (travail.TirageTermine && _attentes.TryGetValue(numero, out var attente))
            attente.TrySetResult();
    }


    private void Prevenir()
    {
        OnPropertyChanged(nameof(Actif));
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(Visibilite));
        OnPropertyChanged(nameof(EnAlerte));
    }
}
