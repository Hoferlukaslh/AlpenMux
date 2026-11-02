/*
    Fichier      :  SetupViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Installation : liste des dépendances avec leur état et un bouton d'action,
        installation complète en un clic ("Tout installer"), journal des commandes,
        progression avec % et temps restant.
        Windows : deux étapes en plus, avant les autres : WSL2 (droits administrateur, redémarrage éventuel)
        et la distribution « AlpenMux » où tournent vLLM et ses dépendances.
*/

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> État d'une dépendance de la page Installation. </summary>
public enum DepState
{
    /// <summary> Vérification en cours. </summary>
    Checking,
    /// <summary> Présente. </summary>
    Ok,
    /// <summary> Absente (requise). </summary>
    Missing,
    /// <summary> Absente (facultative). </summary>
    Optional,
    /// <summary> Présente avec un problème. </summary>
    Warning
}

public sealed partial class DependencyItem : ObservableObject
{
    /// <summary> Clé de la dépendance. </summary>
    public required string Key { get; init; }
    /// <summary> Glyphe de l'icône. </summary>
    public required string Icon { get; init; }

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private DepState _state = DepState.Checking;
    [ObservableProperty] private string? _actionText;
    [ObservableProperty] private string? _secondaryActionText;

    /// <summary> Icône du bouton principal. </summary>
    public string ActionIcon { get; set; } = "\ue20c";
    /// <summary> Icône du bouton secondaire. </summary>
    public string SecondaryIcon { get; set; } = "\ue256";

    /// <summary> Action du bouton principal. </summary>
    public Func<Task>? Action { get; set; }
    /// <summary> Action du bouton secondaire. </summary>
    public Func<Task>? SecondaryAction { get; set; }

    /// <summary> La dépendance est présente. </summary>
    public bool IsOk => State == DepState.Ok;
    /// <summary> Icône de l'état. </summary>
    public string StateIcon => State switch
    {
        DepState.Ok => Icons.CheckCircle,
        DepState.Missing => Icons.XCircle,
        DepState.Warning => Icons.Warning,
        DepState.Optional => Icons.Info,
        _ => Icons.Hourglass,
    };
    /// <summary> Classe de style de l'état. </summary>
    public string StateClass => State switch
    {
        DepState.Ok => "ok",
        DepState.Missing => "error",
        DepState.Warning => "warn",
        _ => "muted",
    };
    /// <summary> Un bouton principal est affiché. </summary>
    public bool HasAction => ActionText != null;
    /// <summary> Un bouton secondaire est affiché. </summary>
    public bool HasSecondaryAction => SecondaryActionText != null;

    partial void OnStateChanged(DepState value)
    {
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(StateIcon));
        OnPropertyChanged(nameof(StateClass));
    }
    partial void OnActionTextChanged(string? value) => OnPropertyChanged(nameof(HasAction));
    partial void OnSecondaryActionTextChanged(string? value) => OnPropertyChanged(nameof(HasSecondaryAction));

    [RelayCommand]
    private async Task Run() { if (Action != null) await Action(); }

    [RelayCommand]
    private async Task RunSecondary() { if (SecondaryAction != null) await SecondaryAction(); }
}

