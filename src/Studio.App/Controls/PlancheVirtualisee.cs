using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Studio.Imaging.Geometry;

namespace Studio.App.Controls;

/// <summary>
/// La planche de vignettes, qui ne fabrique que les tuiles réellement sous les yeux.
///
/// <b>Ce qu'elle remplace.</b> Un <c>WrapPanel</c> dans un <c>ScrollViewer</c> : cet
/// assemblage-là ne virtualise rien, quoi qu'on lui demande — <c>WrapPanel</c> n'est pas un
/// <see cref="VirtualizingPanel"/>, et la propriété <c>IsVirtualizing</c> posée sur la liste
/// n'y change rien. Toutes les tuiles étaient donc construites à l'ouverture du dossier.
///
/// Sur une carte pleine (1200 photos, le plafond de <c>PhotoScanner.MaxAffichable</c>), cela
/// faisait douze cents sous-arbres visuels d'une quinzaine d'éléments — bordures, images,
/// étiquettes, boutons —, soit près de vingt mille objets construits, mesurés et disposés
/// avant que le premier pixel ne s'affiche. Et douze cents vignettes décodées tenues en
/// mémoire vive pour la trentaine qu'un écran montre.
///
/// <b>Ce qu'elle fait.</b> Elle demande à <see cref="GrilleVirtuelle"/> quelles tuiles la
/// zone visible recouvre, ne fabrique que celles-là, et défait les autres. Le défilement
/// reste au pixel — c'est elle qui le porte, par <see cref="IScrollInfo"/> —, si bien que
/// rien ne change pour l'opérateur : même planche, même molette, même barre.
///
/// <b>Les tuiles sont toutes de la même taille</b>, et c'est ce qui rend le calcul exact :
/// le gabarit leur donne des cotes fixes. On en mesure donc UNE, et la position de toutes
/// les autres s'en déduit sans avoir à les construire — c'est précisément ce qu'on cherche à
/// éviter.
///
/// <b>Virtualisation « standard », sans recyclage.</b> Le recyclage économiserait quelques
/// constructions de conteneurs pendant le défilement ; il demande en échange de suivre des
/// conteneurs qui changent de rang sous les pieds du panneau. Le gain se compte ici en
/// dizaines de tuiles, l'économie déjà obtenue en milliers : ce n'est pas là qu'est le sujet.
/// </summary>
public sealed class PlancheVirtualisee : VirtualizingPanel, IScrollInfo
{
    // — le zoom, posé sur la liste et hérité jusqu'ici —

    /// <summary>
    /// Échelle des tuiles, de « réduire » à « agrandir ».
    ///
    /// Elle est ATTACHÉE et héritée : l'écran la pose sur la liste (<c>PhotosGrid</c>), qui
    /// n'a aucun moyen de désigner son panneau — celui-ci naît d'un
    /// <see cref="ItemsPanelTemplate"/>, où un nom ne donne pas de champ. La valeur descend
    /// donc l'arbre jusqu'ici toute seule.
    ///
    /// Elle remplace le <c>ScaleTransform</c> qui coiffait la liste entière : posé là, il
    /// mettait aussi la barre de défilement à l'échelle, et surtout il empêchait le panneau
    /// de connaître la taille réelle de ses tuiles.
    /// </summary>
    public static readonly DependencyProperty EchelleProperty =
        DependencyProperty.RegisterAttached(
            "Echelle", typeof(double), typeof(PlancheVirtualisee),
            new FrameworkPropertyMetadata(
                1.0, FrameworkPropertyMetadataOptions.Inherits, SurChangementDEchelle));

    public static void SetEchelle(DependencyObject cible, double valeur)
    {
        ArgumentNullException.ThrowIfNull(cible);
        cible.SetValue(EchelleProperty, valeur);
    }

    public static double GetEchelle(DependencyObject cible)
    {
        ArgumentNullException.ThrowIfNull(cible);
        return (double)cible.GetValue(EchelleProperty);
    }

    private static void SurChangementDEchelle(DependencyObject cible, DependencyPropertyChangedEventArgs e)
    {
        if (cible is not PlancheVirtualisee planche) return;

        planche._zoom.ScaleX = planche._zoom.ScaleY = Math.Max(0.05, (double)e.NewValue);

        // les tuiles changent de taille : la mesure qu'on gardait ne vaut plus, et le
        // nombre de colonnes non plus
        planche._tailleTuile = Size.Empty;
        planche.InvalidateMeasure();
    }

