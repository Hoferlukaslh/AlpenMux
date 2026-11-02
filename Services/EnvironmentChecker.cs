/*
    Fichier      :  EnvironmentChecker.cs
    Projet       :  AlpenMux

    Description  :
        Garde-fou de l'environnement Python de vLLM. Objectif : l'utilisateur ne doit jamais
        tomber sur un venv incohérent (ex. torch compilé pour CUDA 13.2 et torchaudio pour 13.0).

        1. Vérification statique (instantanée, lecture des dist-info) :
             - torch / torchvision / torchaudio compilés pour le même CUDA (suffixe +cuXXX)
             - versions exigées par vLLM respectées
        2. Test réel (≈ 10-20 s, mis en cache tant que le venv ne change pas) :
             import torch / torchaudio / torchvision / vllm.engine.arg_utils,
             GPU visible par PyTorch, architecture du GPU prise en charge par PyTorch.
        3. Choix du "backend" PyTorch avant une installation :
             simulation (uv --dry-run) pour chaque build CUDA compatible avec le pilote,
             on garde le premier où toute la famille torch vient du même build.
             Le choix est mémorisé dans le venv ; la famille torch est figée (contraintes)
             pour toutes les installations suivantes.
        4. Réparation : réinstalle la famille torch dans un build cohérent.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Paquet de la famille torch installé. </summary>
/// <param name="Name"> Nom du paquet. </param>
/// <param name="Version"> Version complète (2.9.0+cu128). </param>
/// <param name="Suffix"> Build (cu128, rocm6.4, cpu, pypi). </param>
public sealed record TorchPackage(string Name, string Version, string Suffix)
{
    /// <summary> Version sans le suffixe local (2.13.0+cu130 -> 2.13.0). </summary>
    public string BaseVersion => Version.Split('+')[0];
}

/// <summary> Résultat de la vérification statique du venv. </summary>
public sealed class EnvReport
{
    /// <summary> Problèmes bloquants trouvés. </summary>
    public List<string> Problems { get; } = new();
    /// <summary> Paquets de la famille torch installés, par nom. </summary>
    public Dictionary<string, TorchPackage> Torch { get; } = new();
    /// <summary> Aucun problème. </summary>
    public bool Ok => Problems.Count == 0;
}

/// <summary> Résultat du test réel (mis en cache tant que le venv ne change pas). </summary>
public sealed class SmokeResult
{
    /// <summary> Venv testé. </summary>
    public string Venv { get; set; } = "";
    /// <summary> Date de modification des paquets au moment du test. </summary>
    public long Stamp { get; set; }
    /// <summary> Le test a réussi. </summary>
    public bool Ok { get; set; }
    /// <summary> Erreur rencontrée, ou null. </summary>
    public string? Error { get; set; }
    /// <summary> Résumé (versions de torch, CUDA, vLLM, GPU). </summary>
    public string? Summary { get; set; }
    /// <summary> Date du test. </summary>
    public DateTime CheckedAt { get; set; }
}

/// <summary> Garde-fou de l'environnement Python de vLLM : vérification, choix du build PyTorch, réparation. </summary>
public interface IEnvironmentChecker
{
    /// <summary> Paquets installés : nom normalisé -> (nom, version). </summary>
    Dictionary<string, string> InstalledPackages();

    /// <summary> Vérification statique (lecture des dist-info, instantanée). </summary>
    EnvReport StaticCheck();

    /// <summary> Dernier test réel s'il est encore valable pour ce venv, sinon null. </summary>
    SmokeResult? CachedSmoke();

    /// <summary> Lance Python dans le venv et vérifie que tout s'importe et que le GPU est utilisable. </summary>
    Task<SmokeResult> SmokeTestAsync(Action<string>? log = null, CancellationToken ct = default);

    /// <summary> Version maximale de CUDA prise en charge par le pilote ("CUDA Version: 13.2" de nvidia-smi). </summary>
    Task<string?> DriverCudaAsync();

    /// <summary> Build PyTorch mémorisé pour ce venv (cu130, rocm6.4, cpu…). </summary>
    string? SavedBackend { get; }

    /// <summary>
    /// Choisit le build PyTorch pour lequel toute la famille torch est cohérente.
    /// Retourne (backend, versions) ou null si aucun ne convient.
    /// </summary>
    Task<(string Backend, Dictionary<string, string> Versions)?> ChooseBackendAsync(
        IEnumerable<string> packages, IEnumerable<string> extraArgs, Action<string> log, CancellationToken ct);

    /// <summary> Fige la famille torch installée : les installations suivantes ne peuvent plus la modifier. </summary>
    string? WriteConstraints();

    /// <summary> Mémorise le build PyTorch choisi pour ce venv. </summary>
    void Remember(string backend);

    /// <summary> Réinstalle la famille torch dans un build cohérent (versions exigées par vLLM). </summary>
    Task<bool> RepairAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);

    /// <summary> Vérification complète : statique puis test réel. Retourne null si tout va bien, sinon le problème. </summary>
    Task<string?> VerifyAsync(Action<string> log, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IEnvironmentChecker"/>. </summary>
public sealed partial class EnvironmentChecker : IEnvironmentChecker
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;
    private readonly ISystemMonitorService _monitor;
    private readonly IUvPipInstaller _uv;

    /// <summary> Crée le garde-fou. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv et chemin de uv. </param>
    /// <param name="monitor"> Présence d'un GPU NVIDIA. </param>
    /// <param name="uv"> Réinstallation de la famille torch. </param>
    public EnvironmentChecker(ISettingsService settings, IPythonEnvironment python, ISystemMonitorService monitor, IUvPipInstaller uv)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(uv);
        _settings = settings;
        _python = python;
        _monitor = monitor;
        _uv = uv;
    }

    /// <summary> Paquets dont les builds doivent venir de la même variante CUDA / ROCm / CPU. </summary>
    public static readonly string[] TorchFamily = ["torch", "torchvision", "torchaudio"];

    /// <summary> Builds CUDA proposés par PyTorch, du plus récent au plus ancien. </summary>
    private static readonly (string Backend, double Cuda)[] CudaBackends =
    [
        ("cu132", 13.2), ("cu130", 13.0), ("cu129", 12.9), ("cu128", 12.8),
        ("cu126", 12.6), ("cu124", 12.4), ("cu121", 12.1), ("cu118", 11.8),
    ];

    private string Venv => _settings.VenvPath;
    // Chemins Linux (arguments de uv) ; lus et écrits via LinuxHost.Local (\\wsl.localhost\… sous Windows)
    private string BackendFile => LinuxHost.Combine(Venv, $".{AppPaths.Id}-torch-backend");
    private string ConstraintsFile => LinuxHost.Combine(Venv, $".{AppPaths.Id}-constraints.txt");
    private string CacheFile => Path.Combine(SettingsService.ConfigDirectory, "envcheck.json");

    // ---------------------------------------------------------------- Lecture du venv

    private string? SitePackages()
    {
        if (!LinuxHost.DirectoryExists(LinuxHost.Combine(Venv, "lib"))) return null;
        var lib = LinuxHost.Local(LinuxHost.Combine(Venv, "lib"));
        return Directory.GetDirectories(lib, "python*").Select(p => Path.Combine(p, "site-packages")).FirstOrDefault(Directory.Exists);
    }

    /// <inheritdoc />
    public Dictionary<string, string> InstalledPackages()
    {
        var res = new Dictionary<string, string>();
        if (SitePackages() is not { } sp) return res;
        foreach (var d in Directory.GetDirectories(sp, "*.dist-info"))
        {
            var n = Path.GetFileName(d)[..^".dist-info".Length];
            var i = n.IndexOf('-');
            if (i <= 0) continue;
            res[Normalize(n[..i])] = n[(i + 1)..];
        }
        return res;
    }

    private static string Normalize(string name) => name.ToLowerInvariant().Replace('_', '-').Replace('.', '-');

    private static string SuffixOf(string version) => version.Contains('+') ? version[(version.IndexOf('+') + 1)..] : "pypi";

    /// <summary> Versions exigées par vLLM pour la famille torch (Requires-Dist: torch==2.13.0). </summary>
    private Dictionary<string, string> VllmTorchPins()
    {
        var res = new Dictionary<string, string>();
        if (SitePackages() is not { } sp) return res;
        var meta = Directory.GetDirectories(sp, "vllm-*.dist-info").Select(d => Path.Combine(d, "METADATA")).FirstOrDefault(File.Exists);
        if (meta == null) return res;
        foreach (var line in File.ReadLines(meta))
        {
            var m = Regex.Match(line, @"^Requires-Dist:\s*(torch|torchvision|torchaudio)\s*\(?==\s*([^\s;)]+)");
            if (m.Success) res[m.Groups[1].Value] = m.Groups[2].Value;
        }
        return res;
    }

    /// <summary> Date de dernière modification des paquets : change à chaque installation / désinstallation. </summary>
    private long Stamp() => SitePackages() is { } sp ? Directory.GetLastWriteTimeUtc(sp).Ticks : 0;

    // ---------------------------------------------------------------- 1. Vérification statique

    /// <inheritdoc />
    public EnvReport StaticCheck()
    {
        var r = new EnvReport();
        var pkgs = InstalledPackages();
        foreach (var name in TorchFamily)
            if (pkgs.TryGetValue(name, out var v)) r.Torch[name] = new TorchPackage(name, v, SuffixOf(v));

        if (r.Torch.TryGetValue("torch", out var torch))
        {
            foreach (var p in r.Torch.Values.Where(p => p.Name != "torch" && p.Suffix != torch.Suffix))
                r.Problems.Add(Loc.F("Env_SuffixMismatch", p.Name, p.Version, torch.Version));

            foreach (var (name, pinned) in VllmTorchPins())
                if (r.Torch.TryGetValue(name, out var inst) && inst.BaseVersion != pinned)
                    r.Problems.Add(Loc.F("Env_VersionMismatch", name, inst.Version, pinned));
        }
        else if (pkgs.ContainsKey("vllm"))
            r.Problems.Add(Loc.T("Env_NoTorch"));
        return r;
    }

    // ---------------------------------------------------------------- 2. Test réel

    private const string SmokeScript = """
        import json, importlib.util
        info = {}
        try:
            import torch
            info["torch"] = torch.__version__
            info["cuda"] = torch.version.cuda
            info["hip"] = getattr(torch.version, "hip", None)
            info["available"] = torch.cuda.is_available()
            if info["available"]:
                info["archs"] = torch.cuda.get_arch_list()
                info["caps"] = ["%d%d" % torch.cuda.get_device_capability(i) for i in range(torch.cuda.device_count())]
                info["names"] = [torch.cuda.get_device_name(i) for i in range(torch.cuda.device_count())]
            for m in ("torchvision", "torchaudio"):
                if importlib.util.find_spec(m):
                    importlib.import_module(m)
            import vllm
            info["vllm"] = vllm.__version__
            import vllm.engine.arg_utils
            print("ALPENMUX_OK " + json.dumps(info))
        except BaseException as e:
            import traceback; traceback.print_exc()
            info["error"] = "%s: %s" % (type(e).__name__, e)
            print("ALPENMUX_FAIL " + json.dumps(info))
        """;

    /// <inheritdoc />
    public SmokeResult? CachedSmoke()
    {
        try
        {
            if (!File.Exists(CacheFile)) return null;
            var r = JsonSerializer.Deserialize(File.ReadAllText(CacheFile), AppJsonContext.Default.SmokeResult);
            return r != null && r.Venv == Venv && r.Stamp == Stamp() ? r : null;
        }
        catch { return null; }
    }

    /// <inheritdoc />
    public async Task<SmokeResult> SmokeTestAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        var res = new SmokeResult { Venv = Venv, Stamp = Stamp(), CheckedAt = DateTime.Now };
        string output = "";
        int code = -1;
        try
        {
            var lines = new List<string>();
            code = await ProcessRunner.RunAsync(_python.VenvPython, ["-c", SmokeScript],
                (l, p) => { if (!p) lock (lines) lines.Add(l); },
                new Dictionary<string, string?> { ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID", ["VLLM_CONFIGURE_LOGGING"] = "0" }, ct, linux: true);
            output = string.Join('\n', lines);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { output = ex.Message; }

        var okLine = output.Split('\n').LastOrDefault(l => l.StartsWith("ALPENMUX_OK ") || l.StartsWith("ALPENMUX_FAIL "));
        JsonElement info = default;
        if (okLine != null)
            try { info = JsonDocument.Parse(okLine[(okLine.IndexOf(' ') + 1)..]).RootElement; } catch { /* JSON illisible : valeur ignorée */ }

        string? S(string k) => info.ValueKind == JsonValueKind.Object && info.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var available = info.ValueKind == JsonValueKind.Object && info.TryGetProperty("available", out var a) && a.ValueKind == JsonValueKind.True;

        if (okLine == null || okLine.StartsWith("ALPENMUX_FAIL"))
        {
            res.Ok = false;
            res.Error = S("error") ?? output.Split('\n').LastOrDefault(l => l.Length > 0) ?? Loc.F("Common_ExitCode", code);
            log?.Invoke(output);
        }
        else if (_monitor.HasNvidia && !available)
        {
            res.Ok = false;
            res.Error = Loc.F("Env_NoGpuInTorch", S("cuda") ?? "?", await DriverCudaAsync() ?? "?");
        }
        else
        {
            res.Ok = true;
            // Architecture du GPU prise en charge par ce build de PyTorch ?
            if (available && info.TryGetProperty("caps", out var caps) && info.TryGetProperty("archs", out var archs))
            {
                var list = archs.EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                foreach (var cap in caps.EnumerateArray().Select(x => x.GetString() ?? "").Distinct())
                {
                    var supported = list.Contains($"sm_{cap}") ||
                                    list.Where(x => x.StartsWith("compute_")).Any(x => int.TryParse(x[8..], out var c) && c <= int.Parse(cap));
                    if (!supported)
                    {
                        res.Ok = false;
                        res.Error = Loc.F("Env_GpuArchUnsupported", $"sm_{cap}", S("torch") ?? "?");
                    }
                }
            }
            var gpu = available && info.TryGetProperty("names", out var n) ? n.EnumerateArray().FirstOrDefault().GetString() : null;
            res.Summary = $"torch {S("torch")} · CUDA {S("cuda") ?? S("hip") ?? "—"} · vLLM {S("vllm")}" + (gpu != null ? $" · {gpu} ✔" : "");
        }

        try
        {
            Directory.CreateDirectory(SettingsService.ConfigDirectory);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(res, AppJsonContext.Default.SmokeResult));
        }
        catch { /* cache facultatif : le test sera refait */ }
        return res;
    }

    /// <inheritdoc />
    public async Task<string?> DriverCudaAsync()
    {
        if (ProcessRunner.Which("nvidia-smi") == null) return null;
        var (code, output) = await ProcessRunner.CaptureAsync("nvidia-smi", [], 8000);
        var m = Regex.Match(output, @"CUDA Version:\s*([\d.]+)");
        return code == 0 && m.Success ? m.Groups[1].Value : null;
    }

    // ---------------------------------------------------------------- 3. Choix du build PyTorch

    /// <inheritdoc />
    public string? SavedBackend =>
        LinuxHost.Local(BackendFile) is var f && File.Exists(f) && File.ReadAllText(f).Trim() is { Length: > 0 } b ? b : null;

    private void SaveBackend(string backend)
    {
        try { File.WriteAllText(LinuxHost.Local(BackendFile), backend); } catch { /* venv en lecture seule : le build sera redemandé */ }
    }

    /// <summary> Builds à essayer, du plus adapté au moins adapté, selon le matériel. </summary>
    private async Task<List<string>> CandidateBackendsAsync()
    {
        var list = new List<string>();
        if (_monitor.HasNvidia)
        {
            var driver = await DriverCudaAsync();
            var max = double.TryParse(driver, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 99;
            list.AddRange(CudaBackends.Where(b => b.Cuda <= max + 0.001).Select(b => b.Backend));
        }
        list.Add("auto");
        return list.Distinct().ToList();
    }

    [GeneratedRegex(@"^\s*\+\s*([A-Za-z0-9_.\-]+)==(\S+)")]
    private static partial Regex DryRunAddRegex();

    /// <summary>
    /// Simule l'installation et retourne les versions finales de la famille torch
    /// (paquets installés + changements prévus), ou null si la résolution échoue.
    /// </summary>
    private async Task<Dictionary<string, string>?> SimulateAsync(IEnumerable<string> packages, string backend,
        IEnumerable<string> extraArgs, CancellationToken ct)
    {
        var args = new List<string> { "pip", "install", "-p", _python.VenvPython, "--dry-run" };
        args.AddRange(packages);
        args.AddRange(extraArgs);
        args.Add($"--torch-backend={backend}");
        var lines = new List<string>();
        var code = await ProcessRunner.RunAsync(_python.Uv, args, (l, p) => { if (!p) lock (lines) lines.Add(l); }, null, ct, linux: true);
        if (code != 0) return null;

        var final = InstalledPackages().Where(kv => TorchFamily.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var l in lines)
            if (DryRunAddRegex().Match(l) is { Success: true } m && TorchFamily.Contains(Normalize(m.Groups[1].Value)))
                final[Normalize(m.Groups[1].Value)] = m.Groups[2].Value;
        return final;
    }

    /// <inheritdoc />
    public async Task<(string Backend, Dictionary<string, string> Versions)?> ChooseBackendAsync(
        IEnumerable<string> packages, IEnumerable<string> extraArgs, Action<string> log, CancellationToken ct)
    {
        var pkgList = packages.ToList();
        var extra = extraArgs.ToList();
        foreach (var backend in await CandidateBackendsAsync())
        {
            ct.ThrowIfCancellationRequested();
            var final = await SimulateAsync(pkgList, backend, extra, ct);
            if (final == null) { log(Loc.F("Env_BackendUnavailable", backend)); continue; }
            var suffixes = final.Values.Select(SuffixOf).Distinct().ToList();
            var desc = string.Join(", ", final.Select(kv => $"{kv.Key} {kv.Value}"));
            if (suffixes.Count <= 1)
            {
                log(Loc.F("Env_BackendChosen", backend, desc));
                return (backend, final);
            }
            log(Loc.F("Env_BackendInconsistent", backend, desc));
        }
        return null;
    }

    /// <inheritdoc />
    public string? WriteConstraints()
    {
        var pkgs = InstalledPackages();
        var lines = TorchFamily.Where(pkgs.ContainsKey).Select(n => $"{n}=={pkgs[n]}").ToList();
        if (lines.Count == 0) return null;
        try
        {
            File.WriteAllText(LinuxHost.Local(ConstraintsFile), string.Join('\n', lines) + "\n");   // fins de ligne Linux
            if (SuffixOf(pkgs.GetValueOrDefault("torch", "")) is { } s && s != "pypi" && SavedBackend == null) SaveBackend(s);
            return ConstraintsFile;
        }
        catch { return null; }
    }

    /// <inheritdoc />
    public void Remember(string backend) => SaveBackend(backend);

    // ---------------------------------------------------------------- 4. Réparation

    /// <inheritdoc />
    public async Task<bool> RepairAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        progress.UnitsAreBytes = true;
        progress.Start(Loc.T("Env_Repairing"));
        var installed = InstalledPackages();
        var pins = VllmTorchPins();
        var wanted = TorchFamily.Where(n => installed.ContainsKey(n) || pins.ContainsKey(n))
            .Select(n => $"{n}=={(pins.TryGetValue(n, out var p) ? p : installed[n].Split('+')[0])}").ToList();
        if (wanted.Count == 0) { progress.Finish(Loc.T("Env_NoTorch")); return false; }

        log(Loc.F("Env_RepairTarget", string.Join(" ", wanted)));
        var reinstall = TorchFamily.SelectMany(n => new[] { "--reinstall-package", n }).ToList();
        var choice = await ChooseBackendAsync(wanted, reinstall, log, ct);
        if (choice is not { } c)
        {
            log(Loc.T("Env_NoConsistentBackend"));
            progress.Finish(Loc.T("Env_NoConsistentBackend"));
            return false;
        }

        var ok = await _uv.InstallAsync(wanted, Loc.T("Env_Repairing"), progress, log, ct,
            backend: c.Backend, extraArgs: reinstall);
        if (ok) { SaveBackend(c.Backend); WriteConstraints(); }
        return ok;
    }

    /// <inheritdoc />
    public async Task<string?> VerifyAsync(Action<string> log, CancellationToken ct)
    {
        var st = StaticCheck();
        if (!st.Ok) return string.Join("\n", st.Problems);
        log(Loc.T("Env_Testing"));
        var smoke = await SmokeTestAsync(log, ct);
        if (smoke.Ok) log("✔ " + smoke.Summary);
        return smoke.Ok ? null : smoke.Error;
    }
}
