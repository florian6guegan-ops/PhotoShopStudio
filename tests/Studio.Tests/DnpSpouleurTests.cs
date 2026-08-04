using Studio.Printing.Devices.Dnp;

namespace Studio.Tests;

/// <summary>
/// L'état de la DS620 lu dans le SPOULEUR Windows, et le nombre de photos qui lui restent
/// à sortir.
///
/// <b>Le défaut corrigé.</b> Le SDK DNP ne peut pas ouvrir la machine tant que DiLand
/// tourne — il tient le port USB en exclusif — et DiLand tourne pratiquement en permanence
/// en boutique, puisque c'est lui qui reçoit les commandes des bornes. Faute de réponse du
/// SDK, l'écran affichait « En veille » <b>en continu</b> : machine allumée, prête, et même
/// pendant qu'elle tirait. Signalé par l'exploitant le 04/08/2026.
///
/// Le spouleur, lui, répond toujours : c'est par lui que Studio imprime sur cette machine.
///
/// Les essais portent sur la DÉCISION d'état, séparée de WMI pour être vérifiable. La
/// lecture WMI elle-même se contrôle sur la vraie machine avec
/// <c>Studio.PrintProbe dnp</c>.
/// </summary>
public class DnpSpouleurTests
{
    // Win32_Printer.PrinterStatus
    private const int Autre = 1;
    private const int Inconnu = 2;
    private const int Pret = 3;
    private const int EnImpression = 4;
    private const int Prechauffage = 5;
    private const int HorsLigne = 7;

    // Win32_Printer.DetectedErrorState
    private const int PasDErreur = 2;
    private const int PlusDePapier = 4;
    private const int CapotOuvert = 10;

    private static EtatFileDnp Etat(
        int statut, bool horsLigne = false, int erreur = PasDErreur,
        int restantes = 0, bool enPause = false) =>
        DnpSpouleur.Decider(statut, horsLigne, erreur, restantes, enPause).Etat;

    /// <summary>
    /// <b>Le contrôle qui compte.</b> Les valeurs RELEVÉES sur la DP-DS620 de la boutique
    /// le 04/08/2026, DiLand ouvert : <c>PrinterStatus = 3</c>, <c>WorkOffline = False</c>,
    /// <c>DetectedErrorState = 0</c>. Elle est prête — et surtout pas en veille.
    /// </summary>
    [Fact]
    public void La_DS620_de_la_boutique_est_prete_et_non_en_veille()
    {
        Assert.Equal(EtatFileDnp.Prete, Etat(Pret, horsLigne: false, erreur: 0));
    }

    /// <summary>
    /// <c>DetectedErrorState = 0</c> veut dire « inconnu », pas « en panne ».
    ///
    /// C'est la valeur que rend la machine de la boutique en fonctionnement normal : la
    /// traiter comme une erreur repeindrait la tuile en rouge en permanence.
    /// </summary>
    [Fact]
    public void Une_erreur_inconnue_n_est_pas_une_panne()
    {
        Assert.Equal(EtatFileDnp.Prete, Etat(Pret, erreur: 0));
        Assert.Equal(EtatFileDnp.Prete, Etat(Pret, erreur: PasDErreur));
    }

    /// <summary>
    /// Un pilote qui ne se prononce pas, sur une file qui répond, sans panne et sans
    /// travail : un tirage envoyé maintenant partirait. C'est « prête ».
    ///
    /// Afficher « état inconnu » sur une machine qui marche serait exactement le défaut
    /// qu'on corrige.
    /// </summary>
    [Theory]
    [InlineData(Autre)]
    [InlineData(Inconnu)]
    public void Un_statut_vague_sur_une_file_saine_vaut_prete(int statut)
    {
        Assert.Equal(EtatFileDnp.Prete, Etat(statut));
    }

    /// <summary>
    /// <b>Le nombre de photos restantes fait l'état.</b> Des tirages dans la file, c'est
    /// une impression en cours — même quand le pilote se dit encore « prêt » entre deux
    /// pages, ce qu'il fait sans arrêt sur une sublimation.
    /// </summary>
    [Fact]
    public void Des_photos_dans_la_file_valent_impression_en_cours()
    {
        Assert.Equal(EtatFileDnp.Impression, Etat(Pret, restantes: 7));
    }

    [Theory]
    [InlineData(EnImpression)]
    [InlineData(Prechauffage)]
    public void Le_pilote_qui_annonce_l_impression_suffit(int statut)
    {
        Assert.Equal(EtatFileDnp.Impression, Etat(statut));
    }

