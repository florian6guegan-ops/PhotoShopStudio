using System.Diagnostics;

namespace Studio.App.Infrastructure;

/// <summary>
/// UN SEUL des deux logiciels à la fois sur un poste.
///
/// <b>Ce que ça évite, et ça a bloqué une DNP en boutique.</b> Studio Photo et Studio Photo
/// Identité pilotent les machines par le même relais 32 bits, et chacun DÉMARRE LE SIEN
/// (<c>De100BridgeClient.ConnectAsync</c>). Or le relais sert un <b>tube nommé à instance
/// unique</b> : le second à s'ouvrir ne crée pas le sien, il se branche sur le relais du
/// premier. Les deux applications se retrouvent donc à parler à un relais qui appartient à
/// l'une d'elles — et le jour où celle-là se ferme, elle l'emporte avec elle (le relais lui
/// est lié pour ne jamais lui survivre). L'autre garde une connexion morte et <b>plus rien
/// ne part à l'imprimante</b>, sans un mot.
///
/// C'est arrivé à Arcueil le 14/08/2026, le soir où Identité a été installé sur un poste qui
/// portait déjà le Studio.
///
/// La règle est donc simple, et elle se dit à l'ouverture plutôt que de se découvrir devant
/// un client : sur un poste, on ouvre l'un OU l'autre.
/// </summary>
public static class UnSeulLogiciel
{
    /// <summary>Le nom d'exécutable du Studio complet, sans extension.</summary>
    private const string Studio = "Studio.App";

    /// <summary>Le nom d'exécutable du poste identité, sans extension.</summary>
    private const string Identite = "Studio.Identite";

    /// <summary>
    /// L'AUTRE logiciel, s'il tourne déjà sur ce poste. Null quand la voie est libre.
    /// </summary>
    /// <param name="moi">
    /// Le nom d'exécutable de l'application qui démarre, sans extension.
    /// </param>
    /// <returns>Son nom lisible, à montrer à l'opérateur.</returns>
    public static string? LAutreQuiTourne(string moi)
    {
        var autre = moi.Equals(Identite, StringComparison.OrdinalIgnoreCase) ? Studio : Identite;

        try
        {
            // On ne compte que les AUTRES processus : deux fenêtres du même logiciel ne se
            // disputent pas le relais, c'est le même client qui le tient.
            var vivants = Process.GetProcessesByName(autre);
            try
            {
                if (vivants.Length == 0) return null;
            }
            finally
            {
                foreach (var p in vivants) p.Dispose();
            }
        }
        catch (Exception)
        {
            // Pas de droit de lecture sur la liste des processus : on ne bloque rien. Se
            // tromper en refusant l'ouverture serait pire que le défaut qu'on prévient.
            return null;
        }

        return autre.Equals(Studio, StringComparison.OrdinalIgnoreCase)
            ? "Studio Photo"
            : "Studio Photo Identité";
    }
}