    /// <summary>
    /// Le zoom, en UN SEUL objet partagé par toutes les tuiles.
    ///
    /// Posé en <c>LayoutTransform</c> sur chaque conteneur, il fait entrer l'échelle dans la
    /// taille MESURÉE de la tuile : le panneau n'a donc rien à multiplier lui-même, et la
    /// grille se recompose d'elle-même quand l'opérateur zoome. Partagé, il suffit d'en
    /// changer les facteurs pour que toutes les tuiles suivent, y compris celles que le
    /// défilement fabriquera plus tard.
    /// </summary>
    private readonly ScaleTransform _zoom = new(1, 1);

    // — la géométrie du moment —

    /// <summary>Cotes d'une tuile, zoom compris. <see cref="Size.Empty"/> tant qu'aucune n'a été mesurée.</summary>
    private Size _tailleTuile = Size.Empty;

    private int _colonnes = 1;
    private Size _zoneVisible;
    private Size _etendue;
    private double _decalage;

    /// <summary>Nombre de tuiles de la planche, zéro si la liste n'en a pas encore.</summary>
    private static int Compte(ItemsControl? liste) => liste?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var liste = ItemsControl.GetItemsOwner(this);
        var tuiles = Compte(liste);

        // toucher aux enfants force la création du générateur : sans cela, aucun conteneur
        // ne peut être fabriqué
        _ = InternalChildren;

