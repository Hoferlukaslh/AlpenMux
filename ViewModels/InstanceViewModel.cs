/*
    Fichier      :  InstanceViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Un serveur géré par l'application (`vllm serve`, ou llama-server / sd-server pour llama.cpp / stable-diffusion.cpp) :
            - processus dans sa propre session (arrêt propre de tous les enfants)
            - vLLM écrit directement dans le journal de l'instance (InstanceStore) ;
              l'application suit ce fichier : le journal survit à la fermeture de l'application
              et un serveur encore en marche est retrouvé au lancement suivant
            - progression du démarrage (chargement des poids, CUDA graphs…) avec % et temps restant
            - métriques en temps réel lues sur /metrics (requêtes, cache KV, tokens/s)
            - configuration affichée, ajout aux favoris
            - historique conservé jusqu'à la suppression du serveur
            - mise en veille / réveil (--enable-sleep-mode) : la VRAM est libérée, le modèle passe en RAM
            - test de performance (vllm bench serve) dans l'onglet Benchmark
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> État d'un serveur. </summary>
public enum InstanceState
{
    /// <summary> Démarrage en cours. </summary>
    Starting,
    /// <summary> Prêt à répondre. </summary>
    Ready,
    /// <summary> Arrêt en cours. </summary>
    Stopping,
    /// <summary> Arrêté. </summary>
    Stopped,
    /// <summary> Arrêté sur une erreur. </summary>
    Failed
}

public sealed partial class InstanceViewModel : ViewModelBase
{
    private const int MaxLogLines = 5000;
    private const string ExitMarker = "__ALPENMUX_EXIT__";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    // Le shell écrit le code de sortie de vLLM à la fin du journal. Les signaux INT/TERM sont
    // "piégés" (et non ignorés) : vLLM les reçoit normalement et le shell écrit quand même la fin.
    // ALPENMUX_PID (Windows / WSL) : le shell y écrit son PID, qui est aussi le groupe de processus Linux (setsid),
    // pour pouvoir arrêter vLLM et le retrouver depuis Windows.
    private const string Runner =
        "[ -n \"$ALPENMUX_PID\" ] && echo $$ >\"$ALPENMUX_PID\"; " +
        "trap true INT TERM; \"$@\" >>\"$ALPENMUX_LOG\" 2>&1; echo \"" + ExitMarker + " $?\" >>\"$ALPENMUX_LOG\"";

    /// <summary>
    /// Serveur vLLM sous Windows : il tourne dans la distribution WSL, Pid est alors le groupe de processus Linux.
    /// (llama.cpp tourne nativement sous Windows : Pid est celui de cmd.exe.)
    /// </summary>
    private bool InWsl => LinuxHost.IsWsl && !Config.IsNative;

    private string PidFile => Path.Combine(_instanceStore.DirOf(Id), "pid");


    /// <summary> Enregistrement de l'historique. </summary>
    public InstanceRecord Record { get; }
    /// <summary> Configuration de lancement. </summary>
    public LaunchConfig Config => Record.Config;

    /// <summary> Identifiant. </summary>
    public string Id => Record.Id;
    /// <summary> Adresse d'écoute. </summary>
    public string Host => Config.Host;
    /// <summary> Port. </summary>
    public int Port => Config.Port;
    /// <summary> Nom exposé par l'API. </summary>
    public string ServedName => string.IsNullOrWhiteSpace(Config.ServedName) ? Config.ModelRepo : Config.ServedName;
    /// <summary> Clé API exigée, ou null. </summary>
    public string? ApiKey => string.IsNullOrWhiteSpace(Config.ApiKey) ? null : Config.ApiKey;
    /// <summary> Nom du modèle. </summary>
    public string ModelRepo => Config.ModelRepo;

    /// <summary> Nom du moteur qui fait tourner le serveur (pour les textes de la console). </summary>
    public string EngineName => Config.IsLlama ? "llama.cpp" : Config.IsSdCpp ? "stable-diffusion.cpp" : "vLLM";
    /// <summary> Titre de la console. </summary>
    public string ConsoleTitle => Loc.F("Inst_Console", EngineName);
    /// <summary> Aide de la saisie dans la console. </summary>
    public string StdinHint => Loc.F("Inst_Stdin", EngineName);
    /// <summary> Aide du bouton d'interruption. </summary>
    public string InterruptHint => Loc.F("Inst_Interrupt", EngineName);

    /// <summary> Ligne de commande du lancement. </summary>
    public string CommandLine => string.Join(' ', Record.Environment.Where(e => e.Key.EndsWith("VISIBLE_DEVICES"))
        .Select(e => $"{e.Key}={e.Value}").Append(Config.IsLlama ? "llama-server" : Config.IsSdCpp ? "sd-server" : "vllm").Concat(Record.Arguments.Select(LaunchConfig.Quote)));
    /// <summary> Adresse locale du serveur. </summary>
    public string LocalUrl => $"http://127.0.0.1:{Port}";
    /// <summary> Adresse de l'API (…/v1). </summary>
    public string ApiUrl => $"http://{(Host is "0.0.0.0" or "" ? "127.0.0.1" : Host)}:{Port}/v1";
    /// <summary> Fichier du journal. </summary>
    public string LogPath => _instanceStore.LogOf(Id);

    /// <summary> Accès par l'adresse unique du routeur (si actif). </summary>
    public string RouterHint => _router.IsRunning && IsReady ? Loc.F("Router_InstanceHint", _router.BaseUrl, ServedName) : "";

    /// <summary> Lignes du journal affichées. </summary>
    public ObservableCollection<string> Logs { get; } = new();
    /// <summary> Résumé de la configuration. </summary>
    public ObservableCollection<ConfigRow> ConfigRows { get; } = new();
    /// <summary> Progression du démarrage. </summary>
    public ProgressTracker Progress { get; } = new() { UnitsAreBytes = false, EtaMinPercent = 10 };

    [ObservableProperty] private InstanceState _state = InstanceState.Stopped;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _lastError = "";
    [ObservableProperty] private string _stdinText = "";
    [ObservableProperty] private bool   _autoScroll = true;
    [ObservableProperty] private string _uptimeText = "";
    [ObservableProperty] private string _historyText = "";
    [ObservableProperty] private bool   _canSendInput;
    [ObservableProperty] private int    _selectedTab;
    [ObservableProperty] private string _favoriteName = "";
    [ObservableProperty] private string _favoriteStatus = "";

    // Explication de l'échec en clair
    [ObservableProperty] private string _errorTitle = "";
    [ObservableProperty] private string _errorHint = "";
    [ObservableProperty] private FixAction _errorAction;
    [ObservableProperty] private bool _showRawError;
    /// <summary> Une erreur reconnue est expliquée. </summary>
    public bool HasExplainedError => ErrorTitle.Length > 0;
    /// <summary> Un bouton de correction est proposé. </summary>
    public bool HasErrorAction => ErrorAction != FixAction.None;
    /// <summary> Libellé du bouton de correction. </summary>
    public string ErrorActionText => ErrorAction switch
    {
        FixAction.Setup => Loc.T("Err_Action_Setup"),
        FixAction.EditConfig => Loc.T("Err_Action_Edit"),
        FixAction.Token => Loc.T("Err_Action_Token"),
        FixAction.Models => Loc.T("Err_Action_Models"),
        _ => "",
    };
    partial void OnErrorTitleChanged(string value) => OnPropertyChanged(nameof(HasExplainedError));
    partial void OnErrorActionChanged(FixAction value)
    {
        OnPropertyChanged(nameof(HasErrorAction));
        OnPropertyChanged(nameof(ErrorActionText));
    }

    private void ExplainFailure()
    {
        var e = State == InstanceState.Failed ? ErrorExplainer.Explain(Logs.Select(ProcessRunner.StripAnsi)) : null;
        ErrorTitle = e?.Title ?? "";
        ErrorHint = e?.Hint ?? "";
        ErrorAction = e?.Action ?? FixAction.None;
    }

    [RelayCommand]
    private void ToggleRawError() => ShowRawError = !ShowRawError;

    [RelayCommand]
    private void RunErrorAction()
    {
        switch (ErrorAction)
        {
            case FixAction.Setup: _appState.Navigate("Setup"); break;
            case FixAction.EditConfig: _appState.RequestEdit(Config.Clone()); break;
            case FixAction.Token or FixAction.Models: _appState.Navigate("Models"); break;
        }
    }

    // Métriques
    [ObservableProperty] private double _requestsRunning;
    [ObservableProperty] private double _requestsWaiting;
    [ObservableProperty] private double _kvCachePercent;
    [ObservableProperty] private double _promptTps;
    [ObservableProperty] private double _generationTps;
    [ObservableProperty] private double _totalGenerated;
    [ObservableProperty] private int?   _pid;

    /// <summary> Historique du débit de génération. </summary>
    public RingBuffer GenerationHistory { get; } = new(120);
    /// <summary> Historique de l'occupation du cache KV. </summary>
    public RingBuffer KvHistory { get; } = new(120);

    /// <summary> Le processus tourne (démarrage, prêt ou arrêt en cours). </summary>
    public bool IsRunning  => State is InstanceState.Starting or InstanceState.Ready or InstanceState.Stopping;
    /// <summary> Le serveur répond. </summary>
    public bool IsReady    => State == InstanceState.Ready;

    // ---------------------------------------------------------------- Mise en veille

    /// <summary> Le modèle dort : poids en RAM, VRAM libérée ; réveil en quelques secondes. </summary>
    [ObservableProperty] private bool _isSleeping;
    [ObservableProperty] private bool _sleepBusy;
    /// <summary> Le serveur peut être mis en veille. </summary>
    public bool CanSleep => Config.SleepMode && IsReady && !IsSleeping && !SleepBusy;
    /// <summary> Le serveur peut être réveillé. </summary>
    public bool CanWake => Config.SleepMode && IsReady && IsSleeping && !SleepBusy;
    partial void OnIsSleepingChanged(bool value) => RefreshSleepState();
    partial void OnSleepBusyChanged(bool value) => RefreshSleepState();

    private void RefreshSleepState()
    {
        OnPropertyChanged(nameof(CanSleep));
        OnPropertyChanged(nameof(CanWake));
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(StateClass));
        if (State == InstanceState.Ready) StatusText = IsSleeping ? Loc.T("Inst_Sleeping") : Loc.T("Inst_Ready");
        _appState.NotifyInstancesChanged();
    }

    private HttpRequestMessage DevRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, LocalUrl + path);
        if (ApiKey != null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        return req;
    }

    /// <summary> Met le modèle en veille (niveau 1 : poids copiés en RAM, cache KV abandonné). </summary>
    [RelayCommand]
    public async Task<bool> SleepAsync()
    {
        if (!Config.SleepMode || !IsReady || IsSleeping) return IsSleeping;
        await SwitchSleepAsync("Inst_SleepRequested", "/sleep?level=1", "/sleep", sleeping: true);
        return IsSleeping;
    }

    /// <summary> Réveille le modèle ; retourne true s'il est éveillé (ou n'a pas de mode veille). </summary>
    [RelayCommand]
    public async Task<bool> WakeUpAsync()
    {
        if (!IsSleeping) return true;
        await SwitchSleepAsync("Inst_WakeRequested", "/wake_up", "/wake_up", sleeping: false);
        return !IsSleeping;
    }

    /// <summary> Appelle un point d'entrée du mode veille de vLLM et met <see cref="IsSleeping"/> à jour s'il réussit. </summary>
    /// <param name="logKey"> Clé du message écrit dans le journal avant l'appel. </param>
    /// <param name="path"> Chemin appelé (POST). </param>
    /// <param name="label"> Nom du point d'entrée dans les messages d'erreur. </param>
    /// <param name="sleeping"> Valeur de IsSleeping en cas de succès. </param>
    private async Task SwitchSleepAsync(string logKey, string path, string label, bool sleeping)
    {
        SleepBusy = true;
        AppendLog(Loc.T(logKey));
        try
        {
            using var resp = await SleepHttp.SendAsync(DevRequest(HttpMethod.Post, path));
            if (resp.IsSuccessStatusCode) IsSleeping = sleeping;
            else AppendLog($"✘ {label} : " + (int)resp.StatusCode);
        }
        catch (Exception ex) { AppendLog($"✘ {label} : " + ex.Message); }
        finally { SleepBusy = false; }
    }

    /// <summary> Le réveil et la mise en veille copient les poids entre RAM et VRAM : plusieurs secondes. </summary>
    private static readonly HttpClient SleepHttp = new() { Timeout = TimeSpan.FromMinutes(5) };

    private async Task SyncSleepStateAsync(CancellationToken ct)
    {
        using var resp = await Http.SendAsync(DevRequest(HttpMethod.Get, "/is_sleeping"), ct);
        if (!resp.IsSuccessStatusCode) return;
        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("is_sleeping", out var v) && v.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
        {
            var sleeping = v.GetBoolean();
            Dispatcher.UIThread.Post(() => { if (!SleepBusy) IsSleeping = sleeping; });
        }
    }
    /// <summary> Le serveur démarre. </summary>
    public bool IsStarting => State == InstanceState.Starting;
    /// <summary> Le serveur peut être relancé. </summary>
    public bool CanRestart => State is InstanceState.Stopped or InstanceState.Failed;

    /// <summary> Icône de l'état. </summary>
    public string StateIcon => State switch
    {
        InstanceState.Ready when IsSleeping => Icons.Moon,
        InstanceState.Ready    => Icons.CheckCircle,
        InstanceState.Failed   => Icons.XCircle,
        InstanceState.Stopped  => Icons.Stop,
        _                      => Icons.Hourglass,
    };

    /// <summary> Classe de style de l'état. </summary>
    public string StateClass => State switch
    {
        InstanceState.Ready when IsSleeping => "muted",
        InstanceState.Ready  => "ok",
        InstanceState.Failed => "error",
        InstanceState.Stopped => "muted",
        _ => "warn",
    };

    /// <summary> Titre affiché (nom et port). </summary>
    public string Title => $"{ServedName}  ·  :{Port}";

    /// <summary> Modèle de génération d'images (vLLM-Omni, diffusion) : la page Tester propose un prompt d'image. </summary>
    public bool IsImageGen => Config.IsSdCpp || Config.Omni && !IsTts && (File.Exists(Path.Combine(Config.ModelTarget, "model_index.json"))
                              || Regex.IsMatch(Config.ModelRepo, "image|diffusion|flux|stable-diffusion|sd3|sdxl", RegexOptions.IgnoreCase));

    /// <summary> Le modèle accepte des images dans le chat (modèle vision lancé sans le mode texte seul). </summary>
    public bool AcceptsImages => !Config.IsNative && !Config.Omni && !Config.TextOnly &&
                                 _appState.Models.FirstOrDefault(m => m.Target == Config.ModelTarget)?.IsMultimodal == true;

    /// <summary> Onglet Benchmark (créé à la première ouverture). </summary>
    public BenchmarkViewModel Bench => _bench ??= new BenchmarkViewModel(this, _settings, _python, _instanceStore);
    private BenchmarkViewModel? _bench;

    /// <summary> Modèle de synthèse vocale : la page Tester affiche l'interface TTS au lieu du chat. </summary>
    public bool IsTts => _isTts ??= _ttsStore.LooksLikeTts(Config);

    /// <summary> Serveur d'embedding (RAG) : pas de chat, ni dans les cibles de discussion. </summary>
    public bool IsEmbedding => Config.Embedding;
    private bool? _isTts;

    private Process? _process;
    private readonly List<(string Line, bool Replace)> _pending = new();
    private bool _lastWasProgress;
    private DispatcherTimer? _flushTimer;
    private CancellationTokenSource? _runCts;
    private (double Prompt, double Gen, DateTime At)? _prevCounters;
    private double _stage;          // progression globale du démarrage (0-100)
    private volatile bool _ended;   // le processus est terminé
    private int? _exitCode;

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly IApiRouter _router;
    private readonly IInstanceStore _instanceStore;
    private readonly IPythonEnvironment _python;
    private readonly ITtsStore _ttsStore;
    private readonly IUiServices _ui;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="record"> Enregistrement du serveur (nouveau ou lu dans l'historique). </param>
    /// <param name="settings"> Réglages. </param>
    /// <param name="appState"> État partagé (liste des serveurs). </param>
    /// <param name="router"> Routeur (adresse affichée). </param>
    /// <param name="instanceStore"> Historique et journal du serveur. </param>
    /// <param name="python"> Python du venv (benchmark, vllm). </param>
    /// <param name="ttsStore"> Détection des modèles de synthèse vocale. </param>
    /// <param name="ui"> Presse-papiers et liens. </param>
    public InstanceViewModel(InstanceRecord record, ISettingsService settings, IAppState appState, IApiRouter router, IInstanceStore instanceStore, IPythonEnvironment python, ITtsStore ttsStore, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(instanceStore);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(ttsStore);
        ArgumentNullException.ThrowIfNull(ui);
        _settings = settings;
        _appState = appState;
        _router = router;
        _instanceStore = instanceStore;
        _python = python;
        _ttsStore = ttsStore;
        _ui = ui;
        Record = record;
        TotalGenerated = record.TotalGenerated;
        LastError = record.LastError ?? "";
        RefreshConfigRows();
        _router.Changed += () => OnPropertyChanged(nameof(RouterHint));
        Loc.Instance.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(RouterHint));
            OnStateChanged(State);
            RefreshConfigRows();
            ExplainFailure();
        };
    }

    /// <summary>
    /// Serveur de l'historique : journal rechargé, serveur retrouvé s'il tourne encore.
    /// Appelé une fois par <see cref="IInstanceViewModelFactory.Restore"/>, juste après la création.
    /// </summary>
    internal void RestoreFromHistory()
    {
        var vm = this;
        var record = Record;
        foreach (var l in _instanceStore.ReadTail(record.Id, keepAnsi: true).TakeLast(MaxLogLines)) vm.Logs.Add(vm.DisplayLine(l));

        var wasRunning = record.State is nameof(InstanceState.Starting) or nameof(InstanceState.Ready) or nameof(InstanceState.Stopping);
        var wsl = LinuxHost.IsWsl && !record.Config.IsNative;
        if (wasRunning && record.Pid is { } pid && ProcessRunner.GroupAlive(pid, wsl) && ProcessRunner.ProcessMatches(pid, ExitMarker, wsl))
            vm.Reattach(pid);
        else
        {
            if (wasRunning)
            {
                // L'application a été fermée (ou l'ordinateur éteint) pendant que le serveur tournait
                record.State = nameof(InstanceState.Stopped);
                record.EndedAt ??= DateTime.Now;
                _instanceStore.Save(record);
            }
            vm.State = Enum.TryParse<InstanceState>(record.State, out var st) ? st : InstanceState.Stopped;
            vm.OnStateChanged(vm.State);
            vm.ExplainFailure();
        }
        vm.UpdateHistoryText();
    }

    private void RefreshConfigRows()
    {
        ConfigRows.Clear();
        foreach (var r in Config.Describe()) ConfigRows.Add(r);
    }

    partial void OnStateChanged(InstanceState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(IsStarting));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(StateClass));
        OnPropertyChanged(nameof(CanSleep));
        OnPropertyChanged(nameof(CanWake));
        if (value != InstanceState.Ready) IsSleeping = false;
        OnPropertyChanged(nameof(RouterHint));
        StatusText = value switch
        {
            InstanceState.Starting => Loc.T("Inst_Starting"),
            InstanceState.Ready    => IsSleeping ? Loc.T("Inst_Sleeping") : Loc.T("Inst_Ready"),
            InstanceState.Stopping => Loc.T("Inst_Stopping"),
            InstanceState.Stopped  => Loc.T("Inst_Stopped"),
            InstanceState.Failed   => Loc.T("Inst_Failed"),
            _ => "",
        };
        Record.State = value.ToString();
        UpdateHistoryText();
        _appState.NotifyInstancesChanged();
    }

    private void Persist()
    {
        Record.LastError = string.IsNullOrEmpty(LastError) ? null : LastError;
        Record.Pid = Pid;
        Record.TotalGenerated = TotalGenerated;
        _instanceStore.Save(Record);
    }

    private void UpdateHistoryText()
    {
        if (Record.StartedAt is not { } start) { HistoryText = ""; return; }
        var end = IsRunning ? DateTime.Now : Record.EndedAt ?? DateTime.Now;
        HistoryText = Loc.F("Inst_History", start.ToString("g", CultureInfo.CurrentCulture), Format.Duration(end - start), Record.Runs);
    }

    // ---------------------------------------------------------------- Démarrage

    /// <summary> Lance le serveur (nouveau lancement ou relance). </summary>
    public void Start()
    {
        var venv = _settings.VenvPath;
        var env = new Dictionary<string, string?>
        {
            ["VIRTUAL_ENV"] = venv,
            ["PATH"] = LinuxHost.PathWith(LinuxHost.Combine(venv, "bin")),
            ["PYTHONHOME"] = null,
            ["HF_HUB_CACHE"] = LinuxHost.ToLinux(_settings.ModelsDir),
            ["VLLM_CONFIGURE_LOGGING"] = "1",
            // Console colorée : vLLM écrit dans un fichier, il faut forcer les couleurs
            ["NO_COLOR"] = null,
            ["VLLM_LOGGING_COLOR"] = "1",
            ["FORCE_COLOR"] = "1",
            ["ALPENMUX_LOG"] = LinuxHost.ToLinux(LogPath),
        };
        if (InWsl) env["ALPENMUX_PID"] = LinuxHost.ToLinux(PidFile);
        if (!string.IsNullOrWhiteSpace(_settings.Current.HfToken))
            env["HF_TOKEN"] = _settings.Current.HfToken!.Trim();
        foreach (var (k, v) in Record.Environment) env[k] = v;

        Directory.CreateDirectory(_instanceStore.DirOf(Id));
        var offset = _instanceStore.LogLength(Id);

        _stage = 0;
        _prevCounters = null;
        _ended = false;
        _exitCode = null;
        LastError = "";
        ErrorTitle = ErrorHint = "";
        ErrorAction = FixAction.None;
        Pid = null;
        Record.Runs++;
        Record.StartedAt = DateTime.Now;
        Record.EndedAt = null;
        Record.ExitCode = null;
        State = InstanceState.Starting;
        Progress.Start(Loc.T("Inst_Starting"), indeterminate: false);
        Progress.Report(1, 100, Loc.F("Inst_Stage_Init", EngineName));

        _instanceStore.AppendLine(Id, "");
        _instanceStore.AppendLine(Id, Ansi.HeaderColor + Loc.F("Inst_RunHeader", Record.Runs, DateTime.Now.ToString("G", CultureInfo.CurrentCulture)) + Ansi.Reset);
        _instanceStore.AppendLine(Id, Ansi.CommandColor + "$ " + CommandLine + Ansi.Reset);
        StartFlushTimer();

        try
        {
            var exe = Config.IsLlama ? LlamaCppService.ServerExe ?? throw new InvalidOperationException(Loc.T("Launch_NoLlama"))
                    : Config.IsSdCpp ? StableDiffusionCppService.ServerExe ?? throw new InvalidOperationException(Loc.T("Launch_NoSdCpp"))
                    : _python.VllmExe;
            var workDir = Directory.Exists(_settings.ModelsDir) ? _settings.ModelsDir : SettingsService.Home;
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows() && Config.IsNative)
                psi = WindowsRunner(exe, env, workDir);
            else
            {
                var args = new List<string> { "-c", Runner, AppPaths.Id, exe };
                args.AddRange(Record.Arguments);
                psi = LinuxHost.StartInfo("sh", args, env, newSession: true, redirectStdin: true, workDir: InWsl ? LinuxHost.ToLinux(workDir) : workDir);
            }
            if (File.Exists(PidFile)) File.Delete(PidFile);
            _process = new Process { StartInfo = psi };
            _process.Start();
            CanSendInput = !(OperatingSystem.IsWindows() && Config.IsNative);
            if (InWsl) _ = WaitForWslPidAsync(_process);
            else Pid = _process.Id;
        }
        catch (Exception ex)
        {
            _instanceStore.AppendLine(Id, "✘ " + ex.Message);
            LastError = ex.Message;
            _ended = true;
            FollowLog(offset);
            return;
        }
        Persist();

        var proc = _process;
        // Messages éventuels du shell lui-même (vLLM écrit dans le journal)
        _ = ProcessRunner.PumpAsync(proc, (l, _) => _instanceStore.AppendLine(Id, l));
        _ = Task.Run(async () =>
        {
            await proc.WaitForExitAsync();
            _ended = true;
        });
        FollowLog(offset);
    }

    /// <summary>
    /// llama.cpp sous Windows : cmd.exe écrit la sortie dans le journal (le serveur survit à la fermeture de
    /// l'application) puis le code de sortie, comme le shell sous Linux. !errorlevel! : expansion retardée (/v:on).
    /// </summary>
    private ProcessStartInfo WindowsRunner(string exe, Dictionary<string, string?> env, string workDir)
    {
        static string Q(string a) => "\"" + a.Replace("\"", "\\\"") + "\"";
        var line = $"{Q(exe)} {string.Join(' ', Record.Arguments.Select(Q))} >>{Q(LogPath)} 2>&1 & echo {ExitMarker} !errorlevel! >>{Q(LogPath)}";
        var psi = new ProcessStartInfo("cmd.exe", $"/d /s /v:on /c \"{line}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var (k, v) in env.Where(kv => kv.Key is "CUDA_VISIBLE_DEVICES" or "HIP_VISIBLE_DEVICES" or "CUDA_DEVICE_ORDER" or "HF_TOKEN"))
            if (v != null) psi.Environment[k] = v;
        return psi;
    }

    /// <summary> WSL : le groupe Linux du serveur est lu dans le fichier écrit par le shell, dès qu'il existe. </summary>
    private async Task WaitForWslPidAsync(Process proc)
    {
        for (int i = 0; i < 150 && !proc.HasExited; i++)
        {
            try
            {
                if (File.Exists(PidFile) && int.TryParse((await File.ReadAllTextAsync(PidFile)).Trim(), out var pgid))
                {
                    Dispatcher.UIThread.Post(() => { Pid = pgid; Persist(); });
                    return;
                }
            }
            catch { /* processus introuvable : suivi par PID impossible */ }
            await Task.Delay(200);
        }
    }

    /// <summary> Reprend le suivi d'un serveur lancé lors d'une session précédente de l'application. </summary>
    private void Reattach(int pid)
    {
        Pid = pid;
        _ended = false;
        CanSendInput = false;
        State = InstanceState.Starting;
        Progress.Start(Loc.T("Inst_Reattached"), indeterminate: true);
        Logs.Add(Loc.T("Inst_Reattached"));
        StartFlushTimer();

        _ = Task.Run(async () =>
        {
            var wsl = InWsl;
            while (ProcessRunner.GroupAlive(pid, wsl)) await Task.Delay(wsl ? 3000 : 1000);
            _ended = true;
        });
        FollowLog(_instanceStore.LogLength(Id));
    }

    private void StartFlushTimer()
    {
        _flushTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Flush());
        _flushTimer.Start();
    }

    private void FollowLog(long offset)
    {
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        _ = Task.Run(async () =>
        {
            await ProcessRunner.TailAsync(LogPath, offset, OnOutput, () => _ended, ct, keepAnsi: true);
            Dispatcher.UIThread.Post(() => OnExited(_exitCode ?? (_process is { HasExited: true } p ? TryExitCode(p) : null)));
        });
        _ = MetricsLoopAsync(ct);
    }

    private static int? TryExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return null; }
    }

    private void OnExited(int? code)
    {
        if (!IsRunning) return;
        _runCts?.Cancel();
        Flush();
        _flushTimer?.Stop();
        var wasStopping = State == InstanceState.Stopping;
        Record.ExitCode = code;
        Record.EndedAt = DateTime.Now;
        State = wasStopping || code == 0 ? InstanceState.Stopped : InstanceState.Failed;
        Progress.Finish(StatusText);
        if (State == InstanceState.Failed && string.IsNullOrEmpty(LastError))
            LastError = code is { } c ? Loc.F("Common_ExitCode", c) : Loc.T("Inst_UnknownExit");
        Pid = null;
        CanSendInput = false;
        _process = null;
        RequestsRunning = RequestsWaiting = PromptTps = GenerationTps = 0;
        ExplainFailure();
        Persist();
    }

    // ---------------------------------------------------------------- Journal et étapes

    [GeneratedRegex(@"GPU KV cache size: ([\d,]+) tokens")]
    private static partial Regex KvTokensRegex();

    /// <summary> Taille du cache KV annoncée par vLLM au démarrage (tokens), null si inconnue. </summary>
    public long? KvCacheTokens { get; private set; }

    [GeneratedRegex(@"Loading safetensors checkpoint shards:\s+(\d+)%")]
    private static partial Regex ShardsRegex();

    [GeneratedRegex(@"Capturing CUDA graphs?[^:]*:\s+(\d+)%")]
    private static partial Regex GraphsRegex();

    [GeneratedRegex(@"Loading (?:gguf|pt checkpoint|weights)[^:]*:\s+(\d+)%", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLoadRegex();

    [GeneratedRegex(@"(CUDA out of memory|OutOfMemoryError|ValueError: .*|RuntimeError: .*|No available memory for the cache blocks.*|Error: .*)")]
    private static partial Regex ErrorRegex();

    [GeneratedRegex(@"offloaded (\d+)/(\d+) layers to GPU", RegexOptions.IgnoreCase)]
    private static partial Regex LlamaOffloadRegex();

    /// <summary> Texte affiché pour une ligne du journal (le marqueur de fin est traduit et coloré). </summary>
    private string DisplayLine(string line)
    {
        var plain = ProcessRunner.StripAnsi(line);
        if (!plain.StartsWith(ExitMarker)) return line;
        var code = plain[ExitMarker.Length..].Trim();
        return (code == "0" ? Ansi.OkColor : Ansi.ErrorColor) + Loc.F("Inst_ExitedWith", code, EngineName) + Ansi.Reset;
    }

    private void OnOutput(string raw, bool isProgress)
    {
        // raw garde les couleurs (affichage) ; l'analyse se fait sur le texte seul
        var line = ProcessRunner.StripAnsi(raw);
        if (InstanceFilters.IsProbe(line)) return;
        if (line.StartsWith(ExitMarker))
        {
            if (int.TryParse(line[ExitMarker.Length..].Trim(), out var c)) _exitCode = c;
            _ended = true;
        }
        lock (_pending) _pending.Add((DisplayLine(raw), isProgress));
        DetectStage(line);
    }

    private void DetectStage(string line)
    {
        double? p = null;
        string? detail = null;

        if (ShardsRegex().Match(line) is { Success: true } s)
        { p = 10 + int.Parse(s.Groups[1].Value) * 0.5; detail = Loc.F("Inst_Stage_Weights", s.Groups[1].Value); }
        else if (OtherLoadRegex().Match(line) is { Success: true } o)
        { p = 10 + int.Parse(o.Groups[1].Value) * 0.5; detail = Loc.F("Inst_Stage_Weights", o.Groups[1].Value); }
        else if (line.Contains("Model loading took") || line.Contains("Loading weights took"))
        { p = 62; detail = Loc.T("Inst_Stage_Profiling"); }
        else if (line.Contains("torch.compile") || line.Contains("Compiling a graph") || line.Contains("Dynamo bytecode"))
        { p = 66; detail = Loc.T("Inst_Stage_Compile"); }
        else if (line.Contains("KV cache") && (line.Contains("GPU KV cache size") || line.Contains("Available KV cache memory")))
        {
            p = 74; detail = Loc.T("Inst_Stage_Kv");
            if (KvTokensRegex().Match(line) is { Success: true } kv && long.TryParse(kv.Groups[1].Value.Replace(",", ""), out var tokens))
                KvCacheTokens = tokens;
        }
        else if (GraphsRegex().Match(line) is { Success: true } g)
        { p = 75 + int.Parse(g.Groups[1].Value) * 0.2; detail = Loc.F("Inst_Stage_Graphs", g.Groups[1].Value); }
        else if (line.Contains("Starting vLLM API server") || line.Contains("Available routes are"))
        { p = 97; detail = Loc.T("Inst_Stage_Api"); }
        else if (line.Contains("Application startup complete") || line.Contains("server is listening on"))
        {
            Dispatcher.UIThread.Post(MarkReady);
            return;
        }
        // stable-diffusion.cpp (sd-server) : prêt quand il écoute ; poids chargés au premier usage
        else if (Config.IsSdCpp && line.Contains("listening on:"))
        {
            Dispatcher.UIThread.Post(MarkReady);
            return;
        }
        else if (Config.IsSdCpp && line.Contains("loading model from"))
        { p = 20; detail = Loc.T("Inst_Stage_Weights_Llama"); }
        else if (Config.IsSdCpp && line.Contains("total params memory size"))
        { p = 80; detail = Loc.T("Inst_SdCpp_Placed"); }
        // llama.cpp (llama-server)
        else if (line.Contains("llama_model_loader: loaded meta data"))
        { p = 10; detail = Loc.T("Inst_Llama_Meta"); }
        else if (line.Contains("load_tensors:") && Config.IsLlama && _stage < 30)
        { p = 30; detail = Loc.T("Inst_Stage_Weights_Llama"); }
        else if (LlamaOffloadRegex().Match(line) is { Success: true } off)
        { p = 60; detail = Loc.F("Inst_Llama_Offload", off.Groups[1].Value, off.Groups[2].Value); }
        else if (line.Contains("llama_context:") && Config.IsLlama && _stage < 75)
        { p = 75; detail = Loc.T("Inst_Stage_Kv"); }
        else if (line.Contains("warming up the model"))
        { p = 90; detail = Loc.T("Inst_Llama_Warmup"); }
        else if (line.Contains("Initializing a V1 LLM engine") || line.Contains("Resolved architecture"))
        { p = 5; detail = Loc.T("Inst_Stage_Engine"); }

        if (ErrorRegex().Match(line) is { Success: true } err)
            Dispatcher.UIThread.Post(() => LastError = err.Value.Length > 300 ? err.Value[..300] : err.Value);

        if (p is { } v && v >= _stage)
        {
            _stage = v;
            Progress.Report(v, 100, detail);
        }
    }

    private void MarkReady()
    {
        if (State != InstanceState.Starting) return;
        State = InstanceState.Ready;
        Progress.Finish(Loc.T("Inst_Ready"));
        Persist();
    }

    /// <summary> Ligne ajoutée par l'application : écrite dans le journal (affichée via le suivi du fichier). </summary>
    private void AppendLog(string line)
    {
        _instanceStore.AppendLine(Id, line);
        if (!IsRunning) lock (_pending) _pending.Add((line, false));
    }

    private void Flush()
    {
        List<(string Line, bool Replace)> batch;
        lock (_pending)
        {
            batch = new(_pending);
            _pending.Clear();
        }

        foreach (var (line, isProgress) in batch)
        {
            // Les barres tqdm (lignes terminées par \r) remplacent la ligne précédente
            if (_lastWasProgress && Logs.Count > 0) Logs[^1] = line;
            else Logs.Add(line);
            _lastWasProgress = isProgress;
        }
        while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);

        if (IsRunning && Record.StartedAt is { } st) UptimeText = Format.Duration(DateTime.Now - st);
    }

    [RelayCommand]
    private void ClearLogs() => Logs.Clear();

    /// <summary> Journal complet sans couleurs (copie). </summary>
    public string AllLogs() => string.Join('\n', Logs.Select(ProcessRunner.StripAnsi));

    [RelayCommand]
    private void OpenLogFile()
    {
        if (File.Exists(LogPath)) _ui.Open(LogPath);
    }

    [RelayCommand]
    private async Task SendInput()
    {
        if (_process == null || !IsRunning) return;
        var text = StdinText;
        StdinText = "";
        try
        {
            await _process.StandardInput.WriteLineAsync(text);
            await _process.StandardInput.FlushAsync();
            AppendLog("> " + text);
        }
        catch (Exception ex) { AppendLog("✘ " + ex.Message); }
    }

    [RelayCommand]
    private void SendInterrupt()
    {
        if (Pid is { } pid) ProcessRunner.KillGroup(pid, ProcessRunner.SIGINT, InWsl);
    }

    // ---------------------------------------------------------------- Arrêt

    /// <summary> Arrête le serveur : arrêt progressif du groupe de processus (30 s), arrêt forcé ensuite. </summary>
    [RelayCommand]
    public async Task StopAsync()
    {
        if (!IsRunning) return;
        if (Pid is not { } pid)
        {
            // WSL : groupe Linux pas encore connu (tout début du démarrage) -> arrêt de wsl.exe
            if (_process is { HasExited: false } p) { State = InstanceState.Stopping; try { p.Kill(true); } catch { /* processus déjà terminé */ } }
            return;
        }
        State = InstanceState.Stopping;
        AppendLog(Loc.T("Inst_StopRequested"));
        await ProcessRunner.StopGroupAsync(pid, 30, InWsl);
        _ended = true;
    }

    [RelayCommand]
    private void Restart()
    {
        if (IsRunning) return;
        if (_appState.IsPortTaken(Port)) { LastError = Loc.F("Launch_PortTaken", Port); return; }
        Start();
    }

    [RelayCommand]
    private void Remove() => _appState.RemoveInstance(this);

    [RelayCommand]
    private void EditConfig() => _appState.RequestEdit(Config.Clone());

    [RelayCommand]
    private void AddFavorite()
    {
        var name = string.IsNullOrWhiteSpace(FavoriteName) ? $"{ServedName} :{Port}" : FavoriteName.Trim();
        _appState.AddFavorite(name, Config);
        FavoriteName = "";
        FavoriteStatus = Loc.F("Fav_Added", name);
    }

    // ---------------------------------------------------------------- Métriques

    private async Task MetricsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }

            try
            {
                if (State == InstanceState.Starting)
                {
                    // sd-server n'a pas de /health : il répond sur /v1/models dès qu'il écoute
                    using var health = await Http.GetAsync($"{LocalUrl}{(Config.IsSdCpp ? "/v1/models" : "/health")}", ct);
                    if (health.IsSuccessStatusCode) Dispatcher.UIThread.Post(MarkReady);
                    continue;
                }
                if (State != InstanceState.Ready) continue;
                if (Config.SleepMode) await SyncSleepStateAsync(ct);   // la veille peut venir du routeur ou d'un autre client

                var text = await Http.GetStringAsync($"{LocalUrl}/metrics", ct);
                var m = ParseMetrics(text);
                Dispatcher.UIThread.Post(() => ApplyMetrics(m));
            }
            catch (Exception) { /* serveur pas encore prêt ou occupé */ }
        }
    }

    private static Dictionary<string, double> ParseMetrics(string text)
    {
        var d = new Dictionary<string, double>();
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0 || raw[0] == '#') continue;
            var brace = raw.IndexOf('{');
            var space = raw.LastIndexOf(' ');
            if (space <= 0) continue;
            var name = brace > 0 ? raw[..brace] : raw[..space];
            if (!double.TryParse(raw[(space + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            if (double.IsNaN(v)) continue;
            d[name] = d.GetValueOrDefault(name) + v;
        }
        return d;
    }

    private void ApplyMetrics(Dictionary<string, double> m)
    {
        // Noms vLLM, sinon ceux de llama-server (--metrics)
        double Get(params string[] names) => names.Select(n => m.TryGetValue(n, out var v) ? v : (double?)null).FirstOrDefault(v => v != null) ?? 0;
        RequestsRunning = Get("vllm:num_requests_running", "llamacpp:requests_processing");
        RequestsWaiting = Get("vllm:num_requests_waiting", "llamacpp:requests_deferred");
        KvCachePercent  = 100 * Get("vllm:kv_cache_usage_perc", "vllm:gpu_cache_usage_perc", "llamacpp:kv_cache_usage_ratio");

        var prompt = Get("vllm:prompt_tokens_total", "llamacpp:prompt_tokens_total");
        var gen    = Get("vllm:generation_tokens_total", "llamacpp:tokens_predicted_total");
        var now    = DateTime.UtcNow;
        if (_prevCounters is { } p)
        {
            var dt = (now - p.At).TotalSeconds;
            if (dt > 0)
            {
                PromptTps     = Math.Max(0, (prompt - p.Prompt) / dt);
                GenerationTps = Math.Max(0, (gen - p.Gen) / dt);
            }
        }
        _prevCounters = (prompt, gen, now);
        TotalGenerated = gen;
        GenerationHistory.Add(GenerationTps);
        KvHistory.Add(KvCachePercent);
    }
}
