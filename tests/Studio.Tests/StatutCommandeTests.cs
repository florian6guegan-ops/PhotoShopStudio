using Studio.Core.Domain;
using Studio.Printing;

namespace Studio.Tests;

/// <summary>
/// Le statut porté par la COMMANDE, par opposition à celui de chacune de ses enveloppes.
///
/// Il n'était écrit qu'à la création : toutes les commandes restaient « Submitted » à vie,
/// y compris tirées, payées et remises au client. Les écrans n'en souffraient pas — ils
/// lisent les enveloppes — mais <c>order.json</c> est ce qu'on relit des mois plus tard,
/// et ce sur quoi les statistiques s'appuient. Constaté sur toutes les commandes du
/// 07/08/2026.
/// </summary>
public class StatutCommandeTests
{
    private static Order Commande(OrderStatus depart, params EnvelopeStatus[] enveloppes)
    {
        var order = new Order { Status = depart };

        for (var i = 0; i < enveloppes.Length; i++)
            order.Envelopes.Add(new Envelope { Number = i + 1, Status = enveloppes[i] });

        return order;
    }

    private static OrderStatus Apres(OrderStatus depart, params EnvelopeStatus[] enveloppes)
    {
        var order = Commande(depart, enveloppes);
        PrintOrchestrator.MettreAJourStatutCommande(order);
        return order.Status;
    }

    [Fact]
    public void Tout_est_sorti_donc_la_commande_est_prete()
    {
        Assert.Equal(
            OrderStatus.Ready,
            Apres(OrderStatus.Submitted, EnvelopeStatus.Printed, EnvelopeStatus.Printed));
    }

    /// <summary>
    /// Le cas de la commande 07-015 : l'envoi par courriel ne passe par aucune machine, et
    /// son enveloppe est close sans rien imprimer. La commande est prête pour autant.
    /// </summary>
    [Fact]
    public void Une_enveloppe_seule_et_close_suffit()
    {
        Assert.Equal(OrderStatus.Ready, Apres(OrderStatus.Submitted, EnvelopeStatus.Printed));
    }

    /// <summary>
    /// <b>« Prête » veut dire prête à être RETIRÉE.</b> Un agrandissement qui attend encore
    /// la main de l'opérateur sur l'Epson ne l'est pas, même si le reste est sorti :
    /// annoncer le contraire ferait venir le client devant une commande incomplète.
    /// </summary>
    [Theory]
    [InlineData(EnvelopeStatus.AwaitingManualPrint)]
    [InlineData(EnvelopeStatus.Spooled)]
    [InlineData(EnvelopeStatus.Rendering)]
    public void Une_enveloppe_en_cours_retient_la_commande(EnvelopeStatus reste)
    {
        Assert.Equal(
            OrderStatus.Printing,
            Apres(OrderStatus.Submitted, EnvelopeStatus.Printed, reste));
    }

    /// <summary>Tout a été rappelé au minilab : la commande est annulée, surtout pas prête.</summary>
    [Fact]
    public void Tout_annule_annule_la_commande()
    {
        Assert.Equal(
            OrderStatus.Cancelled,
            Apres(OrderStatus.Printing, EnvelopeStatus.Canceled, EnvelopeStatus.Canceled));
    }

    /// <summary>
    /// Une enveloppe rappelée n'empêche pas les autres de conclure : ce qui est sorti est
    /// sorti, et le client a quelque chose à venir chercher.
    /// </summary>
    [Fact]
    public void Une_enveloppe_annulee_parmi_d_autres_ne_retient_rien()
    {
        Assert.Equal(
            OrderStatus.Ready,
            Apres(OrderStatus.Printing, EnvelopeStatus.Printed, EnvelopeStatus.Canceled));
    }

    /// <summary>
    /// Une commande annulée le reste. Ce n'est pas à un tirage tardif — une réimpression
    /// lancée par mégarde, un verdict qui arrive après coup — de la rouvrir.
    /// </summary>
    [Fact]
    public void Une_commande_annulee_ne_se_rouvre_pas()
    {
        Assert.Equal(
            OrderStatus.Cancelled,
            Apres(OrderStatus.Cancelled, EnvelopeStatus.Printed, EnvelopeStatus.Printed));
    }

    /// <summary>Sans enveloppe, il n'y a rien à conclure : le statut ne bouge pas.</summary>
    [Fact]
    public void Une_commande_sans_enveloppe_reste_en_l_etat()
    {
        Assert.Equal(OrderStatus.Submitted, Apres(OrderStatus.Submitted));
    }

    /// <summary>
    /// Une enveloppe en échec ne rend la commande ni prête ni en cours : elle attend une
    /// décision, et le statut de la commande ne doit pas prétendre le contraire.
    /// </summary>
    [Fact]
    public void Une_enveloppe_en_erreur_ne_conclut_pas_la_commande()
    {
        Assert.Equal(
            OrderStatus.Submitted,
            Apres(OrderStatus.Submitted, EnvelopeStatus.Printed, EnvelopeStatus.Error));
    }
}
