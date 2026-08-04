using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// Aiguillage des enveloppes vers le minilab Fuji. Ces vérifications ont lieu avant tout
/// rendu et sans minilab réel : elles portent sur les refus et le choix de machine, là où
/// une erreur enverrait des tirages sur la mauvaise machine ou dans le vide.
/// </summary>
public class MinilabRoutingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioMinilab-" + Guid.NewGuid().ToString("N"));

    public MinilabRoutingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* au mieux */ }
    }

    /// <summary>Minilab factice : enregistre ce qu'on lui envoie, n'imprime rien.</summary>
    private sealed class FauxMinilab(params char[] ready) : IMinilabPrinter
    {
        public List<(De100PrintJob Job, char Machine)> Submitted { get; } = [];

        public IReadOnlyList<char> ReadyMachines() => ready;

        public De100Surface LoadedSurface(char machineId) => De100Surface.Lustre;

        /// <summary>Rouleau chargé ; 0 par défaut = largeur inconnue, aucun contrôle.</summary>
        public int PaperWidthMm { get; set; }

        public int LoadedPaperWidthMm(char machineId) => PaperWidthMm;

        /// <summary>Commandes reçues, chacune avec toutes ses photos. Une enveloppe = une entrée.</summary>
        public List<(IReadOnlyList<De100PrintJob> Jobs, char Machine)> Commandes { get; } = [];

        public string Submit(IReadOnlyList<De100PrintJob> jobs, char machineId)
        {
            Commandes.Add((jobs, machineId));
            foreach (var job in jobs) Submitted.Add((job, machineId));
            return $"OH-{Commandes.Count}";
        }

        /// <summary>Commandes rappelées, dans l'ordre : c'est ce que l'annulation doit produire.</summary>
        public List<string> Canceled { get; } = [];

        /// <summary>Handles que la machine refuse de rappeler — un tirage déjà sorti, par exemple.</summary>
        public HashSet<string> RefuseCancel { get; } = [];

        public void Cancel(string orderHandle)
        {
            if (RefuseCancel.Contains(orderHandle))
                throw new InvalidOperationException($"Le minilab refuse d'annuler {orderHandle}.");

            Canceled.Add(orderHandle);
        }
    }

    private static Product Minilab(string code = "10x15", string? machine = null) => new()
    {
        Code = code,
        Name = code,
        WidthMm = 102,
        HeightMm = 152,
        PrinterName = "",
        PrinterChannel = "Minilab DE100",
        Output = ProductOutput.FujiMinilab,
        MinilabMachineId = machine,
        Price = 0.60m,
    };

    private static Product Spouleur() => new()
    {
        Code = "10x15-dnp",
        Name = "10x15",
        WidthMm = 105,
        HeightMm = 156,
        PrinterName = "DP-DS620",
        Price = 0.60m,
    };

    private PrintOrchestrator Orchestrateur(IMinilabPrinter? minilab, params Product[] produits)
    {
        var store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var catalogDir = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogDir);
        return new PrintOrchestrator(new ProductCatalog(produits), store, catalogDir, minilab);
    }

    private static Order CommandeAvec(params string[] codes)
    {
        var envelope = new Envelope { Number = 1, PrinterChannel = "test" };
        foreach (var code in codes)
        {
            envelope.Lines.Add(new OrderLine
            {
                ProductCode = code,
                UnitPrice = 0.60m,
                Items = { new OrderItem { FileName = "001.jpg", OriginalName = "IMG.jpg", Quantity = 1 } },
            });
        }
        return new Order { DailyNumber = 1, Source = "Test", Envelopes = { envelope } };
    }

    // — refus —

    /// <summary>
    /// Sans relais, un tirage minilab doit être refusé net. Le renvoyer vers le spouleur
    /// le ferait disparaître dans le port « nul » en annonçant un succès imaginaire.
    /// </summary>
    [Fact]
    public void Sans_relais_le_tirage_minilab_est_refuse()
    {
        var orchestrateur = Orchestrateur(minilab: null, Minilab());
        var commande = CommandeAvec("10x15");

        var ex = Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Contains("relais", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EnvelopeStatus.Pending, commande.Envelopes[0].Status);
    }

    [Fact]
    public void Une_enveloppe_ne_peut_pas_melanger_minilab_et_spouleur()
    {
        var orchestrateur = Orchestrateur(new FauxMinilab('A'), Minilab(), Spouleur());
        var commande = CommandeAvec("10x15", "10x15-dnp");

        var ex = Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Contains("circuits", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Le refus doit tomber avant le rendu : rien ne doit être marqué envoyé.</summary>
    [Fact]
    public void Le_refus_intervient_avant_tout_rendu()
    {
        var orchestrateur = Orchestrateur(minilab: null, Minilab());
        var commande = CommandeAvec("10x15");

        Assert.Throws<InvalidOperationException>(
            () => orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]));

        Assert.Equal(EnvelopeStatus.Pending, commande.Envelopes[0].Status);
    }

    // — papier chargé —

    /// <summary>
    /// Un tirage sort dès que le côté qui se pose en travers du rouleau y tient — ce
    /// n'est PAS une égalité de format.
    ///
    /// DiLand tire un 13×18 sur du 152 : le côté de 127 se pose en travers et les 25 mm
    /// restants sortent en bandes blanches. Son journal le prouve, 26 envois « 152x180 ».
    /// Refuser ce cas privait la boutique d'un format qu'elle vend tous les jours
    /// (constaté le 01/08/2026). Seul un rouleau plus étroit que le petit côté rend le
    /// tirage réellement impossible.
    /// </summary>
    [Theory]
    [InlineData(152, 102, 102, true)]  // 10x15 sur rouleau 10 cm
    [InlineData(152, 102, 152, true)]  // 10x15 en travers d'un rouleau 15 cm
    [InlineData(203, 152, 152, true)]  // 15x20 sur rouleau 15 cm
    [InlineData(180, 127, 152, true)]  // 13x18 sur rouleau 15 cm — bandes blanches
    [InlineData(180, 127, 210, true)]  // 13x18 sur rouleau 21 cm — bandes plus larges
    [InlineData(180, 127, 102, false)] // 13x18 sur rouleau 10 cm : le 127 ne rentre pas
    [InlineData(203, 152, 102, false)] // 15x20 sur rouleau 10 cm
    [InlineData(305, 203, 152, false)] // 20x30 sur rouleau 15 cm
    public void Un_tirage_sort_si_son_petit_cote_tient_dans_le_rouleau(
        double pageW, double pageH, int largeurRouleau, bool possible)
    {
        Assert.Equal(possible, PrintOrchestrator.FitsPaperWidth(pageW, pageH, largeurRouleau));
    }

    // — cotes envoyées au minilab —

    /// <summary>
    /// La règle du pilote de DiLand : la PREMIÈRE cote est toujours la largeur du rouleau
    /// chargé, jamais le grand côté du tirage.
    ///
    /// Le 10×15 ne révèle rien — son grand côté EST la largeur du rouleau de 15, donc les
    /// deux raisonnements donnent « 152x102 ». C'est le 15×20 qui tranche : « 152x203 »
    /// avec la bonne règle, « 203x152 » avec l'ancienne — et la machine comprend alors
    /// qu'on lui demande un rouleau de 203 mm, avertit et gâche le tirage (01/08/2026).
    /// </summary>
    [Theory]
    [InlineData(152, 102, 152, 152, 102)] // 10x15 sur rouleau 15 — le cas qui masquait le bug
    [InlineData(102, 152, 152, 152, 102)] // le même, page en portrait
    [InlineData(152, 203, 152, 152, 203)] // 15x20 sur rouleau 15 — le tirage raté
    [InlineData(203, 152, 152, 152, 203)] // le même, page en paysage
    [InlineData(152, 102, 102, 102, 152)] // 10x15 sur rouleau 10 : il sort dans l'autre sens
    [InlineData(210, 297, 210, 210, 297)] // A4 sur rouleau 21
    [InlineData(127, 180, 152, 152, 180)] // 13x18 sur rouleau 15 : « 152x180 », vu 26 fois chez DiLand
    [InlineData(152, 203, 210, 210, 152)] // 15x20 sur rouleau 21 : « 210x152 », vu chez DiLand
    public void La_premiere_cote_envoyee_est_toujours_la_largeur_du_rouleau(
        double pageW, double pageH, int rouleau, double largeurAttendue, double longueurAttendue)
    {
        var (largeur, longueur) = PrintOrchestrator.MinilabPrintSize(pageW, pageH, rouleau);

        Assert.Equal(largeurAttendue, largeur);
        Assert.Equal(longueurAttendue, longueur);
    }

    /// <summary>
    /// L'épreuve de vérité : les douze cotes distinctes que DiLand a réellement envoyées
    /// au DE100 depuis juillet 2026 (9 336 tirages relevés dans ses journaux), sur les
    /// trois rouleaux qui se sont succédé. Chacune doit ressortir à l'identique.
    ///
    /// C'est ce corpus qui a débusqué le cas du 18×24 : les deux côtés sont à égale
    /// distance du rouleau de 210, et départager par « le plus éloigné » refusait un
    /// format que la boutique vend. Le bon critère est « le côté le plus large qui tient
    /// encore en travers du rouleau » — celui qui laisse le moins de blanc.
    /// </summary>
    [Theory]
    [InlineData("10x15", 102, 152, 152, "152x102")]     // 8348 tirages
    [InlineData("8x10", 80, 102, 152, "152x80")]        //  464
    [InlineData("20x25", 203, 256, 210, "210x256")]     //  165
    [InlineData("15x20", 152, 203, 152, "152x203")]     //  144
    [InlineData("13x18", 127, 180, 152, "152x180")]     //  112
    [InlineData("21x29,7", 210, 297, 210, "210x297")]   //   37
    [InlineData("10x15", 102, 152, 210, "210x102")]     //   23
    [InlineData("20x30", 203, 307, 210, "210x307")]     //   13
    [InlineData("18x24", 180, 240, 210, "210x240")]     //   13 — le cas qui a corrigé la règle
    [InlineData("13x18", 127, 180, 210, "210x127")]     //    7
    [InlineData("15x20", 152, 203, 210, "210x152")]     //    6
    [InlineData("15x20", 152, 203, 203, "203x152")]     //    3 — l'ancien rouleau de 20 cm
    public void Les_cotes_reelles_de_DiLand_sont_reproduites(
        string produit, double pageW, double pageH, int rouleau, string attendu)
    {
        var (largeur, longueur) = PrintOrchestrator.MinilabPrintSize(pageW, pageH, rouleau);

        Assert.True(PrintOrchestrator.FitsPaperWidth(pageW, pageH, rouleau),
            $"{produit} devrait sortir sur un rouleau de {rouleau} mm");
        Assert.Equal(attendu, PrintOrchestrator.MinilabSizeName(Minilab(), largeur, longueur));
    }

    /// <summary>
    /// Les cinq largeurs de rouleau que la boutique utilise (102, 127, 152, 203, 210 —
    /// confirmé par l'exploitant le 01/08/2026), sur les formats où le choix du sens de
    /// pose n'est pas évident.
    ///
    /// Le 13×18 est le plus instructif : natif sur 127, il sort à bandes blanches sur
    /// 152, et sur 203 comme sur 210 c'est son GRAND côté qui se pose en travers — la
    /// coupe passe alors de 180 à 127 mm, soit un tiers de papier économisé.
    /// </summary>
    [Theory]
    [InlineData(127, 180, 127, "127x180")] // 13x18 sur son rouleau natif, pleine largeur
    [InlineData(127, 180, 152, "152x180")] // bandes blanches de 12,5 mm
    [InlineData(127, 180, 203, "203x127")] // le 180 se pose en travers : coupe plus courte
    [InlineData(102, 152, 102, "102x152")] // 10x15 sur son rouleau natif
    [InlineData(102, 152, 127, "127x152")]
    [InlineData(102, 152, 152, "152x102")] // le grand côté passe en travers
    [InlineData(203, 256, 203, "203x256")] // 20x25 sur son rouleau natif
    [InlineData(180, 240, 203, "203x240")] // 18x24 sur 203
    [InlineData(80, 102, 102, "102x80")]   // 8x10 sur 102
    public void Les_cinq_rouleaux_de_la_boutique_donnent_la_bonne_cote(
        double pageW, double pageH, int rouleau, string attendu)
    {
        var (largeur, longueur) = PrintOrchestrator.MinilabPrintSize(pageW, pageH, rouleau);

        Assert.Equal(attendu, PrintOrchestrator.MinilabSizeName(Minilab(), largeur, longueur));
    }

    /// <summary>
    /// Ce qui reste réellement impossible : un tirage dont le petit côté dépasse le
    /// rouleau. L'A4 est le cas limite — 210 mm, il ne sort donc PAS du rouleau de 203.
    /// </summary>
    [Theory]
    [InlineData(210, 297, 203)] // A4 sur 203 : il manque 7 mm
    [InlineData(203, 307, 152)] // 20x30 sur 152
    [InlineData(127, 127, 102)] // 13x13 sur 102
    [InlineData(180, 240, 152)] // 18x24 sur 152
    public void Un_petit_cote_plus_large_que_le_rouleau_reste_impossible(
        double pageW, double pageH, int rouleau)
    {
        Assert.False(PrintOrchestrator.FitsPaperWidth(pageW, pageH, rouleau));
    }

    /// <summary>Rouleau inconnu : on retombe sur le grand côté, faute de mieux.</summary>
    [Fact]
    public void Sans_largeur_de_rouleau_connue_on_retombe_sur_le_grand_cote()
    {
        var (largeur, longueur) = PrintOrchestrator.MinilabPrintSize(152, 203, 0);

        Assert.Equal(203, largeur);
        Assert.Equal(152, longueur);
    }

    /// <summary>Le nom de format reprend ces deux cotes, dans cet ordre.</summary>
    [Fact]
    public void Le_nom_de_format_reprend_la_largeur_du_rouleau_en_premier()
    {
        var (largeur, longueur) = PrintOrchestrator.MinilabPrintSize(152, 203, 152);

        Assert.Equal("152x203", PrintOrchestrator.MinilabSizeName(Minilab(), largeur, longueur));
    }

    // — choix de la machine —

    [Fact]
    public void Sans_machine_prete_rien_n_est_envoye()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PrintOrchestrator.ChooseMachine([], requested: null));

        Assert.Contains("prête", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void La_machine_demandee_est_retenue_si_elle_est_prete()
    {
        Assert.Equal('B', PrintOrchestrator.ChooseMachine(['A', 'B'], requested: "B"));
    }

    /// <summary>
    /// Le DE100 de la boutique compte deux machines dont une souvent hors ligne : une
    /// machine demandée mais absente ne doit pas bloquer le tirage.
    /// </summary>
    [Fact]
    public void Une_machine_demandee_mais_hors_ligne_ne_bloque_pas()
    {
        Assert.Equal('B', PrintOrchestrator.ChooseMachine(['B'], requested: "A"));
    }

    [Fact]
    public void Sans_machine_demandee_on_prend_la_premiere_prete()
    {
        Assert.Equal('A', PrintOrchestrator.ChooseMachine(['A', 'B'], requested: null));
        Assert.Equal('A', PrintOrchestrator.ChooseMachine(['A', 'B'], requested: ""));
    }

    // — modèle —

    [Fact]
    public void Un_produit_minilab_ne_designe_aucune_file_Windows()
    {
        var produit = Minilab();

        Assert.Equal(ProductOutput.FujiMinilab, produit.Output);
        Assert.Empty(produit.PrinterName);
        Assert.Equal("Minilab DE100", produit.Channel);
    }

    /// <summary>
    /// Le minilab n'accepte pas les noms commerciaux : il attend « 152x102 », grand côté
    /// en premier. Relevé dans les journaux de DiLand le 31/07/2026.
    /// </summary>
    [Fact]
    public void Le_nom_de_format_minilab_est_en_millimetres_grand_cote_en_premier()
    {
        Assert.Equal("152x102", PrintOrchestrator.MinilabSizeName(Minilab(), 152, 102));
        Assert.Equal("203x152", PrintOrchestrator.MinilabSizeName(Minilab(), 203, 152));
    }

    [Fact]
    public void Un_produit_peut_imposer_son_propre_libelle_de_format()
    {
        var produit = Minilab();
        produit.MinilabPrintSizeName = "10x15POLA";

        Assert.Equal("10x15POLA", PrintOrchestrator.MinilabSizeName(produit, 152, 102));
    }
}
