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

        /// <summary>Les DNP qu'on lui fait voir ; vide par défaut, comme sans relais.</summary>
        public List<Studio.Printing.Devices.Dnp.DnpPrinterInfo> Dnps { get; } = [];

        /// <summary>Envois directs reçus : (image, port, finition, exemplaires).</summary>
        public List<(string Image, int Port, int Finition, int Copies)> EnvoisDirects { get; } = [];

        public Task<IReadOnlyList<Studio.Printing.Devices.Dnp.DnpPrinterInfo>> DnpSnapshotAsync() =>
            Task.FromResult<IReadOnlyList<Studio.Printing.Devices.Dnp.DnpPrinterInfo>>(Dnps);

        public Task<int> DnpPrintAsync(string imagePath, int portNumber, int overcoat, int copies)
        {
            EnvoisDirects.Add((imagePath, portNumber, overcoat, copies));
            return Task.FromResult(copies);
        }

        /// <summary>
        /// Surface chargée machine par machine — le cas du DE100 de la boutique, dont les
        /// deux machines portent justement des rouleaux différents (brillant d'un côté,
        /// lustré de l'autre). Ce qui n'y figure pas retombe sur <see cref="SurfaceParDefaut"/>.
        /// </summary>
        public Dictionary<char, De100Surface> Surfaces { get; } = [];

        /// <summary>
        /// Ce que rend une machine dont l'essai ne dit rien de la surface. Null = elle ne
        /// sait pas, le cas d'un pont qui ne décrit pas le média.
        /// </summary>
        public De100Surface? SurfaceParDefaut { get; set; } = De100Surface.Lustre;

        /// <summary>Machines qui ne répondent pas quand on les interroge sur leur rouleau.</summary>
        public HashSet<char> Muettes { get; } = [];

        public De100Surface? LoadedSurface(char machineId)
        {
            if (Muettes.Contains(machineId))
                throw new InvalidOperationException($"Minilab {machineId} : pas de réponse.");

            return Surfaces.TryGetValue(machineId, out var surface) ? surface : SurfaceParDefaut;
        }

        /// <summary>Rouleau chargé ; 0 par défaut = largeur inconnue, aucun contrôle.</summary>
        public int PaperWidthMm { get; set; }

        /// <summary>
        /// Rouleau propre à une machine, quand elles n'ont pas le même — le cas normal sur
        /// le DE100 de la boutique, qui en compte deux justement pour cela. Ce qui n'y
        /// figure pas retombe sur <see cref="PaperWidthMm"/>.
        /// </summary>
        public Dictionary<char, int> Rouleaux { get; } = [];

        public int LoadedPaperWidthMm(char machineId) =>
            Rouleaux.TryGetValue(machineId, out var largeur) ? largeur : PaperWidthMm;

        /// <summary>
        /// Définition que la machine réclame, en pixels. Vide = elle n'en dit rien, et
        /// l'orchestrateur garde son propre calcul — le cas de toutes les machines qui ne
        /// répondent pas, et celui de tous les essais qui ne s'y intéressent pas.
        /// </summary>
        public Dictionary<(double W, double H), (uint W, uint H)> Definitions { get; } = [];

        public (uint Width, uint Height) ExpectedPixels(
            char machineId, double widthMm, double heightMm, uint dpi) =>
            Definitions.TryGetValue((widthMm, heightMm), out var px) ? px : (0, 0);

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

        /// <summary>Pas de machine, donc pas de compte de tirages : le suivi s'en passe.</summary>
        public Task<De100OrderProgress?> OrderProgressAsync(string orderHandle) =>
            Task.FromResult<De100OrderProgress?>(null);
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

    // — choix de la machine selon le ROULEAU chargé —

    /// <summary>
    /// Rouleaux montés sur le DE100 de la boutique, par machine.
    /// </summary>
    private static Func<char, int> Rouleaux(params (char Machine, int LargeurMm)[] montes) =>
        machine => montes.FirstOrDefault(m => m.Machine == machine).LargeurMm;

    /// <summary>Le format cherché : vrai si le rouleau est assez large.</summary>
    private static Func<int, bool> AuMoins(int millimetres) => largeur => largeur >= millimetres;

    /// <summary>
    /// LE défaut du 04/08/2026 : un 21×29,7 refusé « le rouleau chargé dans la machine A
    /// fait 152 mm » (commandes 04-010 et 04-014) alors que le rouleau de 210 était monté
    /// sur la machine B, juste à côté. Les deux machines du DE100 n'ont jamais le même
    /// rouleau — c'est tout l'intérêt d'en avoir deux.
    /// </summary>
    [Fact]
    public void La_machine_est_choisie_selon_le_rouleau_quon_y_a_monte()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 210)),
            AuMoins(210));

        Assert.Equal(('B', 210), choix);
    }

    /// <summary>
    /// À format égal, rien ne change : la machine par défaut est examinée en premier et
    /// gagne dès que son rouleau convient. Un 10×15 ne doit pas se mettre à sortir de
    /// l'autre machine parce qu'elle porte un rouleau plus large.
    /// </summary>
    [Fact]
    public void La_machine_par_defaut_garde_la_main_quand_son_rouleau_convient()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 210)),
            AuMoins(102));

        Assert.Equal(('A', 152), choix);
    }

    /// <summary>
    /// Aucun rouleau ne convient : on rend le premier qui a répondu, pour
    /// qu'<c>EnsurePaperFits</c> puisse nommer la machine et le rouleau à charger. Rendre
    /// « rien » ferait perdre cette explication, la seule utile à l'opérateur.
    /// </summary>
    [Fact]
    public void Sans_rouleau_convenable_on_garde_la_machine_par_defaut_pour_le_refus()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 127)),
            AuMoins(210));

        Assert.Equal(('A', 152), choix);
    }

    // — le NOM du format envoyé au minilab —

    /// <summary>
    /// <b>Le défaut du 21×29,7, du 04/08/2026.</b> Studio fabrique un nom d'après le
    /// rouleau — « 210x297 » —, et le DE100 l'a refusé six fois de suite sans donner le
    /// moindre motif, pendant que le 18×24 sortait de la MÊME machine sur le MÊME rouleau.
    ///
    /// La base de DiLand a tranché : ses canaux de sortie pour un rouleau de 210 sont
    /// <c>21xS</c> (50 à 210 mm, variable), <c>21xL</c> (210 à 1000 mm, variable) et
    /// <c>A4</c> (297 mm exactement, <b>NON variable</b>). Le 18×24 part en « 210x240 » et
    /// tombe dans le canal variable ; le 21×29,7 fait exactement 297 et correspond au
    /// canal FIXE, que la machine exige par son nom.
    ///
    /// D'où <c>MinilabPrintSizeName = "A4"</c> sur les produits 210 × 297 du catalogue.
    /// </summary>
    [Fact]
    public void Le_nom_de_format_du_produit_l_emporte_sur_celui_deduit_du_rouleau()
    {
        var produit = Minilab("21x29-7");
        produit.MinilabPrintSizeName = "A4";

        Assert.Equal("A4", PrintOrchestrator.MinilabSizeName(produit, 210, 297));
    }

    /// <summary>
    /// Sans nom imposé, on garde celui qu'on déduit du rouleau : c'est ce qui sort des
    /// centaines de fois par jour sur la machine A, et le 18×24 en dépend aussi.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sans_nom_impose_le_format_se_deduit_du_rouleau(string? nom)
    {
        var produit = Minilab("18x24");
        produit.MinilabPrintSizeName = nom;

        Assert.Equal("210x240", PrintOrchestrator.MinilabSizeName(produit, 210, 240));
    }

    // — la machine à NOMMER quand celle qu'on a imposée ne porte pas le format —

    /// <summary>
    /// Une machine imposée n'est jamais détournée : c'est l'opérateur qui l'a choisie, et
    /// lui seul sait quel rouleau il vient de monter. Mais le refus doit dire que la
    /// machine d'à côté porterait le format — sans quoi il envoie changer un rouleau qui
    /// tourne déjà à deux mètres, ce qu'il faisait le 04/08/2026 sur le 21×29,7.
    /// </summary>
    [Fact]
    public void Le_refus_nomme_la_machine_voisine_qui_porte_le_format()
    {
        var trouvee = PrintOrchestrator.MachineQuiPorteLeFormat(
            ['A', 'B'], visee: 'A',
            Rouleaux(('A', 152), ('B', 210)),
            AuMoins(210));

        Assert.Equal(('B', 210), trouvee);
    }

    /// <summary>
    /// Aucune voisine ne convient : on ne propose rien, et le refus retombe sur le rouleau
    /// à charger. Proposer une machine au hasard enverrait tirer un 21×29,7 sur du 127.
    /// </summary>
    [Fact]
    public void Aucune_voisine_ne_porte_le_format_rien_n_est_propose()
    {
        var trouvee = PrintOrchestrator.MachineQuiPorteLeFormat(
            ['A', 'B'], visee: 'A',
            Rouleaux(('A', 152), ('B', 127)),
            AuMoins(210));

        Assert.Equal(PrintOrchestrator.Aucune, trouvee.Machine);
    }

    /// <summary>
    /// Un rouleau de largeur INCONNUE n'est jamais proposé : « la machine B porte du 0 mm »
    /// serait pire que se taire. <c>Rouleaux</c> rend 0 pour une machine qu'il ne connaît
    /// pas, ce qui est exactement ce que la machine répond quand elle n'en dit rien.
    /// </summary>
    [Fact]
    public void Une_largeur_inconnue_n_est_pas_proposee()
    {
        var trouvee = PrintOrchestrator.MachineQuiPorteLeFormat(
            ['A', 'B'], visee: 'A',
            Rouleaux(('A', 152)),
            AuMoins(210));

        Assert.Equal(PrintOrchestrator.Aucune, trouvee.Machine);
    }

    /// <summary>Une voisine muette est sautée : on est déjà dans un chemin d'échec.</summary>
    [Fact]
    public void Une_voisine_qui_ne_repond_pas_est_sautee_a_son_tour()
    {
        var trouvee = PrintOrchestrator.MachineQuiPorteLeFormat(
            ['A', 'B', 'C'], visee: 'A',
            machine => machine == 'B'
                ? throw new InvalidOperationException("La machine n'a pas répondu en 10 s.")
                : 210,
            AuMoins(210));

        Assert.Equal(('C', 210), trouvee);
    }

    /// <summary>
    /// Une machine endormie ne bloque pas : le DE100 en compte deux, dont une souvent
    /// éteinte, et c'est justement le cas où l'autre doit prendre le tirage.
    /// </summary>
    [Fact]
    public void Une_machine_qui_ne_repond_pas_est_sautee()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            machine => machine == 'A'
                ? throw new InvalidOperationException("La machine n'a pas répondu en 10 s.")
                : 210,
            AuMoins(210));

        Assert.Equal(('B', 210), choix);
    }

    /// <summary>
    /// Aucune machine n'a répondu : le choix ne rend rien, et l'appelant met la commande
    /// en attente au lieu de tirer dans le vide.
    /// </summary>
    [Fact]
    public void Aucune_machine_ne_repond_le_choix_ne_rend_rien()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            _ => throw new InvalidOperationException("La machine n'a pas répondu en 10 s."),
            AuMoins(210));

        Assert.Null(choix);
    }

    /// <summary>
    /// Largeur inconnue (machine avare en informations) : on ne bloque rien, comme
    /// <c>EnsurePaperFits</c>. La machine par défaut passe donc, et le contrôle de papier
    /// ne s'exercera pas.
    /// </summary>
    [Fact]
    public void Une_largeur_inconnue_laisse_passer_la_machine_par_defaut()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('B', 210)),
            AuMoins(210));

        Assert.Equal(('A', 0), choix);
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
