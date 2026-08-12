using System.Globalization;
using System.Text.RegularExpressions;

namespace Studio.Core.Catalog;

/// <summary>
/// Le rapprochement entre le NOM d'un produit et ses COTES, pour attraper la saisie en
/// centimètres.
///
/// <b>Le tirage de quatre centimètres.</b> Le 08/08/2026, un poste équipé d'un traceur
/// grand format a sorti un « 40×50 » en 4 × 5 cm. Rien n'était cassé : le produit était
/// réglé sur 40 × 50 <b>millimètres</b>, et l'application a imprimé exactement ce qu'on lui
/// demandait — le journal l'écrivait noir sur blanc, « demandé 40×50 mm ».
///
/// <b>Pourquoi c'est un piège et non une étourderie.</b> Les noms du métier sont en
/// centimètres — 10×15, 30×40 — et les cotes en millimètres, parce qu'un « 10×15 » ne fait
/// pas 100 × 150 mm mais <b>102 × 152</b>, et que la planche d'identité tombe sur 156,1 mm.
/// Lire « 40x50 » sur une fiche et saisir 40 puis 50 est le raisonnement le plus naturel du
/// monde, et rien ne l'arrêtait.
///
/// On ne change donc pas l'unité — le centimètre ferait saisir 15,61 et déplacerait le
/// piège — on rapproche le nom des cotes, et l'on ne parle que d'un rapport de DIX exact.
/// Aucun faux positif : « 10x15 » mesure 10,2 × 15,2 cm et colle à son nom, « 20x30 »
/// mesure 20,3 × 30,7 et colle aussi.
/// </summary>
public static class CotesProduit
{
    /// <summary>Un « 40x50 », « 13X18 », « 21x29,7 » dans un nom ou un code.</summary>
    private static readonly Regex Format = new(
        @"(\d{1,3}(?:[.,]\d)?)\s*[xX×]\s*(\d{1,3}(?:[.,]\d)?)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Les cotes que l'opérateur voulait sans doute, s'il a saisi des centimètres — ou
    /// <c>null</c> quand tout est cohérent.
    ///
    /// La comparaison se fait dans LES DEUX SENS : un produit nommé « 40x50 » peut être
    /// saisi en portrait comme en paysage, et se tromper d'orientation n'est pas se
    /// tromper d'unité.
    /// </summary>
    /// <param name="nom">Le libellé du produit ; son code est essayé à défaut.</param>
    /// <param name="code">Le code du produit.</param>
    /// <param name="largeurMm">Largeur saisie, en millimètres.</param>
    /// <param name="hauteurMm">Hauteur saisie, en millimètres.</param>
    public static (double LargeurMm, double HauteurMm)? SiSaisiEnCentimetres(
        string? nom, string? code, double largeurMm, double hauteurMm)
    {
        if (largeurMm <= 0 || hauteurMm <= 0) return null;

        foreach (var etiquette in new[] { nom, code })
        {
            if (LireLeFormat(etiquette) is not { } attendu) continue;

            // Le nom colle déjà aux cotes, lues en centimètres : rien à signaler. C'est le
            // cas de tout le catalogue de la boutique, et ce test passe AVANT l'autre —
            // sans quoi un hypothétique « 1x2 » de 1 × 2 mm lèverait les deux.
            if (Correspond(attendu, largeurMm / 10, hauteurMm / 10)) return null;

            // Les cotes valent, en MILLIMÈTRES, ce que le nom annonce en centimètres :
            // c'est le rapport de dix, et il ne s'invente pas.
            if (Correspond(attendu, largeurMm, hauteurMm))
                return (largeurMm * 10, hauteurMm * 10);
        }

        return null;
    }

    private static (double A, double B)? LireLeFormat(string? etiquette)
    {
        if (string.IsNullOrWhiteSpace(etiquette)) return null;

        var trouve = Format.Match(etiquette);
        if (!trouve.Success) return null;

        return Nombre(trouve.Groups[1].Value) is { } a && Nombre(trouve.Groups[2].Value) is { } b
            ? (a, b)
            : null;
    }

    private static double? Nombre(string valeur) =>
        double.TryParse(valeur.Replace(',', '.'), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>
    /// Une tolérance d'un centimètre, dans les deux sens : « 20x30 » mesure réellement
    /// 20,3 × 30,7 cm, et « 13x18 » 12,7 × 18. Ces écarts sont la norme, pas l'exception.
    /// </summary>
    private static bool Correspond((double A, double B) attendu, double x, double y) =>
        (Proche(attendu.A, x) && Proche(attendu.B, y))
        || (Proche(attendu.A, y) && Proche(attendu.B, x));

    private static bool Proche(double a, double b) => Math.Abs(a - b) <= 1.0;

    /// <summary>
    /// Petit côté au-dessous duquel un produit ne peut pas être un papier, en millimètres.
    ///
    /// Le plus petit du catalogue de la boutique est le 8×10, qui mesure <b>80 × 102</b>.
    /// Le seuil est posé bien en dessous : il ne s'agit pas de juger un format, seulement
    /// d'attraper l'ordre de grandeur d'une saisie en centimètres — 40 × 50 ou 10 × 15
    /// millimètres, soit un timbre.
    ///
    /// Les cases d'une planche d'identité (35 × 45) ne sont PAS concernées : ce sont des
    /// cellules (<c>SheetCellWidthMm</c>), pas des produits.
    /// </summary>
    public const double PlusPetitPapierMm = 50;

    /// <summary>
    /// Ce qui cloche dans les cotes de ce produit, en une phrase — ou <c>null</c> si tout
    /// va bien.
    ///
    /// <b>Pourquoi ce second point d'entrée.</b> <see cref="SiSaisiEnCentimetres"/> ne
    /// parle que si le NOM porte un format : c'est ce qui lui donne sa précision, et c'est
    /// aussi sa limite. Le poste DESKTOP-KT88VDM avait deux produits en centimètres, et un
    /// seul était nommé « 40x50 » — l'autre s'appelait « E-PHOTO » et mesurait 10 × 15 mm.
    /// Aucun format dans le libellé, donc rien à rapprocher, et il est passé au travers
    /// pendant des semaines (constaté le 12/08/2026).
    ///
    /// On ajoute donc un filet qui ne demande aucun nom : <b>un papier ne fait pas cinq
    /// centimètres</b>. Les deux règles se complètent — la première dit ce qu'il fallait
    /// saisir, la seconde attrape ce qu'aucun nom ne trahissait.
    /// </summary>
    /// <remarks>
    /// Ne corrige RIEN de lui-même. Un catalogue est le travail de l'exploitant : on le
    /// signale, il tranche. Une correction automatique sur des cotes changerait ce qui sort
    /// du papier sans que personne l'ait demandé.
    /// </remarks>
    public static string? Anomalie(string? nom, string? code, double largeurMm, double hauteurMm)
    {
        var libelle = string.IsNullOrWhiteSpace(nom) ? code : nom;

        if (SiSaisiEnCentimetres(nom, code, largeurMm, hauteurMm) is { } voulu)
            return $"« {libelle} » mesure {largeurMm:0.#} × {hauteurMm:0.#} mm, " +
                   $"soit ce que son nom annonce en CENTIMÈTRES — il devrait sans doute " +
                   $"mesurer {voulu.LargeurMm:0.#} × {voulu.HauteurMm:0.#} mm.";

        if (largeurMm > 0 && hauteurMm > 0 && Math.Min(largeurMm, hauteurMm) < PlusPetitPapierMm)
            return $"« {libelle} » mesure {largeurMm:0.#} × {hauteurMm:0.#} mm : " +
                   "aucun papier ne fait cette taille. Des centimètres ont sans doute été " +
                   "saisis à la place des millimètres.";

        return null;
    }
}
