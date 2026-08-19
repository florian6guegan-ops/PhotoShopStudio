using System.Diagnostics;
using System.Runtime.InteropServices;
using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Studio.Core.Domain;
using Studio.Imaging;
using Studio.Printing;

// Ou passe le temps d'un ENVOI PAR COURRIEL, sur une vraie photo de la boutique.
//
// « L'envoi prend jusqu'a 1 min 30 » — Arcueil, 19/08/2026 : 76,6 s de preparation pour UNE
// photo, mesurees au journal. Le journal dit COMBIEN, il ne dit pas DANS QUOI. Cette sonde
// refait la preparation etape par etape, et mesure au passage ce que vaut chaque carte
// graphique du poste : sur kodakidpc il y en a deux, une Quadro K600 de 2013 (1 Go) et
// l'Intel UHD 630, et rien ne dit laquelle DirectML prend.
//
// Usage : CourrielProbe <photo.jpg> [dossier-des-modeles] [dossier-de-sortie]

var source = args.Length > 0 ? args[0] : "";
var modeles = args.Length > 1 ? args[1] : "";
var sortie = args.Length > 2
    ? args[2]
    : Path.Combine(Path.GetTempPath(), "courriel-probe");

if (!File.Exists(source))
{
    Console.WriteLine($"Photo introuvable : {source}");
    return 1;
}

Directory.CreateDirectory(sortie);
MagickInit.Configure();

BiRefNetMatting.Actif = true;
if (Directory.Exists(modeles))
    BiRefNetMatting.DossiersCherches = [modeles, Path.Combine(AppContext.BaseDirectory, "models")];

BackgroundRemoval.Log = m => Console.WriteLine($"   [fond] {m}");
BiRefNetMatting.Log = m => Console.WriteLine($"   [reseau] {m}");
MasqueSujet.Log = m => Console.WriteLine($"   [masque] {m}");
PhotoMailer.Log = m => Console.WriteLine($"   [courriel] {m}");

Console.WriteLine($"Photo   : {source}");
using (var entete = new MagickImage())
{
    entete.Ping(source);
    Console.WriteLine($"Source  : {entete.Width} x {entete.Height} px " +
                      $"({entete.Width * (double)entete.Height / 1e6:0.0} Mpx), " +
                      $"orientation {entete.Orientation}");
}
Console.WriteLine($"Modele  : {BiRefNetMatting.ModeleRetenu ?? "AUCUN"}");
Console.WriteLine();

// ————————————————————————————————————————————————————————————————
// 1. LES CARTES DU POSTE, et ce que chacune vaut sur CE reseau.
// ————————————————————————————————————————————————————————————————
Console.WriteLine("== Cartes graphiques vues par DXGI ==");
foreach (var carte in Adaptateurs())
    Console.WriteLine($"   {carte}");
Console.WriteLine();

var modele = BiRefNetMatting.ModeleRetenu;
if (modele is not null)
{
    Console.WriteLine("== Ce que vaut chaque peripherique sur le reseau (1024 x 1024) ==");
    for (var id = 0; id < 4; id++) MesurerPeripherique(modele, id);
    MesurerProcesseur(modele);
    Console.WriteLine();
}

// ————————————————————————————————————————————————————————————————
// 2. LA PREPARATION, ETAPE PAR ETAPE.
// ————————————————————————————————————————————————————————————————
var cle = $"{new FileInfo(source).FullName}|{new FileInfo(source).Length}|" +
          $"{new FileInfo(source).LastWriteTimeUtc.Ticks}";

var reglages = new ImageAdjustments { WhiteBackground = true, CleDeLaPhoto = cle };

// le cadrage 50 x 70 du 19/08/2026 (photo canadienne), en proportions de la photo entiere
var crop = new CropSpec(0.30, 0.05, 0.42, 0.85);

Console.WriteLine("== Preparation des trois fichiers ==");

Chrono("lecture pleine resolution", () =>
{
    using var image = MagickInit.Lire(source, 0);
    image.AutoOrient();
    Console.Write($"({image.Width}x{image.Height}) ");
});