public sealed partial class SetupViewModel : ViewModelBase
{
    /// <summary> Dépendances affichées. </summary>
    public ObservableCollection<DependencyItem> Items { get; } = new();
    /// <summary> Journal des installations. </summary>
    public ObservableCollection<string> Logs { get; } = new();
    /// <summary> Progression de l'installation en cours. </summary>
    public ProgressTracker Progress { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _showLogs;
    [ObservableProperty] private string _venvPath;
    [ObservableProperty] private string _manualCommand = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _allRequiredOk;

    /// <summary> Levé quand l'état des dépendances change (pastille du menu). </summary>
    public event Action? StatusChanged;

    private CancellationTokenSource? _cts;

    private readonly DependencyItem _driver   = new() { Key = "driver", Icon = "" };
    private readonly DependencyItem _packages = new() { Key = "packages", Icon = "" };
    private readonly DependencyItem _venv     = new() { Key = "venv", Icon = "" };
    private readonly DependencyItem _vllm     = new() { Key = "vllm", Icon = "" };
    private readonly DependencyItem _hf       = new() { Key = "hf", Icon = "" };
    private readonly DependencyItem _env      = new() { Key = "env", Icon = "\ue184", ActionIcon = "\ue5d4", SecondaryIcon = "\ue036" };
    private readonly DependencyItem _omni     = new() { Key = "omni", Icon = "" };
    private readonly DependencyItem _llama    = new() { Key = "llama", Icon = "\ue596", SecondaryIcon = "\ue036" };
    private readonly DependencyItem _sdcpp    = new() { Key = "sdcpp", Icon = "\ue2ca", SecondaryIcon = "\ue036" };
    private readonly DependencyItem _markitdown = new() { Key = "markitdown", Icon = "\ue23a" };
    private readonly DependencyItem _mermaid  = new() { Key = "mermaid", Icon = "\ue2ca" };
    private readonly DependencyItem _latex    = new() { Key = "latex", Icon = "\ue2ca" };
    private readonly DependencyItem _wsl      = new() { Key = "wsl", Icon = "\ue628" };
    private readonly DependencyItem _distro   = new() { Key = "distro", Icon = "\ue1de" };

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly IPythonEnvironment _python;
    private readonly IUiServices _ui;
    private readonly IDependencyService _dependencies;
    private readonly IEnvironmentChecker _environmentChecker;
    private readonly IHfService _hfService;
    private readonly ILlamaCppService _llamaCpp;
    private readonly IStableDiffusionCppService _sdCpp;
    private readonly IMermaidService _mermaidService;
    private readonly ILatexService _latexService;
    private readonly IMarkItDownService _markItDownService;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    /// <param name="appState"> État partagé entre les pages. </param>
    /// <param name="python"> Lecture du venv. </param>
    /// <param name="ui"> Services de l'interface (fichiers, presse-papiers, liens). </param>
    /// <param name="dependencies"> Installation des dépendances. </param>
    /// <param name="environmentChecker"> Vérification de l'environnement PyTorch. </param>
    /// <param name="hfService"> Commande hf et jeton. </param>
    /// <param name="llamaCpp"> Installation de llama.cpp. </param>
    /// <param name="sdCpp"> Installation de stable-diffusion.cpp. </param>
    /// <param name="mermaidService"> Paquet mermaidx. </param>
    /// <param name="latexService"> Paquet ziamath (formules LaTeX). </param>
    /// <param name="markItDownService"> Paquet MarkItDown. </param>
    public SetupViewModel(ISettingsService settings, IAppState appState, IPythonEnvironment python, IUiServices ui, IDependencyService dependencies, IEnvironmentChecker environmentChecker, IHfService hfService, ILlamaCppService llamaCpp, IStableDiffusionCppService sdCpp, IMermaidService mermaidService, ILatexService latexService, IMarkItDownService markItDownService)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(dependencies);
        ArgumentNullException.ThrowIfNull(environmentChecker);
        ArgumentNullException.ThrowIfNull(hfService);
        ArgumentNullException.ThrowIfNull(llamaCpp);
        ArgumentNullException.ThrowIfNull(sdCpp);
        ArgumentNullException.ThrowIfNull(mermaidService);
        ArgumentNullException.ThrowIfNull(markItDownService);
        _settings = settings;
        _appState = appState;
        _python = python;
        _ui = ui;
        _dependencies = dependencies;
        _environmentChecker = environmentChecker;
        _hfService = hfService;
        _llamaCpp = llamaCpp;
        _sdCpp = sdCpp;
        _mermaidService = mermaidService;
        _latexService = latexService;
        _markItDownService = markItDownService;
        _venvPath = _settings.Current.VenvPath;
        var items = LinuxHost.IsWsl
            ? new[] { _driver, _wsl, _distro, _packages, _venv, _vllm, _env, _hf, _omni, _markitdown, _mermaid, _latex, _llama, _sdcpp }
            : new[] { _driver, _packages, _venv, _vllm, _env, _hf, _omni, _markitdown, _mermaid, _latex, _llama, _sdcpp };
        foreach (var i in items) Items.Add(i);

        _wsl.Action    = () => GuardAsync(ct => WslService.InstallWslAsync(Progress, Log, ct));
        _distro.Action = () => GuardAsync(ct => WslService.InstallDistroAsync(Progress, Log, ct));

        _packages.Action = () => GuardAsync(InstallPackagesAsync);
        _venv.Action     = () => GuardAsync(CreateVenv);
        _venv.SecondaryAction = BrowseVenvAsync;
        _vllm.Action     = () => GuardAsync(ct => _dependencies.InstallVllmAsync(Progress, Log, ct, upgrade: _python.VllmInstalled));
        _hf.Action       = () => GuardAsync(InstallHfAsync);
        _omni.Action     = () => GuardAsync(ct => _dependencies.InstallOmniAsync(Progress, Log, ct));
        _markitdown.Action = () => GuardAsync(ct => _markItDownService.InstallAsync(Progress, Log, ct));
        _mermaid.Action = () => GuardAsync(ct => _mermaidService.InstallAsync(Progress, Log, ct));
        _latex.Action = () => GuardAsync(ct => _latexService.InstallAsync(Progress, Log, ct));
        _llama.Action    = () => GuardAsync(ct => _llamaCpp.InstallAsync(Progress, Log, ct));
        _llama.SecondaryAction = () => GuardAsync(ct => _llamaCpp.InstallAsync(Progress, Log, ct));
        // Après installation, les fichiers uniques d'images deviennent lançables : nouvelle analyse des modèles
        _sdcpp.Action = _sdcpp.SecondaryAction = () => GuardAsync(async ct =>
        {
            var ok = await _sdCpp.InstallAsync(Progress, Log, ct);
            if (ok) _ = _appState.RefreshModelsAsync();
            return ok;
        });
        _env.Action      = () => GuardAsync(RepairEnvironmentAsync);
        _env.SecondaryAction = () => GuardAsync(async ct => { await RunSmokeTestAsync(ct); return true; });
        _driver.Action   = () =>
        {
            _ui.Open(LinuxHost.IsWsl ? "https://www.nvidia.com/drivers/"
                : DependencyService.CurrentDistro == Distro.Debian
                ? "https://wiki.debian.org/NvidiaGraphicsDrivers" : "https://wiki.archlinux.org/title/NVIDIA");
            return Task.CompletedTask;
        };

        Loc.Instance.LanguageChanged += Refresh;
    }

