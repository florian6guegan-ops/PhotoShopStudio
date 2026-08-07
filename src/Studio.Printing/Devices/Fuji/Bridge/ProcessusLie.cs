using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Studio.Printing.Devices.Fuji.Bridge;

/// <summary>
/// Lie un processus enfant au nôtre : Windows le tue quand nous disparaissons, quelle que
/// soit la façon dont nous disparaissons.
///
/// <b>Le défaut du 07/08/2026.</b> Le relais 32 bits était bien tué à la déconnexion —
/// mais seulement quand l'application se fermait proprement. Elle a planté deux fois ce
/// jour-là (<c>0xc0000006</c>, une erreur de lecture disque), et le poste de Créteil a été
/// retrouvé le soir avec un relais de la version 1.3.3 encore vivant, alors que la 1.3.4
/// tournait. Deux relais qui se disputent le même tube nommé et le même SDK Fuji, c'est un
/// minilab qui ne répond plus à personne.
///
/// <b>Pourquoi un « job » et non un simple Kill.</b> Un Kill demande à quelqu'un d'être
/// encore là pour le faire. Un job object est tenu par Windows : à la mort du dernier
/// handle — fin normale, plantage, arrêt par le gestionnaire des tâches, coupure de
/// courant du processus — le noyau referme le groupe et tue ce qu'il contient. C'est la
/// seule garantie qui survive à un crash, précisément le cas qu'on veut couvrir.
/// </summary>
internal static class ProcessusLie
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    /// <summary>
    /// Le groupe de cette application. Créé au premier besoin et jamais refermé : c'est sa
    /// fermeture — par nous ou par le noyau — qui emporte les enfants.
    /// </summary>
    private static readonly Lazy<IntPtr> Groupe = new(Creer);

    /// <summary>
    /// Attache le processus au groupe. Rend faux si le système l'a refusé — auquel cas
    /// l'appelant garde son Kill : mieux vaut la garantie faible que pas de garantie.
    /// </summary>
    public static bool Attacher(Process processus)
    {
        ArgumentNullException.ThrowIfNull(processus);

        try
        {
            var groupe = Groupe.Value;
            return groupe != IntPtr.Zero
                   && AssignProcessToJobObject(groupe, processus.Handle);
        }
        catch (Exception)
        {
            // processus déjà mort, droits refusés, plateforme sans job object
            return false;
        }
    }

    private static IntPtr Creer()
    {
        var groupe = CreateJobObject(IntPtr.Zero, null);
        if (groupe == IntPtr.Zero) return IntPtr.Zero;

        var limites = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limites.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        var taille = Marshal.SizeOf(limites);
        var memoire = Marshal.AllocHGlobal(taille);

        try
        {
            Marshal.StructureToPtr(limites, memoire, fDeleteOld: false);

            if (!SetInformationJobObject(
                    groupe, JobObjectExtendedLimitInformation, memoire, (uint)taille))
            {
                CloseHandle(groupe);
                return IntPtr.Zero;
            }

            return groupe;
        }
        finally
        {
            Marshal.FreeHGlobal(memoire);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributs, string? nom);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr groupe, int classe, IntPtr informations, uint taille);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr groupe, IntPtr processus);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr objet);

#pragma warning disable CS0649 // renseignées par le noyau, jamais par nous
    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
#pragma warning restore CS0649
}
