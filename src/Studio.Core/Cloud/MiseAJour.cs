using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Studio.Core.Cloud;

/// <summary>Une version publiée, telle que GitHub la décrit.</summary>
/// <param name="Version">Numéro de version, sans le « v » de l'étiquette.</param>
/// <param name="Titre">Nom de la publication, montré à l'opérateur.</param>
/// <param name="Notes">Ce que la version change, tel qu'on l'a écrit en publiant.</param>
/// <param name="Url">Adresse de l'archive à télécharger.</param>
/// <param name="Octets">Taille de l'archive, pour l'annoncer avant de la prendre.</param>
public sealed record VersionPubliee(
    Version Version, string Titre, string Notes, string Url, long Octets)
{
    public string TailleLisible => $"{Octets / (1024.0 * 1024):0.0} Mo";
}

/// <summary>
/// La mise à jour de l'application, prise sur les publications du dépôt.
///
/// <b>Pourquoi.</b> Le raccourci du bureau recompile depuis les sources : c'est le poste de
/// celui qui développe, et cela ne vaut que pour lui. Un collègue n'a ni le SDK, ni le
/// dépôt, ni l'envie de compiler — il lui faut une application qui se tienne à jour toute
/// seule, sinon chaque correction demanderait un déplacement ou un fichier à transmettre à
/// la main.
///
/// <b>Ce qui n'est JAMAIS fait automatiquement : l'installation.</b> On vérifie, on
/// annonce, l'opérateur décide. Une mise à jour qui s'installerait toute seule fermerait
/// l'application — peut-être au milieu d'une commande, devant un client. La vérification,
/// elle, ne coûte qu'une requête et ne dérange personne.
/// </summary>
public sealed class MiseAJour
{
    /// <summary>Dépôt d'où viennent les versions.</summary>
    public const string Depot = "florian6guegan-ops/PhotoShopStudio";

    /// <summary>
    /// Nom de l'archive attendue dans une publication.
    ///
    /// Reconnue par son EXTENSION et non par son nom exact : le numéro de version y figure,
    /// et un nom figé obligerait à le répéter à l'identique à chaque publication.
    /// </summary>
    private const string ExtensionArchive = ".zip";

    private readonly HttpClient _client;

    /// <param name="client">
    /// Client HTTP à employer. Fourni de l'extérieur pour que les essais n'aient pas
    /// besoin du réseau.
    /// </param>
    public MiseAJour(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;

        // GitHub refuse une requête sans agent, et le sien doit être stable : c'est ce
        // qu'on lira dans ses journaux le jour où le quota sera dépassé
        if (!_client.DefaultRequestHeaders.UserAgent.Any())
            _client.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue("StudioPhoto", "1.0"));

        _client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>Adresse de la dernière publication du dépôt.</summary>
    public static string UrlDerniereVersion =>
        $"https://api.github.com/repos/{Depot}/releases/latest";

