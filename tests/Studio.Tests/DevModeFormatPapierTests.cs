using System.Drawing.Printing;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Le format papier d'un DEVMODE se résout par son NOM, jamais par l'index reçu.
///
/// <b>Trois postes, un même pilote DNP, trois numérotations.</b> Relevé le 12/08/2026 :
///
/// <list type="table">
/// <item><term>Maisons-Alfort</term><description><c>(6x4)</c> = 121</description></item>
/// <item><term>Créteil</term><description><c>(6x4)</c> = 127</description></item>
/// <item><term>DESKTOP-KT88VDM</term><description><c>(6x4)</c> = 147</description></item>
/// </list>
///
/// Le catalogue livré embarque le DEVMODE capturé à Maisons-Alfort : les deux autres postes
/// recevaient un index inexistant chez eux — 121 pour une plage valide de 145 à 155 sur le
/// troisième — et la planche identité sortait dans une page trop grande. Chacun a été
/// rustiné à la main, et le défaut revenait à chaque publication.
///
/// Ces essais ne touchent aucune imprimante : ils portent sur les octets, qui sont tout ce
/// qui voyage.
/// </summary>
public class DevModeFormatPapierTests
{
    private const int OffsetPaperSize = 78;
    private const int OffsetFormName = 102;

    /// <summary>Un DEVMODE plausible : le nom de format et l'index qu'on veut.</summary>
    private static byte[] Devmode(string nomDuFormat, short index)
    {
        var octets = new byte[1276];   // la taille reelle du devmode de la boutique
        BitConverter.GetBytes(index).CopyTo(octets, OffsetPaperSize);
        System.Text.Encoding.Unicode.GetBytes(nomDuFormat).CopyTo(octets, OffsetFormName);
        return octets;
    }

    private static short FormatDe(byte[] devmode) => BitConverter.ToInt16(devmode, OffsetPaperSize);

    /// <summary>
    /// Le poste de test n'a pas forcément de DS620 : on prend n'importe quelle imprimante
    /// installée et l'un de SES formats. La règle est la même, elle ne connaît pas les DNP.
    /// </summary>
    private static (PrinterSettings Reglages, string Nom, int Index)? UnFormatDuPoste()
    {
        foreach (var nom in PrinterSettings.InstalledPrinters.Cast<string>())
        {
            var reglages = new PrinterSettings { PrinterName = nom };
            if (!reglages.IsValid) continue;

            var format = reglages.PaperSizes.Cast<PaperSize>()
                .FirstOrDefault(p => !string.IsNullOrWhiteSpace(p.PaperName) && p.RawKind > 0);

            if (format is not null) return (reglages, format.PaperName, format.RawKind);
        }

        return null;
    }

    /// <summary>
    /// LE cas de la panne : le nom est connu du poste, l'index reçu est celui d'un autre.
    /// Il doit être remplacé par celui d'ici.
    /// </summary>
    [Fact]
    public void Un_index_venu_d_un_autre_poste_est_recale()
    {
        if (UnFormatDuPoste() is not { } poste) return;   // aucune imprimante : rien à vérifier

        // 4242 ne peut etre l'index de personne : c'est « l'index d'un autre poste »
        var recale = DevMode.RecalerLeFormatPapier(poste.Reglages, Devmode(poste.Nom, 4242));

        Assert.Equal((short)poste.Index, FormatDe(recale));
    }

    /// <summary>Déjà bon : on ne touche à rien, et surtout pas au tableau reçu.</summary>
    [Fact]
    public void Un_index_deja_juste_laisse_les_octets_intacts()
    {
        if (UnFormatDuPoste() is not { } poste) return;

        var origine = Devmode(poste.Nom, (short)poste.Index);
        var recale = DevMode.RecalerLeFormatPapier(poste.Reglages, origine);

        Assert.Same(origine, recale);
    }

    /// <summary>
    /// Un format que le pilote local ne connaît pas : on rend les octets tels quels. C'est
    /// le comportement d'avant, et il n'a jamais empêché un tirage de partir.
    /// </summary>
    [Fact]
    public void Un_format_inconnu_du_poste_ne_change_rien()
    {
        if (UnFormatDuPoste() is not { } poste) return;

        var origine = Devmode("format-qui-n-existe-nulle-part", 121);
        var recale = DevMode.RecalerLeFormatPapier(poste.Reglages, origine);

        Assert.Same(origine, recale);
        Assert.Equal((short)121, FormatDe(recale));
    }

    /// <summary>Sans nom de format, il n'y a rien à résoudre.</summary>
    [Fact]
    public void Un_devmode_sans_nom_de_format_ne_change_rien()
    {
        if (UnFormatDuPoste() is not { } poste) return;

        var origine = Devmode("", 121);

        Assert.Same(origine, DevMode.RecalerLeFormatPapier(poste.Reglages, origine));
    }

    /// <summary>
    /// Un DEVMODE tronqué ne doit pas lever : il vient d'un fichier, et un fichier abîmé
    /// se rencontre.
    /// </summary>
    [Fact]
    public void Un_devmode_trop_court_ne_leve_pas()
    {
        if (UnFormatDuPoste() is not { } poste) return;

        var court = new byte[80];

        Assert.Same(court, DevMode.RecalerLeFormatPapier(poste.Reglages, court));
    }

    /// <summary>
    /// Les octets d'origine ne sont JAMAIS modifiés sur place : ils viennent du catalogue et
    /// sont partagés d'un tirage à l'autre.
    /// </summary>
    [Fact]
    public void Le_tableau_d_origine_n_est_pas_modifie()
    {
        if (UnFormatDuPoste() is not { } poste) return;

        var origine = Devmode(poste.Nom, 4242);
        DevMode.RecalerLeFormatPapier(poste.Reglages, origine);

        Assert.Equal((short)4242, FormatDe(origine));
    }
}
