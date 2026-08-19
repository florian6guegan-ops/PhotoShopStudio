using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;

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
    /// Verdicts que la machine doit encore rendre, et ceux déjà reçus.
    ///
    /// <b>Distincts des feuilles</b> : le DE100 répond une fois par TIRAGE, exemplaires
    /// compris. Une commande de trois photos dont l'une est en double, c'est quatre
    /// feuilles et trois verdicts. Compter les uns pour les autres faisait, au choix,
    /// annoncer un total faux ou déclarer l'enveloppe finie avant l'heure.
    /// </summary>
    private int _verdictsAttendus;
    private int _verdictsRecus;

    /// <summary>
    /// Vrai dès qu'un relevé du compteur de la machine a abouti.
    ///
    /// Tant qu'il est faux, l'avancement retombe sur les verdicts — c'est-à-dire sur un
    /// affichage qui saute à la fin, comme avant. Un relais muet ne doit pas laisser la
    /// barre à zéro sans jamais la remplir.
    /// </summary>
    private bool _compteurSuivi;

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

    /// <summary>
    /// Vrai quand la machine a rendu son verdict sur tous les tirages envoyés.
    ///
    /// Compté en VERDICTS et non en feuilles : attendre autant de réponses que de feuilles
    /// laisserait une commande à exemplaires multiples affichée jusqu'au délai de garde.
    /// </summary>
    public bool TirageTermine => _verdictsAttendus > 0 && _verdictsRecus >= _verdictsAttendus;

    /// <summary>
    /// Passe de l'envoi au tirage : tout est parti, la machine travaille. C'est ici que
    /// commence l'attente qui compte pour l'opérateur — celle du papier qui sort.
    /// </summary>
    /// <param name="feuilles">Feuilles que la machine doit sortir, exemplaires compris.</param>
    /// <param name="verdicts">
    /// Réponses attendues de la machine ; à défaut, autant que de feuilles — c'est le cas
    /// des circuits où un tirage vaut une réponse.
    /// </param>
    internal void CommencerLeTirage(int feuilles, int verdicts = 0)
    {
        _attendus = feuilles;
        _verdictsAttendus = verdicts > 0 ? verdicts : feuilles;
        _verdictsRecus = 0;
        Sortis = 0;
        Rates = 0;
        Total = feuilles;
        Faits = 0;
        Etape = "Tirage en cours";
        DebutDuTirage = DateTimeOffset.Now;
    }

    /// <summary>
    /// Ce que le COMPTEUR de la machine dit être sorti depuis le début de la commande.
    ///
    /// <b>C'est la seule façon de voir la barre avancer pendant le tirage.</b> Le DE100 ne
    /// notifie qu'une fois la commande entière terminée (<c>De100JobTracker.Report</c> ne
    /// rend une issue que sur un statut définitif) : jusque-là, aucun verdict n'arrive et
    /// l'affichage restait figé à « 0 / 30 » plusieurs minutes avant de sauter à « 30 / 30 ».
    /// Son compteur de tirages, lui, monte feuille par feuille.
    ///
    /// Borné au total et jamais décroissant : le compteur est global à la machine, et une
    /// commande lancée à côté depuis DiLand ne doit pas faire dépasser cent pour cent ni
    /// reculer l'affichage.
    /// </summary>
    internal void NoterFeuillesSorties(int feuilles)
    {
        _compteurSuivi = true;

        var borne = Math.Clamp(feuilles, 0, Math.Max(0, _attendus));
        if (borne <= Sortis) return;

        Sortis = borne;
        Faits = borne;
    }

    /// <summary>
    /// Feuilles que cette commande doit encore sortir.
    ///
    /// Sert à la commande SUIVANTE sur la même machine : le minilab tire dans l'ordre où il
    /// reçoit, donc ce qui reste ici tombera avant le premier tirage de la suivante, et ne
    /// doit pas être compté pour elle.
    /// </summary>
    internal int RestantASortir => Math.Max(0, _attendus - Sortis);

    /// <summary>
    /// Quand la machine a commencé à sortir du papier. Sert à deux choses : estimer ce
    /// qu'il reste, et MESURER le débit réel une fois la commande finie.
    /// </summary>
    public DateTimeOffset DebutDuTirage { get; private set; }

    /// <summary>
    /// Le format tiré, et la longueur de papier qu'un tirage consomme. Posés par
    /// l'orchestrateur au moment de l'envoi : c'est de là que dépend la cadence.
    /// </summary>
    public string Format { get; internal set; } = "";

    public int LongueurMm { get; internal set; }

    /// <summary>
    /// Ce qu'il reste à attendre, ou null quand on ne peut rien en dire.
    ///
    /// <b>Mesuré sur CETTE commande dès qu'elle a de quoi</b> : trois photos sorties
    /// suffisent à connaître la cadence du moment, qui vaut mieux que n'importe quelle
    /// moyenne — la machine peut être froide, occupée, ou en pleine maintenance. En deçà,
    /// on prend le débit appris sur ce format, et à défaut la valeur par défaut.
    /// </summary>
    public TimeSpan? DureeRestante
    {
        get
        {
            // en FEUILLES, comme le reste du bandeau : c'est du papier qu'on attend
            var restants = _attendus - Sortis;
            if (restants <= 0 || TirageTermine || DebutDuTirage == default) return null;

            if (Sortis >= 3)
            {
                var ecoule = DateTimeOffset.Now - DebutDuTirage;
                return TimeSpan.FromSeconds(ecoule.TotalSeconds / Sortis * restants);
            }

            return EstimationDuree.Restant(restants, LongueurMm, Debit);
        }
    }

    /// <summary>Le débit appris sur ce format, posé par le suivi.</summary>
    internal DebitMesure? Debit { get; set; }

    /// <summary>
    /// Redit au bandeau que la durée a changé, sans qu'aucune photo ne soit sortie.
    ///
    /// <see cref="DureeRestante"/> se calcule à la lecture : rien ne la notifie d'elle-même,
    /// et l'affichage resterait figé entre deux photos — vingt secondes sur un A4.
    /// </summary>
    internal void RafraichirLaDuree() => OnPropertyChanged(nameof(DureeRestante));

    /// <summary>
    /// Le motif du premier échec, tel que la MACHINE l'a donné. Vide tant que tout sort.
    ///
    /// Le premier et non le dernier : sur une commande refusée en bloc, ils sont tous
    /// identiques, et c'est le premier qui dit ce qui s'est passé. Il reste affiché tant
    /// que la commande est là — c'est la seule trace visible sans ouvrir le journal.
    /// </summary>
    public string MotifDEchec
    {
        get => _motifDEchec;
        private set => Set(ref _motifDEchec, value);
    }

    private string _motifDEchec = "";

    /// <summary>La machine a rendu son verdict sur une photo.</summary>
    /// <param name="motif">Ce que la machine dit du refus, ou vide.</param>
    internal void NoterTirage(bool reussi, string motif = "")
    {
        _verdictsRecus++;
        if (!reussi) Rates++;

        if (!reussi && MotifDEchec.Length == 0 && !string.IsNullOrWhiteSpace(motif))
            MotifDEchec = motif.Trim();

        // Le verdict porte sur un TIRAGE, qui peut valoir plusieurs feuilles : il ne dit
        // donc pas à lui seul combien de papier est sorti. Deux cas, et deux seulement :
        //
        // - tout est rentré sans échec : ce qui était attendu est sorti, on cale le compte
        //   sur le total. Le compteur de la machine peut avoir une lecture de retard, et
        //   terminer sur « 29 / 30 » ferait douter d'une commande pourtant complète ;
        // - sans compteur disponible, on avance quand même d'un cran par verdict pour que
        //   la barre ne reste pas plate — c'est le comportement d'avant, faute de mieux.
        if (TirageTermine && Rates == 0) Sortis = _attendus;
        else if (!_compteurSuivi && reussi) Sortis++;

        Faits = Sortis;
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

    /// <summary>Nombre de FEUILLES parties au minilab, retenu pour attendre leur sortie.</summary>
    internal int PhotosEnvoyees { get; private set; }

    /// <summary>Réponses que la machine rendra sur cet envoi — voir <see cref="TirageTermine"/>.</summary>
    internal int VerdictsAttendus { get; private set; }

    /// <summary>
    /// Handle de la commande côté minilab, ou null hors de ce circuit.
    ///
    /// C'est la clé qui permet de demander à la machine où en est CETTE commande, au lieu
    /// de lire son compteur général et d'y attribuer tout ce qui sort.
    /// </summary>
    internal string? HandleMinilab { get; private set; }

    /// <summary>
    /// Ce qui est réellement parti à la machine, quel que soit le circuit.
    ///
    /// <see cref="PhotosEnvoyees"/> ne vaut que pour le minilab : lui seul annonce une
    /// étape « Envoi », parce que lui seul reçoit toute l'enveloppe d'un coup. Le spouleur
    /// et l'envoi direct DNP remettent page par page et n'annoncent que « Impression » —
    /// leur compte se lit donc sur <see cref="Faits"/>.
    ///
    /// Sans cette distinction, les neuf tirages DNP du 07/08/2026 se sont tous annoncés
    /// « envoyée (0 photo(s)) » dans le journal, alors qu'un tirage était bien sorti à
    /// chaque fois.
    /// </summary>
    internal int TiragesPartis => PhotosEnvoyees > 0 ? PhotosEnvoyees : Faits;

    internal void Avancer(PrintProgress avancement)
    {
        Etape = avancement.Etape;
        Total = avancement.Total;
        Faits = avancement.Faits;
        if (avancement.Machine is not null) Machine = avancement.Machine;

        if (avancement.Etape != PrintProgress.Envoi) return;

        PhotosEnvoyees = avancement.Faits;
        VerdictsAttendus = avancement.Verdicts;
        if (avancement.Handle is { Length: > 0 } handle) HandleMinilab = handle;

        // Le minilab est le seul circuit où l'envoi n'est PAS le tirage : il reçoit toute
        // l'enveloppe en quelques secondes, puis met plusieurs minutes à la sortir. C'est
        // le nombre de verdicts attendus qui le trahit — lui seul en annonce.
        if (avancement.Verdicts > 0) _versMinilab = true;
    }

    private bool _versMinilab;

    /// <summary>
    /// Vrai quand <see cref="Faits"/> compte du PAPIER SORTI, et non des pages remises.
    ///
    /// <b>Ce qui se passait sans cette distinction.</b> Sur le minilab, la barre se
    /// remplissait pendant l'envoi — quelques secondes, aucun papier — puis
    /// <see cref="CommencerLeTirage"/> remettait le compte à zéro pour le vrai tirage :
    /// l'opérateur voyait « Envoi 20 / 20 », puis « 0 / 20 photo(s) sorties », puis une
    /// remontée lente. Sur une commande d'une seule photo, « 1 / 1 » puis « 0 / 1 » puis
    /// « 1 / 1 ». Le compte ne décalait pas, il RECULAIT.
    ///
    /// Les autres circuits — spouleur Windows, envoi direct DNP — remettent page par page
    /// et ne sauront jamais dire ce qui est tombé : chez eux, ce qui est remis est le seul
    /// avancement qu'on puisse montrer, et il ne recule pas.
    /// </summary>
    public bool CompteDuPapierSorti =>
        !_versMinilab || Etape.StartsWith("Tirage", StringComparison.Ordinal);

    // ----- la file d'attente : ce qui n'a pas encore commencé -----

    /// <summary>
    /// La machine que cette commande VISE. Null quand elle n'occupe rien de partagé —
    /// agrandissement, courriel.
    /// </summary>
    public RessourceDImpression? Ressource { get; internal init; }

    /// <summary>Tirages annoncés, tels que la commande les porte, avant tout rendu.</summary>
    public int TiragesPrevus { get; internal init; }

    /// <summary>Ce qu'il faut pour la démarrer le moment venu ; null dès qu'elle tourne.</summary>
    internal Order? Commande { get; set; }

    internal Action<IProgress<PrintProgress>, CancellationToken>? Imprimer { get; set; }

    internal Action? ApresSucces { get; set; }

    private bool _enFile;
    private bool _enPause;
    private int _rang;
    private string? _machineAttendue;

    /// <summary>
    /// Vrai tant que la commande attend son tour : rien n'est rendu, rien n'est parti,
    /// aucun papier n'est engagé. C'est le seul état où l'on peut encore tout reprendre.
    /// </summary>
    public bool EnFile
    {
        get => _enFile;
        internal set
        {
            if (!Set(ref _enFile, value)) return;
            OnPropertyChanged(nameof(EtatFile));
        }
    }

    /// <summary>
    /// Mise en pause par l'opérateur : elle ne partira pas, même quand la machine se
    /// libère. Les suivantes de la même machine, elles, passent devant.
    ///
    /// <b>Ne vaut que dans la file.</b> Une commande déjà transmise au minilab ne se
    /// suspend pas : le DE100 ne connaît que l'annulation (<c>PIF_CancelOrder</c>), et
    /// prétendre le contraire ferait croire à un tirage arrêté qui continue de sortir.
    /// </summary>
    public bool EnPause
    {
        get => _enPause;
        internal set
        {
            if (!Set(ref _enPause, value)) return;
            OnPropertyChanged(nameof(EtatFile));
            OnPropertyChanged(nameof(PauseLabel));
        }
    }

    /// <summary>Rang dans la file, 1 pour la prochaine à partir.</summary>
    public int Rang
    {
        get => _rang;
        internal set
        {
            if (!Set(ref _rang, value)) return;
            OnPropertyChanged(nameof(EtatFile));
        }
    }

    /// <summary>
    /// La machine que la commande d'AVANT occupe, quand elle l'a dite.
    ///
    /// C'est la seule machine qu'on puisse annoncer honnêtement : celle-ci attend la même
    /// ressource, donc elle sortira là où l'autre sort. Tant que la commande en cours n'a
    /// pas nommé sa machine, on s'en tient au libellé de la ressource.
    /// </summary>
    public string? MachineAttendue
    {
        get => _machineAttendue;
        internal set
        {
            if (!Set(ref _machineAttendue, value)) return;
            OnPropertyChanged(nameof(DetailFile));
        }
    }

    /// <summary>Une feuille blanche doit sortir avant celle-ci, pour la séparer de la précédente.</summary>
    public bool SeparationDemandee { get; internal set; }

    /// <summary>Ce que la ligne du bandeau annonce : combien de tirages, et pour où.</summary>
    public string TitreFile =>
        $"{TiragesPrevus} tirage(s)" +
        (Ressource is null ? "" : $" · {Ressource.Libelle}");

    public string DetailFile
    {
        get
        {
            var ou = MachineAttendue is { Length: > 0 } machine
                ? $"Sortira sur la machine {machine}"
                : "Sortira sur la machine qui tire en ce moment";

            return SeparationDemandee
                ? $"{ou}, précédée d'une feuille blanche."
                : $"{ou}.";
        }
    }

    public string EtatFile => EnPause
        ? "⏸  En pause — elle ne partira pas"
        : Rang <= 1
            ? "Prochaine à partir"
            : $"En attente — {Rang}ᵉ de la file";

    public string PauseLabel => EnPause ? "▶  Reprendre" : "⏸  Pause";

    internal void Liberer() => _arret.Dispose();

    /// <summary>Redit tout ce que le bandeau de la file affiche.</summary>
    internal void RafraichirLaFile()
    {
        OnPropertyChanged(nameof(TitreFile));
        OnPropertyChanged(nameof(DetailFile));
        OnPropertyChanged(nameof(EtatFile));
        OnPropertyChanged(nameof(PauseLabel));
    }
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

    /// <summary>
    /// Ce qui attend son tour : lancé par l'opérateur, mais pas encore parti.
    ///
    /// <b>Séparée des travaux en cours, et c'est délibéré.</b> Le bandeau des machines, la
    /// ligne d'une commande de borne, le compteur de tirages : tout ce qui existait lit
    /// <see cref="Travaux"/> et n'y trouve que des commandes réellement engagées. Une
    /// commande en file n'a rien rendu, rien envoyé, et n'occupe aucune machine — elle
    /// n'a rien à faire dans ces vues-là. Elle a la sienne, sur l'accueil.
    /// </summary>
    public ReadOnlyObservableCollection<TravailImpression> File { get; }

    private readonly ObservableCollection<TravailImpression> _file = [];

    public SuiviImpressions()
    {
        Travaux = new ReadOnlyObservableCollection<TravailImpression>(_travaux);
        File = new ReadOnlyObservableCollection<TravailImpression>(_file);
    }

    /// <summary>Vrai tant qu'au moins une commande est en train de s'imprimer.</summary>
    public bool Actif => _travaux.Count > 0;

    /// <summary>Vrai quand quelque chose attend son tour : le bandeau de la file s'ouvre.</summary>
    public bool FileActive => _file.Count > 0;

    public int NbEnFile => _file.Count;

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
                // Un refus l'emporte sur le décompte : « Impression en cours — commande
                // 04-027 » pendant que la machine vient de tout refuser est le pire des
                // affichages. Le motif de la machine passe donc devant.
                if (_travaux.FirstOrDefault(t => t.Rates > 0 && t.MotifDEchec.Length > 0) is { } fautif)
                    return $"Commande {fautif.Numero} — {fautif.Rates} tirage(s) refusé(s) " +
                           $"par la machine : {fautif.MotifDEchec}";

                var numeros = string.Join(", ", _travaux.Select(t => t.Numero));
                var enCours = _travaux.Count == 1
                    ? $"Impression en cours — commande {numeros}"
                    : $"Impressions en cours — commandes {numeros}";

                // Ce qui attend son tour se dit ICI aussi : le bandeau des machines suit
                // l'opérateur d'écran en écran, là où la file ne se voit que sur l'accueil.
                return _file.Count switch
                {
                    0 => enCours,
                    1 => $"{enCours}  ·  1 commande attend son tour",
                    _ => $"{enCours}  ·  {_file.Count} commandes attendent leur tour",
                };
            }

            return _note ?? "";
        }
    }

    public Visibility Visibilite => Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Vrai quand le bandeau porte un avertissement : il doit alors se voir autrement.
    ///
    /// Un tirage refusé compte comme tel, même pendant que la commande tourne encore : le
    /// fond vert « tout va bien » sur une machine qui refuse est exactement ce qui fait
    /// rater un incident.
    /// </summary>
    public bool EnAlerte =>
        (_travaux.Count == 0 && _note is not null) || _travaux.Any(t => t.Rates > 0);

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
    public void Lancer(
        Order commande,
        Action<IProgress<PrintProgress>, CancellationToken> imprimer,
        Action? apresSucces = null)
    {
        ArgumentNullException.ThrowIfNull(commande);
        ArgumentNullException.ThrowIfNull(imprimer);

        var ressource = RessourceDImpression.Pour(commande);

        var travail = new TravailImpression(commande.DisplayNumber)
        {
            Ressource = ressource,
            TiragesPrevus = RessourceDImpression.TiragesDe(commande),
            Commande = commande,
            Imprimer = imprimer,
            ApresSucces = apresSucces,
        };

        // La machine est-elle déjà prise par une commande à nous ? Alors celle-ci attend :
        // on ne la détourne pas vers une machine libre — l'opérateur a monté un rouleau, et
        // c'est ce rouleau-là qu'il veut. Voir RessourceDImpression.
        if (ressource is not null && EstOccupee(ressource.Cle))
        {
            // Celle qui la précède VRAIMENT : la dernière déjà en file sur cette machine,
            // et seulement à défaut celle qui tire en ce moment. Sur une troisième commande
            // d'affilée, annoncer celle qui sort ferait croire qu'elle part juste après —
            // et la feuille blanche qu'on accepte ne la sépare pas de celle-là, mais de
            // celle du milieu.
            MettreEnFile(travail, DerniereDeLaFile(ressource.Cle) ?? Occupant(ressource.Cle));
            return;
        }

        Demarrer(travail);
    }

    /// <summary>La commande qui occupe cette machine en ce moment, s'il y en a une.</summary>
    private TravailImpression? Occupant(string cle) =>
        _travaux.FirstOrDefault(t => t.Ressource?.Cle == cle);

    /// <summary>La dernière qui attend sur cette machine — celle derrière qui on se range.</summary>
    private TravailImpression? DerniereDeLaFile(string cle) =>
        _file.LastOrDefault(t => t.Ressource?.Cle == cle);

    /// <summary>
    /// Machines dont la commande suivante est en train de démarrer.
    ///
    /// <b>Sans cela, la feuille blanche ouvre une fenêtre.</b> Elle part au minilab depuis
    /// une tâche de fond, et pendant cette seconde-là la commande précédente a quitté
    /// <see cref="_travaux"/> sans que la suivante y soit encore entrée : la machine paraît
    /// libre. Une commande lancée juste à cet instant partirait DIRECTEMENT, en même temps
    /// que celle qui attendait depuis dix minutes — soit exactement ce que la file existe
    /// pour empêcher.
    /// </summary>
    private readonly HashSet<string> _clesEnDemarrage = [];

    /// <summary>Vrai quand cette machine est prise, ou sur le point de l'être.</summary>
    private bool EstOccupee(string cle) =>
        Occupant(cle) is not null || _clesEnDemarrage.Contains(cle);

    /// <summary>
    /// La dernière machine sur laquelle chaque file a réellement sorti du papier.
    ///
    /// Sert à la feuille blanche d'une commande qu'on RÉVEILLE : mise en pause pendant que
    /// la précédente sortait, elle reprend sur une machine déjà libre, et plus rien ne
    /// dirait alors où tirer son séparateur. Sa demande serait perdue sans un mot.
    /// </summary>
    private readonly Dictionary<string, string> _derniereMachine = [];

    /// <summary>
    /// Range la commande derrière celle qui occupe la machine.
    ///
    /// <b>Aucune question posée.</b> La feuille blanche se règle une fois pour toutes dans
    /// les Paramètres du poste (<see cref="PosteSettings.SeparerLesCommandes"/>) : une
    /// boîte à chaque mise en file ferait trois clics de plus dans un coup de feu — et
    /// c'est justement dans un coup de feu qu'on enchaîne les commandes.
    /// </summary>
    /// <param name="devant">
    /// La commande qui occupe la machine, ou null quand la suivante est justement en train
    /// de démarrer — la machine est prise dans les deux cas.
    /// </param>
    private void MettreEnFile(TravailImpression travail, TravailImpression? devant)
    {
        travail.EnFile = true;

        // Celle d'avant attend peut-être elle-même : elle n'a alors pas de machine à elle,
        // mais elle sait laquelle elle attend, et c'est la même.
        travail.MachineAttendue = devant?.Machine ?? devant?.MachineAttendue;

        var celleDAvant = devant is null ? "en cours" : devant.Numero;

        // La séparation ne vaut que sur le MINILAB : lui seul reçoit une image et la tire
        // telle quelle. Sur la DS620, une feuille blanche coûterait un panneau de
        // sublimation entier — ruban et papier — pour séparer deux paquets qui sortent déjà
        // l'un après l'autre dans un bac qu'on vide à la main.
        travail.SeparationDemandee =
            App.Services.Poste.SeparerLesCommandes &&
            travail.Ressource?.Cle.StartsWith("minilab:", StringComparison.Ordinal) == true;

        _file.Add(travail);
        Renumeroter();

        FileLog.Write($"Impression : commande {travail.Numero} mise en file derrière " +
                      $"{celleDAvant} ({travail.Ressource?.Libelle})" +
                      (travail.SeparationDemandee ? ", avec feuille blanche de séparation" : ""));

        _note = $"Commande {travail.Numero} en attente derrière {celleDAvant} — " +
                "elle partira toute seule.";
        Prevenir();
    }

    /// <summary>Remet les rangs à jour : ils se lisent dans le bandeau, ils doivent être justes.</summary>
    private void Renumeroter()
    {
        var rangs = new Dictionary<string, int>();

        foreach (var attente in _file)
        {
            var cle = attente.Ressource?.Cle ?? "";
            rangs[cle] = rangs.TryGetValue(cle, out var rang) ? rang + 1 : 1;
            attente.Rang = rangs[cle];
        }
    }

    /// <summary>
    /// Retire une commande de la file : elle ne partira pas.
    ///
    /// Rien n'est perdu — elle n'avait rien rendu ni rien envoyé — et elle reste dans
    /// « Commandes du jour », d'où on la relancera. C'est la même règle que partout
    /// ailleurs : Studio ne réimprime jamais tout seul.
    /// </summary>
    public void RetirerDeLaFile(TravailImpression travail)
    {
        ArgumentNullException.ThrowIfNull(travail);
        if (!_file.Remove(travail)) return;

        travail.EnFile = false;
        travail.Liberer();
        Renumeroter();

        FileLog.Write($"Impression : commande {travail.Numero} retirée de la file par l'opérateur");
        _note = $"Commande {travail.Numero} retirée de la file — rien n'a été imprimé. " +
                "Elle reste dans « Commandes du jour ».";
        Prevenir();
    }

    /// <summary>
    /// Met en pause, ou remet dans le tour. Une commande reprise part tout de suite si la
    /// machine est libre : sans cela, elle attendrait la fin d'une commande qui n'existe pas.
    /// </summary>
    public void BasculerLaPause(TravailImpression travail)
    {
        ArgumentNullException.ThrowIfNull(travail);
        if (!_file.Contains(travail)) return;

        travail.EnPause = !travail.EnPause;

        FileLog.Write($"Impression : commande {travail.Numero} " +
                      (travail.EnPause ? "mise en pause dans la file" : "remise dans la file"));

        if (!travail.EnPause) DemarrerLaSuite(travail.Ressource?.Cle, machineLiberee: null);
        Prevenir();
    }

    /// <summary>
    /// Fait partir la prochaine commande de cette machine, s'il y en a une et si elle
    /// n'est pas en pause.
    /// </summary>
    /// <param name="machineLiberee">
    /// La machine que la commande précédente vient de quitter, quand elle l'a dite : c'est
    /// sur elle que sort la feuille blanche de séparation. Null quand on ne sort de nulle
    /// part — une pause qu'on lève, par exemple.
    /// </param>
    private async void DemarrerLaSuite(string? cle, string? machineLiberee)
    {
        if (cle is null) return;
        if (EstOccupee(cle)) return;

        if (machineLiberee is { Length: > 0 }) _derniereMachine[cle] = machineLiberee;
        else _derniereMachine.TryGetValue(cle, out machineLiberee);

        var suivant = _file.FirstOrDefault(t => t.Ressource?.Cle == cle && !t.EnPause);
        if (suivant is null) return;

        _file.Remove(suivant);
        suivant.EnFile = false;
        Renumeroter();

        // la machine est retenue AVANT le premier await : voir _clesEnDemarrage
        _clesEnDemarrage.Add(cle);
        Prevenir();

        try
        {
            // LA FEUILLE BLANCHE, entre les deux commandes et non au milieu de l'une
            // d'elles. Elle part sur la machine que la précédente vient de quitter — la
            // seule dont on sache avec certitude qu'elle a sorti le papier d'avant.
            if (suivant.SeparationDemandee && machineLiberee is { Length: > 0 } machine)
            {
                var lettre = machine[0];
                var sortie = await Task.Run(() => App.Services.Printer.TirerFeuilleDeSeparation(lettre));

                if (!sortie)
                    _note = $"Commande {suivant.Numero} : la feuille blanche de séparation n'a pas " +
                            "pu être tirée (voir le journal). La commande part quand même.";
            }
        }
        finally
        {
            _clesEnDemarrage.Remove(cle);
        }

        Demarrer(suivant);
    }

    /// <summary>
    /// Fait partir la commande pour de bon : c'est ici que commencent le rendu et les
    /// envois, et donc le papier.
    /// </summary>
    private async void Demarrer(TravailImpression travail)
    {
        var commande = travail.Commande!;
        var imprimer = travail.Imprimer!;
        var apresSucces = travail.ApresSucces;

        _travaux.Add(travail);
        _note = null;
        _surAcquittement = null;
        Prevenir();

        FileLog.Write($"Impression : commande {travail.Numero} lancée en tâche de fond");

        // Progress<T> rejoue sur le fil qui l'a créé — ici celui de l'interface — donc
        // les propriétés notifiées ne traversent jamais de frontière de thread
        var avancement = new Progress<PrintProgress>(rapport =>
        {
            travail.Avancer(rapport);

            // La machine n'est nommée qu'à l'envoi : c'est le moment où les commandes qui
            // attendent derrière peuvent enfin dire OÙ elles sortiront.
            foreach (var attente in _file.Where(t => t.Ressource?.Cle == travail.Ressource?.Cle))
                attente.MachineAttendue = travail.Machine;
        });

        try
        {
            var jeton = travail.Jeton;
            await Task.Run(() => imprimer(avancement, jeton), CancellationToken.None);

            FileLog.Write($"Impression : commande {travail.Numero} envoyée " +
                          $"({travail.TiragesPartis} photo(s))");
            apresSucces?.Invoke();

            // Tout est PARTI, rien n'est encore SORTI. Sur le minilab, l'attente qui
            // compte pour l'opérateur commence maintenant : on reste affiché tant que la
            // machine n'a pas rendu son verdict sur chaque photo.
            await AttendreLesTirages(travail, commande);
        }
        catch (PrintCanceledException arret)
        {
            // arrêt VOULU : ce n'est pas un échec, et le message dit exactement ce qui
            // est parti et ce qui a été rappelé
            _note = arret.Message;
            FileLog.Write($"Impression : commande {travail.Numero} arrêtée — {arret.Message}");
        }
        catch (PrinterNotReadyException attente)
        {
            // Pas un échec non plus : la commande est rangée en attente avec le rang de la
            // dernière page sortie, et la file la reprendra dès que la machine répondra.
            // Surtout ne pas parler de réimpression — ce serait tirer en double.
            _note = attente.Message;
            FileLog.Write($"Impression : commande {travail.Numero} en attente — {attente.Message}");
        }
        catch (PrintUnconfirmedException doute)
        {
            // Ni échec ni attente : on NE SAIT PAS si le tirage est sorti. Le message porte
            // déjà la seule consigne utile — regarder le bac —, et il ne faut surtout pas
            // le compléter par le « reste réimprimable » du cas général : c'est exactement
            // la phrase qui fait réimprimer un tirage déjà tombé.
            _note = doute.Message;
            FileLog.Write($"Impression : commande {travail.Numero} sortie non confirmée — {doute.Message}");
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
            var machine = travail.Machine;
            var cle = travail.Ressource?.Cle;

            _travaux.Remove(travail);
            travail.Liberer();
            Prevenir();

            // La machine est libre : la suivante part, feuille blanche comprise. Dans le
            // finally, donc quoi qu'il soit arrivé à celle-ci — arrêtée, en échec, sortie :
            // une commande qui reste coincée dans la file parce que la précédente a mal
            // fini est exactement le genre de commande qu'on retrouve le lendemain.
            DemarrerLaSuite(cle, machine);
        }
    }

    /// <summary>
    /// Arrête tout ce qui est en cours — le geste de panique, un seul bouton.
    ///
    /// La file est vidée en premier : arrêter la commande en cours pendant qu'une autre
    /// attend derrière la ferait partir aussitôt, ce qui est le contraire de ce qu'on
    /// demande en appuyant là-dessus.
    /// </summary>
    public void ToutArreter()
    {
        foreach (var attente in _file.ToList()) RetirerDeLaFile(attente);
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
    private async Task AttendreLesTirages(TravailImpression travail, Order commande)
    {
        // Circuits sans accusé de sortie — spouleur, DNP : on ne saura jamais quand le
        // papier est tombé. Le client est prévenu dès que tout est REMIS à la machine,
        // c'est le mieux qu'on puisse promettre de ce côté.
        if (travail.PhotosEnvoyees <= 0 || travail.Machine is null or "D")
        {
            await PrevenirLeClientSiDemande(travail, commande);
            return;
        }

        // le débit déjà mesuré sur ce format, s'il y en a un : il sert le temps que la
        // commande en cours ait de quoi se chronométrer elle-même
        travail.Format = App.Services.Printer.DernierFormatMinilab ?? "";
        travail.LongueurMm = App.Services.Printer.DerniereLongueurMinilabMm;
        travail.Debit = App.Services.Debits.TryGetValue(travail.Format, out var debit) ? debit : null;

        travail.CommencerLeTirage(travail.PhotosEnvoyees, travail.VerdictsAttendus);

        // LE COMPTE DE LA MACHINE, POUR CETTE COMMANDE-LÀ.
        //
        // Il n'y a plus de point de départ à relever ni de soustraction à faire : le minilab
        // tient un compteur par commande (ST_ORDER_INFO.printedNum), et c'est celui-là qu'on
        // lui demande. Ce qu'il sort par ailleurs — une autre commande de Studio, DiLand sur
        // la même machine — ne le touche pas.
        //
        // Le compteur GÉNÉRAL de la machine servait jusqu'ici, faute de mieux : il fallait
        // en retrancher la valeur du départ, et tout ce qui passait entre-temps venait
        // gonfler l'avancement. C'est ce que lit le pilote de DiLand depuis toujours, et
        // c'est pourquoi son affichage, lui, ne décalait pas.
        var parCommande = travail.HandleMinilab is { Length: > 0 };

        if (!parCommande)
            FileLog.Write($"Suivi : commande {travail.Numero} — pas de handle minilab, " +
                          "l'avancement se lira sur les verdicts.");

        // le dernier compte vu, pour le bilan de fin
        De100OrderProgress? dernierEtat = null;

        // le bandeau montre une DURÉE qui s'écoule : sans battement, elle resterait figée
        // entre deux photos, et une commande d'A4 ne bouge que toutes les vingt secondes
        var battement = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        battement.Tick += (_, _) => travail.RafraichirLaDuree();
        battement.Start();

        // …et l'avancement, lui, se lit sur la machine. Deux fois moins souvent que le
        // battement : chaque relevé traverse le relais 32 bits pendant que la machine
        // travaille, et une photo 10×15 met une dizaine de secondes à sortir — interroger
        // plus vite ne montrerait rien de plus.
        // <b>Un seul relevé à la fois.</b> Le battement ne suspend pas ses coups pendant
        // qu'on attend la machine : un relais lent — ou occupé à imprimer — laisserait les
        // lectures se chevaucher et empiler les requêtes. C'est exactement ce qui tuait le
        // relais 32 bits (voir De100Protocol), et il ne faut pas le lui redemander.
        var lectureEnCours = false;

        // Le premier relevé muet est DIT, une fois.
        //
        // Il ne l'était pas : pour éviter une ligne toutes les dix secondes, ce chemin
        // avalait ses échecs sans un mot — et la commande 11-029 du 11/08/2026 est sortie
        // sans que rien n'avance à l'écran ni ne s'écrive au journal. Un silence par
        // relevé, oui ; un silence sur le premier, non.
        var muetSignale = false;

        var releve = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        releve.Tick += async (_, _) =>
        {
            if (!parCommande) return;
            if (lectureEnCours) return;

            lectureEnCours = true;
            try
            {
                var etat = await App.Services.Minilab.OrderProgressAsync(travail.HandleMinilab!);
                if (etat is null)
                {
                    if (!muetSignale)
                    {
                        muetSignale = true;
                        FileLog.Write(
                            $"Suivi : commande {travail.Numero} — la machine ne dit rien de " +
                            $"« {travail.HandleMinilab} ». L'avancement retombe sur les verdicts.");
                    }

                    return;
                }

                dernierEtat = etat;
                travail.NoterFeuillesSorties((int)etat.Printed);
                Prevenir();
            }
            finally
            {
                lectureEnCours = false;
            }
        };
        releve.Start();

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

            // Les deux minuteurs sont arrêtés ICI, et non après le bloc.
            //
            // Un DispatcherTimer démarré est retenu par le répartiteur : rien ne le ramasse
            // tant qu'il tourne. Posés après le try, ils n'étaient arrêtés que sur les deux
            // sorties prévues — fin normale ou expiration. Toute autre issue (l'opérateur
            // qui quitte, une lecture qui lève) les laissait battre POUR TOUJOURS : le
            // relevé interrogeait le relais 32 bits toutes les dix secondes, indéfiniment,
            // en retenant au passage la commande et son suivi. Sur une journée de comptoir,
            // c'est un relevé de plus par commande passée.
            battement.Stop();
            releve.Stop();
        }

        // BILAN, une ligne par commande : ce que la machine a compté pour ELLE, et ce que
        // l'écran en a montré. Les deux doivent coïncider — c'est le contrôle du suivi.
        if (dernierEtat is { } etatFinal)
            FileLog.Write(
                $"Suivi : commande {travail.Numero} — la machine {travail.Machine} compte " +
                $"{etatFinal.Printed}/{etatFinal.Total} tirage(s) pour cette commande " +
                $"({De100JobTracker.Describe(etatFinal.Status)}), " +
                $"{travail.Sortis}/{travail.Total} montrés à l'écran.");

        // LA MACHINE A RÉPONDU SUR TOUT, ET SANS ÉCHEC : l'enveloppe est close sur le
        // disque. C'est ce qui manquait — les verdicts arrivaient, le bandeau les montrait,
        // le journal les écrivait, mais rien ne l'inscrivait sur la commande : elle
        // ressortait « impression non confirmée » à chaque démarrage suivant.
        //
        // Jamais après une expiration (TirageTermine est alors faux) ni s'il reste un
        // tirage raté : dans ces deux cas c'est bien à l'opérateur de trancher.
        if (travail.TirageTermine && travail.Rates == 0)
        {
            try
            {
                App.Services.Printer.ConfirmerSortieMinilab(commande);
            }
            catch (Exception ex)
            {
                // Le papier est sorti : c'est l'essentiel, et l'opérateur pourra toujours
                // confirmer à la main. Rien ici ne doit empêcher de prévenir le client.
                FileLog.Write($"Commande {travail.Numero} : sortie non enregistrée", ex);
            }
        }

        // Ce qu'on vient de mesurer sert aux commandes suivantes. Seulement quand tout est
        // sorti : une commande interrompue en cours de route a passé du temps à attendre
        // l'opérateur, pas à imprimer, et sa moyenne serait fausse.
        if (travail.Rates == 0 && travail.Sortis >= 2)
            App.Services.NoterLeDebit(
                travail.Format, travail.Sortis, DateTimeOffset.Now - travail.DebutDuTirage);

        await PrevenirLeClientSiDemande(travail, commande);

        if (travail.Rates > 0)
            _note = $"Commande {travail.Numero} : {travail.Sortis} photo(s) sorties, " +
                    $"{travail.Rates} en échec. Réimpression depuis « Commandes du jour ».";
    }

    /// <summary>
    /// Le compteur de tirages d'une machine, ou null si elle ne l'a pas donné.
    ///
    /// Jamais fatal : ce relevé ne sert qu'à faire avancer une barre. Une machine qui ne
    /// répond pas laisse simplement l'affichage retomber sur les verdicts, et la commande
    /// s'imprime exactement pareil.
    /// </summary>
    private static async Task<long?> LireLeCompteur(string? machine)
    {
        if (string.IsNullOrEmpty(machine)) return null;

        try
        {
            var etats = await App.Services.Minilab.SnapshotAsync();
            return etats
                .FirstOrDefault(e => e.MachineId.ToString()
                    .Equals(machine, StringComparison.OrdinalIgnoreCase))
                ?.TotalPrintCount;
        }
        catch (Exception ex)
        {
            FileLog.Write($"Suivi : compteur de la machine {machine} illisible", ex);
            return null;
        }
    }

    /// <summary>
    /// Prévient le client que sa commande est prête, si son adresse a été prise au comptoir.
    ///
    /// <b>Quand la machine a fini, pas quand l'envoi est parti.</b> C'est toute la
    /// différence : sur le minilab, envoyer trente tirages prend quelques secondes et les
    /// sortir prend plusieurs minutes. Annoncer « c'est prêt » à l'envoi ferait venir le
    /// client devant une machine qui travaille encore.
    ///
    /// <b>Jamais si un tirage a échoué</b> : on ne fait pas venir quelqu'un pour une
    /// commande incomplète. L'opérateur reprend la main, réimprime, et préviendra
    /// lui-même depuis « Commandes du jour ».
    ///
    /// <b>Une seule fois</b> : <c>CustomerNotified</c> est écrit sur la commande. Une
    /// réimpression n'enverra pas un second message — deux courriels pour la même commande
    /// font douter le client de ce qu'il doit venir chercher.
    /// </summary>
    private async Task PrevenirLeClientSiDemande(TravailImpression travail, Order commande)
    {
        if (commande.CustomerEmail is not { Length: > 0 } adresse) return;
        if (commande.CustomerNotified) return;

        if (travail.Rates > 0)
        {
            FileLog.Write($"Commande {travail.Numero} : client NON prévenu — " +
                          $"{travail.Rates} tirage(s) en échec.");
            return;
        }

        var quoi = DecrireLaCommande(commande);

        try
        {
            await Task.Run(() => PhotoMailer.PrevenirCommandePrete(
                App.Services.Mail, adresse, commande.DisplayNumber, quoi, commande.CustomerName));

            commande.CustomerNotified = true;
            App.Services.Store.Save(commande);

            FileLog.Write($"Commande {travail.Numero} : client prévenu à {adresse}.");

            _note = $"Commande {travail.Numero} : le client a été prévenu à {adresse}.";
            Prevenir();
        }
        catch (Exception ex)
        {
            // Le tirage est sorti : ce qui compte est fait. Un courriel qui ne part pas se
            // rattrape depuis « Commandes du jour », et ne doit pas ressembler à un échec
            // d'impression.
            FileLog.Write($"Commande {travail.Numero} : impossible de prévenir {adresse}", ex);

            _note = $"Commande {travail.Numero} sortie, mais le client n'a pas pu être " +
                    $"prévenu ({ex.Message}). Réessayez depuis « Commandes du jour ».";
            Prevenir();
        }
    }

    /// <summary>Ce que la commande contient, en produits — c'est ce que le client comprend.</summary>
    private static string DecrireLaCommande(Order commande) =>
        string.Join(", ", commande.Envelopes
            .SelectMany(e => e.Lines)
            .GroupBy(l => App.Services.Catalog.Find(l.ProductCode)?.Name ?? l.ProductCode)
            .Select(g => $"{g.Sum(l => l.TotalPrints)} × {g.Key}"));

    /// <summary>
    /// Le minilab a rendu son verdict sur un tirage.
    ///
    /// L'identifiant est celui que l'orchestrateur a fabriqué à l'envoi :
    /// <c>{numéro}-{enveloppe}-{rang}</c>, où le numéro contient lui-même un tiret
    /// (« 01-016 »). On retire donc les DEUX derniers segments pour retrouver la commande.
    /// </summary>
    /// <param name="motif">Ce que la machine dit du refus, ou vide.</param>
    public void TirageTermine(string jobId, bool reussi, string motif = "")
    {
        var numero = PrintOrchestrator.OrderNumberOf(jobId);
        if (numero is null) return;

        var travail = _travaux.FirstOrDefault(t => t.Numero == numero);
        if (travail is null) return;

        travail.NoterTirage(reussi, motif);
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
        OnPropertyChanged(nameof(FileActive));
        OnPropertyChanged(nameof(NbEnFile));
    }
}
