namespace Studio.Core.Domain;

/// <summary>
/// Les corrections qui ne portent QUE sur le sujet détouré, et jamais sur le fond.
///
/// <b>Pourquoi elles existent.</b> Sur une photo d'identité, le fond est imposé — uni,
/// clair, souvent reposé en blanc ou en gris par Studio lui-même. Tout ce qu'on fait pour
/// rattraper un visage sous-exposé le dégrade : remonter l'exposition grise le blanc,
/// pousser le contraste y fait apparaître l'ombre du sujet, et la saturation le teinte.
/// L'opérateur en était réduit à choisir entre un visage juste et un fond conforme.
///
/// Le sujet est détouré par le même réseau que les fonds blanc et gris
/// (<c>BiRefNetMatting</c>), déjà éprouvé au comptoir : il n'y a pas deux découpes
/// différentes dans l'application, et ce qui marche pour poser un fond marche pour
/// épargner celui-ci.
///
/// <b>Le jeu de réglages est volontairement plus court que celui du panneau principal.</b>
/// Température et teinte en sont absentes à dessein : appliquées au sujet seul, elles
/// décalent la peau par rapport au fond et se voient immédiatement sur un tirage
/// d'identité. Ce qui reste est ce qui sert vraiment — la lumière, le relief, l'éclat.
/// </summary>
public sealed class CorrectionsSujet
{
    /// <summary>
    /// Faux = rien de tout ceci ne s'applique, et l'image est corrigée comme avant.
    ///
    /// C'est une bascule et non une déduction depuis les curseurs : l'opérateur qui remet
    /// ses réglages à zéro tout en gardant la sélection affichée doit voir le fond
    /// redevenir normal sans que la sélection se perde.
    /// </summary>
    public bool Actif { get; set; }

    /// <summary>
    /// Élargit (positif) ou resserre (négatif) le contour de la sélection, en pixels de
    /// l'image traitée.
    ///
    /// Le détourage tombe rarement au pixel près sur des cheveux. Élargir d'un poil évite
    /// le liseré non corrigé qui trahit la retouche autour de la tête ; resserrer sert
    /// quand le réseau a mordu sur le fond.
    /// </summary>
    public double ContourPx { get; set; }

    /// <summary>
    /// Adoucit la transition entre sujet et fond, en pixels.
    ///
    /// Sans elle, une correction un peu franche se voit comme un découpage aux ciseaux :
    /// c'est la BORDURE qui trahit une retouche, bien plus que la correction elle-même.
    /// </summary>
    public double AdoucissementPx { get; set; } = 3;

    /// <summary>Exposition en diaphragmes, comme le réglage global.</summary>
    public double Exposure { get; set; }

    public double Contrast { get; set; }

    /// <summary>Ombres, −100..100 : c'est celui qui déniche un visage en contre-jour.</summary>
    public double Shadows { get; set; }

    /// <summary>Hautes lumières, −100..100 : récupère un front ou une joue brûlés au flash.</summary>
    public double Highlights { get; set; }

    public double Saturation { get; set; }

    /// <summary>Vibrance : n'intensifie que les couleurs ternes, et épargne les teints de peau.</summary>
    public double Vibrance { get; set; }

    /// <summary>Clarté : contraste local, donne du relief sans toucher aux couleurs.</summary>
    public double Clarity { get; set; }

    /// <summary>Netteté, 0..100.</summary>
    public double Sharpness { get; set; }

    /// <summary>
    /// Vrai quand aucun curseur ne demande quoi que ce soit. Le contour et l'adoucissement
    /// n'en font PAS partie : seuls, ils ne changent rien à l'image.
    /// </summary>
    public bool AucuneCorrection =>
        Exposure == 0 && Contrast == 0 && Shadows == 0 && Highlights == 0 &&
        Saturation == 0 && Vibrance == 0 && Clarity == 0 && Sharpness == 0;

    /// <summary>Vrai quand il n'y a rien à faire du tout : éteint, ou sans aucun réglage.</summary>
    public bool IsNeutral => !Actif || AucuneCorrection;

    public CorrectionsSujet Clone() => (CorrectionsSujet)MemberwiseClone();
}
