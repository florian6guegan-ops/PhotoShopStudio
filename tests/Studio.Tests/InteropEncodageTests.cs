using System.Reflection;
using System.Runtime.InteropServices;

namespace Studio.Tests;

/// <summary>
/// L'encodage des chaînes envoyées au SDK Fuji.
///
/// <b>Le défaut du 12/08/2026, et il était invisible.</b> <c>PIF_GetOrderInfo</c> était
/// déclarée sans <c>CharSet</c> — donc en ANSI, le défaut de .NET — alors que ses voisines
/// qui reçoivent le même handle de commande (<c>PIF_CancelOrder</c>,
/// <c>PIF_ExpressOrder</c>) sont en Unicode depuis toujours. Le handle partait en octets
/// simples là où le SDK attend de l'UTF-16 : il ne retrouvait jamais la commande, rendait un
/// code de refus, et l'avancement restait à zéro d'un bout à l'autre du tirage.
///
/// Rien ne plantait, rien n'était rouge. Six commandes minilab, six relevés muets, sur les
/// trois boutiques à la fois.
///
/// <b>Cet essai lit les ATTRIBUTS</b>, pas le comportement : il tourne sans SDK, sans
/// machine, et il attrapera la prochaine déclaration ajoutée à la va-vite.
/// </summary>
public class InteropEncodageTests
{
    private static Type Interop =>
        typeof(Studio.Printing.Devices.Fuji.De100Driver).Assembly
            .GetType("Studio.Printing.Devices.Fuji.De100Interop", throwOnError: true)!;

    private static IEnumerable<MethodInfo> FonctionsImportees() =>
        Interop.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<DllImportAttribute>() is not null);

    /// <summary>
    /// TOUTE fonction du SDK qui reçoit ou rend une CHAÎNE doit être en Unicode.
    ///
    /// ⚠ <b>Les paramètres <c>char</c> sont hors du compte, et ce n'est pas un oubli.</b>
    /// <c>PIF_DevIsReady</c>, <c>PIF_DevGetPrinterInfo</c> et leurs voisines reçoivent un
    /// identifiant de machine — « A », « B » — que le SDK attend sur UN octet. Les passer en
    /// Unicode en enverrait deux et casserait la détection des machines, qui fonctionne
    /// depuis toujours. La règle porte sur les chaînes, pas sur les caractères.
    /// </summary>
    [Fact]
    public void Toute_fonction_qui_passe_une_chaine_est_en_Unicode()
    {
        var fautives = new List<string>();

        foreach (var fonction in FonctionsImportees())
        {
            var passeDuTexte = fonction.GetParameters().Any(p =>
                p.ParameterType == typeof(System.Text.StringBuilder) ||
                p.ParameterType == typeof(string));

            if (!passeDuTexte) continue;

            var import = fonction.GetCustomAttribute<DllImportAttribute>()!;
            if (import.CharSet != CharSet.Unicode)
                fautives.Add($"{fonction.Name} (CharSet = {import.CharSet})");
        }

        Assert.True(fautives.Count == 0,
            "Ces fonctions passent du texte au SDK sans CharSet.Unicode — le handle y part " +
            "en ANSI et le SDK ne le reconnaît pas :\n  " + string.Join("\n  ", fautives));
    }

    /// <summary>
    /// Le cas nommé, pour que l'essai reste lisible quand il tombe : c'est celle-là qui a
    /// coûté la journée.
    /// </summary>
    [Fact]
    public void PIF_GetOrderInfo_est_en_Unicode()
    {
        var fonction = FonctionsImportees().Single(m => m.Name == "PIF_GetOrderInfo");

        Assert.Equal(CharSet.Unicode, fonction.GetCustomAttribute<DllImportAttribute>()!.CharSet);
    }

    /// <summary>
    /// Et la structure qu'elle remplit doit l'être aussi : ses champs texte se lisent avec
    /// le même encodage, sans quoi le handle rendu serait illisible à son tour.
    /// </summary>
    [Fact]
    public void La_structure_d_une_commande_est_en_Unicode()
    {
        var structure = Interop.Assembly
            .GetType("Studio.Printing.Devices.Fuji.ST_ORDER_INFO", throwOnError: true)!;

        var disposition = structure.StructLayoutAttribute;

        Assert.NotNull(disposition);
        Assert.Equal(CharSet.Unicode, disposition!.CharSet);
    }
}