    /// <inheritdoc />
    public override void OnNavigatedTo()
    {
        if (IsBusy) return;
        VenvPath = _settings.Current.VenvPath;
        Refresh();
    }

    private void Log(string line) => Dispatcher.UIThread.Post(() =>
    {
        Logs.Add(line);
        while (Logs.Count > 3000) Logs.RemoveAt(0);
    });

    /// <summary> Vérifie l'état de chaque dépendance. </summary>
    [RelayCommand]
    public void Refresh()
    {
        // Pilote / GPU
        _driver.Title = Loc.T("Dep_Driver");
        _driver.Description = Loc.T(LinuxHost.IsWsl ? "Dep_Driver_Desc_Win" : "Dep_Driver_Desc");
        _driver.State = DepState.Checking;
        _ = Task.Run(async () =>
        {
            var info = await _python.NvidiaDriverAsync();
            Dispatcher.UIThread.Post(() =>
            {
                _driver.State = info != null ? DepState.Ok : DepState.Warning;
                _driver.Detail = info != null ? Loc.F("Dep_Driver_Found", info) : Loc.T("Dep_Driver_Missing");
                _driver.ActionText = info != null ? null : LinuxHost.IsWsl ? Loc.T("Dep_Driver_Help_Win")
                    : Loc.F("Dep_Driver_Help", DependencyService.CurrentDistro == Distro.Debian ? "Debian" : "Arch");
            });
        });

        // Windows : WSL2 et la distribution AlpenMux (vLLM ne tourne que sous Linux)
        var wslOk = !LinuxHost.IsWsl || (WslService.WslReady && WslService.DistroInstalled);
        if (LinuxHost.IsWsl) RefreshWsl();

        // Paquets système
        var missing = _dependencies.MissingPackages();
        _packages.Title = Loc.F("Dep_Packages", LinuxHost.IsWsl ? $"apt · {LinuxHost.Distro}" : DependencyService.PackageManager);
        _packages.Description = string.Join(" · ", DependencyService.SystemPackages.Select(p => $"{p.Package} ({Loc.T(p.Purpose)})"));
        _packages.State = missing.Count == 0 ? DepState.Ok : DepState.Missing;
        _packages.Detail = missing.Count == 0 ? Loc.T("Dep_Packages_Ok") : Loc.F("Dep_Packages_Missing", string.Join(", ", missing.Select(m => m.Package)));
        _packages.ActionText = missing.Count == 0 ? null : Loc.T("Common_Install");
        ManualCommand = missing.Count == 0 ? "" : DependencyService.ManualInstallCommand(missing);
        if (!DependencyService.CanInstallPackages && missing.Count > 0)
            _packages.Detail += " — " + Loc.T("Dep_NotArch");
        _packages.ActionText = missing.Count == 0 || !DependencyService.CanInstallPackages || !wslOk ? null : Loc.T("Common_Install");

        // Environnement virtuel
        var venvOk = _python.VenvExists;
        _venv.Title = Loc.T("Dep_Venv");
        _venv.Description = Loc.F("Dep_Venv_Desc", _settings.Current.PythonVersion);
        _venv.State = venvOk ? DepState.Ok : DepState.Missing;
        _venv.Detail = venvOk ? _settings.VenvPath : Loc.F("Dep_Venv_Missing", _settings.VenvPath);
        _venv.ActionText = venvOk || !wslOk ? null : Loc.T("Dep_Venv_Create");
        // Windows : le venv est dans la distribution WSL (chemin Linux, modifiable dans les Paramètres)
        _venv.SecondaryActionText = LinuxHost.IsWsl ? null : Loc.T("Dep_Venv_Choose");

        // vLLM
        var vv = _python.VllmVersion;
        _vllm.Title = "vLLM";
        _vllm.Description = Loc.T("Dep_Vllm_Desc");
        _vllm.State = _python.VllmInstalled ? DepState.Ok : DepState.Missing;
        _vllm.Detail = _python.VllmInstalled ? Loc.F("Dep_Version", vv ?? "?") : Loc.T("Dep_Vllm_Missing");
        _vllm.ActionText = !venvOk ? null : _python.VllmInstalled ? Loc.T("Common_Update") : Loc.T("Common_Install");

        // hf CLI
        var hf = _hfService.HfExecutable();
        _hf.Title = Loc.T("Dep_Hf");
        _hf.Description = Loc.T("Dep_Hf_Desc");
        _hf.State = hf != null ? DepState.Ok : DepState.Missing;
        _hf.Detail = hf ?? Loc.T("Dep_Hf_Missing");
        _hf.ActionText = hf != null ? null : Loc.T("Common_Install");

        // vLLM-Omni (optionnel)
        var ov = _python.OmniVersion;
        _omni.Title = Loc.T("Dep_Omni");
        _omni.Description = Loc.T("Dep_Omni_Desc");
        _omni.State = ov != null ? DepState.Ok : DepState.Optional;
        _omni.Detail = ov != null ? Loc.F("Dep_Version", ov) : Loc.T("Dep_Optional");
        _omni.ActionText = ov == null && _python.VllmInstalled ? Loc.T("Common_Install") : null;

        // llama.cpp (optionnel) : modèles GGUF, lus directement (pas de copie)
        RefreshPrebuiltItem(_llama, "llama.cpp", "Dep_Llama_Desc", LlamaCppService.Installed, LlamaCppService.InstalledVersion,
            _llamaCpp.LatestAsync);

        // markitdown (optionnel) : documents joints aux discussions, convertis en Markdown
        var mv = _markItDownService.Version;
        _markitdown.Title = "markitdown";
        _markitdown.Description = Loc.T("Dep_MarkItDown_Desc");
        _markitdown.State = mv != null ? DepState.Ok : DepState.Optional;
        _markitdown.Detail = mv != null ? Loc.F("Dep_Version", mv) : Loc.T("Dep_Optional");
        _markitdown.ActionText = mv == null && _python.VenvExists ? Loc.T("Common_Install") : null;

        // mermaidx (optionnel) : diagrammes Mermaid des réponses du chat
        var mmv = _mermaidService.Version;
        _mermaid.Title = "mermaidx";
        _mermaid.Description = Loc.T("Dep_Mermaid_Desc");
        _mermaid.State = mmv != null ? DepState.Ok : DepState.Optional;
        _mermaid.Detail = mmv != null ? Loc.F("Dep_Version", mmv) : Loc.T("Dep_Optional");
        _mermaid.ActionText = mmv == null && _python.VenvExists ? Loc.T("Common_Install") : null;

        // ziamath (optionnel) : formules LaTeX des réponses du chat
        var lv = _latexService.Version;
        _latex.Title = "ziamath";
        _latex.Description = Loc.T("Dep_Latex_Desc");
        _latex.State = lv != null ? DepState.Ok : DepState.Optional;
        _latex.Detail = lv != null ? Loc.F("Dep_Version", lv) : Loc.T("Dep_Optional");
        _latex.ActionText = lv == null && _python.VenvExists ? Loc.T("Common_Install") : null;

        // stable-diffusion.cpp (optionnel) : images depuis les fichiers uniques (ComfyUI / Automatic1111), sans conversion
        RefreshPrebuiltItem(_sdcpp, "stable-diffusion.cpp", "Dep_SdCpp_Desc", StableDiffusionCppService.Installed,
            StableDiffusionCppService.InstalledVersion, _sdCpp.LatestAsync);

        // Cohérence de l'environnement (famille torch, test réel)
        UpdateEnvItem();

        AllRequiredOk = wslOk && missing.Count == 0 && venvOk && _python.VllmInstalled && hf != null && _env.State != DepState.Missing;
        Summary = AllRequiredOk ? Loc.T("Dep_Summary_Ok") : Loc.T("Dep_Summary_Missing");
        StatusChanged?.Invoke();
    }

