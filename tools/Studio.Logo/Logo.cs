using ImageMagick;
using ImageMagick.Drawing;

namespace Studio.Logo;

/// <summary>
/// Dessine le logo de Studio Photo : un diaphragme orange dans un anneau bleu, sur la
/// tuile sombre de l'application.
///
/// Il est DESSINÉ et non peint à la main pour une raison simple : une icône Windows tient
/// six définitions, de 256 px à 16 px, et redessiner chacune à la main donne six dessins
/// qui ne se ressemblent pas. Ici tout est en fractions du côté, donc le 16 px est
/// exactement le 256 px en plus petit.
/// </summary>
public static class Logo
{
    private static readonly MagickColor Fond = new("#12181E");
    private static readonly MagickColor Bleu = new("#2C9BD6");
    private static readonly MagickColor Orange = new("#F0932B");

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
            // dessiné en grand puis réduit : les traits fins d'un diaphragme tracés
            // directement à 16 px disparaissent, alors que réduits ils se voient encore
            var image = Dessiner(cote * 4);
            image.Resize(cote, cote);
            image.Format = MagickFormat.Png32;
            icone.Add(image);
        }

        icone.Write(cheminIco, MagickFormat.Ico);
    }

    /// <param name="cote">Côté de l'image carrée, en pixels.</param>
    /// <param name="rotationDegres">
    /// Rotation des lames du diaphragme. Le reste ne bouge pas : c'est ce qui donne le
    /// curseur d'attente, où seul le mécanisme tourne dans sa monture.
    /// </param>
    /// <param name="avecTuile">
    /// La tuile sombre aux angles arrondis. Vraie pour l'icône, où elle sépare le logo du
    /// bureau ; fausse pour le curseur, où un carré opaque de 32 px promené sur l'écran
    /// masquerait ce qu'on est en train de regarder.
    /// </param>
    internal static MagickImage Dessiner(uint cote, double rotationDegres = 0, bool avecTuile = true)
    {
        var image = new MagickImage(MagickColors.Transparent, cote, cote);
        double c = cote;
        var centre = c / 2;
        var rotation = rotationDegres * Math.PI / 180;

        var dessin = new Drawables();

        // la tuile : un carré aux angles arrondis, comme les icônes du système
        if (avecTuile)
            dessin.FillColor(Fond)
                .StrokeColor(MagickColors.Transparent)
                .RoundRectangle(0, 0, c - 1, c - 1, c * 0.22, c * 0.22);

        // l'anneau bleu : la monture de l'objectif
        dessin.FillColor(MagickColors.Transparent)
            .StrokeColor(Bleu)
            .StrokeWidth(c * 0.055)
            .Circle(centre, centre, centre + c * 0.345, centre);

        // Le diaphragme : un disque orange évidé d'un hexagone, puis six entailles sombres
        // qui donnent aux lames leur inclinaison. C'est ce que l'œil reconnaît d'un
        // diaphragme, et le seul détail qui survive à la réduction en 16 px.
        var rayon = c * 0.275;
        var trou = c * 0.115;

        dessin.FillColor(Orange).StrokeColor(MagickColors.Transparent)
            .Circle(centre, centre, centre + rayon, centre);

        var sommets = new List<PointD>();
        for (var i = 0; i < 6; i++)
        {
            var angle = Math.PI / 2 + i * Math.PI / 3 + rotation;
            sommets.Add(new PointD(
                centre + trou * Math.Cos(angle),
                centre - trou * Math.Sin(angle)));
        }

        dessin.FillColor(Fond).Polygon(sommets);

        // une entaille par lame : du sommet de l'hexagone vers le bord, en biais
        dessin.StrokeColor(Fond).StrokeWidth(c * 0.032).FillColor(MagickColors.Transparent);
        for (var i = 0; i < 6; i++)
        {
            var angle = Math.PI / 2 + i * Math.PI / 3 + rotation;
            var vers = angle + Math.PI / 3;

            dessin.Line(
                centre + trou * Math.Cos(angle), centre - trou * Math.Sin(angle),
                centre + rayon * 1.02 * Math.Cos(vers), centre - rayon * 1.02 * Math.Sin(vers));
        }

        image.Draw(dessin);
        return image;
    }
}
