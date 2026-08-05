using ImageMagick;
using Studio.Imaging.Faces;

namespace Studio.Imaging;

/// <summary>
/// Enlève les yeux rouges du flash.
///
/// <b>Ce que c'est, physiquement.</b> Le flash éclaire le fond de l'œil à travers une
/// pupille grande ouverte, et la rétine — irriguée de sang — le renvoie en rouge vif. Le
/// défaut ne tient donc ni à l'exposition ni à la balance des blancs : aucun curseur ne le
/// rattrape, et c'est pour cela que DiLand en fait un outil à part.
///
/// <b>Où l'on a le droit de toucher.</b> Uniquement dans un petit disque autour de chaque
/// PUPILLE, trouvée par le détecteur de visages — jamais sur l'image entière. Une écharpe
/// rouge, une joue rosée, une bouche maquillée passent tous le test du « rouge dominant » ;
/// le seul rempart fiable est de ne regarder que là où un œil se trouve. C'est aussi ce qui
/// rend la correction sûre sur une photo de famille de six personnes.
///
/// <b>Comment.</b> Un pixel est « rouge d'œil » quand son canal rouge écrase largement les
/// deux autres. On ne le noircit pas : on ramène le rouge au niveau du vert et du bleu, ce
/// qui rend une pupille grise et non un trou noir — un trou noir se voit autant que le
/// rouge, et c'est le défaut des retouches faites à la va-vite.
/// </summary>
public static class YeuxRouges
{
    /// <summary>
    /// Le détecteur de visages, posé par l'application au démarrage.
    ///
    /// Il vit ici en statique plutôt qu'en paramètre parce que la correction est demandée
    /// depuis le PIPELINE (<see cref="ImageAdjuster"/>), qui reçoit une image et des
    /// réglages, et n'a aucun moyen de connaître le chemin du modèle ONNX. Même mécanique
    /// que <c>BiRefNetMatting.DossiersCherches</c>.
    ///
    /// Null = pas de modèle sur ce poste : la case reste sans effet plutôt que de faire
    /// échouer un tirage.
    /// </summary>
    public static FaceDetector? Detecteur { get; set; }

    /// <summary>Journal optionnel, branché sur FileLog par l'application.</summary>
    public static Action<string>? Log { get; set; }

    /// <summary>
    /// Rayon du disque corrigé, en part de la LARGEUR DU VISAGE.
    ///
    /// YuNet donne le centre de l'œil, pas son contour. Un douzième de la largeur du visage
    /// couvre l'iris entier avec de la marge, sans atteindre le sourcil ni l'aile du nez —
    /// vérifié sur les portraits d'identité du poste, où le visage occupe presque tout le
    /// cadre.
    /// </summary>
    private const double RayonParLargeurDeVisage = 1.0 / 12;

    /// <summary>
    /// Au-delà de ce rapport rouge / (vert + bleu), le pixel est tenu pour un œil rouge.
    ///
    /// 1,5 est le seuil classique. Plus bas, une paupière rosée y passerait ; plus haut, un
    /// œil rouge un peu sombre resterait rouge. Le disque étant déjà restreint à la pupille,
    /// on peut se permettre d'être franc.
    /// </summary>
    private const double SeuilDeRouge = 1.5;

    /// <summary>En dessous, le pixel est trop sombre pour qu'un rouge y veuille dire quelque chose.</summary>
    private const int RougeMinimal = 60;

    /// <summary>
    /// Corrige les yeux rouges de l'image, en place.
    ///
    /// Ne lève jamais : une photo sans visage, un modèle absent, un détecteur qui refuse —
    /// tout cela laisse l'image telle quelle. Un tirage ne doit pas échouer parce qu'une
    /// case a été cochée.
    /// </summary>
    /// <returns>Vrai si au moins un pixel a été corrigé.</returns>
    public static bool Appliquer(IMagickImage<byte> image)
    {
        ArgumentNullException.ThrowIfNull(image);

        if (Detecteur is not { } detecteur) return false;

        try
        {
            var visages = detecteur.DetectAll(image);
            if (visages.Count == 0) return false;

            return Corriger(image, visages);
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Yeux rouges : correction impossible ({ex.Message}) — image inchangée.");
            return false;
        }
    }

    /// <summary>
    /// La correction proprement dite, séparée de la détection pour être vérifiable sans
    /// modèle ONNX ni visage réel.
    /// </summary>
    internal static bool Corriger(IMagickImage<byte> image, IReadOnlyList<DetectedFace> visages)
    {
        var largeur = (int)image.Width;
        var hauteur = (int)image.Height;
        if (largeur <= 0 || hauteur <= 0) return false;

        using var pixels = image.GetPixels();
        var octets = pixels.ToByteArray(PixelMapping.RGB);
        if (octets is null) return false;

        var touche = false;

        foreach (var visage in visages)
        {
            // le rayon suit la taille du visage : un visage lointain a des yeux minuscules
            var rayon = Math.Max(3, (int)Math.Round(visage.Box.Width * largeur * RayonParLargeurDeVisage));

            foreach (var oeil in visage.Eyes)
                touche |= NeutraliserLeDisque(
                    octets, largeur, hauteur,
                    (int)Math.Round(oeil.X * largeur), (int)Math.Round(oeil.Y * hauteur), rayon);
        }

        if (!touche) return false;

        pixels.SetPixels(octets);
        return true;
    }

    /// <summary>
    /// Ramène le rouge au niveau des deux autres canaux, dans le disque donné.
    ///
    /// Le disque est parcouru ligne par ligne, bornes calculées d'avance : un test
    /// d'appartenance par pixel de l'image entière coûterait le prix d'une passe complète
    /// pour quelques centaines de pixels utiles.
    /// </summary>
    private static bool NeutraliserLeDisque(
        byte[] rgb, int largeur, int hauteur, int cx, int cy, int rayon)
    {
        var rayonCarre = (long)rayon * rayon;
        var touche = false;

        var yDebut = Math.Max(0, cy - rayon);
        var yFin = Math.Min(hauteur - 1, cy + rayon);

        for (var y = yDebut; y <= yFin; y++)
        {
            var dy = y - cy;
            var demiLargeur = (int)Math.Sqrt(Math.Max(0, rayonCarre - (long)dy * dy));

            var xDebut = Math.Max(0, cx - demiLargeur);
            var xFin = Math.Min(largeur - 1, cx + demiLargeur);

            for (var x = xDebut; x <= xFin; x++)
            {
                var p = (y * largeur + x) * 3;

                int r = rgb[p];
                int v = rgb[p + 1];
                int b = rgb[p + 2];

                if (r < RougeMinimal) continue;
                if (r <= (v + b) / 2.0 * SeuilDeRouge) continue;

                // La pupille devient GRISE, pas noire : on remplace le rouge par la moyenne
                // des deux autres canaux. Un trou noir se remarque autant que le rouge.
                var neutre = (byte)((v + b) / 2);
                rgb[p] = neutre;
                touche = true;
            }
        }

        return touche;
    }
}
