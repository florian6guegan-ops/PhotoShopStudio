using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace Studio.App.Infrastructure;

/// <summary>
/// La retouche d'une photo dans un logiciel EXTÉRIEUR — Photoshop, GIMP —, et son retour
/// dans Studio dès qu'elle est enregistrée.
///
/// <b>Aucune API, aucun greffon.</b> C'est ce qui rend la chose possible là où
/// l'intégration générative avait échoué : l'abonnement Photoshop ne donne pas accès à son
/// API, et écrire une extension demanderait de la faire vivre à chaque version. Ici on ne
/// parle pas à Photoshop du tout — on lui donne un fichier, et on regarde ce fichier
/// changer sur le disque. Un logiciel qui sait enregistrer un JPEG suffit, ce qui vaut
/// aussi pour GIMP.
///
/// <b>L'original n'est JAMAIS touché.</b> La photo travaillée est une copie posée dans les
/// données du poste. C'est la seule règle qui ne se négocie pas : le dossier d'origine est
/// souvent la carte mémoire du client, ou son dossier à lui, et une retouche écrite par
/// dessus serait irrattrapable — il repart avec la carte.
/// </summary>
internal static class RetoucheExterne
{
    /// <param name="Nom">Ce qu'on affiche à l'opérateur : « Photoshop », « GIMP ».</param>
    /// <param name="Chemin">L'exécutable à lancer.</param>
    internal sealed record Editeur(string Nom, string Chemin);

    /// <summary>
    /// Le logiciel de retouche installé sur ce poste, ou null s'il n'y en a pas.
    ///
    /// Photoshop d'abord — c'est celui de l'atelier —, GIMP ensuite. <b>On lance
    /// l'exécutable NOMMÉ et jamais l'association Windows</b> : sur ces postes, le
    /// double-clic sur un JPEG ouvre ImageGlass (installé pour lire les HEIC), et « ouvrir
    /// pour retoucher » se serait donc soldé par une visionneuse.
    /// </summary>
    /// <param name="configure">
    /// Chemin forcé dans les réglages du poste. Il l'emporte sur tout : c'est la porte de
    /// sortie quand une version s'installe ailleurs que là où on la cherche.
    /// </param>
    public static Editeur? Trouver(string? configure = null)
    {
        if (!string.IsNullOrWhiteSpace(configure) && File.Exists(configure))
            return new Editeur(Path.GetFileNameWithoutExtension(configure), configure);

        return Photoshop() ?? Gimp();
    }

    /// <summary>
    /// Photoshop, par le registre : Adobe y range le dossier d'installation de chaque
    /// version sous sa clef.
    ///
    /// La version la PLUS RÉCENTE gagne. Un poste d'atelier accumule les millésimes — celui
    /// d'ici porte la 2024 et la 2026 —, et laisser le hasard de l'ordre des clefs décider
    /// ouvrirait un jour l'une, un jour l'autre.
    /// </summary>
    private static Editeur? Photoshop()
    {
        foreach (var racine in (string[])["SOFTWARE\\Adobe\\Photoshop", "SOFTWARE\\WOW6432Node\\Adobe\\Photoshop"])
        {
            try
            {
                using var cle = Registry.LocalMachine.OpenSubKey(racine);
                if (cle is null) continue;

                var trouve = cle.GetSubKeyNames()
                    .Select(nom => (Version: LireLaVersion(nom), Nom: nom))
                    .Where(v => v.Version > 0)
                    .OrderByDescending(v => v.Version)
                    .Select(v => CheminDePhotoshop(cle, v.Nom))
                    .FirstOrDefault(c => c is not null);

                if (trouve is not null) return new Editeur("Photoshop", trouve);
            }
            catch (Exception ex)
            {
                // registre illisible : ce n'est pas une panne, on essaiera GIMP
                FileLog.Write($"Lecture du registre Photoshop impossible ({racine})", ex);
            }
        }

        return null;
    }

