using System;
using System.Windows;
using System.Windows.Input;

namespace Studio.App.Infrastructure;

/// <summary>
/// Écoute le clavier de la FENÊTRE pour le compte d'un élément, sans jamais s'abonner
/// deux fois.
///
/// Il faut écouter la fenêtre et non l'élément : un événement clavier remonte depuis ce
/// qui a le focus, et le focus n'est pas sur la photo tant qu'on n'a pas cliqué dedans —
/// or c'est justement le geste que l'opérateur ne fait pas avant d'appuyer sur T.
///
/// **Le défaut que cette classe existe pour empêcher.** Le branchement se faisait ainsi :
///
/// <code>
/// Loaded   += (_, _) => Window.GetWindow(this).PreviewKeyDown += OnPreviewKeyDown;
/// Unloaded += (_, _) => Window.GetWindow(this).PreviewKeyDown -= OnPreviewKeyDown;
/// </code>
///
/// WPF déclenche <c>Loaded</c> PLUSIEURS FOIS sur un même élément — reparentage,
/// retemplatage d'un conteneur — sans <c>Unloaded</c> entre les deux. Le gestionnaire se
/// retrouvait abonné deux fois, et comme T est une BASCULE, un appui la faisait jouer deux
/// fois : le mode redressement ne s'armait jamais et le bandeau n'apparaissait pas.
/// Signalé par l'exploitant le 04/08/2026 (« je ne peux pas redresser avec la molette en
/// appuyant sur T »), et visible au journal : <c>T=False (armé=False)</c> juste après
/// l'appui.
///
/// Deux précautions, et les deux comptent :
/// <list type="number">
///   <item>l'abonnement est IDEMPOTENT — un second <c>Loaded</c> ne double rien ;</item>
///   <item>la fenêtre est RETENUE — se désabonner via <c>Window.GetWindow</c> au moment de
///   l'<c>Unloaded</c> peut viser une autre fenêtre, ou aucune : l'élément est déjà
///   détaché de l'arbre visuel, et l'ancien abonnement survivrait.</item>
/// </list>
/// </summary>
internal sealed class ToucheFenetre
{
    private readonly FrameworkElement _element;
    private readonly KeyEventHandler _gestionnaire;
    private readonly Action? _auDepart;
    private Window? _fenetre;

    /// <param name="element">L'élément dont la vie borne l'écoute.</param>
    /// <param name="gestionnaire">Appelé sur le <c>PreviewKeyDown</c> de la fenêtre.</param>
    /// <param name="auDepart">
    /// Appelé quand l'élément quitte l'écran — l'occasion de désarmer un mode qui n'aurait
    /// plus de sens au retour.
    /// </param>
    public ToucheFenetre(FrameworkElement element, KeyEventHandler gestionnaire, Action? auDepart = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(gestionnaire);

        _element = element;
        _gestionnaire = gestionnaire;
        _auDepart = auDepart;

        _element.Loaded += (_, _) => Brancher();
        _element.Unloaded += (_, _) =>
        {
            Debrancher();
            _auDepart?.Invoke();
        };
    }

    private void Brancher()
    {
        var fenetre = Window.GetWindow(_element);
        if (fenetre is null || ReferenceEquals(fenetre, _fenetre)) return;

        Debrancher();
        _fenetre = fenetre;
        _fenetre.PreviewKeyDown += _gestionnaire;
    }

    private void Debrancher()
    {
        if (_fenetre is null) return;

        _fenetre.PreviewKeyDown -= _gestionnaire;
        _fenetre = null;
    }
}
