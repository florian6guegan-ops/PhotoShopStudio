using System.Text.Json;
using System.Text.Json.Serialization;
using Studio.Core.Domain;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// L'historique des trente jours de Studio Photo Identité.
///
/// Ce que ces essais tiennent :
///
/// - <b>une photo imprimée PUIS envoyée ne fait qu'UNE entrée</b>, avec les deux pastilles :
///   le client n'a fait faire qu'une photo ;
/// - <b>le travail revient ENTIER</b> — cadrage, repères de crâne et de menton, fond blanc,
///   corrections. C'est toute la raison d'être de l'historique : ne pas remettre le fond ;
/// - <b>trente jours, et la photo s'efface d'elle-même.</b> Ce sont des photos de clients ;
/// - <b>seules les photos FAITES y entrent</b> — imprimées ou envoyées, jamais celles qu'on
///   a seulement ouvertes. Tranché par l'exploitant le 19/08/2026.
/// </summary>
public class HistoriqueIdentiteTests : IDisposable
{
    private readonly string _dossier =
        Path.Combine(Path.GetTempPath(), "Historique-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* au mieux */ }
        GC.SuppressFinalize(this);
    }

    private HistoriqueIdentite Historique() => new(_dossier);

    private static PhotoFaite Photo(string fichier = @"D:\cache\travail\20260819\IMG_1234-a1b2c3d4.jpg",
        DateTimeOffset? quand = null)
    {
        var moment = quand ?? DateTimeOffset.Now;

        return new PhotoFaite
        {
            Cle = PhotoFaite.CleDe(fichier, moment),
            FaiteLe = moment,
            ModifieeLe = moment,
            NomDuFichier = "IMG_1234.jpg",
            Chemin = fichier,
            Imprimee = true,
            Commande = "19-003",
            Resume = "France · 35×45 · 6 photos",
            Travail = new TravailEnAttente
            {
                PhotosDirectory = Path.GetDirectoryName(fichier)!,
                AvecSousDossiers = false,
                Titre = "Identité 35×45",
                Identite = new IdentiteEnAttente
                {
                    Country = "France",
                    Document = "Passeport",
                    WidthMm = 35,
                    HeightMm = 45,
                    HeadMinMm = 32,
                    HeadMaxMm = 36,
                    Chemins = [fichier],
                    PhotoCourante = Path.GetFileName(fichier),
                    Photos =
                    [
                        new PhotoIdentiteEnAttente
                        {
                            FileName = Path.GetFileName(fichier),
                            Selected = true,
                            Quantity = 1,
                            Copies = 6,
                            Prete = true,
                            CropX = 0.1, CropY = 0.2, CropWidth = 0.5, CropHeight = 0.6,
                            CrownX = 0.5, CrownY = 0.25,
                            ChinX = 0.5, ChinY = 0.62,
                            AxeVisage = 0.51,
                            Redressement = 1.5,
                            FondBlanc = true,
                            Corrections = new ImageAdjustments { Exposure = 0.2, AutoLevels = true },
                        },
                    ],
                },
            },
        };
    }

    [Fact]
    public void Le_travail_revient_entier()
    {
        var historique = Historique();
        historique.Noter(Photo());

        var relue = historique.Lister().Single();
        var photo = relue.Travail.Identite!.Photos.Single();

        Assert.Equal("IMG_1234.jpg", relue.NomDuFichier);
        Assert.Equal("France", relue.Travail.Identite.Country);
        Assert.Equal(35, relue.Travail.Identite.WidthMm);

        // le cadrage
        Assert.Equal(0.1, photo.CropX, 3);
        Assert.Equal(0.6, photo.CropHeight, 3);

        // ⚠ LES REPÈRES : c'est ce que les commandes ne gardent PAS, et la raison même
        // pour laquelle l'historique est un journal à part. Sans eux, la photo rouverte
        // relance la détection de visage et écrase le placement manuel.
        Assert.Equal(0.25, photo.CrownY!.Value, 3);
        Assert.Equal(0.62, photo.ChinY!.Value, 3);
        Assert.Equal(0.51, photo.AxeVisage, 3);
        Assert.True(photo.Prete);

        // le fond et les corrections — « ne pas avoir à remettre le fond »
        Assert.True(photo.FondBlanc);
        Assert.True(photo.Corrections.AutoLevels);
        Assert.Equal(0.2, photo.Corrections.Exposure, 3);

        Assert.Equal(1.5, photo.Redressement, 3);
        Assert.Equal(6, photo.Copies);
    }

