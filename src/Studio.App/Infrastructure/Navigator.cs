using System.Windows.Controls;

namespace Studio.App.Infrastructure;

/// <summary>Navigation par pile d'écrans dans la fenêtre principale.</summary>
public static class Navigator
{
    private static readonly Stack<UserControl> Stack = new();

    public static event Action<UserControl, string>? Navigated;

    public static void Go(UserControl view, string title)
    {
        Stack.Push(view);
        Navigated?.Invoke(view, title);
    }

    /// <summary>Retour à l'accueil : vide la pile et affiche la vue fournie.</summary>
    public static void Home(UserControl homeView, string title)
    {
        Stack.Clear();
        Stack.Push(homeView);
        Navigated?.Invoke(homeView, title);
    }

    public static bool CanGoBack => Stack.Count > 1;

    public static void Back()
    {
        if (Stack.Count <= 1) return;
        Stack.Pop();
        var (view, title) = (Stack.Peek(), Stack.Peek().Tag as string ?? "");
        Navigated?.Invoke(view, title);
    }
}
