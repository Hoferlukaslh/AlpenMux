/*
    Fichier      :  SystemMonitorService.cs
    Projet       :  AlpenMux

    Description  :
        Collecte périodique des mesures système (thread d'arrière-plan) :
            - CPU     : /proc/stat (total + par cœur), /proc/cpuinfo, hwmon (température)
            - Mémoire : /proc/meminfo (RAM + swap)
            - Réseau  : /proc/net/dev (débit, utilisé aussi pour estimer les téléchargements)
            - GPU     : nvidia-smi (NVIDIA) ou sysfs amdgpu (AMD)
        Windows : CPU, mémoire et réseau via WindowsSystemInfo, GPU via nvidia-smi.exe (pilote Windows).
        Chaque mesure est publiée sur le thread UI via l'événement Updated.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Mesure d'un GPU. </summary>
/// <param name="Index"> Index du GPU. </param>
/// <param name="Name"> Nom du modèle. </param>
/// <param name="Vendor"> Fabricant (NVIDIA, AMD). </param>
/// <param name="Util"> Utilisation (%). </param>
/// <param name="MemUsed"> Mémoire utilisée (octets). </param>
/// <param name="MemTotal"> Mémoire totale (octets). </param>
/// <param name="MemFree"> Mémoire libre (octets). </param>
/// <param name="Temp"> Température (°C). </param>
/// <param name="Power"> Consommation (W). </param>
/// <param name="PowerLimit"> Limite de consommation (W). </param>
/// <param name="Fan"> Ventilateur (%). </param>
/// <param name="ClockMhz"> Fréquence (MHz). </param>
/// <param name="Uuid"> Identifiant unique (ou chemin du périphérique). </param>
public sealed record GpuSample(
    int Index, string Name, string Vendor, double Util, double MemUsed, double MemTotal, double MemFree,
    double? Temp, double? Power, double? PowerLimit, double? Fan, double? ClockMhz, string Uuid);

/// <summary> Processus qui utilise un GPU. </summary>
/// <param name="Pid"> Identifiant du processus. </param>
/// <param name="Name"> Nom du programme. </param>
/// <param name="Memory"> Mémoire GPU utilisée (octets). </param>
/// <param name="GpuIndex"> Index du GPU. </param>
public sealed record GpuProcess(int Pid, string Name, double Memory, int GpuIndex);

/// <summary> Mesure complète du système à un instant. </summary>
/// <param name="CpuPercent"> Utilisation du CPU (%). </param>
/// <param name="CoreUsage"> Utilisation de chaque cœur (%). </param>
/// <param name="CpuTemp"> Température du CPU (°C). </param>
/// <param name="CpuFreqMhz"> Fréquence moyenne (MHz). </param>
/// <param name="LoadAvg"> Charge moyenne (1, 5, 15 min). </param>
/// <param name="RamUsed"> RAM utilisée (octets). </param>
/// <param name="RamTotal"> RAM totale (octets). </param>
/// <param name="SwapUsed"> Swap utilisé (octets). </param>
/// <param name="SwapTotal"> Swap total (octets). </param>
/// <param name="NetRx"> Débit reçu (octets/s). </param>
/// <param name="NetTx"> Débit envoyé (octets/s). </param>
/// <param name="NetRxTotal"> Total reçu depuis le démarrage (octets). </param>
/// <param name="Gpus"> Mesures des GPU. </param>
/// <param name="GpuProcesses"> Processus sur les GPU. </param>
/// <param name="Uptime"> Temps depuis le démarrage du système. </param>
public sealed record SystemSample(
    double CpuPercent, double[] CoreUsage, double? CpuTemp, double CpuFreqMhz, double[] LoadAvg,
    double RamUsed, double RamTotal, double SwapUsed, double SwapTotal,
    double NetRx, double NetTx, long NetRxTotal,
    IReadOnlyList<GpuSample> Gpus, IReadOnlyList<GpuProcess> GpuProcesses, TimeSpan Uptime);

/// <summary> Mesures du système (CPU, RAM, réseau, GPU) relevées périodiquement. </summary>
public interface ISystemMonitorService
{
    /// <summary> Levé après chaque mesure (sur le thread de l'interface). </summary>
    event Action<SystemSample>? Updated;

    /// <summary> Dernière mesure, ou null avant la première. </summary>
    SystemSample? Latest { get; }

    /// <summary> Nom du processeur. </summary>
    string CpuName { get; }

    /// <summary> Nombre de cœurs logiques. </summary>
    int    CoreCount { get; }

    /// <summary> nvidia-smi est-il disponible ? </summary>
    bool HasNvidia { get; }

    /// <summary> Capacité de calcul par GPU (8.9 pour une RTX 40xx…), lue une fois au démarrage. </summary>
    IReadOnlyDictionary<int, double> ComputeCaps { get; }

    /// <summary> Démarre les mesures périodiques (sans effet si elles tournent déjà). </summary>
    void Start();

    /// <summary> Arrête les mesures. </summary>
    void Stop();

    /// <summary> Lit l'état des GPU (nvidia-smi, sysfs pour AMD). </summary>
    Task<IReadOnlyList<GpuSample>> ReadGpusAsync();
}

/// <summary> Implémentation de <see cref="ISystemMonitorService"/>. </summary>
public sealed class SystemMonitorService : ISystemMonitorService
{

    /// <inheritdoc />
    public event Action<SystemSample>? Updated;

    /// <inheritdoc />
    public SystemSample? Latest { get; private set; }

    /// <inheritdoc />
    public string CpuName { get; }
    /// <inheritdoc />
    public int    CoreCount { get; }

    /// <inheritdoc />
    public bool HasNvidia { get; }

    private CancellationTokenSource? _cts;
    private long[]? _prevCpu;
    private long[][]? _prevCores;
    private (long Rx, long Tx, DateTime At)? _prevNet;
    private readonly string? _cpuTempFile;
    private readonly List<string> _amdCards = new();
    private IReadOnlyList<GpuProcess> _lastProcs = Array.Empty<GpuProcess>();
    private int _tick;

    private readonly ISettingsService _settings;

    private readonly ILogger<SystemMonitorService> _logger;

    /// <summary> Crée le service et lit les informations fixes (processeur, GPU). </summary>
    /// <param name="settings"> Réglages (fréquence des mesures). </param>
    /// <param name="logger"> Journal des erreurs. </param>
    public SystemMonitorService(ISettingsService settings, ILogger<SystemMonitorService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        CpuName   = OperatingSystem.IsWindows() ? WindowsSystemInfo.CpuName() : ReadCpuName();
        CoreCount = Environment.ProcessorCount;
        HasNvidia = ProcessRunner.Which("nvidia-smi") != null;
        _cpuTempFile = FindCpuTempFile();

        try
        {
            foreach (var card in Directory.GetDirectories("/sys/class/drm", "card*"))
            {
                var vendor = Path.Combine(card, "device", "vendor");
                if (!Path.GetFileName(card).Contains('-') && File.Exists(vendor) &&
                    File.ReadAllText(vendor).Trim() == "0x1002" &&
                    File.Exists(Path.Combine(card, "device", "mem_info_vram_total")))
                    _amdCards.Add(Path.Combine(card, "device"));
            }
        }
        catch { /* pas de sysfs */ }
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<int, double> ComputeCaps { get; private set; } = new Dictionary<int, double>();

    private async Task LoadComputeCapsAsync()
    {
        if (!HasNvidia) return;
        var (code, output) = await ProcessRunner.CaptureAsync("nvidia-smi", ["--query-gpu=index,compute_cap", "--format=csv,noheader"], 8000);
        if (code != 0) return;
        var d = new Dictionary<int, double>();
        foreach (var l in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = l.Split(',');
            if (f.Length == 2 && int.TryParse(f[0].Trim(), out var i) && Num(f[1]) is { } c) d[i] = c;
        }
        ComputeCaps = d;
    }

    /// <inheritdoc />
    public void Start()
    {
        _ = LoadComputeCapsAsync();
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var s = await SampleAsync();
                    Latest = s;
                    Dispatcher.UIThread.Post(() => Updated?.Invoke(s));
                }
                catch (Exception ex) { _logger.LogWarning("[Monitor] {Message}", ex.Message); }

                // Attente par tranches : un réglage raccourci pendant une longue attente est pris en compte tout de suite
                var wait = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    while (true)
                    {
                        var delay = Math.Clamp(_settings.Current.MonitorIntervalMs, 50, 600_000);
                        var left = delay - wait.ElapsedMilliseconds;
                        if (left <= 0) break;
                        await Task.Delay((int)Math.Min(left, 200), ct);
                    }
                }
                catch (OperationCanceledException) { }
            }
        }, ct);
    }

    /// <inheritdoc />
    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task<SystemSample> SampleAsync()
    {
        if (OperatingSystem.IsWindows()) return await SampleWindowsAsync();
        var (cpu, cores) = ReadCpu();
        var mem  = ReadMeminfo();
        var net  = ReadNet();
        var gpus = await ReadGpusAsync();

        if (HasNvidia && _tick++ % 3 == 0)
            _lastProcs = await ReadNvidiaProcessesAsync(gpus);

        double uptime = 0;
        try { uptime = double.Parse(File.ReadAllText("/proc/uptime").Split(' ')[0], CultureInfo.InvariantCulture); }
        catch { /* /proc/uptime absent (Windows) */ }

        return new SystemSample(
            cpu, cores, ReadCpuTemp(), ReadCpuFreq(), ReadLoadAvg(),
            mem.GetValueOrDefault("MemTotal") - mem.GetValueOrDefault("MemAvailable"),
            mem.GetValueOrDefault("MemTotal"),
            mem.GetValueOrDefault("SwapTotal") - mem.GetValueOrDefault("SwapFree"),
            mem.GetValueOrDefault("SwapTotal"),
            net.Rx, net.Tx, net.RxTotal, gpus, _lastProcs, TimeSpan.FromSeconds(uptime));
    }

    /// <summary> Mesure sous Windows : mêmes calculs que /proc, à partir des compteurs de Windows. </summary>
    private async Task<SystemSample> SampleWindowsAsync()
    {
        var cores = WindowsSystemInfo.CoreTimes();
        var total = cores.Length == 0 ? new long[8] : Enumerable.Range(0, 8).Select(i => cores.Sum(c => c[i])).ToArray();
        var (cpu, per) = CpuUsage(total, cores);
        var (ramTotal, ramUsed, swapTotal, swapUsed) = WindowsSystemInfo.Memory();
        var (rx, tx) = WindowsSystemInfo.NetTotals();
        var net = NetRates(rx, tx);
        var gpus = await ReadGpusAsync();
        if (HasNvidia && _tick++ % 3 == 0)
            _lastProcs = await ReadNvidiaProcessesAsync(gpus);
        return new SystemSample(cpu, per, null, WindowsSystemInfo.CpuMhz(), [0, 0, 0],
            ramUsed, ramTotal, swapUsed, swapTotal, net.Rx, net.Tx, rx, gpus, _lastProcs,
            TimeSpan.FromMilliseconds(Environment.TickCount64));
    }

    // ---------------------------------------------------------------- CPU

    private (double Total, double[] Cores) ReadCpu()
    {
        var lines = File.ReadAllLines("/proc/stat").Where(l => l.StartsWith("cpu")).ToArray();
        static long[] Parse(string l) => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(x => long.TryParse(x, out var v) ? v : 0).ToArray();
        return CpuUsage(Parse(lines[0]), lines.Skip(1).Select(Parse).ToArray());
    }

    /// <summary> Utilisation depuis la mesure précédente, à partir des temps cumulés (format /proc/stat). </summary>
    private (double Total, double[] Cores) CpuUsage(long[] total, long[][] cores)
    {
        static double Usage(long[] now, long[]? prev)
        {
            if (prev == null || prev.Length != now.Length) return 0;
            long idle  = (now[3] + (now.Length > 4 ? now[4] : 0)) - (prev[3] + (prev.Length > 4 ? prev[4] : 0));
            long total = now.Take(8).Sum() - prev.Take(8).Sum();
            return total <= 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
        }

        var res   = Usage(total, _prevCpu);
        var per   = cores.Select((c, i) => Usage(c, _prevCores != null && i < _prevCores.Length ? _prevCores[i] : null)).ToArray();
        _prevCpu = total;
        _prevCores = cores;
        return (res, per);
    }

    private static string ReadCpuName()
    {
        try
        {
            var l = File.ReadLines("/proc/cpuinfo").FirstOrDefault(x => x.StartsWith("model name"));
            return l?.Split(':', 2)[1].Trim() ?? "CPU";
        }
        catch { return "CPU"; }
    }

    private static double ReadCpuFreq()
    {
        try
        {
            var f = File.ReadLines("/proc/cpuinfo").Where(x => x.StartsWith("cpu MHz"))
                .Select(x => double.Parse(x.Split(':', 2)[1].Trim(), CultureInfo.InvariantCulture)).ToList();
            return f.Count > 0 ? f.Average() : 0;
        }
        catch { return 0; }
    }

    private static double[] ReadLoadAvg()
    {
        try
        {
            return File.ReadAllText("/proc/loadavg").Split(' ').Take(3)
                .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        }
        catch { return [0, 0, 0]; }
    }

    private static string? FindCpuTempFile()
    {
        try
        {
            foreach (var hw in Directory.GetDirectories("/sys/class/hwmon"))
            {
                var name = File.ReadAllText(Path.Combine(hw, "name")).Trim();
                if (name is "k10temp" or "coretemp" or "zenpower" or "cpu_thermal")
                {
                    var f = Path.Combine(hw, "temp1_input");
                    if (File.Exists(f)) return f;
                }
            }
        }
        catch { /* capteurs de température illisibles */ }
        return null;
    }

    private double? ReadCpuTemp()
    {
        try { return _cpuTempFile == null ? null : double.Parse(File.ReadAllText(_cpuTempFile).Trim()) / 1000.0; }
        catch { return null; }
    }

    // ---------------------------------------------------------------- Mémoire / réseau

    private static Dictionary<string, double> ReadMeminfo()
    {
        var d = new Dictionary<string, double>();
        foreach (var l in File.ReadLines("/proc/meminfo"))
        {
            var parts = l.Split(':', 2);
            if (parts.Length < 2) continue;
            var v = parts[1].Trim().Split(' ')[0];
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var kb))
                d[parts[0]] = kb * 1024;
        }
        return d;
    }

    private (double Rx, double Tx) NetRates(long rx, long tx)
    {
        var now = DateTime.UtcNow;
        double rxRate = 0, txRate = 0;
        if (_prevNet is { } p)
        {
            var dt = (now - p.At).TotalSeconds;
            if (dt > 0) { rxRate = Math.Max(0, (rx - p.Rx) / dt); txRate = Math.Max(0, (tx - p.Tx) / dt); }
        }
        _prevNet = (rx, tx, now);
        return (rxRate, txRate);
    }

    private (double Rx, double Tx, long RxTotal) ReadNet()
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var l in File.ReadLines("/proc/net/dev").Skip(2))
            {
                var parts = l.Split(':', 2);
                var ifname = parts[0].Trim();
                if (ifname == "lo" || ifname.StartsWith("docker") || ifname.StartsWith("veth") || ifname.StartsWith("br-"))
                    continue;
                var f = parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                rx += long.Parse(f[0]);
                tx += long.Parse(f[8]);
            }
        }
        catch { /* /proc/net/dev illisible : débit à zéro */ }

        var (rxRate, txRate) = NetRates(rx, tx);
        return (rxRate, txRate, rx);
    }

    /// <summary> Total d'octets reçus par le réseau (lecture directe, hors échantillonnage). </summary>
    public static long ReadNetRxTotal()
    {
        if (OperatingSystem.IsWindows()) return WindowsSystemInfo.NetTotals().Rx;
        long rx = 0;
        try
        {
            foreach (var l in File.ReadLines("/proc/net/dev").Skip(2))
            {
                var parts = l.Split(':', 2);
                if (parts[0].Trim() == "lo") continue;
                rx += long.Parse(parts[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
            }
        }
        catch { /* /proc/net/dev illisible : total inconnu */ }
        return rx;
    }

    // ---------------------------------------------------------------- GPU

    private static double? Num(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<GpuSample>> ReadGpusAsync()
    {
        var list = new List<GpuSample>();
        if (HasNvidia)
        {
            var (code, output) = await ProcessRunner.CaptureAsync("nvidia-smi",
            [
                "--query-gpu=index,name,utilization.gpu,memory.used,memory.total,memory.free,temperature.gpu,power.draw,power.limit,fan.speed,clocks.sm,uuid",
                "--format=csv,noheader,nounits",
            ], 8000);
            if (code == 0)
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = line.Split(',').Select(x => x.Trim()).ToArray();
                    if (f.Length < 12) continue;
                    const double mib = 1024 * 1024;
                    list.Add(new GpuSample(
                        (int)(Num(f[0]) ?? list.Count), f[1], "NVIDIA",
                        Num(f[2]) ?? 0, (Num(f[3]) ?? 0) * mib, (Num(f[4]) ?? 0) * mib, (Num(f[5]) ?? 0) * mib,
                        Num(f[6]), Num(f[7]), Num(f[8]), Num(f[9]), Num(f[10]), f[11]));
                }
            }
        }

        foreach (var dev in _amdCards)
        {
            try
            {
                double Read(string f) => double.TryParse(File.ReadAllText(Path.Combine(dev, f)).Trim(), out var v) ? v : 0;
                var total = Read("mem_info_vram_total");
                var used  = Read("mem_info_vram_used");
                double? temp = null, power = null;
                var hw = Directory.GetDirectories(Path.Combine(dev, "hwmon")).FirstOrDefault();
                if (hw != null)
                {
                    if (File.Exists(Path.Combine(hw, "temp1_input")))
                        temp = double.Parse(File.ReadAllText(Path.Combine(hw, "temp1_input")).Trim()) / 1000;
                    var pf = new[] { "power1_average", "power1_input" }.Select(x => Path.Combine(hw, x)).FirstOrDefault(File.Exists);
                    if (pf != null) power = double.Parse(File.ReadAllText(pf).Trim()) / 1e6;
                }
                list.Add(new GpuSample(list.Count, "AMD GPU", "AMD", Read("gpu_busy_percent"),
                    used, total, total - used, temp, power, null, null, null, dev));
            }
            catch { /* GPU AMD illisible : ignoré */ }
        }
        return list;
    }

    private static async Task<IReadOnlyList<GpuProcess>> ReadNvidiaProcessesAsync(IReadOnlyList<GpuSample> gpus)
    {
        var (code, output) = await ProcessRunner.CaptureAsync("nvidia-smi",
            ["--query-compute-apps=pid,process_name,used_memory,gpu_uuid", "--format=csv,noheader,nounits"], 8000);
        if (code != 0) return Array.Empty<GpuProcess>();

        var res = new List<GpuProcess>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split(',').Select(x => x.Trim()).ToArray();
            if (f.Length < 4 || !int.TryParse(f[0], out var pid)) continue;
            var gpu = gpus.FirstOrDefault(g => g.Uuid == f[3]);
            res.Add(new GpuProcess(pid, f[1], (Num(f[2]) ?? 0) * 1024 * 1024, gpu?.Index ?? 0));
        }
        return res;
    }
}
