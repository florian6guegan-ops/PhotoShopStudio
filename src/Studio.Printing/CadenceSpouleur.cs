namespace Studio.Printing;

/// <summary>
/// Ce qu'on peut faire, maintenant, avec la file d'une imprimante.
/// </summary>
/// <param name="PeutEnvoyer">Vrai si une page de plus peut partir tout de suite.</param>
/// <param name="Panne">Ce qui ne va pas sur la machine ; vide si elle se porte bien.</param>
/// <param name="PagesEnFile">Pages remises à Windows qui ne sont pas encore sorties.</param>
public sealed record PlaceEnFile(bool PeutEnvoyer, string Panne, int PagesEnFile)
{
    public bool EnPanne => Panne.Length > 0;
}

/// <summary>
/// Alimente une imprimante AU RYTHME OÙ ELLE SORT LE PAPIER, au lieu de lui déverser
/// toute la commande d'un coup.
///
/// <b>Le défaut qu'elle corrige.</b> <c>PrintOrchestrator.PrintPages</c> remettait les
/// pages au spouleur aussi vite qu'il les acceptait : onze tirages en cinq secondes sur la
/// commande 04-024 du 04/08/2026. Trois conséquences, toutes vues en boutique :
///
/// 1. <b>on ne peut plus reprendre au bon endroit.</b> Sur six cents photos, une panne
///    d'encre à la troisième laisse quand même partir les cinq cent quatre-vingt-dix-sept
///    autres : le point de reprise dit « 600 remises », la machine n'en a sorti que deux,
///    et il n'existe plus aucun moyen de savoir où reprendre ;
/// 2. <b>la machine se bloque.</b> Une DS620 qui reçoit onze travaux à la file, dont
///    certains changent de forme de papier, s'arrête au premier — c'est ce qui est arrivé ;
/// 3. <b>l'opérateur ne peut plus rien arrêter.</b> Ce que Windows a pris lui appartient ;
///    seule la file de l'imprimante permet encore de l'en empêcher.
///
/// <b>Ce qu'elle fait.</b> Avant chaque page, elle regarde la file : si la machine est en
/// panne, elle le dit et on s'arrête là ; si la file est pleine, elle patiente. Rien de
/// plus. Les pages qui n'ont pas été remises n'ont pas à être reprises — c'est ce qui rend
/// la reprise exacte.
///
/// <b>Elle ne bloque jamais indéfiniment.</b> Passé <see cref="AttenteMaximale"/> sans que
/// la file descende, on considère que la machine ne suit plus et on rend la main à
/// l'appelant, qui mettra la commande en attente.
/// </summary>
public sealed class CadenceSpouleur
{
    private readonly Func<PlaceEnFile> _lire;
    private readonly Action<TimeSpan> _patienter;

    /// <param name="lire">Lecture de l'état de la file. Ne doit jamais lever.</param>
    /// <param name="patienter">
    /// Attente entre deux lectures. Injectée pour que les essais tournent sans dormir.
    /// </param>
    public CadenceSpouleur(Func<PlaceEnFile> lire, Action<TimeSpan> patienter)
    {
        ArgumentNullException.ThrowIfNull(lire);
        ArgumentNullException.ThrowIfNull(patienter);

        _lire = lire;
        _patienter = patienter;
    }

    /// <summary>
    /// Pages qu'on accepte de laisser d'avance dans la file.
    ///
    /// Pas zéro : la machine ne doit jamais attendre après nous, sinon chaque tirage coûte
    /// un aller-retour de lecture et la cadence s'effondre. Pas dix non plus : c'est
    /// exactement ce qu'on veut éviter. Trois laisse la mécanique tourner sans à-coups tout
    /// en bornant ce qu'on perdrait de vue en cas de panne.
    /// </summary>
    public int PlafondEnFile { get; init; } = 3;

