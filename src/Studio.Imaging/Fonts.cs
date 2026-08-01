using ImageMagick;

namespace Studio.Imaging;

/// <summary>
/// Choix de police pour les mentions portées sur un tirage.
///
/// Sans choix explicite, ImageMagick retombe sur une fonte interne minuscule, illisible
/// sur le papier. Il faut donc lui nommer une police installée — et la nommer une fois
/// pour toutes, l'énumération des familles coûtant cher.
/// </summary>
public static class Fonts
{
    private static string? _sansEmpattement;

    /// <summary>
    /// La première police sans empattement installée : plus lisible en petit corps qu'une
    /// police à empattements, et c'est en petit corps qu'on écrit sur un tirage.
    /// </summary>
    public static string SansEmpattement()
    {
        if (_sansEmpattement is not null) return _sansEmpattement;

        var installees = MagickNET.FontFamilies.ToList();
        _sansEmpattement = new[] { "Arial", "Segoe UI", "Tahoma", "Verdana", "Calibri" }
                               .FirstOrDefault(installees.Contains)
                           ?? installees.FirstOrDefault()
                           ?? "Arial";

        return _sansEmpattement;
    }
}