    /// <summary> Windows : état de WSL2 et de la distribution AlpenMux. </summary>
    private void RefreshWsl()
    {
        var ready = WslService.WslReady;
        _wsl.Title = "WSL2";
        _wsl.Description = Loc.T("Wsl_Desc");
        _wsl.State = ready ? DepState.Ok : DepState.Missing;
        _wsl.Detail = ready ? Loc.F("Dep_Version", WslService.Version ?? "?")
                    : WslService.RebootRequired ? Loc.T("Wsl_Reboot") : Loc.T("Wsl_Missing");
        _wsl.ActionText = ready || WslService.RebootRequired ? null : Loc.T("Common_Install");

        var distro = WslService.DistroInstalled;
        _distro.Title = Loc.F("Wsl_Distro", LinuxHost.Distro);
        _distro.Description = Loc.T("Wsl_Distro_Desc");
        _distro.State = distro ? DepState.Ok : DepState.Missing;
        _distro.Detail = distro ? Loc.F("Wsl_Distro_Path", WslService.DistroDir) : Loc.T("Wsl_Distro_Missing");
        _distro.ActionText = distro || !ready ? null : Loc.T("Common_Install");
        if (distro)
            _ = Task.Run(() =>
            {
                // GPU vu depuis Linux : le pilote Windows le partage avec WSL
                var gpu = WslService.GpuInDistro();
                Dispatcher.UIThread.Post(() => _distro.Detail = Loc.F("Wsl_Distro_Path", WslService.DistroDir)
                    + " · " + (gpu != null ? Loc.F("Wsl_Gpu", gpu) : Loc.T("Wsl_NoGpu")));
            });
    }

