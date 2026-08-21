using Studio.Core.Domain;
using Studio.Imaging.Geometry;

namespace Studio.Tests;

/// <summary>
/// La bande basse quand la planche est déclarée HORS NORME.
///
/// Photo d'école, souvenir au format identité, ou le client qui tient à sa pose contre
/// l'avis du comptoir : la boutique tire ce qu'on lui demande, mais elle n'écrit pas
/// « PHOTOS CONFORMES » sur un tirage dont elle sait que la mairie le refusera. Ce qui se
/// joue ici n'est pas de l'allure, c'est ce que la boutique aura à montrer trois semaines
/// plus tard.
/// </summary>
public class SheetFooterMentionTests
{
    private static readonly DateTime Moment = new(2026, 8, 21, 10, 0, 0);

    [Fact]
    public void HorsNorme_RemplaceLaMention_EtNeSyAjoutePas()
    {
        var bande = SheetFooter.Pour(Moment, new MarqueSettings(), nonConforme: true);

        Assert.Equal(MarqueSettings.MentionNonConforme, bande.Mention);
        Assert.DoesNotContain(MarqueSettings.MentionParDefaut, bande.Mention);
    }

    [Fact]
    public void HorsNorme_RemplaceMemeUneMentionReglee()
    {
        // une boutique qui a réécrit sa promesse ne la voit pas survivre à l'avertissement :
        // affirmer la conformité et la démentir sur la même bande n'aurait aucun sens
        var marque = new MarqueSettings(Mention: "NOS PHOTOS PASSENT PARTOUT");

        var bande = SheetFooter.Pour(Moment, marque, nonConforme: true);

        Assert.Equal(MarqueSettings.MentionNonConforme, bande.Mention);
    }

    [Fact]
    public void HorsNorme_SortMemeQuandLaBandeEstEteinte()
    {
        // le seul endroit où l'on passe outre BandeActive, et c'est délibéré : la bande est
        // un ornement qu'on a le droit de retirer, l'avertissement est une protection
        var marque = new MarqueSettings(BandeActive: false, NomMagasin: "Photo Concept");

        var bande = SheetFooter.Pour(Moment, marque, nonConforme: true);

        Assert.Equal(MarqueSettings.MentionNonConforme, bande.Mention);
        Assert.False(bande.DateSeule);
    }

    [Fact]
    public void HorsNorme_GardeLaSignatureDuMagasin()
    {
        // la signature dit QUI a tiré, pas ce que vaut le tirage : elle reste
        var marque = new MarqueSettings(NomMagasin: "Photo Concept Maisons-Alfort");

        var bande = SheetFooter.Pour(Moment, marque, nonConforme: true);

        Assert.Equal("Photo Concept Maisons-Alfort", bande.NomMagasin);
    }

    [Fact]
    public void PlancheOrdinaire_GardeLaMentionDeConformite()
    {
        var bande = SheetFooter.Pour(Moment, new MarqueSettings());

        Assert.Equal(MarqueSettings.MentionParDefaut, bande.Mention);
    }

    [Fact]
    public void BandeEteinte_SansAvertissement_ResteALaDateSeule()
    {
        // le défaut ne bouge pas : une boutique qui a éteint la bande la garde éteinte
        var bande = SheetFooter.Pour(Moment, new MarqueSettings(BandeActive: false));

        Assert.True(bande.DateSeule);
    }
}
