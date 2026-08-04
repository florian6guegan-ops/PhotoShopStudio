using System.IO;
using Microsoft.Win32;

namespace Studio.App.Infrastructure;

/// <summary>
/// Ce que ce poste a comme carte graphique, et combien de mémoire vidéo — pour dire à
/// l'opérateur si le détourage par réseau de neurones tiendra chez lui.
///
/// <b>Lu dans le registre, pas dans WMI.</b> <c>Win32_VideoController.AdapterRAM</c> est un
/// <c>uint32</c> exprimé en octets : il plafonne à 4 294 967 295, et rend donc « 4 Go » sur
/// toutes les cartes de 6, 8, 12 ou 24 Go. Sur une machine à 8 Go, l'avertissement du
/// modèle puissant se serait déclenché à tort — c'est-à-dire exactement dans le cas qu'on
/// cherche à autoriser. Le pilote publie la vraie valeur en 64 bits sous
/// <c>HardwareInformation.qwMemorySize</c>.
///
/// Rien n'est deviné : une clé illisible rend null, et l'écran n'affiche alors pas de
/// chiffre plutôt qu'un chiffre faux.
/// </summary>
internal static class CarteGraphique
{
    /// <summary>La classe des adaptateurs d'affichage, dans le registre.</summary>
    private const string ClasseAffichage =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>Une carte du poste.</summary>
    /// <param name="Nom">Le nom publié par le pilote, ex. « NVIDIA Quadro P2000 ».</param>
    /// <param name="MemoireGo">Mémoire vidéo en gigaoctets, ou null si le pilote ne la publie pas.</param>
    public sealed record Carte(string Nom, double? MemoireGo)
    {
        public override string ToString() =>
            MemoireGo is { } go ? $"{Nom} — {go:0.#} Go de mémoire vidéo" : Nom;
    }

    /// <summary>
    /// Les cartes du poste, la mieux dotée d'abord.
    ///
    /// La mieux dotée d'abord parce que c'est elle que DirectML retiendra sur un portable
    /// à deux cartes (chipset Intel + carte dédiée), et c'est donc elle qui décide si le
    /// modèle puissant tiendra.
    /// </summary>
    public static IReadOnlyList<Carte> Cartes()
    {
        var cartes = new List<Carte>();

        try
        {
            using var classe = Registry.LocalMachine.OpenSubKey(ClasseAffichage);
            if (classe is null) return cartes;

            foreach (var nomSousCle in classe.GetSubKeyNames())
            {
                // les sous-clés d'un adaptateur sont numérotées « 0000 », « 0001 »… ;
                // « Configuration » et « Properties » n'en sont pas
                if (nomSousCle.Length != 4 || !nomSousCle.All(char.IsDigit)) continue;

                using var cle = classe.OpenSubKey(nomSousCle);
                if (cle?.GetValue("DriverDesc") is not string nom || string.IsNullOrWhiteSpace(nom))
                    continue;

                cartes.Add(new Carte(nom.Trim(), MemoireDe(cle)));
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException
                                      or IOException)
        {
            // registre inaccessible : on ne sait pas, et on le dira comme tel
        }

        return cartes
            .OrderByDescending(c => c.MemoireGo ?? 0)
            .ToList();
    }

    /// <summary>La carte qui décide, ou null si le registre n'a rien donné.</summary>
    public static Carte? Principale() => Cartes().FirstOrDefault();

    private static double? MemoireDe(RegistryKey cle)
    {
        // le pilote peut la publier en QWORD ou en binaire de huit octets, selon les
        // fabricants ; les deux formes se rencontrent sur des postes réels
        var brut = cle.GetValue("HardwareInformation.qwMemorySize");

        var octets = brut switch
        {
            long valeur => valeur,
            int valeur => valeur,
            byte[] { Length: 8 } tableau => BitConverter.ToInt64(tableau, 0),
            _ => 0L,
        };

        if (octets <= 0) return null;

        return octets / 1024.0 / 1024.0 / 1024.0;
    }
}
