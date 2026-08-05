using System.Text.RegularExpressions;

namespace Studio.Tests;

/// <summary>
/// Les contrôles qui portent leur couleur EUX-MÊMES doivent être habillés globalement.
///
/// <b>Le défaut</b> (signalé le 05/08/2026) : « il y a encore certains textes en noir sur
/// fond bleu ». WPF donne aux <c>CheckBox</c>, <c>RadioButton</c> et <c>TextBox</c> la
/// couleur de texte du SYSTÈME — du noir — et un fond blanc aux derniers. Sur les panneaux
/// bleu-gris de l'application (<c>CardBrush #1E2731</c>, <c>PanelBrush #2A3440</c>), une
/// vingtaine de libellés étaient donc écrits en noir sur bleu.
///
/// C'est le pendant du « noir sur noir » des <c>TextBlock</c>, à ceci près qu'il se corrige
/// à UN endroit : ces trois types tiennent leur couleur de <c>Control.Foreground</c>, que
/// rien ne leur transmet depuis le conteneur. D'où des styles IMPLICITES dans
/// <c>App.xaml</c> — et cet essai, pour qu'on ne les retire pas par mégarde.
/// </summary>
public class ContrasteXamlTests
{
    private static string RacineApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidat = Path.Combine(dir.FullName, "src", "Studio.App");
            if (Directory.Exists(candidat)) return candidat;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Dossier src/Studio.App introuvable.");
    }

    private static string AppXaml() => File.ReadAllText(Path.Combine(RacineApp(), "App.xaml"));

    /// <summary>
    /// Un style SANS clé : c'est ce qui le rend implicite, donc appliqué aux contrôles que
    /// personne n'a habillés un par un.
    /// </summary>
    [Theory]
    [InlineData("CheckBox")]
    [InlineData("RadioButton")]
    [InlineData("TextBox")]
    public void Le_controle_porte_un_style_implicite_avec_une_couleur(string type)
    {
        var xaml = AppXaml();

        var style = new Regex(
            $@"<Style\s+TargetType=""{type}""\s*>(.*?)</Style>",
            RegexOptions.Singleline);

        var trouve = style.Match(xaml);

        Assert.True(trouve.Success,
            $"App.xaml ne porte plus de style implicite pour {type} : ses libellés " +
            "repasseront en noir sur le fond sombre.");

        Assert.Contains("Property=\"Foreground\"", trouve.Groups[1].Value);
    }

    /// <summary>
    /// <b>Un style NOMMÉ ne reprend pas le style implicite</b> — le piège déjà rencontré
    /// sur les listes déroulantes de l'écran des agrandissements. Sans <c>BasedOn</c>, les
    /// champs de saisie habillés localement seraient restés blancs au milieu d'un écran
    /// sombre.
    /// </summary>
    [Fact]
    public void Les_styles_nommes_de_champs_reprennent_le_style_implicite()
    {
        var racine = RacineApp();

        var fichiers = Directory.EnumerateFiles(racine, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

        var nomme = new Regex(@"<Style\s+x:Key=""[^""]+""\s+TargetType=""TextBox""[^>]*>");

        var orphelins = new List<string>();

        foreach (var fichier in fichiers)
            foreach (Match m in nomme.Matches(File.ReadAllText(fichier)))
                if (!m.Value.Contains("BasedOn"))
                    orphelins.Add($"{Path.GetFileName(fichier)} : {m.Value.Trim()}");

        Assert.True(orphelins.Count == 0,
            "Ces styles de TextBox ne reprennent pas le style implicite et resteront " +
            "blancs sur fond sombre :\n" + string.Join("\n", orphelins));
    }
}
