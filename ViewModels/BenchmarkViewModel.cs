/*
    Fichier      :  BenchmarkViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Onglet Benchmark d'un serveur : `vllm bench serve` lancé contre ce serveur (requêtes aléatoires de
        longueur choisie, N requêtes, K en parallèle). Résultats : débit (requêtes/s, tokens/s), temps avant
        le premier token (TTFT), temps par token (TPOT). Chaque mesure est enregistrée à côté du journal
        du serveur (~/.local/share/alpenmux/instances/<id>/bench-*.json) et reste affichée dans l'historique.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> Une mesure : réglages et résultats principaux. </summary>
public sealed record BenchRun(DateTime At, string Settings, string Throughput, string Latency, List<ConfigRow> Rows);

public sealed partial class BenchmarkViewModel : ViewModelBase
{
    private readonly InstanceViewModel _server;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _inputLen = "512";
    [ObservableProperty] private string _outputLen = "128";
    [ObservableProperty] private string _numPrompts = "32";
    [ObservableProperty] private string _concurrency = "8";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private double _percent;

    /// <summary> Sortie du test en cours. </summary>
    public ObservableCollection<string> Log { get; } = new();
    /// <summary> Tests précédents de ce serveur. </summary>
    public ObservableCollection<BenchRun> History { get; } = new();
    /// <summary> Au moins un test précédent. </summary>
    public bool HasHistory => History.Count > 0;

    /// <summary> Benchmark possible : serveur vLLM de texte prêt (pas llama.cpp, stable-diffusion.cpp ni synthèse vocale). </summary>
    public bool IsAvailable => !_server.Config.IsNative && !_server.Config.Omni && !_server.IsTts;

    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;
    private readonly IInstanceStore _instanceStore;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="server"> Serveur testé. </param>
    /// <param name="settings"> Réglages (venv). </param>
    /// <param name="python"> Commande vllm du venv. </param>
    /// <param name="instanceStore"> Dossier du serveur (historique des tests). </param>
    public BenchmarkViewModel(InstanceViewModel server, ISettingsService settings, IPythonEnvironment python, IInstanceStore instanceStore)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(instanceStore);
        _settings = settings;
        _python = python;
        _instanceStore = instanceStore;
        _server = server;
        History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasHistory));
        LoadHistory();
    }

    private string Dir => _instanceStore.DirOf(_server.Id);

    private static int Parse(string s, int def, int min, int max) => int.TryParse(s.Trim(), out var v) ? Math.Clamp(v, min, max) : def;

    [GeneratedRegex(@"(\d+)/(\d+)\s*\[")]
    private static partial Regex TqdmRegex();

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning || !_server.IsReady || !IsAvailable) return;
        if (_server.IsSleeping && !await _server.WakeUpAsync()) return;

        var input = Parse(InputLen, 512, 1, 1_000_000);
        var output = Parse(OutputLen, 128, 1, 100_000);
        var prompts = Parse(NumPrompts, 32, 1, 10_000);
        var conc = Parse(Concurrency, 8, 1, 1024);
        // Moins de requêtes que de places en parallèle : vLLM n'en lancerait que `prompts` à la fois.
        // On complète pour avoir au moins deux vagues complètes (mesure moins bruitée).
        var notes = new List<string>();
        if (prompts < conc * 2)
        {
            notes.Add(Loc.F("Bench_PromptsRaised", prompts, conc * 2, conc));
            prompts = Math.Min(10_000, conc * 2);
            NumPrompts = prompts.ToString();
        }
        // Le serveur ne traite que --max-num-seqs requêtes à la fois, le reste attend
        if (_server.Config.MaxNumSeqs is { } seqs && conc > seqs)
            notes.Add(Loc.F("Bench_SeqsLimit", conc, seqs));
        // Cache KV trop petit pour toutes les requêtes simultanées : les autres attendent (premier token très tardif)
        else if (_server.KvCacheTokens is { } kv && (long)conc * (input + output) > kv)
            notes.Add(Loc.F("Bench_KvLimit", conc, kv.ToString("N0", CultureInfo.CurrentCulture), Math.Max(1, kv / (input + output))));
        var file = $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var c = _server.Config;

        var args = new List<string>
        {
            "bench", "serve", "--backend", "openai-chat", "--endpoint", "/v1/chat/completions",
            "--base-url", $"http://127.0.0.1:{c.Port}",
            "--model", LinuxHost.ToLinux(c.ModelTarget), "--served-model-name", _server.ServedName,
            "--tokenizer", LinuxHost.ToLinux(string.IsNullOrWhiteSpace(c.Tokenizer) ? c.ModelTarget : c.Tokenizer),
            "--dataset-name", "random", "--random-input-len", input.ToString(), "--random-output-len", output.ToString(),
            "--num-prompts", prompts.ToString(), "--max-concurrency", conc.ToString(), "--ignore-eos",
            "--save-result", "--result-dir", LinuxHost.ToLinux(Dir), "--result-filename", file,
            // Longueurs choisies : vLLM ne les écrit pas d'office dans le fichier de résultats
            "--metadata", $"input_len={input}", $"output_len={output}",
        };
        // Requêtes de chauffe non comptées (graphes CUDA, caches) : sans elles, les mesures courtes varient beaucoup
        // (option de `vllm bench serve`, absente de la liste lue dans `vllm serve --help=all`)
        args.Add("--num-warmups"); args.Add(Math.Min(conc, prompts).ToString());
        if (c.TrustRemoteCode) args.Add("--trust-remote-code");
        var env = new Dictionary<string, string?>
        {
            ["VIRTUAL_ENV"] = _settings.VenvPath,
            ["PATH"] = LinuxHost.PathWith(LinuxHost.Combine(_settings.VenvPath, "bin")),
            ["HF_HUB_CACHE"] = LinuxHost.ToLinux(_settings.ModelsDir),
            ["OPENAI_API_KEY"] = _server.ApiKey,
            ["NO_COLOR"] = "1",
        };

        IsRunning = true;
        Percent = 0;
        Log.Clear();
        Status = Loc.T("Bench_Running");
        _cts = new CancellationTokenSource();
        foreach (var n in notes) Log.Add("# " + n);
        Log.Add("$ vllm " + string.Join(' ', args.Select(LaunchConfig.Quote)));
        int code;
        try
        {
            code = await ProcessRunner.RunAsync(_python.VllmExe, args, (line, isProgress) =>
            {
                var text = ProcessRunner.StripAnsi(line);
                Dispatcher.UIThread.Post(() =>
                {
                    if (TqdmRegex().Match(text) is { Success: true } m && double.TryParse(m.Groups[2].Value, out var total) && total > 0)
                        Percent = 100 * double.Parse(m.Groups[1].Value) / total;
                    if (isProgress) return;
                    Log.Add(text);
                    while (Log.Count > 400) Log.RemoveAt(0);
                });
            }, env, _cts.Token, linux: true);
        }
        catch (OperationCanceledException) { code = -1; }
        finally { IsRunning = false; }

        var path = Path.Combine(Dir, file);
        if (code == 0 && File.Exists(path) && ReadRun(path) is { } run)
        {
            History.Insert(0, run);
            Status = string.Join(" ", notes.Prepend(Loc.T("Bench_Done")));
            Percent = 100;
        }
        else Status = code == -1 ? Loc.T("Bench_Cancelled") : Loc.F("Common_ExitCode", code);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void DeleteRun(BenchRun? run)
    {
        if (run == null) return;
        History.Remove(run);
        try { File.Delete(Path.Combine(Dir, $"bench-{run.At:yyyyMMdd-HHmmss}.json")); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
    }

    private void LoadHistory()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.GetFiles(Dir, "bench-*.json").OrderByDescending(f => f))
            if (ReadRun(f) is { } run) History.Add(run);
    }

    /// <summary> Résultats enregistrés par vllm bench serve (--save-result). </summary>
    private static BenchRun? ReadRun(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var r = doc.RootElement;
            double D(string k) => r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : double.NaN;
            string N(double v, string fmt = "0.0") => double.IsNaN(v) ? "—" : v.ToString(fmt, CultureInfo.CurrentCulture);
            var at = DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path)["bench-".Length..], "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : File.GetLastWriteTime(path);

            // Métadonnées enregistrées en texte (--metadata clé=valeur)
            double M(string k) => r.TryGetProperty(k, out var v) && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN;
            // Parallélisme réel : jamais plus que le nombre de requêtes
            var conc = Math.Min(D("max_concurrency"), D("num_prompts"));
            var settings = Loc.F("Bench_Settings", N(D("num_prompts"), "0"), N(conc, "0"),
                N(M("input_len"), "0"), N(M("output_len"), "0"));
            if (settings.Contains('—'))
            {
                // Anciennes versions : réglages absents du fichier, on garde le nombre de requêtes
                settings = Loc.F("Bench_SettingsShort", N(D("completed"), "0"));
            }
            var rows = new List<ConfigRow>
            {
                new(Loc.T("Bench_Completed"), $"{N(D("completed"), "0")} · {N(D("duration"), "0.0")} s"),
                new(Loc.T("Bench_ReqThroughput"), N(D("request_throughput"), "0.00") + " req/s"),
                new(Loc.T("Bench_OutThroughput"), N(D("output_throughput"), "0") + " tok/s"),
                new(Loc.T("Bench_TotalThroughput"), N(D("total_token_throughput"), "0") + " tok/s"),
                new(Loc.T("Bench_Ttft"), $"{N(D("mean_ttft_ms"), "0")} ms  (p99 {N(D("p99_ttft_ms"), "0")} ms)"),
                new(Loc.T("Bench_Tpot"), $"{N(D("mean_tpot_ms"), "0.0")} ms  (p99 {N(D("p99_tpot_ms"), "0.0")} ms)"),
                new(Loc.T("Bench_Itl"), $"{N(D("mean_itl_ms"), "0.0")} ms"),
            };
            return new BenchRun(at, settings,
                Loc.F("Bench_ThroughputShort", N(D("output_throughput"), "0")),
                Loc.F("Bench_LatencyShort", N(D("mean_ttft_ms"), "0"), N(D("mean_tpot_ms"), "0.0")), rows);
        }
        catch { return null; }
    }
}
