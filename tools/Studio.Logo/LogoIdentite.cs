using ImageMagick;
using ImageMagick.Drawing;

namespace Studio.Logo;

/// <summary>
/// Dessine le logo de Studio Photo Identité : une silhouette claire dans un cadre sarcelle,
/// aux proportions d'une photo d'identité, sur la tuile sombre de la famille.
///
/// <b>Pourquoi il en fallait un.</b> Les deux logiciels portaient la MÊME icône — Identité
/// pointait sur <c>..\Studio.App\Assets\studio-photo.ico</c>. Sur un poste qui a les deux
/// raccourcis, rien ne les distinguait : ni sur le bureau, ni dans la barre des tâches, ni
/// dans Alt-Tab. C'est aussi ce qui rend une bascule accidentelle possible, alors qu'un seul
/// des deux peut tourner à la fois (voir <c>UnSeulLogiciel</c>).
///
/// <b>Ce qui le rattache à la famille, et ce qui l'en distingue.</b> La tuile sombre aux
/// angles arrondis est la même — c'est la marque. Tout le reste change, et exprès :
/// le SARCELLE d'Identité au lieu du bleu et de l'orange du Studio, et un CADRE VERTICAL au
/// lieu de l'anneau rond de l'objectif. Deux formes qui ne se confondent pas d'un coup d'œil,
/// même en 16 px.
///
/// <b>Le cadre est au rapport 35 × 45</b>, celui de la norme française. Personne ne le
/// mesurera, mais c'est ce qui lui donne sa silhouette juste : une photo d'identité n'a pas
/// les proportions d'un tirage ordinaire, et l'œil le sait sans le nommer.
///
/// Comme <see cref="Logo"/>, tout est en fractions du côté : le 16 px est exactement le
/// 256 px en plus petit, au lieu de six dessins qui ne se ressemblent pas.
/// </summary>
public static class LogoIdentite
{
    /// <summary>La tuile de la famille, partagée avec <see cref="Logo"/>.</summary>
    private static readonly MagickColor Fond = new("#12181E");

    /// <summary>Le sarcelle d'Identité, version lumineuse — celle qui tient sur du sombre.</summary>
    private static readonly MagickColor Sarcelle = new("#26BCC2");

    /// <summary>La silhouette : le clair de la palette d'Identité.</summary>
    private static readonly MagickColor Clair = new("#E7ECF1");

    /// <summary>Écrit l'icône Windows multi-définitions et un aperçu en PNG.</summary>
    public static void Ecrire(string cheminIco, string cheminPng)
    {
        using (var grand = Dessiner(512))
            grand.Write(cheminPng);

        // De 256 à 16 : les tailles que Windows va chercher selon l'endroit — bureau,
        // barre des tâches, explorateur en liste, coin de la fenêtre.
        using var icone = new MagickImageCollection();
        foreach (var cote in new[] { 256u, 128u, 64u, 48u, 32u, 16u })
        {
            // dessiné en grand puis réduit : le trait du cadre tracé directement à 16 px
            // deviendrait un pâté, alors que réduit il reste un trait
            var image = Dessiner(cote * 4);
            image.Resize(cote, cote);
            image.Format = MagickFormat.Png32;
            icone.Add(image);
        }

        icone.Write(cheminIco, MagickFormat.Ico);
    }

    /// <param name="cote">Côté de l'image carrée, en pixels.</param>
    /// <param name="avecTuile">
    /// La tuile sombre aux angles arrondis. Vraie pour l'icône, où elle sépare le logo du
    /// bureau ; fausse si l'on veut le motif seul, à poser sur autre chose.
    /// </param>
    internal static MagickImage Dessiner(uint cote, bool avecTuile = true)
    {
        var image = new MagickImage(MagickColors.Transparent, cote, cote);
        double c = cote;
        var centre = c / 2;

        var dessin = new Drawables();

        if (avecTuile)
            dessin.FillColor(Fond)
                .StrokeColor(MagickColors.Transparent)
                .RoundRectangle(0, 0, c - 1, c - 1, c * 0.22, c * 0.22);

        // LE CADRE, au rapport 35 × 45 de la norme française.
        var hauteur = c * 0.64;
        var largeur = hauteur * 35.0 / 45.0;
        var trait = c * 0.052;

        var gauche = centre - largeur / 2;
        var droite = centre + largeur / 2;
        var haut = centre - hauteur / 2;
        var bas = centre + hauteur / 2;

        dessin.FillColor(MagickColors.Transparent)
            .StrokeColor(Sarcelle)
            .StrokeWidth(trait)
            .RoundRectangle(gauche, haut, droite, bas, c * 0.045, c * 0.045);

        // LA SILHOUETTE : tête et épaules, le seul motif qui survive à la réduction.
        //
        // Les épaules sont un DEMI-disque (180° → 360°) et non un disque entier : sa base
        // plate se range juste sous le bord bas du cadre, de sorte que le buste semble
        // sortir du bas de l'image comme sur une vraie photo d'identité.
        dessin.FillColor(Clair).StrokeColor(MagickColors.Transparent).StrokeWidth(0);

        // La tête est GRANDE, et c'est la norme qui le veut : 32 à 36 mm de menton au
        // sommet du crâne pour 45 mm de photo. Une petite tête perdue au milieu du cadre
        // ferait un avatar d'application, pas une photo d'identité.
        var tete = c * 0.115;
        dessin.Circle(centre, centre - c * 0.075, centre + tete, centre - c * 0.075);

        // La base plate du demi-disque se pose EXACTEMENT sur le bord intérieur du cadre :
        // un pixel plus bas et le buste barre le trait sarcelle, ce qui se voit dès 32 px.
        var interieurBas = bas - trait / 2;
        dessin.Ellipse(centre, interieurBas, c * 0.20, c * 0.215, 180, 360);

        image.Draw(dessin);
        return image;
    }
}