        if (tuiles == 0)
        {
            DefaireLesTuiles(0, -1);
            _tailleTuile = Size.Empty;
            MettreAJourLeDefilement(availableSize, 0);
            return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 0);
        }

        // Une tuile est mesurée AVANT toutes les autres, et c'est elle qui donne la grille :
        // combien de colonnes, quelle hauteur de rangée, donc quelles tuiles sont visibles.
        // Elles sont toutes identiques — le gabarit leur donne des cotes fixes.
        if (!TuileConnue) _tailleTuile = MesurerUneTuile();

        // La mesure n'a rien donné de sensé — gabarit pas encore appliqué, tuile sans cotes.
        // On ne la RETIENT surtout pas : une taille nulle mise en cache donnerait zéro rangée
        // à toutes les mesures suivantes, et la planche resterait blanche pour de bon. On
        // pose la grille d'une colonne pour ce passage, et on remesurera au suivant.
        if (!TuileConnue)
        {
            _colonnes = 1;
            _tailleTuile = Size.Empty;
            MettreAJourLeDefilement(availableSize, 0);

            // et on repasse dès que l'arbre visuel est posé, sans quoi rien ne redemanderait
            // jamais de mesure et la planche resterait vide
            Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded, InvalidateMeasure);

            return new Size(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, 0);
        }

        _colonnes = GrilleVirtuelle.Colonnes(availableSize.Width, _tailleTuile.Width);

        // Le défilement est reborné AVANT de choisir la tranche, et non après : la fenêtre a
        // pu s'agrandir ou la planche raccourcir depuis le dernier passage, et fabriquer des
        // tuiles d'après un décalage périmé laisserait un blanc sous la dernière rangée.
        MettreAJourLeDefilement(availableSize,
            GrilleVirtuelle.Hauteur(tuiles, _colonnes, _tailleTuile.Height));

        var tranche = GrilleVirtuelle.Tranche(
            tuiles, availableSize.Width, _zoneVisible.Height,
            _tailleTuile.Width, _tailleTuile.Height, _decalage);

        FabriquerLesTuiles(tranche.Premier, tranche.Dernier);
        DefaireLesTuiles(tranche.Premier, tranche.Dernier);

        // La hauteur RENDUE est celle de la zone visible, pas celle de la planche entière :
        // c'est le panneau qui porte le défilement (IScrollInfo), et annoncer la hauteur
        // totale ferait grandir la fenêtre au lieu de faire défiler.
        return new Size(
            double.IsInfinity(availableSize.Width) ? _colonnes * _tailleTuile.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? _etendue.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        // sans cotes de tuile, il n'y a rien à poser — et surtout pas un rectangle bâti sur
        // Size.Empty, dont les cotes valent moins l'infini
        if (!TuileConnue) return finalSize;

        var generateur = ItemContainerGenerator;

        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var enfant = InternalChildren[i];
            var rang = generateur.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (rang < 0) continue;

            var (x, y) = GrilleVirtuelle.Position(
                rang, _colonnes, _tailleTuile.Width, _tailleTuile.Height);

            // le décalage est retranché ici, et nulle part ailleurs : les tuiles vivent dans
            // le repère de la planche entière, l'écran n'en montre qu'une fenêtre glissante
            enfant.Arrange(new Rect(x, y - _decalage, _tailleTuile.Width, _tailleTuile.Height));
        }

        return finalSize;
    }

    /// <summary>
    /// Fabrique la première tuile pour la mesurer, et rien de plus.
    ///
    /// C'est l'œuf et la poule : savoir quelles tuiles sont visibles demande de connaître
    /// leur taille, et la connaître demande d'en avoir fabriqué une. On en fabrique donc une
    /// — la première — que la mesure qui suit gardera de toute façon si elle est visible.
    /// </summary>
    private Size MesurerUneTuile()
    {
        FabriquerLesTuiles(0, 0);

        return InternalChildren.Count > 0 ? InternalChildren[0].DesiredSize : Size.Empty;
    }

    /// <summary>
    /// Vrai quand la tuile a été mesurée et que ses cotes tiennent debout. <c>Size.Empty</c>
    /// n'est pas le seul cas à écarter : une tuile mesurée à zéro ne l'est pas non plus.
    /// </summary>
    private bool TuileConnue =>
        !_tailleTuile.IsEmpty && _tailleTuile.Width > 0 && _tailleTuile.Height > 0;

    /// <summary>
    /// Construit les conteneurs de <paramref name="premier"/> à <paramref name="dernier"/>,
    /// bornes comprises, et les mesure.
    ///
    /// Chacun reçoit le <see cref="_zoom"/> partagé : sa taille mesurée porte alors déjà
    /// l'échelle, et le panneau n'a plus à en tenir compte.
    /// </summary>
    private void FabriquerLesTuiles(int premier, int dernier)
    {
        if (premier < 0 || dernier < premier) return;

        var generateur = ItemContainerGenerator;
        var depart = generateur.GeneratorPositionFromIndex(premier);

        // GeneratorPosition compte à partir du conteneur EXISTANT qui précède : décalage nul
        // veut dire « celui-ci même », donc on insère à sa place ; sinon juste après lui.
        var place = depart.Offset == 0 ? depart.Index : depart.Index + 1;

        using (generateur.StartAt(depart, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            for (var rang = premier; rang <= dernier; rang++, place++)
            {
                if (generateur.GenerateNext(out var neuf) is not UIElement tuile) break;

                if (neuf)
                {
                    if (place >= InternalChildren.Count) AddInternalChild(tuile);
                    else InsertInternalChild(place, tuile);

                    generateur.PrepareItemContainer(tuile);
                }

                if (tuile is FrameworkElement cadre) cadre.LayoutTransform = _zoom;

                // sans contrainte : le gabarit fixe les cotes de la tuile, et c'est cette
                // taille-là — zoom compris — qui commande toute la grille
                tuile.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            }
        }
    }

    /// <summary>
    /// Défait tout ce qui est sorti de la tranche visible. C'est ici que se gagne la
    /// mémoire : la vignette décodée part avec le conteneur qui la portait.
    /// </summary>
    private void DefaireLesTuiles(int premier, int dernier)
    {
        var generateur = ItemContainerGenerator;

        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var place = new GeneratorPosition(i, 0);
            var rang = generateur.IndexFromGeneratorPosition(place);

            if (rang >= premier && rang <= dernier) continue;

            generateur.Remove(place, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    /// <summary>
    /// Les enfants dont les rangs ont bougé — photos écartées, doublons recréés — ne peuvent
    /// pas rester en place : leurs conteneurs porteraient d'autres photos que celles que leur
    /// position annonce.
    /// </summary>
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        switch (args.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove:
            case System.Collections.Specialized.NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;

            case System.Collections.Specialized.NotifyCollectionChangedAction.Reset:
                RemoveInternalChildRange(0, InternalChildren.Count);
                _decalage = 0;
                break;
        }

        // une planche qui change de contenu peut aussi changer de gabarit
        _tailleTuile = Size.Empty;
    }

    // — défilement (IScrollInfo) —

    /// <summary>
    /// Ce que la molette fait avancer d'un cran, en tuiles.
    ///
    /// Une rangée entière serait brutale sur des tuiles de deux cent trente pixels : le
    /// tiers correspond au pas d'un ScrollViewer ordinaire, et c'est le geste que
    /// l'opérateur connaît.
    /// </summary>
    private double PasDeLigne => Math.Max(1, _tailleTuile.IsEmpty ? 48 : _tailleTuile.Height / 3);

    public bool CanVerticallyScroll { get; set; } = true;

    /// <summary>
    /// Toujours faux : la planche s'enroule, elle ne déborde jamais en largeur. Autoriser le
    /// défilement horizontal ferait mesurer les tuiles sur une largeur infinie, donc une
    /// seule rangée sans fin.
    /// </summary>
    public bool CanHorizontallyScroll
    {
        get => false;
        set { /* la planche s'enroule : rien à faire défiler de côté */ }
    }

    public double ExtentWidth => _etendue.Width;
    public double ExtentHeight => _etendue.Height;
    public double ViewportWidth => _zoneVisible.Width;
    public double ViewportHeight => _zoneVisible.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _decalage;
    public ScrollViewer? ScrollOwner { get; set; }

    private void MettreAJourLeDefilement(Size disponible, double hauteurTotale)
    {
        var zone = new Size(
            double.IsInfinity(disponible.Width) ? 0 : disponible.Width,
            double.IsInfinity(disponible.Height) ? hauteurTotale : disponible.Height);

        var etendue = new Size(zone.Width, hauteurTotale);

        // le décalage se reborne : la fenêtre a pu grandir, ou la planche raccourcir, et un
        // décalage devenu trop grand laisserait un écran blanc sous la dernière rangée
        var decalage = Math.Max(0, Math.Min(_decalage, Math.Max(0, etendue.Height - zone.Height)));

        if (zone == _zoneVisible && etendue == _etendue && decalage == _decalage) return;

        _zoneVisible = zone;
        _etendue = etendue;
        _decalage = decalage;

        ScrollOwner?.InvalidateScrollInfo();
    }

    public void SetVerticalOffset(double offset)
    {
        var borne = Math.Max(0, Math.Min(offset, Math.Max(0, _etendue.Height - _zoneVisible.Height)));
        if (Math.Abs(borne - _decalage) < 0.5) return;

        _decalage = borne;
        ScrollOwner?.InvalidateScrollInfo();

        // la tranche visible a changé : il faut refabriquer, pas seulement redisposer
        InvalidateMeasure();
    }

    public void SetHorizontalOffset(double offset) { /* la planche s'enroule */ }

    public void LineUp() => SetVerticalOffset(_decalage - PasDeLigne);
    public void LineDown() => SetVerticalOffset(_decalage + PasDeLigne);
    public void MouseWheelUp() => SetVerticalOffset(_decalage - PasDeLigne * 3);
    public void MouseWheelDown() => SetVerticalOffset(_decalage + PasDeLigne * 3);
    public void PageUp() => SetVerticalOffset(_decalage - _zoneVisible.Height);
    public void PageDown() => SetVerticalOffset(_decalage + _zoneVisible.Height);

    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }

    /// <summary>
    /// Amène une tuile sous les yeux — ce que fait le clavier en passant d'une tuile à
    /// l'autre. Sans elle, le focus sortirait de l'écran sans que rien ne bouge.
    /// </summary>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (visual is null || _tailleTuile.IsEmpty) return rectangle;

        var tuile = InternalChildren.Cast<UIElement>()
            .FirstOrDefault(enfant => enfant == visual || enfant.IsAncestorOf(visual));
        if (tuile is null) return rectangle;

        var rang = ItemContainerGenerator.IndexFromGeneratorPosition(
            new GeneratorPosition(InternalChildren.IndexOf(tuile), 0));
        if (rang < 0) return rectangle;

        var (_, y) = GrilleVirtuelle.Position(
            rang, _colonnes, _tailleTuile.Width, _tailleTuile.Height);

        if (y < _decalage) SetVerticalOffset(y);
        else if (y + _tailleTuile.Height > _decalage + _zoneVisible.Height)
            SetVerticalOffset(y + _tailleTuile.Height - _zoneVisible.Height);

        return rectangle;
    }
}
