namespace Studio.App.Infrastructure;

/// <summary>
/// L'habillage du poste, quand l'application hôte sait en changer.
///
/// Studio Photo Identité porte deux palettes — claire et sombre — et sait les échanger sans
/// redémarrer. Le Studio complet n'en a qu'une. L'écran des réglages est partagé : il doit
/// donc pouvoir DEMANDER le changement sans connaître celui qui l'exécute, et ne montrer la
/// case que là où elle veut dire quelque chose.
///
/// Null = l'hôte ne sait pas changer de palette ; la case ne s'affiche pas.
/// </summary>
public static class Habillage
{
    /// <summary>Posée au démarrage par l'application qui sait échanger ses palettes.</summary>
    public static Action<bool>? Appliquer { get; set; }

    /// <summary>Vrai quand le poste peut basculer entre clair et sombre.</summary>
    public static bool EstReglable => Appliquer is not null;
}