    [Fact]
    public void Une_file_en_pause_n_est_pas_une_impression_en_cours()
    {
        // rien ne sortira tant qu'elle n'est pas relancée : le dire autrement ferait
        // attendre l'opérateur devant une machine qui ne bougera pas
        Assert.Equal(EtatFileDnp.EnPause, Etat(Pret, restantes: 3, enPause: true));
    }

    [Fact]
    public void Hors_ligne_l_emporte_sur_tout_le_reste()
    {
        Assert.Equal(EtatFileDnp.HorsLigne, Etat(Pret, horsLigne: true, restantes: 5));
        Assert.Equal(EtatFileDnp.HorsLigne, Etat(HorsLigne));
    }

    /// <summary>
    /// Une panne reste une panne, même avec des tirages qui patientent derrière — c'est
    /// elle qu'il faut lire en premier.
    /// </summary>
    [Theory]
    [InlineData(PlusDePapier)]
    [InlineData(CapotOuvert)]
    public void Une_panne_l_emporte_sur_la_file(int erreur)
    {
        Assert.Equal(EtatFileDnp.Erreur, Etat(Pret, erreur: erreur, restantes: 12));
    }

    [Fact]
    public void La_panne_est_dite_en_clair()
    {
        var (etat, message) = DnpSpouleur.Decider(Pret, false, PlusDePapier, 0, false);

        Assert.Equal(EtatFileDnp.Erreur, etat);
        Assert.Equal("Plus de papier.", message);
    }

    // ----- ce que l'opérateur lit -----

    /// <summary>
    /// <b>Le nombre de photos restantes est affiché</b> — c'est ce que l'exploitant a
    /// demandé, et ce qui permet de savoir si on a le temps de servir quelqu'un d'autre.
    /// </summary>
    [Fact]
    public void Le_libelle_annonce_les_photos_restantes()
    {
        Assert.Equal("Impression en cours — 12 photos restantes",
            DnpSpouleur.Decrire(new EtatSpouleurDnp("DP-DS620", EtatFileDnp.Impression, 12, 12, "")));

        Assert.Equal("Impression en cours — 1 photo restante",
            DnpSpouleur.Decrire(new EtatSpouleurDnp("DP-DS620", EtatFileDnp.Impression, 1, 1, "")));
    }

    [Fact]
    public void Le_libelle_dit_prete_sans_chiffre_inutile()
    {
        Assert.Equal("Prête à imprimer",
            DnpSpouleur.Decrire(new EtatSpouleurDnp("DP-DS620", EtatFileDnp.Prete, 0, 0, "")));
    }

    [Fact]
    public void Le_libelle_d_une_panne_reprend_le_message_du_pilote()
    {
        Assert.Equal("Capot ouvert.",
            DnpSpouleur.Decrire(new EtatSpouleurDnp(
                "DP-DS620", EtatFileDnp.Erreur, 0, 0, "Capot ouvert.")));
    }

    /// <summary>
    /// Une DNP vue par le seul spouleur n'est plus déclarée « en veille » : c'est
    /// <see cref="DnpPrinterInfo.VueParLeSpouleur"/> qui départage, et les écrans s'en
    /// servent pour choisir quoi afficher.
    /// </summary>
    [Fact]
    public void Une_DNP_vue_par_le_spouleur_n_est_plus_endormie()
    {
        var info = Injoignable() with
        {
            Spouleur = new EtatSpouleurDnp("DP-DS620", EtatFileDnp.Prete, 0, 0, ""),
        };

        Assert.True(info.EndormieOuInjoignable);   // le SDK ne la voit toujours pas
        Assert.True(info.VueParLeSpouleur);        // mais le spouleur, si
    }

    /// <summary>Sans spouleur exploitable, on retombe bien sur « en veille ».</summary>
    [Fact]
    public void Sans_spouleur_exploitable_elle_reste_endormie()
    {
        Assert.False(Injoignable().VueParLeSpouleur);

        var muet = Injoignable() with { Spouleur = EtatSpouleurDnp.Inconnu("DP-DS620") };
        Assert.False(muet.VueParLeSpouleur);
    }

    private static DnpPrinterInfo Injoignable() => new(
        PortNumber: -1,
        SerialNumber: "",
        FirmwareVersion: "",
        Status: new DnpStatus(0x80000000u),
        MediaRemaining: 0,
        MediaInitialCount: 0,
        MediaSize: DnpMediaSize.None,
        MediaClass: DnpMediaClass.Unknown,
        QueuedPrints: 0,
        LifetimePrints: 0,
        WindowsQueueName: "DP-DS620");
}
