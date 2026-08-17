using System.IO;

namespace Studio.App.Infrastructure;

/// <summary>
/// Lequel des deux logiciels tourne.
///
/// <b>Le dépôt en porte deux, et ils partagent presque tout</b> : mêmes écrans, même racine
/// de données, même catalogue. Ce qui les distingue tient à l'EXÉCUTABLE, pas à un réglage
/// de poste — c'est déjà ce dont se sert l'écran des réglages pour choisir sa suite de
/// publications (<c>v1.5.19</c> contre <c>identite-v1.5.19</c>).
///
/// <b>À ne pas confondre avec le MODE du poste</b> (<c>AccueilStudio.EnIdentiteVerrouille</c>,
/// <c>mode.json</c>) : le Studio complet peut être verrouillé sur l'identité à Arcueil sans
/// être Studio Photo Identité pour autant. Les deux questions sont voisines et n'ont pas la
/// même réponse ; celle-ci répond à « quel programme est ouvert ».
/// </summary>
public static class Logiciel
{
    /// <summary>Le fichier exécutable qui tourne, nom seul.</summary>
    public static string Executable =>
        Path.GetFileName(Environment.ProcessPath) ?? "Studio.App.exe";

    /// <summary>Vrai dans Studio Photo Identité, faux dans le Studio complet.</summary>
    public static bool EstIdentite =>
        Executable.Equals("Studio.Identite.exe", StringComparison.OrdinalIgnoreCase);
}
