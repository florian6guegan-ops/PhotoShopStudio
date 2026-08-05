using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using Studio.Core;

namespace Studio.Web;

/// <summary>Le réseau du magasin, tel qu'un téléphone doit le recevoir.</summary>
/// <param name="Ssid">Nom du réseau.</param>
/// <param name="Password">Clé ; vide pour un réseau ouvert.</param>
/// <param name="Security">« WPA », « WEP » ou « nopass ».</param>
/// <param name="Hidden">Réseau à SSID masqué : le téléphone doit le savoir pour s'y joindre.</param>
public sealed record WifiNetwork(string Ssid, string Password, string Security, bool Hidden = false);

/// <summary>
/// Le réseau du magasin, saisi à la main dans <c>config/wifi.json</c>.
///
/// <b>Pourquoi ce fichier existe alors que Windows connaît déjà le réseau.</b> Le poste de
/// l'atelier est une station de travail branchée en Ethernet : elle n'a pas de carte sans
/// fil, donc aucun profil WiFi à lire. Le code du magasin doit pourtant s'afficher — c'est
/// le téléphone du client qui se connecte, pas le poste. Rempli, ce fichier l'emporte sur
/// la lecture automatique.
/// </summary>
public sealed class WifiConfig
{
    /// <summary>Nom du réseau. Vide = on tente la lecture du profil Windows.</summary>
    public string Ssid { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>« WPA » (le cas courant), « WEP », ou « nopass » pour un réseau ouvert.</summary>
    public string Security { get; set; } = "WPA";

    /// <summary>Réseau à SSID masqué.</summary>
    public bool Hidden { get; set; }

    /// <summary>Le réseau décrit ici, ou null si le fichier n'a pas été rempli.</summary>
    public WifiNetwork? Network() => string.IsNullOrWhiteSpace(Ssid)
        ? null
        : new WifiNetwork(Ssid.Trim(), Password, Securite(), Hidden);

    private string Securite()
    {
        var demande = (Security ?? "").Trim();
        if (demande.Equals("nopass", StringComparison.OrdinalIgnoreCase)) return "nopass";
        if (demande.Equals("WEP", StringComparison.OrdinalIgnoreCase)) return "WEP";

        // tout le reste — « WPA », « WPA2 », « WPA3 », ou une faute de frappe — vaut WPA :
        // les téléphones négocient, et un réseau protégé annoncé « ouvert » échouerait
        return string.IsNullOrEmpty(Password) ? "nopass" : "WPA";
    }
}

/// <summary>
/// Le code QR qui connecte le téléphone du client au WiFi du magasin.
///
/// <b>Pourquoi il existe.</b> Le QR d'envoi de photos ne mène nulle part tant que le
/// téléphone est sur son réseau mobile : le serveur d'upload n'écoute que sur le réseau
/// local. Le client devait donc trouver le WiFi, le nom du réseau et la clé tout seul.
/// Deux codes numérotés — se connecter, puis envoyer — retirent cette étape.
///
/// <b>D'où viennent le nom et la clé.</b> Du profil WiFi que Windows garde déjà : le poste
/// est sur le réseau du magasin, il en connaît la clé, rien n'est à saisir ni à tenir à
/// jour. La lecture passe par l'EXPORT XML du profil (<c>netsh wlan export profile</c>) et
/// non par l'affichage texte : l'affichage est traduit, et « Contenu de la clé » deviendrait
/// « Key Content » sur un Windows anglais. Le XML, lui, ne l'est pas.
/// </summary>
public static class WifiQr
{
    /// <summary>
    /// Le réseau auquel ce poste est connecté, ou null.
    ///
    /// Null est un cas NORMAL, pas une erreur : poste en Ethernet, profil sans clé
    /// enregistrée, ou stratégie d'entreprise qui interdit de l'exporter. L'appelant
    /// masque alors le code, il n'affiche pas d'avertissement.
    /// </summary>
    public static WifiNetwork? Current()
    {
        try
        {
            var ssid = SsidConnecte();
            return string.IsNullOrEmpty(ssid) ? null : LireProfil(ssid);
        }
        catch (Exception)
        {
            // netsh absent, service WLAN arrêté, poste sans carte sans fil : pas de code WiFi
            return null;
        }
    }