Chrono("masque, PREMIER passage (reseau)", () =>
{
    using var image = MagickInit.Lire(source, 0);
    image.AutoOrient();
    using var masque = MasqueSujet.Nu(image, cle);
    Console.Write(masque is null ? "(rien rendu) " : $"({masque.Width}x{masque.Height}) ");
});

Chrono("masque, SECOND passage (memoire)", () =>
{
    using var image = MagickInit.Lire(source, 0);
    image.AutoOrient();
    using var masque = MasqueSujet.Nu(image, cle);
    Console.Write(masque is null ? "(rien rendu) " : $"({masque.Width}x{masque.Height}) ");
});

// Le detail de ce que coute UN masque, etape par etape : le reseau ne fait qu'une part du
// temps, et le reste se paie a la taille de la PHOTO alors que le reseau, lui, rend du
// 1024 x 1024.
using (var image = MagickInit.Lire(source, 0))
{
    image.AutoOrient();

    ImageMagick.MagickImage? brut = null;
    byte[]? png = null;

    Chrono("  reseau seul (CalculerMasque)", () =>
    {
        brut = BiRefNetMatting.CalculerMasque(image);
        Console.Write(brut is null ? "(rien) " : $"({brut.Width}x{brut.Height}) ");
    });

    if (brut is not null)
    {
        Chrono("  encodage PNG du masque (mise en cache)", () =>
        {
            png = brut.ToByteArray(MagickFormat.Png);
            Console.Write($"({png.Length / 1024} Ko) ");
        });

        Chrono("  decodage PNG du masque (reprise)", () =>
        {
            using var relu = new MagickImage(png!);
            Console.Write($"({relu.Width}x{relu.Height}) ");
        });

        Chrono("  reduction du masque a 1600 px puis PNG", () =>
        {
            using var petit = (MagickImage)brut.Clone();
            petit.Resize(new MagickGeometry(1600, 1600));
            var octets = petit.ToByteArray(MagickFormat.Png);
            Console.Write($"({petit.Width}x{petit.Height}, {octets.Length / 1024} Ko) ");
        });

        brut.Dispose();
    }
}

Chrono("PhotoMailer.Preparer AVEC fond blanc", () =>
{
    var lot = PhotoMailer.Preparer(source, crop, 0, 0, reglages, sortie, "avec-fond");
    Console.Write($"({new FileInfo(lot.HauteDefinition).Length / 1024} Ko) ");
});

Chrono("PhotoMailer.Preparer SANS fond blanc", () =>
{
    var lot = PhotoMailer.Preparer(source, crop, 0, 0,
        new ImageAdjustments { CleDeLaPhoto = cle }, sortie, "sans-fond");
    Console.Write($"({new FileInfo(lot.HauteDefinition).Length / 1024} Ko) ");
});

Console.WriteLine();
Console.WriteLine($"Fichiers dans : {sortie}");
return 0;

static void Chrono(string quoi, Action action)
{
    Console.Write($"   {quoi,-42} : ");
    var chrono = Stopwatch.StartNew();
    try
    {
        action();
        Console.WriteLine($"{chrono.Elapsed.TotalSeconds,7:0.00} s");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ECHEC apres {chrono.Elapsed.TotalSeconds:0.00} s — {ex.Message}");
    }
}

// Un passage du reseau sur un peripherique DirectML donne, entree factice : on ne mesure
// que la machine, pas la photo.
static void MesurerPeripherique(string modele, int id)
{
    Console.Write($"   DirectML peripherique {id,-20} : ");

    try
    {
        var options = new SessionOptions
        {
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        };
        options.AppendExecutionProvider_DML(id);

        var chargement = Stopwatch.StartNew();
        using var session = new InferenceSession(modele, options);
        chargement.Stop();

        var (premier, suivant) = DeuxPassages(session);
        Console.WriteLine($"chargement {chargement.Elapsed.TotalSeconds,6:0.00} s · " +
                          $"1er passage {premier,7:0.00} s · 2e passage {suivant,7:0.00} s");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"indisponible — {Court(ex.Message)}");
    }
}

