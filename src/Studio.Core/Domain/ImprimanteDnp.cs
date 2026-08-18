namespace Studio.Core.Domain;

/// <summary>
/// Reconnaît une imprimante à sublimation DNP à son nom de file Windows.
///
/// Les modèles de la gamme commencent tous par « DP-DS », « DS6 », « DS8 » ou « QW » ;
/// c'est le nom que pose le pilote du constructeur, celui des boutiques étant « DP-DS620 ».
///
/// <b>Pourquoi la règle est ici et nulle part ailleurs.</b> Elle vivait recopiée à trois
/// endroits — la présence DiLand, le dialogue du pilote, et l'envoi direct de
/// l'orchestrateur — chacun avec sa propre liste de préfixes. Une gamme qui s'agrandit
/// aurait été ajoutée dans l'un et oubliée dans les autres : la machine aurait alors été
/// vue par l'écran d'état mais pas par l'envoi direct, ce qui ne se remarque qu'au tirage.
/// C'est la règle du dépôt — les BOUTONS se doublent, ce qu'ils font, non.
/// </summary>
public static class ImprimanteDnp
{
    private static readonly string[] Prefixes = ["DP-DS", "DS6", "DS8", "QW"];

    public static bool EstUneDnp(string? nomDeFile) =>
        !string.IsNullOrWhiteSpace(nomDeFile)
        && Prefixes.Any(p => nomDeFile.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}