    /// <summary>
    /// La version publiée, ou <c>null</c> s'il n'y en a pas d'exploitable.
    ///
    /// <b>Ne lève jamais pour une raison de réseau.</b> Un poste hors ligne, un dépôt
    /// injoignable, un quota dépassé : ce sont des circonstances ordinaires, et aucune ne
    /// doit empêcher de travailler. On rend <c>null</c> et l'application continue.
    /// </summary>
    public async Task<VersionPubliee?> DernierePubliee(CancellationToken ct = default)
    {
        try
        {
            using var reponse = await _client.GetAsync(UrlDerniereVersion, ct);
            if (!reponse.IsSuccessStatusCode) return null;

            var json = await reponse.Content.ReadAsStringAsync(ct);
            return Lire(json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Analyse la réponse de GitHub. Séparée de l'appel réseau pour être vérifiable sans
    /// lui.
    /// </summary>
    public static VersionPubliee? Lire(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            var racine = document.RootElement;

            // un brouillon ou une préversion n'est pas destiné aux postes de la boutique
            if (Vrai(racine, "draft") || Vrai(racine, "prerelease")) return null;

            var etiquette = Texte(racine, "tag_name");
            if (LireLaVersion(etiquette) is not { } version) return null;

            if (!racine.TryGetProperty("assets", out var pieces)
                || pieces.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var piece in pieces.EnumerateArray())
            {
                var nom = Texte(piece, "name");
                if (!nom.EndsWith(ExtensionArchive, StringComparison.OrdinalIgnoreCase)) continue;

                var url = Texte(piece, "browser_download_url");
                if (url.Length == 0) continue;

                var octets = piece.TryGetProperty("size", out var taille)
                    && taille.TryGetInt64(out var valeur) ? valeur : 0;

                return new VersionPubliee(
                    version,
                    Texte(racine, "name") is { Length: > 0 } titre ? titre : etiquette,
                    Texte(racine, "body"),
                    url,
                    octets);
            }

            // publication sans archive : rien à installer, et le dire vaut mieux que de
            // proposer une mise à jour qui échouerait au téléchargement
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Le numéro porté par une étiquette. Accepte « v1.2.3 » comme « 1.2.3 » : les deux
    /// conventions se croisent, et l'on ne va pas faire échouer une mise à jour sur un « v ».
    /// </summary>
    public static Version? LireLaVersion(string etiquette)
    {
        if (string.IsNullOrWhiteSpace(etiquette)) return null;

        var nettoyee = etiquette.Trim().TrimStart('v', 'V');
        return Version.TryParse(nettoyee, out var version) ? version : null;
    }

    /// <summary>
    /// Y a-t-il quelque chose de plus récent que ce qui tourne ?
    ///
    /// <b>Strictement supérieur</b> : republier la même version — parce qu'on a corrigé la
    /// description, par exemple — ne doit pas proposer une réinstallation à tous les postes.
    /// </summary>
    public static bool EstPlusRecente(Version publiee, Version installee) =>
        publiee > installee;

    /// <summary>
    /// Télécharge l'archive d'une version dans un dossier de travail, et rend son chemin.
    /// </summary>
    public async Task<string> Telecharger(
        VersionPubliee version, string dossierTravail, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        Directory.CreateDirectory(dossierTravail);
        var archive = Path.Combine(dossierTravail, $"studio-{version.Version}.zip");

        using (var flux = await _client.GetStreamAsync(version.Url, ct))
        using (var fichier = File.Create(archive))
        {
            await flux.CopyToAsync(fichier, ct);
        }

        return archive;
    }

    /// <summary>
    /// Prépare l'installation : l'archive est extraite à côté, et un script est écrit pour
    /// remplacer l'application une fois qu'elle sera fermée.
    ///
    /// <b>Pourquoi un script et non une copie directe.</b> Windows verrouille les fichiers
    /// d'un programme qui tourne : l'application ne peut pas se remplacer elle-même. Le
    /// script attend qu'elle se termine, recopie, puis la relance — c'est la seule façon
    /// d'y arriver sans installateur.
    /// </summary>
    /// <param name="archive">L'archive téléchargée.</param>
    /// <param name="dossierInstalle">Où l'application est installée.</param>
    /// <param name="executable">L'exécutable à relancer.</param>
    /// <returns>Le chemin du script à lancer pour terminer l'installation.</returns>
    public static string PreparerLInstallation(
        string archive, string dossierInstalle, string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(dossierInstalle);

        var travail = Path.GetDirectoryName(archive)!;
        var extrait = Path.Combine(travail, "nouvelle-version");

        if (Directory.Exists(extrait)) Directory.Delete(extrait, recursive: true);
        ZipFile.ExtractToDirectory(archive, extrait);

        // Certaines archives portent un dossier unique à leur racine : on descend dedans,
        // sinon la copie recréerait ce dossier dans l'installation au lieu de la remplacer.
        var source = Directory.GetFiles(extrait).Length == 0
                     && Directory.GetDirectories(extrait) is { Length: 1 } seul
            ? seul[0]
            : extrait;

        var script = Path.Combine(travail, "installer-maj.cmd");
        File.WriteAllText(script, Script(source, dossierInstalle, executable), EncodageDeCmd);

        return script;
    }

    /// <summary>
    /// La page de codes dans laquelle <c>cmd.exe</c> relit un <c>.cmd</c> — jamais UTF-8.
    ///
    /// Le corps du script est écrit sans accent, mais les CHEMINS n'ont pas ce luxe : ils
    /// portent le nom du compte Windows. Un poste ouvert sous « PhotoConcept Créteil » a vu
    /// son <c>é</c> écrit en UTF-8 (<c>C3 A9</c>) puis relu en page 850, où ces deux octets
    /// valent <c>├╣</c> : robocopy cherchait un dossier qui n'existe pas, et le bouton
    /// « Mettre à jour » ne faisait rien de visible (10/08/2026).
    ///
    /// La page OEM reste l'exception : tout le reste du logiciel écrit en UTF-8.
    /// </summary>
    private static Encoding EncodageDeCmd
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException)
            {
                // page introuvable : UTF-8 échouera peut-être, mais ne masque rien de plus
                return Encoding.UTF8;
            }
        }
    }

    /// <summary>
    /// Le script d'installation. Sans accent : PowerShell et cmd lisent les .cmd dans la
    /// page de codes du système, et les accents y ressortent en charabia. Les chemins, eux,
    /// ne se choisissent pas — c'est <see cref="EncodageDeCmd"/> qui les sauve.
    /// </summary>
    private static string Script(string source, string destination, string executable) =>
        $"""
        @echo off
        rem Termine l'installation d'une mise a jour de Studio Photo.
        rem Ecrit par l'application : ne pas modifier a la main.

        rem On attend que l'application soit VRAIMENT fermee. Elle vient de demander sa
        rem propre fermeture, mais Windows ne libere pas ses fichiers instantanement.
        :attendre
        tasklist /FI "IMAGENAME eq Studio.App.exe" 2>nul | find /I "Studio.App.exe" >nul
        if not errorlevel 1 (
            timeout /t 1 /nobreak >nul
            goto :attendre
        )

        rem Le relais du minilab tient les memes DLL : il doit partir aussi.
        taskkill /IM Studio.De100Host.exe /F >nul 2>&1
        timeout /t 1 /nobreak >nul

        rem /E tous les sous-dossiers, /R:2 deux essais sur un fichier encore tenu.
        rem On ne PURGE pas la destination : les donnees du poste n'y sont pas, mais un
        rem profil ICC ou un DEVMODE depose a la main s'y trouve peut-etre.
        robocopy "{source}" "{destination}" /E /R:2 /W:1 /NFL /NDL /NJH /NJS >nul

        rem robocopy rend 0 a 7 en cas de succes ; 8 et au-dela sont des echecs.
        if errorlevel 8 (
            echo.
            echo   La mise a jour n'a pas pu etre installee.
            echo   L'application precedente est intacte : relancez-la normalement.
            echo.
            pause
            exit /b 1
        )

        start "" "{executable}"
        exit /b 0
        """;

    private static string Texte(JsonElement element, string nom) =>
        element.TryGetProperty(nom, out var valeur) && valeur.ValueKind == JsonValueKind.String
            ? valeur.GetString() ?? ""
            : "";

    private static bool Vrai(JsonElement element, string nom) =>
        element.TryGetProperty(nom, out var valeur) && valeur.ValueKind == JsonValueKind.True;
}
