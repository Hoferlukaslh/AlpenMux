/*
    Fichier      :  WindowsSystemInfo.cs
    Projet       :  AlpenMux

    Description  :
        Mesures système sous Windows (équivalents de /proc pour SystemMonitorService) :
            - CPU     : NtQuerySystemInformation (temps par cœur), nom dans le registre
            - Mémoire : GlobalMemoryStatusEx (RAM, fichier d'échange)
            - Réseau  : compteurs des cartes réseau (hors boucle locale et cartes virtuelles de WSL / Hyper-V,
                        dont le trafic passe déjà par la carte physique)
        Température du CPU et charge moyenne : non disponibles sans pilote, laissées vides.
*/

using System;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace AlpenMux.Services;

/// <summary> Mesures du système sous Windows (API Win32), pour le moniteur. </summary>
public static partial class WindowsSystemInfo
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPerformance
    {
        public long IdleTime, KernelTime, UserTime, DpcTime, InterruptTime;
        public int InterruptCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int infoClass, nint info, int length, out int returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    private const int SystemProcessorPerformanceInformation = 8;

    /// <summary> Temps par cœur au format de /proc/stat : [user, nice, system, idle, iowait, irq, softirq, steal]. </summary>
    public static long[][] CoreTimes()
    {
        var n = Environment.ProcessorCount;
        var size = Marshal.SizeOf<ProcessorPerformance>();
        var buf = Marshal.AllocHGlobal(size * n);
        try
        {
            if (NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, size * n, out var ret) != 0) return [];
            var res = new long[ret / size][];
            for (int i = 0; i < res.Length; i++)
            {
                var p = Marshal.PtrToStructure<ProcessorPerformance>(buf + i * size);
                // KernelTime comprend le temps d'inactivité
                res[i] = [p.UserTime, 0, p.KernelTime - p.IdleTime, p.IdleTime, 0, 0, 0, 0];
            }
            return res;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary> (RAM totale, RAM utilisée, échange total, échange utilisé) en octets. </summary>
    public static (double Total, double Used, double SwapTotal, double SwapUsed) Memory()
    {
        var s = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref s)) return (0, 0, 0, 0);
        // TotalPageFile = RAM + fichier d'échange
        var swapTotal = Math.Max(0, (double)s.TotalPageFile - s.TotalPhys);
        var swapUsed = Math.Max(0, ((double)s.TotalPageFile - s.AvailPageFile) - ((double)s.TotalPhys - s.AvailPhys));
        return (s.TotalPhys, s.TotalPhys - s.AvailPhys, swapTotal, Math.Min(swapUsed, swapTotal));
    }

    /// <summary> Octets reçus et envoyés depuis le démarrage, cartes physiques seulement. </summary>
    public static (long Rx, long Tx) NetTotals()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (ni.Name.StartsWith("vEthernet", StringComparison.OrdinalIgnoreCase) || ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)) continue;
                var st = ni.GetIPStatistics();
                rx += st.BytesReceived;
                tx += st.BytesSent;
            }
        }
        catch { /* interfaces réseau illisibles : débit à zéro */ }
        return (rx, tx);
    }

    /// <summary> Nom du processeur (registre). </summary>
    public static string CpuName()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    /// <summary> Fréquence nominale du CPU (MHz), lue dans le registre. </summary>
    public static double CpuMhz()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return key?.GetValue("~MHz") is int mhz ? mhz : 0;
        }
        catch { return 0; }
    }
}
