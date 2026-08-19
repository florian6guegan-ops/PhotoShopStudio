using System.Runtime.InteropServices;

namespace Studio.Imaging;

/// <summary>
/// Les cartes graphiques du poste, DANS L'ORDRE OÙ DIRECTML LES NUMÉROTE.
///
/// <b>« Périphérique 0 » ne veut rien dire tant qu'on ne sait pas qui est 0.</b> Le
/// détourage demandait la carte n° 0 en dur, et sur un poste à deux cartes ce numéro tombe
/// où il tombe : à Arcueil, un poste porte une Quadro K600 de 2013 (1 Go, sans demi-
/// précision matérielle) À CÔTÉ d'une Intel UHD 630 qui, elle, calcule le fp16 nativement
/// et puise dans les 8 Go du système. Le réseau tournait sur la mauvaise, et un envoi par
/// courriel y prenait 76 s.
///
/// Le fournisseur DirectML suit l'énumération DXGI : on la lit donc telle quelle, sans
/// interpréter. C'est la seule liste qui parle le même langage que
/// <c>AppendExecutionProvider_DML(n)</c>.
/// </summary>
public static class CartesGraphiques
{
    /// <param name="Numero">Le numéro à passer à DirectML.</param>
    /// <param name="Nom">Ce que le pilote déclare, tel quel.</param>
    /// <param name="MemoireDedieeMo">Mémoire vidéo propre à la carte.</param>
    /// <param name="MemoirePartageeMo">Ce qu'elle peut prendre à la mémoire du poste.</param>
    /// <param name="Logicielle">
    /// Vrai pour le « Microsoft Basic Render Driver » et ses semblables : ils répondent à
    /// tout et ne calculent rien de rapide. On ne les mesure pas.
    /// </param>
    public sealed record Carte(
        int Numero, string Nom, ulong MemoireDedieeMo, ulong MemoirePartageeMo, bool Logicielle)
    {
        public override string ToString() =>
            $"{Numero} · {Nom} ({MemoireDedieeMo} Mo dédiés" +
            (Logicielle ? ", LOGICIELLE" : "") + ")";
    }

    /// <summary>Nombre d'adaptateurs qu'on accepte d'énumérer. Aucun poste n'en a huit.</summary>
    private const uint MaximumEnumere = 8;

    /// <summary>
    /// La liste des cartes, ou vide si DXGI ne répond pas — auquel cas l'appelant garde le
    /// périphérique 0, qui est ce qu'il faisait avant.
    /// </summary>
    public static IReadOnlyList<Carte> Lister()
    {
        var cartes = new List<Carte>();

        // Ce module cible net8.0 tout court — comme le reste de Studio.Imaging — et DXGI
        // n'existe que sur Windows. La garde n'est pas décorative : elle est ce qui permet
        // au compilateur de laisser passer l'interop COM sans annoter tout ce qui appelle.
        if (!OperatingSystem.IsWindows()) return cartes;

        var facteur = IntPtr.Zero;
        var guid = typeof(IDXGIFactory1).GUID;

        try
        {
            if (CreateDXGIFactory1(ref guid, out facteur) != 0 || facteur == IntPtr.Zero)
                return cartes;

            var fabrique = (IDXGIFactory1)Marshal.GetObjectForIUnknown(facteur);

            for (uint i = 0; i < MaximumEnumere; i++)
            {
                if (fabrique.EnumAdapters1(i, out var adaptateur) != 0 || adaptateur is null) break;

                try
                {
                    if (adaptateur.GetDesc1(out var desc) != 0) break;

                    cartes.Add(new Carte(
                        (int)i,
                        desc.Description.Trim(),
                        (ulong)desc.DedicatedVideoMemory / 1024 / 1024,
                        (ulong)desc.SharedSystemMemory / 1024 / 1024,
                        (desc.Flags & DrapeauLogiciel) != 0));
                }
                finally
                {
                    Marshal.ReleaseComObject(adaptateur);
                }
            }
        }
        catch (Exception)
        {
            // DXGI absent (session sans bureau, poste sans carte) : on ne sait pas, et ne
            // rien savoir ne doit pas empêcher le détourage de tourner comme avant.
            return cartes;
        }
        finally
        {
            if (facteur != IntPtr.Zero) Marshal.Release(facteur);
        }

        return cartes;
    }

    /// <summary>DXGI_ADAPTER_FLAG_SOFTWARE.</summary>
    private const uint DrapeauLogiciel = 2;

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    // Les méthodes qu'on n'appelle pas sont tout de même déclarées : une interface COM se
    // lit par la POSITION de ses méthodes dans la table, et en omettre une décalerait toutes
    // les suivantes.
    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
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
    private interface IDXGIAdapter1
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
    private struct AdapterDesc1
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