    /// <summary>La clef d'Adobe porte un numéro à virgule (« 200.0 ») ; 0 si ce n'en est pas un.</summary>
    private static double LireLaVersion(string nomDeClef) =>
        double.TryParse(nomDeClef, System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : 0;

    private static string? CheminDePhotoshop(RegistryKey racine, string version)
    {
        using var cle = racine.OpenSubKey(version);
        if (cle?.GetValue("ApplicationPath") is not string dossier || dossier.Length == 0) return null;

        var exe = Path.Combine(dossier, "Photoshop.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>
    /// GIMP, à sa place habituelle. Cherché APRÈS Photoshop, et gardé quand même : c'est le
    /// repli d'un poste sans licence Adobe.
    /// </summary>
    private static Editeur? Gimp()
    {
        foreach (var programmes in (string?[])
                 [
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 ])
        {
            if (string.IsNullOrEmpty(programmes) || !Directory.Exists(programmes)) continue;

            try
            {
                // « GIMP 3 », « GIMP 2 »… le plus récent d'abord, comme pour Photoshop
                foreach (var dossier in Directory.EnumerateDirectories(programmes, "GIMP*")
                             .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    var bin = Path.Combine(dossier, "bin");
                    if (!Directory.Exists(bin)) continue;

                    // le nom porte la version (gimp-3.2.exe) : on prend le dernier, et on
                    // écarte la console, qui n'ouvre aucune fenêtre
                    var exe = Directory.EnumerateFiles(bin, "gimp-*.exe")
                        .Where(f => !Path.GetFileName(f).Contains("console", StringComparison.OrdinalIgnoreCase)
                                    && !Path.GetFileName(f).Contains("debug", StringComparison.OrdinalIgnoreCase)
                                    && !Path.GetFileName(f).Contains("script", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (exe is not null) return new Editeur("GIMP", exe);
                }
            }
            catch (Exception ex)
            {
                FileLog.Write($"Recherche de GIMP impossible ({programmes})", ex);
            }
        }

        return null;
    }

    // ----- la copie de travail -----

    /// <summary>
    /// Copie la photo dans le dossier de retouche et rend le chemin de la COPIE.
    ///
    /// C'est cette copie qu'on ouvre, qu'on surveille et qu'on tire ensuite. L'original
    /// reste tel quel dans le dossier du client.
    /// </summary>
    public static string PreparerLaCopie(string original, string dossierDeRetouche)
    {
        Directory.CreateDirectory(dossierDeRetouche);

        var copie = Path.Combine(dossierDeRetouche, NomDeLaCopie(original,
            deja => File.Exists(Path.Combine(dossierDeRetouche, deja))));

        File.Copy(original, copie, overwrite: false);

        // une copie hérite de l'attribut « lecture seule » de la carte mémoire, et
        // Photoshop refuserait alors d'enregistrer — sans toujours dire pourquoi
        var infos = new FileInfo(copie);
        if (infos.IsReadOnly) infos.IsReadOnly = false;

        return copie;
    }

    /// <summary>
    /// Le nom de la copie : celui de l'original, suivi d'un numéro s'il est déjà pris.
    ///
    /// <b>Le nom d'origine est gardé</b>, parce que c'est lui que l'opérateur lit sur la
    /// planche index et que le client cite au comptoir. Un nom de la forme
    /// « retouche-4f2a…jpg » rendrait la photo méconnaissable à l'écran des tirages.
    ///
    /// L'extension ne change pas non plus : le logiciel de retouche enregistre alors dans
    /// le même format d'un simple Ctrl+S, sans boîte « Enregistrer sous » où l'on peut se
    /// tromper de dossier — et c'est ce fichier-là que Studio surveille.
    /// </summary>
    /// <param name="estPris">Vrai si ce nom existe déjà dans le dossier de retouche.</param>
    internal static string NomDeLaCopie(string original, Func<string, bool> estPris)
    {
        var racine = Path.GetFileNameWithoutExtension(original);
        var extension = Path.GetExtension(original);

        if (!estPris(racine + extension)) return racine + extension;

        for (var n = 2; n < 10_000; n++)
        {
            var essai = $"{racine} ({n}){extension}";
            if (!estPris(essai)) return essai;
        }

        // dix mille homonymes : on ne bloque pas le comptoir pour autant
        return $"{racine} ({Guid.NewGuid():N}){extension}";
    }

    /// <summary>
    /// Ouvre les fichiers dans le logiciel de retouche, tous d'un coup.
    ///
    /// Un seul lancement pour toute la sélection : Photoshop les ouvre en onglets, et
    /// l'opérateur passe de l'un à l'autre sans revenir dans Studio.
    /// </summary>
    public static void Ouvrir(Editeur editeur, IReadOnlyList<string> fichiers)
    {
        ArgumentNullException.ThrowIfNull(editeur);
        ArgumentNullException.ThrowIfNull(fichiers);
        if (fichiers.Count == 0) return;

        var demarrage = new ProcessStartInfo(editeur.Chemin) { UseShellExecute = false };
        foreach (var fichier in fichiers) demarrage.ArgumentList.Add(fichier);

        Process.Start(demarrage);
        FileLog.Write($"Retouche : {fichiers.Count} photo(s) ouverte(s) dans {editeur.Nom}.");
    }
}
