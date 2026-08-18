using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Studio.App.Infrastructure;
using Studio.App.Views;

namespace Studio.Identite;

/// <summary>
/// La fenêtre de Studio Photo Identité : l'en-tête de la maquette, et dessous l'écran
/// courant.
///
/// <b>Elle héberge les écrans du Studio</b> — <c>Navigator</c> n'est qu'une pile et un
/// événement, sans lien avec une fenêtre particulière. C'est ce qui permet à ce logiciel-ci
/// d'être neuf sans rien réécrire du parcours d'identité, qui sert en boutique depuis des
/// semaines. Les écrans seront remplacés un à un par ceux de la maquette.
///
/// <b>Pas de sortie vers le Studio complet.</b> Sur un poste identité verrouillé du Studio,
/// cinq appuis dans le coin plus le PIN déverrouillaient le Studio de la boutique. Ici il
/// n'y a pas de Studio derrière : le geste ne fait rien, et c'est juste. L'engrenage du
/// réglage courriel, lui, fonctionne.
/// </summary>
public partial class FenetreIdentite : Window
{
    public FenetreIdentite()
    {
        InitializeComponent();

        Navigator.Navigated += SurNavigation;

        Loaded += (_, _) =>
        {
            AccueilStudio.Rentrer();

            // Le bandeau de mise a jour : il n'installe rien, il annonce. La surveillance
            // est partagee avec la fenetre du Studio complet — c'est elle qui sait que ce
            // logiciel-ci suit les publications « identite-v », et pas celles du Studio.
            SurveillanceMaj.Demarrer(Dispatcher, version =>
            {
                MajBannerText.Text = $"⬆  Mise à jour {version.ToString(3)} disponible";
                MajBanner.Visibility = Visibility.Visible;
            });
        };
        Closed += (_, _) => Navigator.Navigated -= SurNavigation;
    }

    private void SurNavigation(UserControl ecran, string titre)
    {
        HoteEcran.Content = ecran;
        TitreEcran.Text = string.IsNullOrWhiteSpace(titre) ? "Photos d'identité" : titre;
    }

    /// <summary>
    /// Les réglages, SANS code.
    ///
    /// Ils étaient derrière le code staff, par prudence : le mot de passe du compte courriel
    /// y figure. Mais ce logiciel n'est pas une borne face au client — c'est l'outil de
    /// celui qui tient le comptoir, et lui demander un code pour changer le dossier des
    /// photos, c'est un obstacle sans contrepartie. Retiré à la demande, le 14/08/2026.
    /// </summary>
    private void OnReglages(object sender, RoutedEventArgs e) =>
        Navigator.Go(new ReglagesIdentiteView(), "Réglages");

    /// <summary>
    /// Le bandeau mene aux reglages, ou vit le bouton « Installer ». On n'installe jamais
    /// depuis le bandeau lui-meme : un clic malheureux ne doit pas remplacer le logiciel
    /// devant un client.
    /// </summary>
    private void OnMajBannerClicked(object sender, MouseButtonEventArgs e) =>
        Navigator.Go(new ReglagesIdentiteView(), "Réglages");

    /// <summary>
    /// Repartir de zéro. C'est le geste le plus fréquent du comptoir — un client part, le
    /// suivant se présente — et il valait un aller-retour par le menu.
    /// </summary>
    private void OnClientSuivant(object sender, RoutedEventArgs e) => AccueilStudio.Rentrer();

    private void OnQuitter(object sender, RoutedEventArgs e)
    {
        var reponse = MessageBox.Show(
            "Fermer Studio Photo Identité ?",
            "Studio Photo Identité", MessageBoxButton.YesNo, MessageBoxImage.Question,
            MessageBoxResult.No);

        if (reponse == MessageBoxResult.Yes) Close();
    }