    /// <summary>
    /// La chaîne que les appareils photo d'iOS et d'Android reconnaissent pour proposer
    /// « Se connecter au réseau ». Format de la spécification WPA/WPA2 QR (ZXing).
    ///
    /// Les caractères <c>\ ; , : "</c> DOIVENT être échappés : un mot de passe contenant un
    /// point-virgule couperait la chaîne en deux et le téléphone lirait une clé tronquée,
    /// sans rien signaler.
    /// </summary>
    public static string Payload(WifiNetwork network)
    {
        ArgumentNullException.ThrowIfNull(network);

        var securite = network.Security;
        var cle = securite.Equals("nopass", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $"P:{Echapper(network.Password)};";

        return $"WIFI:T:{securite};S:{Echapper(network.Ssid)};{cle}" +
               (network.Hidden ? "H:true;" : "") +
               ";";
    }

    /// <summary>Le PNG du code, prêt à afficher.</summary>
    public static byte[] Png(WifiNetwork network, int pixelsPerModule = 12) =>
        QrPng.For(Payload(network), pixelsPerModule);

    private static string Echapper(string valeur)
    {
        var sortie = new StringBuilder(valeur.Length + 8);
        foreach (var c in valeur)
        {
            if (c is '\\' or ';' or ',' or ':' or '"') sortie.Append('\\');
            sortie.Append(c);
        }
        return sortie.ToString();
    }

    /// <summary>
    /// Le SSID de l'interface connectée.
    ///
    /// On compare la clé de la ligne à « SSID » EXACTEMENT : « BSSID » se termine par les
    /// mêmes quatre lettres et le suit immédiatement dans la sortie de netsh. Un
    /// <c>Contains</c> rendrait l'adresse MAC du point d'accès.
    /// </summary>
    private static string? SsidConnecte()
    {
        var sortie = Netsh("wlan show interfaces");
        if (sortie is null) return null;

        foreach (var ligne in sortie.Split('\n'))
        {
            var separateur = ligne.IndexOf(':');
            if (separateur < 0) continue;

            if (!ligne[..separateur].Trim().Equals("SSID", StringComparison.OrdinalIgnoreCase))
                continue;

            var valeur = ligne[(separateur + 1)..].Trim();
            if (valeur.Length > 0) return valeur;
        }

        return null;
    }

    /// <summary>
    /// Le profil exporté en XML, seule forme non traduite que netsh sache produire.
    /// L'export écrit un fichier : on le pose dans un dossier temporaire à nous, qu'on
    /// efface ensuite — il contient la clé du réseau en clair.
    /// </summary>
    private static WifiNetwork? LireProfil(string ssid)
    {
        var dossier = Path.Combine(Path.GetTempPath(), "studio-wifi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dossier);
        try
        {
            if (Netsh($"wlan export profile name=\"{ssid}\" key=clear folder=\"{dossier}\"") is null)
                return null;

            var fichier = Directory.GetFiles(dossier, "*.xml").FirstOrDefault();
            if (fichier is null) return null;

            var doc = XDocument.Load(fichier);
            XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";

            var nom = doc.Descendants(ns + "SSIDConfig").Descendants(ns + "name").FirstOrDefault()?.Value ?? ssid;
            var authentification = doc.Descendants(ns + "authentication").FirstOrDefault()?.Value ?? "";
            var cle = doc.Descendants(ns + "keyMaterial").FirstOrDefault()?.Value ?? "";
            var masque = string.Equals(
                doc.Descendants(ns + "nonBroadcast").FirstOrDefault()?.Value, "true",
                StringComparison.OrdinalIgnoreCase);

            var securite = Securite(authentification, cle);

            // clé attendue mais absente : le profil est protégé (stratégie d'entreprise).
            // Un QR sans mot de passe ferait échouer la connexion sans rien expliquer.
            if (securite != "nopass" && cle.Length == 0) return null;

            return new WifiNetwork(nom, cle, securite, masque);
        }
        finally
        {
            try { Directory.Delete(dossier, recursive: true); }
            catch (IOException) { /* le fichier disparaîtra avec le dossier temporaire */ }
        }
    }

    /// <summary>
    /// Le type attendu par la spécification du QR : « WPA » couvre WPA, WPA2 et WPA3 —
    /// les téléphones ne distinguent pas, ils négocient.
    /// </summary>
    internal static string Securite(string authentification, string cle)
    {
        if (authentification.Contains("WPA", StringComparison.OrdinalIgnoreCase)) return "WPA";
        if (authentification.Contains("shared", StringComparison.OrdinalIgnoreCase)) return "WEP";
        if (authentification.Contains("WEP", StringComparison.OrdinalIgnoreCase)) return "WEP";
        return cle.Length > 0 ? "WPA" : "nopass";
    }

    private static string? Netsh(string arguments)
    {
        var demarrage = new ProcessStartInfo("netsh", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // netsh écrit dans la page de codes de la console : sans cela, un SSID accentué
            // revient en mojibake, et le nom rendu ne rouvre plus le profil
            StandardOutputEncoding = EncodageConsole(),
        };

        using var processus = Process.Start(demarrage);
        if (processus is null) return null;

        var sortie = processus.StandardOutput.ReadToEnd();

        // netsh rend la main tout de suite ; la seconde de garde n'est là que pour ne pas
        // bloquer l'affichage si le service WLAN est en train de démarrer
        if (!processus.WaitForExit(TimeSpan.FromSeconds(5))) return null;

        return processus.ExitCode == 0 ? sortie : null;
    }

    /// <summary>
    /// La page de codes de la console. Une application WPF n'en a pas : le getter rend
    /// alors la page du système, mais il peut aussi lever selon l'hôte — d'où le repli.
    /// </summary>
    private static Encoding EncodageConsole()
    {
        try
        {
            return Console.OutputEncoding;
        }
        catch (Exception)
        {
            return Encoding.Default;
        }
    }
}