    /// <summary>
    /// Ligne d'un moteur précompilé facultatif (llama.cpp, stable-diffusion.cpp) : état, version, bouton « Installer » ;
    /// s'il est installé, une nouvelle version publiée est cherchée en arrière-plan (bouton « Mettre à jour »).
    /// </summary>
    /// <param name="item"> Ligne de la page. </param>
    /// <param name="title"> Nom du moteur. </param>
    /// <param name="descriptionKey"> Clé de la description. </param>
    /// <param name="installed"> Le serveur est installé. </param>
    /// <param name="version"> Version installée. </param>
    /// <param name="latest"> Recherche de la dernière publication. </param>
    private static void RefreshPrebuiltItem(DependencyItem item, string title, string descriptionKey, bool installed, string? version,
        Func<CancellationToken, Task<BinaryRelease?>> latest)
    {
        item.Title = title;
        item.Description = Loc.T(descriptionKey);
        item.State = installed ? DepState.Ok : DepState.Optional;
        item.Detail = installed ? Loc.F("Dep_Version", version ?? "?") : Loc.T("Dep_Optional");
        item.ActionText = installed ? null : Loc.T("Common_Install");
        item.SecondaryActionText = null;
        if (!installed) return;
        _ = Task.Run(async () =>
        {
            BinaryRelease? release = null;
            try { release = await latest(CancellationToken.None); } catch { /* pas de réseau : version disponible inconnue */ }
            if (release == null || release.Tag == version) return;
            Dispatcher.UIThread.Post(() =>
            {
                item.Detail = Loc.F("Dep_Llama_Update", version ?? "?", release.Tag);
                item.SecondaryActionText = Loc.T("Common_Update");
            });
        });
    }