    [Fact]
    public void Imprimee_puis_envoyee_ne_fait_quune_entree_avec_les_deux_pastilles()
    {
        var historique = Historique();

        historique.Noter(Photo());

        // le client repart avec sa planche, puis demande le fichier par courriel — l'écran
        // note la MÊME photo, cette fois envoyée, et l'opérateur a remonté la planche à 8
        var envoyee = Photo();
        envoyee.Imprimee = false;
        envoyee.Envoyee = true;
        envoyee.Commande = null;
        envoyee.Travail.Identite!.Photos[0].Copies = 8;
        historique.Noter(envoyee);

        var entree = Assert.Single(historique.Lister());
        Assert.Single(Directory.EnumerateFiles(_dossier, "*.json"));

        // les deux gestes, et le numéro de commande du premier n'est pas perdu
        Assert.True(entree.Imprimee);
        Assert.True(entree.Envoyee);
        Assert.Equal("🖨 ✉", entree.Pastille);
        Assert.Equal("19-003", entree.Commande);

        // le travail, lui, est le DERNIER : l'opérateur a pu recadrer entre les deux
        Assert.Equal(8, entree.Travail.Identite!.Photos[0].Copies);
    }

    [Fact]
    public void Le_meme_fichier_un_autre_jour_est_une_autre_photo()
    {
        var historique = Historique();

        historique.Noter(Photo(quand: DateTimeOffset.Now));
        historique.Noter(Photo(quand: DateTimeOffset.Now.AddDays(-1)));

        Assert.Equal(2, historique.Lister().Count);
    }

    [Fact]
    public void Passe_trente_jours_la_photo_seface()
    {
        var historique = Historique();
        historique.Noter(Photo());

        Vieillir(TimeSpan.FromDays(31));

        Assert.Empty(historique.Lister());

        // et le fichier avec : ce sont des photos de clients
        Assert.Empty(Directory.EnumerateFiles(_dossier, "*.json"));
    }

    [Fact]
    public void A_vingt_neuf_jours_la_photo_est_encore_la()
    {
        var historique = Historique();
        historique.Noter(Photo());

        Vieillir(TimeSpan.FromDays(29));

        Assert.Single(historique.Lister());
    }

    [Fact]
    public void Un_fichier_abime_nemporte_pas_les_autres()
    {
        var historique = Historique();
        historique.Noter(Photo());

        File.WriteAllText(Path.Combine(_dossier, "abime.json"), "{ ceci n'est pas du JSON");

        Assert.Single(historique.Lister());
    }

    [Fact]
    public void Le_premier_geste_garde_son_heure()
    {
        var historique = Historique();

        var matin = DateTimeOffset.Now.AddHours(-3);
        historique.Noter(Photo(quand: matin));

        // ⚠ la clé est celle du fichier ET DE LA JOURNÉE : la reprise de l'après-midi doit
        // tomber sur la même entrée, et c'est l'heure de la PLANCHE qui reste
        var reprise = Photo(quand: matin);
        reprise.Envoyee = true;
        historique.Noter(reprise);

        var entree = Assert.Single(historique.Lister());
        Assert.Equal(matin.ToUnixTimeSeconds(), entree.FaiteLe.ToUnixTimeSeconds());
        Assert.True(entree.ModifieeLe > entree.FaiteLe);
    }

    [Fact]
    public void La_plus_recemment_touchee_dabord()
    {
        var historique = Historique();

        historique.Noter(Photo(@"D:\cache\travail\20260819\ancienne.jpg"));
        historique.Noter(Photo(@"D:\cache\travail\20260819\recente.jpg"));

        // la première est reprise : elle repasse en tête, c'est celle qu'on rouvre
        Vieillir(TimeSpan.FromMinutes(5), "ancienne.jpg");

        Assert.Equal("ancienne.jpg",
            Path.GetFileName(historique.Lister()[0].Chemin));
    }

    /// <summary>
    /// Recule (ou avance) la date de dernier dépôt des entrées écrites, en réécrivant leur
    /// fichier : <see cref="HistoriqueIdentite.Noter"/> pose toujours l'instant présent, et
    /// c'est bien ce qu'on veut de lui.
    /// </summary>
    private void Vieillir(TimeSpan de, string? seulement = null)
    {
        // ⚠ LES MÊMES OPTIONS QUE LE MAGASIN, et il faut qu'elles le restent : il écrit ses
        // énumérations en toutes lettres (« Standard »), et les relire avec les options par
        // défaut de System.Text.Json — qui les attend en nombres — fait échouer la lecture
        // dès qu'une énumération entre dans la fiche. C'est arrivé à l'ajout du format vendu
        // sur la planche d'identité, le 20/08/2026.
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };

        foreach (var chemin in Directory.EnumerateFiles(_dossier, "*.json"))
        {
            var photo = JsonSerializer.Deserialize<PhotoFaite>(File.ReadAllText(chemin), options);
            if (photo is null) continue;
            if (seulement is not null &&
                !Path.GetFileName(photo.Chemin).Equals(seulement, StringComparison.OrdinalIgnoreCase))
                continue;

            photo.ModifieeLe = seulement is null
                ? photo.ModifieeLe - de
                : photo.ModifieeLe + de;

            File.WriteAllText(chemin, JsonSerializer.Serialize(photo, options));
        }
    }
}