    /// <summary>Temps entre deux lectures de la file, quand elle est pleine.</summary>
    public TimeSpan PasDAttente { get; init; } = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// Au-delà, on cesse d'attendre et on rend la main.
    ///
    /// Large : une DS620 sort un 10×15 en une quinzaine de secondes, et une file de trois
    /// se vide donc en moins d'une minute. Cinq minutes sans le moindre mouvement veut dire
    /// que la machine ne travaille plus, quoi qu'en dise son état.
    /// </summary>
    public TimeSpan AttenteMaximale { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Journal optionnel.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// Attend que la file puisse prendre une page de plus.
    /// </summary>
    /// <param name="plafond">
    /// Pages tolérées dans la file. <see cref="PlafondEnFile"/> pendant l'impression, 0
    /// pour attendre qu'elle se vide en fin de commande.
    /// </param>
    /// <param name="ct">Arrêt demandé par l'opérateur.</param>
    /// <returns>
    /// Ce que la file a dit en dernier. <see cref="PlaceEnFile.EnPanne"/> = la machine
    /// demande une intervention, l'appelant doit s'arrêter et mettre la commande en attente.
    /// </returns>
    public PlaceEnFile Attendre(int plafond, CancellationToken ct = default)
    {
        var debut = DateTimeOffset.UtcNow;
        var derniereFile = int.MaxValue;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var place = _lire();

            // une panne l'emporte sur tout : inutile d'attendre une file qui ne descendra
            // pas, et surtout il ne faut pas lui remettre une page de plus
            if (place.EnPanne) return place;

            if (place.PagesEnFile <= plafond) return place with { PeutEnvoyer = true };

            // la file descend : on remet le compteur d'attente à zéro. Ce n'est pas la
            // durée totale qui compte — une commande de six cents photos passe légitimement
            // une heure ici — mais le temps SANS le moindre progrès.
            if (place.PagesEnFile < derniereFile)
            {
                derniereFile = place.PagesEnFile;
                debut = DateTimeOffset.UtcNow;
            }

            if (DateTimeOffset.UtcNow - debut > AttenteMaximale)
            {
                Log?.Invoke($"Spouleur : {place.PagesEnFile} page(s) en file sans avancer depuis " +
                            $"{AttenteMaximale.TotalMinutes:0} min — on rend la main.");
                return place with
                {
                    PeutEnvoyer = false,
                    Panne = $"la machine n'a rien sorti depuis {AttenteMaximale.TotalMinutes:0} minutes " +
                            $"({place.PagesEnFile} page(s) en attente dans sa file)",
                };
            }

            _patienter(PasDAttente);
        }
    }

    /// <summary>
    /// Pages réellement SORTIES, d'après ce qui reste en file.
    ///
    /// C'est ce nombre-là que le point de reprise doit retenir, et non les pages remises à
    /// Windows : la différence entre les deux est exactement ce qu'on réimprimerait pour
    /// rien, ou pire, ce qu'on sauterait.
    /// </summary>
    /// <param name="pagesRemises">Pages données au spouleur depuis le début de l'enveloppe.</param>
    public int PagesSorties(int pagesRemises)
    {
        var place = _lire();

        // File illisible (PagesEnFile négatif) : on ne sait rien de plus que ce qu'on a
        // remis, et on s'en tient là. Soustraire un nombre négatif annoncerait PLUS de
        // pages sorties qu'il n'en est parti — et la reprise sauterait une photo.
        if (place.PagesEnFile < 0) return pagesRemises;

        return Math.Clamp(pagesRemises - place.PagesEnFile, 0, pagesRemises);
    }

    /// <summary>
    /// Où reprendre après une interruption : une page AVANT la dernière sortie.
    ///
    /// <b>On refait volontairement la dernière.</b> Quand une machine s'arrête faute
    /// d'encre ou de ruban, la photo en cours sort pâle, striée, ou à moitié — et rien ne
    /// permet de le savoir depuis le logiciel. Une feuille refaite coûte quelques centimes ;
    /// une photo ratée glissée au milieu d'un paquet de six cents coûte le paquet.
    /// Demandé par l'exploitant le 04/08/2026.
    /// </summary>
    public static int ReprendreA(int pagesSorties) => Math.Max(0, pagesSorties - 1);
}