    /// <summary> Résultat du test réel en cours (pour ne pas en lancer deux). </summary>
    private bool _smokeRunning;

    private void UpdateEnvItem()
    {
        _env.Title = Loc.T("Env_Title");
        _env.Description = Loc.T("Env_Desc");
        _env.SecondaryActionText = _python.VllmInstalled ? Loc.T("Env_Test") : null;
        if (!_python.VllmInstalled)
        {
            _env.State = DepState.Optional;
            _env.Detail = Loc.T("Env_NeedsVllm");
            _env.ActionText = null;
            return;
        }

        var st = _environmentChecker.StaticCheck();
        var smoke = _environmentChecker.CachedSmoke();
        if (!st.Ok)
        {
            _env.State = DepState.Missing;
            _env.Detail = string.Join("\n", st.Problems);
            _env.ActionText = Loc.T("Env_Repair");
        }
        else if (smoke == null)
        {
            _env.State = DepState.Checking;
            _env.Detail = Loc.T("Env_Testing");
            _env.ActionText = null;
            if (!_smokeRunning && !IsBusy) _ = RunSmokeTestAsync(CancellationToken.None);   // test automatique en arrière-plan
        }
        else if (!smoke.Ok)
        {
            _env.State = DepState.Missing;
            _env.Detail = smoke.Error ?? "?";
            _env.ActionText = Loc.T("Env_Repair");
        }
        else
        {
            _env.State = DepState.Ok;
            _env.Detail = smoke.Summary ?? "";
            _env.ActionText = null;
        }
    }

    private async Task RunSmokeTestAsync(CancellationToken ct)
    {
        if (_smokeRunning) return;
        _smokeRunning = true;
        _env.State = DepState.Checking;
        _env.Detail = Loc.T("Env_Testing");
        try { await _environmentChecker.SmokeTestAsync(Log, ct); }
        catch (Exception ex) { Log("✘ " + ex.Message); }
        finally
        {
            _smokeRunning = false;
            Dispatcher.UIThread.Post(Refresh);
        }
    }

    private async Task<bool> RepairEnvironmentAsync(CancellationToken ct)
    {
        if (!await _environmentChecker.RepairAsync(Progress, Log, ct)) return false;
        var problem = await _environmentChecker.VerifyAsync(Log, ct);
        if (problem != null) { Log("⚠ " + problem); return false; }
        Progress.Finish(Loc.T("Env_Repaired"));
        return true;
    }

