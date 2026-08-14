using System;
using System.Threading.Tasks;

namespace Studio.App.Infrastructure;

/// <summary>
/// Une relecture qui touche le disque, tenue HORS du fil de l'interface.
///
/// <b>Pourquoi.</b> L'accueil relit le dépôt DiLand toutes les quinze secondes, et il le
/// faisait sur le fil de l'interface : copie de la base, requête, énumération de dossiers,
/// lecture de fichiers. Tant que DiLand va bien, personne ne le voit. Le jour où DiLand se
/// bloque ou tient un fichier, <b>Studio attend avec lui</b> — au bout de cinq secondes sans
/// répondre, Windows déclare la fenêtre figée et la ferme. C'est arrivé à Créteil le
/// 12/08/2026 (1.4.3) puis le 14/08/2026 (1.5.6) : deux figeages de suite chaque fois, avec
/// DiLand en difficulté au même moment.
///
/// <b>Ce que ça change.</b> La lecture part sur un fil de fond. Passé le plafond, l'accueil
/// le DIT et rend la main à l'opérateur — le reste de l'écran continue de servir. La lecture
/// n'est pas abandonnée pour autant : une E/S bloquée ne s'annule pas, et un dépôt qui répond
/// enfin au bout de trente secondes a quand même la bonne réponse. Elle se posera à son
/// retour.
///
/// <b>Et pas deux à la fois.</b> Tant qu'une lecture n'a pas rendu la main, les demandes
/// suivantes sont ignorées : sinon un dépôt bloqué ferait empiler un fil toutes les quinze
/// secondes, jusqu'à en manquer.
/// </summary>
public sealed class RelectureNonBloquante
{
    private readonly TimeSpan _plafond;
    private bool _enCours;

    /// <param name="plafond">
    /// Au-delà, on prévient l'appelant sans attendre la fin. À choisir SOUS les cinq
    /// secondes que Windows accorde à une fenêtre avant de la dire figée.
    /// </param>
    public RelectureNonBloquante(TimeSpan plafond) => _plafond = plafond;

    /// <summary>Vrai tant qu'une lecture n'a pas rendu la main — plafond dépassé ou non.</summary>
    public bool EnCours => _enCours;

    /// <summary>
    /// Lance <paramref name="lire"/> en fond et pose son résultat au retour.
    ///
    /// Les rappels reviennent sur le contexte de l'appelant — le fil de l'interface quand
    /// c'est une vue qui demande, ce qui les autorise à toucher aux contrôles.
    /// </summary>
    /// <param name="lire">La part qui touche le disque. Jamais sur le fil de l'interface.</param>
    /// <param name="poser">Le résultat, à afficher.</param>
    /// <param name="enRetard">Le plafond est passé et la lecture court toujours.</param>
    /// <param name="enEchec">La lecture a levé. Le dépôt est illisible, pas lent.</param>
    public async Task Demander<T>(
        Func<T> lire,
        Action<T> poser,
        Action? enRetard = null,
        Action<Exception>? enEchec = null)
    {
        ArgumentNullException.ThrowIfNull(lire);
        ArgumentNullException.ThrowIfNull(poser);

        if (_enCours) return;
        _enCours = true;

        var lecture = Task.Run(lire);
        try
        {
            if (await Task.WhenAny(lecture, Task.Delay(_plafond)) != lecture)
                enRetard?.Invoke();

            poser(await lecture);
        }
        catch (Exception ex)
        {
            enEchec?.Invoke(ex);
        }
        finally
        {
            _enCours = false;
        }
    }
}