    /// <summary>
    /// Échap ne doit pas fermer le poste : la fenêtre n'a pas de bordure, et un appui
    /// malheureux devant un client laisserait un bureau Windows nu.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.System && e.SystemKey == Key.F4) e.Handled = true;
        base.OnPreviewKeyDown(e);
    }

    // ----- la fenêtre s'arrête à la barre des tâches -----

    /// <summary>
    /// <b>Une fenêtre SANS BORDURE agrandie couvre l'écran ENTIER</b>, barre des tâches
    /// comprise — c'est le comportement de Windows, pas un réglage de l'application. Et
    /// comme la barre des tâches est au premier plan, ce sont les quarante derniers pixels
    /// de la page qui passent DESSOUS : la rangée de boutons du bas (« Enregistrer »,
    /// « Retour », la phrase de conformité) devenait inatteignable.
    ///
    /// Signalé le 17/08/2026 : « certains boutons sont trop bas sur l'écran ». Le Studio
    /// complet n'a jamais eu le défaut — sa fenêtre garde sa bordure, et Windows l'agrandit
    /// alors à la zone de travail tout seul.
    ///
    /// On répond nous-mêmes à WM_GETMINMAXINFO en donnant la zone de TRAVAIL du moniteur
    /// (<c>rcWork</c>) au lieu de sa surface entière (<c>rcMonitor</c>). Pris sur le
    /// moniteur qui porte la fenêtre, et non sur l'écran principal : à Créteil comme à
    /// Arcueil le poste peut recevoir un second écran, et la barre des tâches n'y est pas
    /// forcément du même côté.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(SurMessageDeFenetre);

        // ⚠ ON MAXIMISE ICI, ET SURTOUT PAS DANS LE XAML.
        //
        // Déclarée « Maximized » dès le XAML, la fenêtre était dimensionnée par Windows
        // AVANT que le hook ci-dessus n'existe : une fenêtre sans bordure maximisée déborde
        // alors de l'épaisseur du cadre de redimensionnement — huit pixels de chaque côté,
        // hors de l'écran. C'est le « mal cadré, il doit y avoir 5 mm de chaque » signalé le
        // 18/08/2026, et c'est aussi pourquoi le correctif du 17/08 sur la barre des tâches
        // ne se voyait qu'à moitié : il répondait à un message qui était déjà passé.
        WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Réduire dans la barre des tâches.
    ///
    /// Sans barre de titre, aucun des trois boutons de Windows n'existe : le poste ne
    /// pouvait ni s'effacer pour aller chercher un fichier, ni sortir du plein écran.
    /// </summary>
    private void OnReduire(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    /// <summary>Plein écran ou fenêtre, d'un même bouton — comme un double-clic sur un titre.</summary>
    private void OnAgrandirOuRestaurer(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// L'en-tête fait office de barre de titre : on y attrape la fenêtre pour la déplacer,
    /// et le double-clic bascule entre plein écran et fenêtre.
    ///
    /// Sans cela, une fenêtre restaurée restait là où Windows l'avait posée, sans aucun
    /// moyen de la bouger — <c>WindowStyle="None"</c> supprime la barre de titre, donc le
    /// geste avec.
    /// </summary>
    private void OnEnteteAttrapee(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnAgrandirOuRestaurer(sender, e);
            return;
        }

        // DragMove lève si le bouton a déjà été relâché — un clic bref sur l'en-tête suffit
        // à provoquer la course. La fenêtre ne doit pas se fermer pour si peu.
        try
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }
        catch (InvalidOperationException)
        {
            // le bouton était déjà relâché : il n'y a rien à déplacer
        }
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const int MoniteurLePlusProche = 0x0002;

    private static IntPtr SurMessageDeFenetre(
        IntPtr fenetre, int message, IntPtr wParam, IntPtr lParam, ref bool traite)
    {
        if (message != WmGetMinMaxInfo) return IntPtr.Zero;

        var moniteur = MonitorFromWindow(fenetre, MoniteurLePlusProche);
        if (moniteur == IntPtr.Zero) return IntPtr.Zero;

        var infos = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(moniteur, ref infos)) return IntPtr.Zero;

        var cotes = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // Les positions sont relatives au coin haut-gauche du MONITEUR, pas du bureau :
        // sur un second écran placé à gauche, des coordonnées absolues enverraient la
        // fenêtre hors de l'écran.
        cotes.ptMaxPosition.X = infos.rcWork.Left - infos.rcMonitor.Left;
        cotes.ptMaxPosition.Y = infos.rcWork.Top - infos.rcMonitor.Top;
        cotes.ptMaxSize.X = infos.rcWork.Right - infos.rcWork.Left;
        cotes.ptMaxSize.Y = infos.rcWork.Bottom - infos.rcWork.Top;

        // Sans la borne haute, Windows autorise encore l'agrandissement à l'écran entier
        // et le réglage ci-dessus ne tient pas.
        cotes.ptMaxTrackSize = cotes.ptMaxSize;

        Marshal.StructureToPtr(cotes, lParam, fDeleteOld: false);
        traite = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr fenetre, int drapeaux);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr moniteur, ref MONITORINFO infos);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
