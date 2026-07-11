using System.Windows;
using System.Windows.Threading;

namespace Studio.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        try
        {
            Services = AppServices.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Impossible de démarrer :\n{ex.Message}",
                "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // dernière ligne de défense : on informe, on journalise, on ne meurt pas en silence
        Infrastructure.FileLog.Write("Exception non gérée", e.Exception);
        MessageBox.Show(
            $"Une erreur inattendue s'est produite :\n{e.Exception.Message}\n\nL'application continue.",
            "Studio Photo", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}