static void MesurerProcesseur(string modele)
{
    Console.Write($"   {"Processeur (sans carte)",-33} : ");

    try
    {
        using var session = new InferenceSession(modele, new SessionOptions());
        var (premier, suivant) = DeuxPassages(session);
        Console.WriteLine($"1er passage {premier,7:0.00} s · 2e passage {suivant,7:0.00} s");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"indisponible — {Court(ex.Message)}");
    }
}

static (double Premier, double Suivant) DeuxPassages(InferenceSession session)
{
    var entree = session.InputMetadata.First();
    var nomSortie = session.OutputMetadata.First().Key;
    const int cote = 1024;

    var valeurs = new float[3 * cote * cote];
    for (var i = 0; i < valeurs.Length; i++) valeurs[i] = (i % 255) / 255f;

    NamedOnnxValue Tenseur() => entree.Value.ElementType == typeof(Float16)
        ? NamedOnnxValue.CreateFromTensor(entree.Key,
            new DenseTensor<Float16>(
                valeurs.Select(v => (Float16)v).ToArray(), [1, 3, cote, cote]))
        : NamedOnnxValue.CreateFromTensor(entree.Key,
            new DenseTensor<float>(valeurs, [1, 3, cote, cote]));

    var chrono = Stopwatch.StartNew();
    using (session.Run([Tenseur()], [nomSortie])) { }
    var premier = chrono.Elapsed.TotalSeconds;

    chrono.Restart();
    using (session.Run([Tenseur()], [nomSortie])) { }
    return (premier, chrono.Elapsed.TotalSeconds);
}

static string Court(string message) =>
    message.Length <= 120 ? message.ReplaceLineEndings(" ") : message.ReplaceLineEndings(" ")[..120];

// ————————————————————————————————————————————————————————————————
// DXGI : la liste des cartes, DANS L'ORDRE OU DIRECTML LES NUMEROTE.
//
// C'est la question du poste d'Arcueil : « peripherique 0 » ne dit rien tant qu'on ne sait
// pas qui est 0. Le fournisseur DirectML suit l'enumeration DXGI, on la lit donc telle
// quelle.
// ————————————————————————————————————————————————————————————————
static IEnumerable<string> Adaptateurs()
{
    IntPtr facteur = IntPtr.Zero;

    var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IID_IDXGIFactory1
    if (Dxgi.CreateDXGIFactory1(ref guid, out facteur) != 0 || facteur == IntPtr.Zero)
    {
        yield return "DXGI indisponible.";
        yield break;
    }

    try
    {
        var fabrique = (Dxgi.IDXGIFactory1)Marshal.GetObjectForIUnknown(facteur);

        for (uint i = 0; i < 8; i++)
        {
            if (fabrique.EnumAdapters1(i, out var adaptateur) != 0 || adaptateur is null) break;

            adaptateur.GetDesc1(out var desc);
            Marshal.ReleaseComObject(adaptateur);

            var logiciel = (desc.Flags & 2) != 0 ? " [LOGICIEL]" : "";
            yield return
                $"{i} · {desc.Description.Trim()} — " +
                $"video dediee {(ulong)desc.DedicatedVideoMemory / 1024 / 1024} Mo, " +
                $"systeme partage {(ulong)desc.SharedSystemMemory / 1024 / 1024} Mo{logiciel}";
        }
    }
    finally
    {
        if (facteur != IntPtr.Zero) Marshal.Release(facteur);
    }
}

internal static class Dxgi
{
    [DllImport("dxgi.dll", ExactSpelling = true)]
    internal static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        // IDXGIObject — jamais appelees, mais leurs cases doivent exister dans la vtable
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIFactory
        [PreserveSig] int EnumAdapters(uint index, out IntPtr adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr window, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr window);
        [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);

        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index,
            [MarshalAs(UnmanagedType.Interface)] out IDXGIAdapter1? adapter);
        [PreserveSig] bool IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter1
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(ref Guid name, uint size, IntPtr data);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint size, IntPtr data);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);

        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
        [PreserveSig] int GetDesc(out IntPtr desc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid name, out long version);

        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
}