    private async Task GuardAsync(Func<CancellationToken, Task<bool>> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        ShowLogs = true;
        _cts = new CancellationTokenSource();
        try
        {
            if (!await action(_cts.Token)) Log("✘ " + Loc.T("Dep_StepFailed"));
        }
        catch (OperationCanceledException) { Log(Loc.T("Common_Cancelled")); Progress.Finish(Loc.T("Common_Cancelled")); }
        catch (Exception ex) { Log("✘ " + ex.Message); Progress.Finish(ex.Message); }
        finally
        {
            IsBusy = false;
            _cts = null;
            WslService.Invalidate();
            Refresh();
            // Les capacités de vLLM (formats, architectures) ont pu changer : nouvelle analyse des modèles
            _ = _appState.RefreshModelsAsync();
        }
    }

    private Task<bool> InstallPackagesAsync(CancellationToken ct) =>
        _dependencies.InstallSystemPackagesAsync(_dependencies.MissingPackages(), Progress, Log, ct);

    private Task<bool> CreateVenv(CancellationToken ct) => _dependencies.CreateVenvAsync(Progress, Log, ct);

    private async Task<bool> InstallHfAsync(CancellationToken ct)
    {
        // Dans le venv si possible (rien à demander), sinon paquet système (Arch seulement : pas de paquet Debian)
        if (_python.VenvExists)
            return await _dependencies.SafeInstallAsync(["huggingface_hub[cli,hf_xet]"], Loc.T("Dep_InstallingHf"), false, Progress, Log, ct);
        var pkg = DependencyService.SystemPackages.Where(p => p.Executable == "hf").ToList();
        if (pkg.Count == 0) { Log("✘ " + Loc.T("Dep_Hf_NeedVenv")); return false; }
        return await _dependencies.InstallSystemPackagesAsync(pkg, Progress, Log, ct);
    }

    /// <summary> Installe tout ce qui manque, dans l'ordre. </summary>
    [RelayCommand]
    private Task InstallAll() => GuardAsync(async ct =>
    {
        if (LinuxHost.IsWsl)
        {
            if (!WslService.WslReady && !await WslService.InstallWslAsync(Progress, Log, ct)) return false;
            if (!WslService.DistroInstalled && !await WslService.InstallDistroAsync(Progress, Log, ct)) return false;
            WslService.Invalidate();
        }
        var missing = _dependencies.MissingPackages();
        if (missing.Count > 0 && DependencyService.CanInstallPackages && !await InstallPackagesAsync(ct)) return false;
        if (!_python.VenvExists && !await CreateVenv(ct)) return false;
        if (!_python.VllmInstalled &&
            !await _dependencies.InstallVllmAsync(Progress, Log, ct)) return false;
        // hf CLI : dans le venv (seule possibilité sur Debian, pas de paquet)
        if (_hfService.HfExecutable() == null && !await InstallHfAsync(ct)) return false;
        Log("✔ " + Loc.T("Dep_Summary_Ok"));
        return true;
    });

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private Task CopyManualCommand() => _ui.CopyAsync(ManualCommand);

    [RelayCommand]
    private Task CopyLogs() => _ui.CopyAsync(string.Join('\n', Logs));

    private async Task BrowseVenvAsync()
    {
        var path = await _ui.PickFolderAsync(Loc.T("Dep_Venv_Choose"), Path.GetDirectoryName(_settings.VenvPath));
        if (path == null) return;
        VenvPath = path;
        ApplyVenv();
    }

    [RelayCommand]
    private void ApplyVenv()
    {
        _settings.Current.VenvPath = LinuxHost.ToLinux(VenvPath.Trim());
        _settings.Save();
        if (!_python.VenvExists)
        {
            // L'utilisateur a peut-être choisi le dossier parent (ex. ~/vllm-env au lieu de ~/vllm-env/.venv)
            var nested = LinuxHost.Combine(_settings.VenvPath, ".venv");
            if (LinuxHost.FileExists(LinuxHost.Combine(nested, "bin", "python")))
            {
                VenvPath = nested;
                _settings.Current.VenvPath = nested;
                _settings.Save();
            }
        }
        Refresh();
    }
}
