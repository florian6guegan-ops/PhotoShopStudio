using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Studio.App.Infrastructure;

/// <summary>
/// Raccourcis clavier d'un écran, sur le modèle du gestionnaire central de DiLand
/// (<c>ApplicationKeyManager</c>).
///
/// Les opérateurs de la boutique travaillent au clavier : ils enchaînent les commandes
/// sans quitter la photo des yeux, et une souris coûte des secondes à chaque tirage. Les
/// touches sont donc reprises TELLES QUELLES de DiLand — un raccourci différent serait
/// pire que pas de raccourci du tout, parce qu'il ferait faire l'inverse de ce qu'on croit.
///
/// La saisie de texte l'emporte toujours : tant qu'un champ a le focus, les touches lui
/// reviennent, sinon taper « 5 » dans un nom de client changerait une quantité.
/// </summary>
public sealed class KeyMap
{
    private readonly Dictionary<(ModifierKeys Modifiers, Key Key), Action> _actions = new();

    /// <summary>Touche seule, sans modificateur.</summary>
    public KeyMap On(Key key, Action action) => Register(ModifierKeys.None, key, action);

    /// <summary>Touche avec Ctrl.</summary>
    public KeyMap OnCtrl(Key key, Action action) => Register(ModifierKeys.Control, key, action);

    /// <summary>Plusieurs touches pour la même action (le pavé numérique double les chiffres).</summary>
    public KeyMap On(IEnumerable<Key> keys, Action action)
    {
        foreach (var key in keys) Register(ModifierKeys.None, key, action);
        return this;
    }

    private KeyMap Register(ModifierKeys modifiers, Key key, Action action)
    {
        _actions[(modifiers, key)] = action;
        return this;
    }

    /// <summary>
    /// Branche les raccourcis sur un écran. À appeler une fois, au chargement.
    ///
    /// L'écoute se fait en amont (<c>PreviewKeyDown</c>) pour que les touches de
    /// navigation — flèches, espace — nous parviennent avant que WPF ne les consomme pour
    /// déplacer le focus entre boutons.
    /// </summary>
    public void Attach(FrameworkElement element)
    {
        ArgumentNullException.ThrowIfNull(element);

        // On écoute au niveau de la FENÊTRE, pas de l'écran.
        //
        // Sur l'écran lui-même, les raccourcis ne partaient que si le focus clavier s'y
        // trouvait — or cliquer une vignette ou faire défiler la planche le déplace, et
        // Ctrl+A restait sans effet. L'écoute est retirée quand l'écran est fermé, pour
        // qu'un écran caché ne réponde plus aux touches.
        KeyEventHandler? ecoute = null;

        element.Loaded += (_, _) =>
        {
            if (Window.GetWindow(element) is not { } fenetre || ecoute is not null) return;

            ecoute = Handle;
            fenetre.PreviewKeyDown += ecoute;
        };

        element.Unloaded += (_, _) =>
        {
            if (Window.GetWindow(element) is not { } fenetre || ecoute is null) return;

            fenetre.PreviewKeyDown -= ecoute;
            ecoute = null;
        };

        void Handle(object sender, KeyEventArgs e)
        {
            if (!element.IsVisible) return;
            if (IsTyping(sender)) return;

            // Ctrl seul nous intéresse : Alt appartient aux menus, Maj aux majuscules
            var modifiers = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt);
            if (modifiers is not (ModifierKeys.None or ModifierKeys.Control)) return;

            // Alt Gr se présente comme Ctrl+Alt : c'est une saisie, pas un raccourci
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!_actions.TryGetValue((modifiers, key), out var action)) return;

            action();
            e.Handled = true;
        }
    }

    /// <summary>Vrai si l'utilisateur est en train de saisir du texte : on ne lui vole pas ses touches.</summary>
    private static bool IsTyping(object sender) =>
        Keyboard.FocusedElement is TextBoxBase or PasswordBox or ComboBox { IsEditable: true };
}
