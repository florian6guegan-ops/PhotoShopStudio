using System.Diagnostics;

namespace Studio.Web;

/// <summary>Règle de pare-feu entrante pour le serveur d'upload (l'app tourne en admin).</summary>
public static class Firewall
{
    private const string RuleName = "Studio Photo - upload telephone";

    /// <summary>Crée la règle si absente. Sans droits admin : false, l'app reste utilisable en local.</summary>
    public static bool EnsureRule(int port)
    {
        try
        {
            if (Run("advfirewall firewall show rule name=\"" + RuleName + "\"") == 0)
                return true;
            return Run(
                $"advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={port}") == 0;
        }
        catch
        {
            return false;
        }
    }

    private static int Run(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("netsh", arguments)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit(15_000);
        return process.ExitCode;
    }
}
