using ImageMagick;
using Studio.Core.Catalog;
using Studio.Core.Domain;
using Studio.Printing;
using Studio.Printing.Devices.Fuji;
using Studio.Store;

namespace Studio.Tests;

/// <summary>
/// La finition choisie par le client à la borne, et le rouleau sur lequel son tirage doit
/// sortir.
///
/// <b>Ce que ces essais protègent.</b> Le DE100 de la boutique compte deux machines qui
/// portent des rouleaux différents — brillant sur l'une, lustré sur l'autre — et la
/// finition n'est pas un réglage d'impression : c'est le PAPIER. Studio choisissait sa
/// machine sur la seule largeur du rouleau, si bien qu'une commande lustrée partait sur la
/// machine brillante dès qu'elle portait le bon format. Le tirage sortait propre, au bon
/// format, et c'est le client qui découvrait la mauvaise surface en ouvrant sa pochette :
/// une erreur qu'aucun contrôle en aval ne rattrape.
///
/// Une finition ne se vérifie autrement qu'en gâchant du papier, d'où ces essais.
/// </summary>
public class FinitionMinilabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "StudioFinition-" + Guid.NewGuid().ToString("N"));

    public FinitionMinilabTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // — le vocabulaire, d'un bout à l'autre de la chaîne —

    /// <summary>
    /// Les deux lectures de DiLand doivent rendre la MÊME finition : la base quand DiLand
    /// tourne, le fichier de la borne quand il est fermé. Si elles divergeaient, une
    /// commande changerait de rouleau selon l'heure à laquelle on la reprend.
    /// </summary>
    [Theory]
    [InlineData(1, "Glossy")]
    [InlineData(2, "Matte")]
    [InlineData(3, "Luster")]
    public void La_base_et_le_fichier_de_la_borne_disent_la_meme_finition(int code, string nom)
    {
        Assert.Equal(FinitionPapier.DepuisDiLand(code), FinitionPapier.DepuisDiLand(nom));
        Assert.NotNull(FinitionPapier.DepuisDiLand(code));
    }

    /// <summary>
    /// Un code ou un mot qu'on ne sait pas traduire n'impose RIEN. C'est la règle qui
    /// permet de brancher Studio sur une autre installation de DiLand sans la connaître :
    /// au pire on perd l'exigence de finition, jamais la commande.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void Un_code_papier_inconnu_n_impose_aucune_finition(int code)
    {
        Assert.Null(FinitionPapier.DepuisDiLand(code));
    }

    [Theory]
    [InlineData("Undefined")]
    [InlineData("")]
    [InlineData(null)]
    public void Un_mot_de_papier_inconnu_n_impose_aucune_finition(string? nom)
    {
        Assert.Null(FinitionPapier.DepuisDiLand(nom));
    }

    // — du nom à la surface —

    /// <summary>
    /// Les noms que le client et l'opérateur emploient réellement, et la surface que le
    /// minilab attend derrière. La reconnaissance porte sur les MOTS : « format » contient
    /// « mat », et une finition annoncée de travers coûte le rouleau.
    /// </summary>
    [Theory]
    [InlineData("Brillant", De100Surface.Glossy)]
    [InlineData("brillant", De100Surface.Glossy)]
    [InlineData("Glossy", De100Surface.Glossy)]
    [InlineData("Lustré", De100Surface.Lustre)]
    [InlineData("lustre", De100Surface.Lustre)]
    [InlineData("Luster", De100Surface.Lustre)]
    [InlineData("Mat", De100Surface.Matte)]
    [InlineData("Mat fin", De100Surface.FineArtMatte)]
    public void Le_nom_d_une_finition_donne_la_surface_du_rouleau(string nom, De100Surface attendue)
    {
        Assert.Equal(attendue, PrintOrchestrator.FinitionMinilab(nom));
    }

    /// <summary>
    /// <b>Rien de demandé, rien d'imposé</b> — et c'est le cas le plus fréquent, celui de
    /// tout le comptoir. Une finition inconnue ne doit surtout pas se transformer en
    /// exigence : elle ferait refuser des commandes qu'on tirait sans rien dire la veille.
    ///
    /// C'est ce qui distingue cette règle de celle de la DNP, qui doit bien annoncer
    /// quelque chose à la machine et retombe donc sur le brillant.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Format A4")]        // « format » contient « mat » : surtout pas du mat
    [InlineData("Papier du client")]
    public void Une_finition_absente_ou_incomprise_n_impose_aucun_rouleau(string? nom)
    {
        Assert.Null(PrintOrchestrator.FinitionMinilab(nom));
    }

    // — le choix de la machine —

    private static Func<char, int> Rouleaux(params (char Machine, int Largeur)[] rouleaux) =>
        machine => rouleaux.First(r => r.Machine == machine).Largeur;

    private static Func<char, bool> Surfaces(params char[] quiPortentLaFinition) =>
        quiPortentLaFinition.Contains;

    /// <summary>
    /// Le cas de la boutique : la machine A est en brillant, la B en lustré, et les deux
    /// portent le format. Une commande lustrée doit partir sur la B <b>même si la A est
    /// la machine par défaut</b> — c'est tout l'objet de la correction.
    /// </summary>
    [Fact]
    public void Une_commande_lustree_part_sur_la_machine_qui_porte_le_lustre()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 152)),
            porteLeFormat: _ => true,
            porteLaFinition: Surfaces('B'));

        Assert.Equal(('B', 152), choix);
    }

    /// <summary>À finition égale, rien ne change : la machine par défaut reste choisie.</summary>
    [Fact]
    public void A_finition_egale_la_machine_par_defaut_reste_choisie()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 152)),
            porteLeFormat: _ => true,
            porteLaFinition: Surfaces('A', 'B'));

        Assert.Equal(('A', 152), choix);
    }

    /// <summary>
    /// La finition ne fait pas oublier le format : la machine retenue doit porter les
    /// deux. Une machine lustrée dont le rouleau est trop étroit ne sert à rien — le
    /// tirage n'en sortirait pas.
    /// </summary>
    [Fact]
    public void La_machine_retenue_doit_porter_le_format_ET_la_finition()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B', 'C'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 102), ('C', 210)),
            porteLeFormat: largeur => largeur >= 210,
            porteLaFinition: Surfaces('B', 'C'));

        Assert.Equal(('C', 210), choix);
    }

    /// <summary>
    /// Aucune machine ne porte la finition : on retombe sur le choix par le format. La
    /// machine rendue n'est pas la bonne, et c'est voulu — c'est elle que le refus
    /// nommera, avec ce qu'elle a réellement chargé.
    /// </summary>
    [Fact]
    public void Sans_machine_a_la_bonne_finition_le_choix_retombe_sur_le_format()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 152)),
            porteLeFormat: _ => true,
            porteLaFinition: Surfaces());

        Assert.Equal(('A', 152), choix);
    }

    /// <summary>
    /// Sans finition demandée, le choix est EXACTEMENT celui d'avant. C'est la garantie
    /// que le comptoir, qui ne nomme jamais de finition, n'a rien vu changer.
    /// </summary>
    [Fact]
    public void Sans_finition_demandee_le_choix_est_celui_d_avant()
    {
        var avant = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 210)),
            porteLeFormat: largeur => largeur >= 210);

        var apres = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            Rouleaux(('A', 152), ('B', 210)),
            porteLeFormat: largeur => largeur >= 210,
            porteLaFinition: null);

        Assert.Equal(avant, apres);
        Assert.Equal(('B', 210), apres);
    }

    /// <summary>
    /// Une machine muette est sautée sans bloquer : elle ne doit pas empêcher sa voisine
    /// de prendre le travail. Une machine endormie est le cas ordinaire, pas une panne.
    /// </summary>
    [Fact]
    public void Une_machine_muette_ne_bloque_pas_le_choix()
    {
        var choix = PrintOrchestrator.ChoisirSelonLeRouleau(
            ['A', 'B'], defaut: 'A',
            machine => machine == 'A' ? throw new InvalidOperationException("endormie") : 152,
            porteLeFormat: _ => true,
            porteLaFinition: Surfaces('A', 'B'));

        Assert.Equal(('B', 152), choix);
    }

    // — le tirage lui-même —

    /// <summary>Minilab factice à deux machines, chacune avec son rouleau et sa surface.</summary>
    private sealed class FauxMinilab : IMinilabPrinter
    {
        public FauxMinilab(params char[] pretes) => Pretes = pretes;

        private IReadOnlyList<char> Pretes { get; }

        /// <summary>Surface par machine. Absente = la machine ne sait pas la dire.</summary>
        public Dictionary<char, De100Surface> Surfaces { get; } = [];

        /// <summary>Envois reçus : les tirages et la machine qui les a pris.</summary>
        public List<(IReadOnlyList<De100PrintJob> Jobs, char Machine)> Envoyes { get; } = [];

        public IReadOnlyList<char> ReadyMachines() => Pretes;

        public De100Surface? LoadedSurface(char machineId) =>
            Surfaces.TryGetValue(machineId, out var surface) ? surface : null;

        public int LoadedPaperWidthMm(char machineId) => 152;

        public Task<IReadOnlyList<Studio.Printing.Devices.Dnp.DnpPrinterInfo>> DnpSnapshotAsync() =>
            Task.FromResult<IReadOnlyList<Studio.Printing.Devices.Dnp.DnpPrinterInfo>>([]);

        public Task<int> DnpPrintAsync(string imagePath, int portNumber, int overcoat, int copies) =>
            Task.FromResult(0);

        public (uint Width, uint Height) ExpectedPixels(
            char machineId, double widthMm, double heightMm, uint dpi) => (0, 0);

        public string Submit(IReadOnlyList<De100PrintJob> jobs, char machineId)
        {
            Envoyes.Add((jobs, machineId));
            return $"OH-{Envoyes.Count}";
        }

        public void Cancel(string orderHandle) { }

        /// <summary>Pas de machine, donc pas de compte de tirages : le suivi s'en passe.</summary>
        public Task<De100OrderProgress?> OrderProgressAsync(string orderHandle) =>
            Task.FromResult<De100OrderProgress?>(null);
    }

    private static Product Produit() => new()
    {
        Code = "10x15",
        Name = "10x15",
        WidthMm = 102,
        HeightMm = 152,
        Dpi = 300,
        PrinterName = "",
        PrinterChannel = "Minilab DE100",
        Output = ProductOutput.FujiMinilab,
        Price = 0.60m,
    };

    private OrderFolderStore _store = null!;

    private PrintOrchestrator Orchestrateur(IMinilabPrinter minilab)
    {
        _store = new OrderFolderStore(Path.Combine(_root, "orders"));
        var catalogDir = Path.Combine(_root, "catalog");
        Directory.CreateDirectory(catalogDir);
        return new PrintOrchestrator(new ProductCatalog([Produit()]), _store, catalogDir, minilab);
    }

    /// <summary>Une commande d'un tirage, avec un vrai fichier à rendre.</summary>
    private Order Commande(string? finition)
    {
        var envelope = new Envelope { Number = 1, PrinterChannel = "Minilab DE100" };
        var ligne = new OrderLine { ProductCode = "10x15", UnitPrice = 0.60m };
        var order = new Order { DailyNumber = 1, Source = "Borne", Envelopes = { envelope } };
        envelope.Lines.Add(ligne);

        var photosDir = _store.GetPhotosFolder(order);
        Directory.CreateDirectory(photosDir);
        using (var image = new MagickImage(MagickColors.CadetBlue, 900, 600))
            image.Write(Path.Combine(photosDir, "001.jpg"));

        ligne.Items.Add(new OrderItem
        {
            FileName = "001.jpg",
            OriginalName = "001.jpg",
            Quantity = 1,
            Finish = finition,
        });

        return order;
    }

    /// <summary>
    /// Le tirage complet : une commande lustrée arrivée d'une borne doit sortir de la
    /// machine qui porte le lustré, et s'annoncer en lustré.
    /// </summary>
    [Fact]
    public void Un_tirage_lustre_sort_de_la_machine_lustree()
    {
        var minilab = new FauxMinilab('A', 'B')
        {
            Surfaces = { ['A'] = De100Surface.Glossy, ['B'] = De100Surface.Lustre },
        };
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        var (jobs, machine) = Assert.Single(minilab.Envoyes);
        Assert.Equal('B', machine);
        Assert.Equal(De100Surface.Lustre, jobs[0].Surface);
    }

    /// <summary>Le même tirage en brillant part sur l'autre machine, sans rien changer d'autre.</summary>
    [Fact]
    public void Un_tirage_brillant_sort_de_la_machine_brillante()
    {
        var minilab = new FauxMinilab('A', 'B')
        {
            Surfaces = { ['A'] = De100Surface.Glossy, ['B'] = De100Surface.Lustre },
        };
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(FinitionPapier.Brillant);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        var (jobs, machine) = Assert.Single(minilab.Envoyes);
        Assert.Equal('A', machine);
        Assert.Equal(De100Surface.Glossy, jobs[0].Surface);
    }

    /// <summary>
    /// Aucune machine n'a le rouleau demandé : <b>le tirage part quand même</b>, et
    /// l'opérateur est prévenu. Bloquer arrêterait la boutique, et lui seul sait si ce
    /// client-là acceptera du brillant. Ce qu'on lui doit, c'est de ne pas le laisser
    /// l'apprendre par le client.
    /// </summary>
    [Fact]
    public void Sans_le_bon_rouleau_le_tirage_part_mais_previent()
    {
        var minilab = new FauxMinilab('A', 'B')
        {
            Surfaces = { ['A'] = De100Surface.Glossy, ['B'] = De100Surface.Glossy },
        };
        var orchestrateur = Orchestrateur(minilab);
        var avertissements = new List<string>();
        orchestrateur.Avertir = avertissements.Add;
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.Single(minilab.Envoyes);

        var message = Assert.Single(avertissements);
        Assert.Contains("lustré", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("brillant", message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// L'avertissement doit être VU : il part par <c>Avertir</c>, qui le pose dans le
    /// bandeau, et pas seulement par le journal. Un avertissement qui ne va que dans un
    /// fichier n'avertit personne.
    /// </summary>
    [Fact]
    public void L_avertissement_ne_se_contente_pas_du_journal()
    {
        var minilab = new FauxMinilab('A') { Surfaces = { ['A'] = De100Surface.Glossy } };
        var orchestrateur = Orchestrateur(minilab);
        var vus = new List<string>();
        orchestrateur.Avertir = vus.Add;
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.NotEmpty(vus);
    }

    /// <summary>
    /// L'avertissement doit dire QUOI FAIRE. Quand une machine voisine porte le rouleau,
    /// c'est elle qu'il faut nommer : envoyer changer un rouleau qui tourne à deux mètres
    /// est la pire des réponses, et c'est ce que faisait déjà le refus de format avant
    /// qu'on le corrige.
    /// </summary>
    [Fact]
    public void L_avertissement_nomme_la_machine_voisine_qui_porte_le_rouleau()
    {
        // la machine imposée est la A, en brillant ; la B a bien le lustré
        var minilab = new FauxMinilab('A', 'B')
        {
            Surfaces = { ['A'] = De100Surface.Glossy, ['B'] = De100Surface.Lustre },
        };
        var orchestrateur = Orchestrateur(minilab);
        var avertissements = new List<string>();
        orchestrateur.Avertir = avertissements.Add;
        orchestrateur.PreferredMinilabMachine = "A";
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        // le choix de l'opérateur ne se discute pas : le tirage part bien sur la A
        Assert.Equal('A', Assert.Single(minilab.Envoyes).Machine);
        Assert.Contains("machine B", Assert.Single(avertissements));
    }

    /// <summary>
    /// <b>Aucun bruit quand tout va bien.</b> Un avertissement qui tombe sur des tirages
    /// corrects finit ignoré, et le jour où il compte vraiment personne ne le lit.
    /// </summary>
    [Fact]
    public void Le_bon_rouleau_ne_declenche_aucun_avertissement()
    {
        var minilab = new FauxMinilab('A', 'B')
        {
            Surfaces = { ['A'] = De100Surface.Glossy, ['B'] = De100Surface.Lustre },
        };
        var orchestrateur = Orchestrateur(minilab);
        var avertissements = new List<string>();
        orchestrateur.Avertir = avertissements.Add;
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.Equal('B', Assert.Single(minilab.Envoyes).Machine);
        Assert.Empty(avertissements);
    }

    /// <summary>Une machine muette sur son média ne déclenche pas d'alerte en l'air.</summary>
    [Fact]
    public void Une_surface_inconnue_ne_declenche_aucun_avertissement()
    {
        var minilab = new FauxMinilab('A');   // aucune surface déclarée
        var orchestrateur = Orchestrateur(minilab);
        var avertissements = new List<string>();
        orchestrateur.Avertir = avertissements.Add;
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.Single(minilab.Envoyes);
        Assert.Empty(avertissements);
    }

    /// <summary>
    /// <b>Portabilité.</b> Une machine qui ne décrit pas son média ne doit RIEN bloquer :
    /// le tirage part comme avant, sur le rouleau chargé. Sans cette règle, un magasin
    /// dont le pont DE100 est plus avare que le nôtre verrait toutes ses commandes de
    /// bornes refusées — pour une information qu'il n'a jamais eue.
    /// </summary>
    [Fact]
    public void Une_machine_qui_ignore_sa_surface_n_empeche_pas_de_tirer()
    {
        var minilab = new FauxMinilab('A');   // aucune surface déclarée
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        var (jobs, machine) = Assert.Single(minilab.Envoyes);
        Assert.Equal('A', machine);

        // la machine attend une valeur : on retombe sur le brillant, comme avant
        Assert.Equal(De100Surface.Glossy, jobs[0].Surface);
    }

    /// <summary>
    /// <b>Portabilité.</b> Un magasin à une seule machine tire comme avant tant qu'il a le
    /// bon rouleau — rien dans la règle ne suppose qu'il y en ait deux.
    /// </summary>
    [Fact]
    public void Un_magasin_a_une_seule_machine_tire_comme_avant()
    {
        var minilab = new FauxMinilab('A') { Surfaces = { ['A'] = De100Surface.Lustre } };
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(FinitionPapier.Lustre);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        Assert.Single(minilab.Envoyes);
    }

    /// <summary>
    /// Sans finition — tout le comptoir — le tirage part sur la machine par défaut et
    /// s'annonce avec le rouleau chargé, exactement comme avant la correction.
    /// </summary>
    [Fact]
    public void Sans_finition_le_tirage_part_comme_avant()
    {
        var minilab = new FauxMinilab('A', 'B')
        {
            Surfaces = { ['A'] = De100Surface.Glossy, ['B'] = De100Surface.Lustre },
        };
        var orchestrateur = Orchestrateur(minilab);
        var commande = Commande(finition: null);

        orchestrateur.PrintEnvelope(commande, commande.Envelopes[0]);

        var (jobs, machine) = Assert.Single(minilab.Envoyes);
        Assert.Equal('A', machine);
        Assert.Equal(De100Surface.Glossy, jobs[0].Surface);
    }
}
