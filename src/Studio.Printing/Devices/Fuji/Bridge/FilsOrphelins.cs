namespace Studio.Printing.Devices.Fuji.Bridge;

/// <summary>
/// Combien d'appels au SDK sont partis sans jamais revenir — et à partir de quand il faut
/// cesser d'en lancer.
///
/// <b>Pourquoi ce compte existe.</b> Le relais répond toujours, même quand le SDK ne rend
/// pas la main : passé son délai, il répond « muette » et abandonne le fil. Le fil, lui,
/// reste bloqué dans l'appel natif — le SDK ne s'interrompt pas, on n'y peut rien.
///
/// Ce qui manquait, c'est une BORNE. Le relais est en <b>32 bits</b> : deux gigaoctets
/// d'espace d'adressage, un mégaoctet de pile par fil. Laisser leur nombre courir n'est pas
/// tenable, et cette classe les compte.
///
/// ⚠ <b>Ce n'est PAS ce qui faisait planter le relais à Créteil</b>, contrairement à ce que
/// cette classe a d'abord prétendu. Le journal d'événements Windows a tranché le
/// 12/08/2026 : cinq plantages, tous dans <c>MSVCR90.dll</c> avec le code
/// <c>0xc0000417</c> — un paramètre invalide passé au CRT par le SDK DNP, qui n'est pas
/// réentrant. Au moment du diagnostic le relais tenait en <b>59 Mo et douze fils</b> : la
/// mémoire n'y était pour rien. Le vrai correctif est le verrou de <c>verrouDnp</c> dans le
/// relais.
///
/// Cette borne-ci reste utile — un SDK durablement coincé ne doit pas recevoir du travail
/// supplémentaire — mais elle protège d'une panne qu'on n'a jamais observée. À ne pas citer
/// comme la cause d'un plantage.
///
/// <b>Saturé, on répond sans rien lancer.</b> Le SDK est manifestement coincé ; lui envoyer
/// du travail supplémentaire ne fait qu'avancer l'heure du crash. Un redémarrage est le seul
/// remède, et mieux vaut un bandeau qui l'annonce qu'un relais qui meurt en silence.
/// </summary>
public sealed class FilsOrphelins
{
    /// <summary>
    /// Huit : de quoi encaisser une rafale du bandeau pendant qu'une machine imprime, et
    /// rester très loin de ce qui met l'espace d'adressage en danger.
    /// </summary>
    public const int PlafondParDefaut = 8;

    private readonly int _plafond;
    private int _perdus;

    public FilsOrphelins(int plafond = PlafondParDefaut)
    {
        if (plafond < 1) throw new ArgumentOutOfRangeException(nameof(plafond));
        _plafond = plafond;
    }

    /// <summary>Appels partis et jamais revenus, à cet instant.</summary>
    public int Perdus => Volatile.Read(ref _perdus);

    /// <summary>
    /// Le SDK est-il coincé au point qu'il ne faille plus rien lui envoyer ?
    /// </summary>
    public bool Sature => Perdus >= _plafond;

    /// <summary>Un appel vient d'être abandonné.</summary>
    public void Abandonne() => Interlocked.Increment(ref _perdus);

    /// <summary>
    /// Un appel abandonné a fini par revenir : le SDK n'était que lent.
    ///
    /// <b>C'est ce retour qui rouvre la porte</b> — sans lui, un poste simplement lent
    /// resterait fermé jusqu'au redémarrage, et l'on aurait remplacé un plantage par une
    /// panne.
    /// </summary>
    public void Revenu()
    {
        // jamais sous zéro : un décompte de trop rouvrirait la porte à tort
        if (Interlocked.Decrement(ref _perdus) < 0) Interlocked.Exchange(ref _perdus, 0);
    }
}
