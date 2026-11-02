/*
    Fichier      :  ModelsViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Modèles :
            - onglet "Mes modèles"    : téléchargements et conversions en cours (une carte et une barre chacun ; pause,
                                        reprise, annulation ; repris au lancement suivant) puis modèles détectés :
                                        estimation VRAM, ouvrir / supprimer / lancer, convertir un fichier unique ComfyUI
            - onglet "Télécharger"    : recherche Hugging Face, liste des fichiers, choix des variantes GGUF (lancées avec llama.cpp),
                                        taille à télécharger et place libre, avertissement de format,
                                        hf download --local-dir (plusieurs téléchargements possibles en parallèle)
            - onglet "Hugging Face"   : token (facultatif, nécessaire pour les modèles protégés)
        Le dossier des modèles est modifiable en haut de la page.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class ModelItemViewModel : ViewModelBase
{
    /// <summary> Modèle affiché. </summary>
    public ModelInfo Model { get; }

    [ObservableProperty] private bool _isConfirmingDelete;

    private readonly IAppState _appState;
    private readonly IUiServices _ui;
    private readonly Action<ModelInfo> _convert;

    /// <summary> Crée la ligne d'un modèle et calcule s'il tient en VRAM. </summary>
    /// <param name="model"> Modèle détecté. </param>
    /// <param name="gpus"> GPU actuels (VRAM libre). </param>
    /// <param name="contextCap"> Plafond du contexte automatique (réglages). </param>
    /// <param name="appState"> Demande de lancement. </param>
    /// <param name="ui"> Ouverture du dossier. </param>
    /// <param name="convert"> Lance la conversion d'un fichier unique (page Modèles). </param>
    public ModelItemViewModel(ModelInfo model, IReadOnlyList<GpuSample> gpus, int contextCap, IAppState appState, IUiServices ui,
        Action<ModelInfo> convert)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(convert);
        _appState = appState;
        _ui = ui;
        _convert = convert;
        Model = model;
        var fit = ModelFit.Describe(model, gpus, contextCap);
        VramText = fit.Budget == null
            ? Format.Bytes(model.BaseBytes)
            : fit.TensorParallel > 1 ? $"{Format.Bytes(fit.Plan.EstimatedBytes)} × {fit.TensorParallel} GPU"
            : $"{Format.Bytes(fit.Plan.EstimatedBytes)}  ({fit.Plan.Ratio * 100:0} %)";
        (FitText, FitClass, FitIcon) = (fit.Text, fit.Class, fit.Icon);
    }

    /// <summary> Nom affiché. </summary>
    public string Name => Model.Repo;
    /// <summary> Taille sur le disque affichée. </summary>
    public string SizeText => Format.Bytes(Model.DiskBytes);
    /// <summary> VRAM estimée affichée. </summary>
    public string VramText { get; }
    /// <summary> Verdict « tient / ne tient pas » affiché. </summary>
    public string FitText { get; }
    /// <summary> Classe de style du verdict. </summary>
    public string FitClass { get; }
    /// <summary> Icône du verdict. </summary>
    public string FitIcon { get; }
    /// <summary> Disposition affichée (dossier, cache, GGUF). </summary>
    public string LayoutText => Model.Layout switch
    {
        "cache" => Loc.T("Model_LayoutCache"),
        "gguf" => "GGUF",
        _ => Loc.T("Model_LayoutFolder"),
    };
    /// <summary> Moteur utilisé affiché. </summary>
    public string ModeText => Model.IsGguf ? "llama.cpp" : Model.SdCppLaunchable ? "stable-diffusion.cpp"
        : Model.HasSdCppAlternative ? "vLLM-Omni · stable-diffusion.cpp" : Model.IsOmni ? "vLLM-Omni" : "vLLM";
    /// <summary> Bouton « Lancer » : caché pour un fichier unique qui ne peut qu'être converti (FLUX, Z-Image). </summary>
    public bool ShowLaunch => !Model.CanConvert || Model.SdCppLaunchable;
    /// <summary> Conversion en avant (bouton principal) quand le fichier ne peut pas être lancé tel quel. </summary>
    public bool ConvertIsPrimary => Model.CanConvert && Model.IsBroken;
    /// <summary> Problème du modèle. </summary>
    public string ProblemText => Model.Problem ?? "";
    /// <summary> Avertissement du modèle. </summary>
    public string WarningText => Model.Warning ?? "";
    /// <summary> Dépôt d'origine affiché. </summary>
    public string SourceText => Model.SourceRepo is { } r ? $"huggingface.co/{r}" : "";

    [RelayCommand]
    private void Launch() => _appState.RequestLaunch(Model);

    /// <summary> Fichier unique (ComfyUI…) : conversion au format diffusers (la page Modèles lance la tâche). </summary>
    [RelayCommand]
    private void Convert() => _convert(Model);

    [RelayCommand]
    private void OpenFolder() => _ui.Open(Model.Root);

    [RelayCommand]
    private void AskDelete() => IsConfirmingDelete = true;

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDelete()
    {
        IsConfirmingDelete = false;
        try
        {
            await Task.Run(() =>
            {
                if (Model.IsGguf)
                {
                    // Variante GGUF : seulement ses fichiers, le dossier est supprimé s'il ne contient plus de modèle
                    foreach (var f in GgufParts(Model.Target)) File.Delete(f);
                    if (!Directory.EnumerateFiles(Model.Root, "*.gguf").Any(f => !ModelScanner.IsAuxiliaryGguf(f)) &&
                        !File.Exists(Path.Combine(Model.Root, "config.json")))
                        Directory.Delete(Model.Root, true);
                }
                else if (Model.Layout == "cache")
                    Directory.Delete(Path.GetFullPath(Path.Combine(Model.Root, "..", "..")), true);   // models--org--nom complet
                else
                    Directory.Delete(Model.Root, true);
            });
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Delete] {ex.Message}"); }
        await _appState.RefreshModelsAsync();
    }

    /// <summary> Fichier GGUF et ses éventuelles autres parties (nom-00001-of-00003.gguf…). </summary>
    private static IEnumerable<string> GgufParts(string first)
    {
        var m = Regex.Match(Path.GetFileName(first), @"^(.*)-\d{5}-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);
        if (!m.Success) return [first];
        return Directory.GetFiles(Path.GetDirectoryName(first)!, $"{m.Groups[1].Value}-*-of-{m.Groups[2].Value}.gguf");
    }
}

/// <summary> Fichier d'un dépôt dans la liste des fichiers. </summary>
/// <param name="file"> Fichier. </param>
/// <param name="owner"> Page Modèles (ajout aux motifs inclus). </param>
public sealed partial class HfFileViewModel(HfFile file, ModelsViewModel owner) : ViewModelBase
{
    /// <summary> Chemin dans le dépôt. </summary>
    public string Path => file.Path;
    /// <summary> Taille (octets). </summary>
    public long Bytes => file.Size;
    /// <summary> Taille affichée. </summary>
    public string SizeText => Format.Bytes(file.Size);

    [RelayCommand]
    private void Include() => owner.AddInclude(file.Path);
}

/// <summary> Une variante GGUF d'un dépôt (un fichier, ou plusieurs parties d'un même fichier). </summary>
public sealed partial class GgufVariantViewModel : ObservableObject
{
    /// <summary> Quantification (Q4_K_M…). </summary>
    public required string Quant { get; init; }
    /// <summary> Nom du fichier (première partie). </summary>
    public required string FileName { get; init; }
    /// <summary> Motif de téléchargement de la variante. </summary>
    public required string Pattern { get; init; }
    /// <summary> Taille totale (octets). </summary>
    public required long Bytes { get; init; }
    /// <summary> Nombre de parties. </summary>
    public int Parts { get; init; } = 1;
    /// <summary> Taille affichée. </summary>
    public string SizeText => Format.Bytes(Bytes);
    /// <summary> Nombre de parties affiché. </summary>
    public string PartsText => Parts > 1 ? Loc.F("Hf_GgufParts", Parts) : "";
    [ObservableProperty] private bool _isSelected;
}

/// <summary> État d'un téléchargement ou d'une conversion. </summary>
public enum DownloadState
{
    /// <summary> En cours. </summary>
    Running,
    /// <summary> Mis en pause par l'utilisateur. </summary>
    Paused,
    /// <summary> Terminé. </summary>
    Done,
    /// <summary> Échoué. </summary>
    Failed,
    /// <summary> Annulé. </summary>
    Cancelled
}

/// <summary>
/// Un téléchargement (ou une conversion) : sa propre barre de progression, son journal, annulable et relançable.
/// Pause : hf download est arrêté, les fichiers reçus restent ; « Reprendre » relance hf, qui repart de là.
/// </summary>
public sealed partial class DownloadJob : ObservableObject
{
    private readonly IHfService _hfService;
    private readonly IUiServices _ui;

    /// <summary> Crée la tâche (pas encore démarrée). </summary>
    /// <param name="hfService"> Téléchargement avec hf. </param>
    /// <param name="ui"> Ouverture du dossier de destination. </param>
    public DownloadJob(IHfService hfService, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(hfService);
        ArgumentNullException.ThrowIfNull(ui);
        _hfService = hfService;
        _ui = ui;
    }

    /// <summary> Dépôt téléchargé (ou fichier converti). </summary>
    public required string Repo { get; init; }
    /// <summary> Dossier de destination. </summary>
    public required string LocalDir { get; init; }
    /// <summary> Motifs inclus. </summary>
    public string? Include { get; init; }
    /// <summary> Motifs exclus. </summary>
    public string? Exclude { get; init; }
    /// <summary> Révision. </summary>
    public string? Revision { get; init; }
    /// <summary> Variante choisie. </summary>
    public string? Variant { get; init; }

    /// <summary> Autre travail que le téléchargement (conversion d'un fichier unique), avec la même carte de progression. </summary>
    public Func<ProgressTracker, Action<string>, CancellationToken, Task<bool>>? Work { get; init; }
    /// <summary> Tâche de conversion (et non de téléchargement). </summary>
    public bool IsConversion => Work != null;

    /// <summary> Titre affiché. </summary>
    public string Title => Work != null ? Loc.F("Conv_JobTitle", Repo) : Variant is { Length: > 0 } v ? $"{Repo} · {v}" : Repo;

    /// <summary> Téléchargement à mémoriser pour le reprendre au démarrage. </summary>
    public PendingDownload Pending => new()
    {
        Repo = Repo, LocalDir = LocalDir, Include = Include, Exclude = Exclude, Revision = Revision, Variant = Variant,
        Paused = IsPaused,
    };
    /// <summary> Progression. </summary>
    public ProgressTracker Progress { get; } = new();
    /// <summary> Journal de la tâche. </summary>
    public ObservableCollection<string> Logs { get; } = new();

    [ObservableProperty] private DownloadState _state = DownloadState.Running;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _showLogs;

    /// <summary> La tâche est en cours. </summary>
    public bool IsRunning => State == DownloadState.Running;

    /// <summary> Ce téléchargement concerne-t-il ce modèle ? (variante GGUF : seulement les fichiers demandés) </summary>
    public bool Covers(ModelInfo m)
    {
        var root = Path.GetFullPath(m.Root);
        var dir = Path.GetFullPath(LocalDir);
        if (!root.StartsWith(dir)) return false;
        if (m.IsGguf && Variant != null) return HfService.GlobFilter(Include)(Path.GetFileName(m.Target));
        return true;
    }
    /// <summary> La tâche est terminée (réussie ou non). </summary>
    public bool IsFinished => State is not (DownloadState.Running or DownloadState.Paused);
    /// <summary> La tâche a échoué ou a été annulée. </summary>
    public bool IsFailed => State is DownloadState.Failed or DownloadState.Cancelled;
    /// <summary> La tâche est en pause. </summary>
    public bool IsPaused => State == DownloadState.Paused;
    /// <summary> Barre affichée en cours et en pause (elle garde sa position). </summary>
    public bool ShowProgress => IsRunning || IsPaused;
    /// <summary> Pause possible pour un téléchargement (une conversion ne se reprend pas). </summary>
    public bool CanPause => IsRunning && !IsConversion;

    private CancellationTokenSource? _cts;
    private bool _pausing;
    /// <summary> Appelé quand l'utilisateur retire la tâche de la liste. </summary>
    public Action<DownloadJob>? Dismissed { get; set; }
    /// <summary> Appelé quand l'utilisateur relance la tâche. </summary>
    public Action<DownloadJob>? Retried { get; set; }
    /// <summary> Appelé quand l'utilisateur reprend la tâche en pause. </summary>
    public Action<DownloadJob>? Resumed { get; set; }

    partial void OnStateChanged(DownloadState value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsPaused));
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(CanPause));
    }

    /// <summary> Téléchargement repris en pause au lancement de l'application (pas de processus). </summary>
    public void MarkPaused()
    {
        State = DownloadState.Paused;
        StatusText = Loc.T("Hf_Paused");
    }

    /// <summary> Exécute la tâche jusqu'à sa fin, son annulation ou sa mise en pause. </summary>
    public async Task RunAsync()
    {
        State = DownloadState.Running;
        StatusText = "";
        _pausing = false;
        _cts = new CancellationTokenSource();
        try
        {
            void Log(string l) => Dispatcher.UIThread.Post(() =>
            {
                Logs.Add(l);
                while (Logs.Count > 500) Logs.RemoveAt(0);
            });
            var ok = Work != null
                ? await Work(Progress, Log, _cts.Token)
                : await _hfService.DownloadAsync(Repo, LocalDir, Include, Exclude, Revision, Progress, Log, _cts.Token);
            State = ok ? DownloadState.Done : DownloadState.Failed;
            StatusText = ok ? Loc.F("Hf_SavedTo", LocalDir) : Loc.T(Work != null ? "Conv_Failed" : "Hf_Failed");
            if (!ok) ShowLogs = true;
        }
        catch (OperationCanceledException) when (_pausing)
        {
            // hf download arrêté, fichiers partiels gardés : la reprise repart de là
            State = DownloadState.Paused;
            StatusText = Loc.T("Hf_Paused");
            Progress.SetDetail(StatusText);
        }
        catch (OperationCanceledException)
        {
            State = DownloadState.Cancelled;
            StatusText = Loc.T("Hf_Cancelled");
            Progress.Finish(StatusText);
        }
        catch (Exception ex)
        {
            State = DownloadState.Failed;
            StatusText = "✘ " + ex.Message;
            Progress.Finish(ex.Message);
        }
        finally { _cts = null; }
    }

    [RelayCommand]
    private void Cancel()
    {
        // En pause, aucun processus : l'annulation retire simplement la carte
        if (IsPaused) { State = DownloadState.Cancelled; StatusText = Loc.T("Hf_Cancelled"); Progress.Finish(StatusText); Dismissed?.Invoke(this); return; }
        _cts?.Cancel();
    }

    [RelayCommand]
    private void Pause()
    {
        if (!CanPause) return;
        _pausing = true;
        _cts?.Cancel();
    }

    [RelayCommand]
    private void Resume() => Resumed?.Invoke(this);

    [RelayCommand]
    private void Retry() => Retried?.Invoke(this);

    [RelayCommand]
    private void Dismiss() => Dismissed?.Invoke(this);

    [RelayCommand]
    private void ToggleLogs() => ShowLogs = !ShowLogs;

    [RelayCommand]
    private void OpenFolder()
    {
        if (Directory.Exists(LocalDir)) _ui.Open(LocalDir);
    }
}

public sealed partial class ModelsViewModel : ViewModelBase
{
    /// <summary> Modèles installés. </summary>
    public ObservableCollection<ModelItemViewModel> Items { get; } = new();
    /// <summary> Téléchargements et conversions. </summary>
    public ObservableCollection<DownloadJob> Downloads { get; } = new();

    /// <summary> Nombre de téléchargements en cours a changé (badge du menu). </summary>
    public event Action? DownloadsChanged;
    /// <summary> Nombre de téléchargements en cours. </summary>
    public int RunningDownloads => Downloads.Count(d => d.IsRunning);
    /// <summary> Résultats de la recherche. </summary>
    public ObservableCollection<HfSearchResult> SearchResults { get; } = new();
    /// <summary> Fichiers du dépôt choisi. </summary>
    public ObservableCollection<HfFileViewModel> RepoFiles { get; } = new();
    /// <summary> Variantes GGUF du dépôt choisi. </summary>
    public ObservableCollection<GgufVariantViewModel> GgufVariants { get; } = new();

    [ObservableProperty] private string _modelsDir;
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private bool _hasDownloads;
    [ObservableProperty] private string _localTabTitle = Loc.T("Models_Tab_Local");

    // Téléchargement
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string _searchStatus = "";
    [ObservableProperty] private HfSearchResult? _selectedResult;
    [ObservableProperty] private string _repoId = "";
    [ObservableProperty] private string _include = "";
    [ObservableProperty] private bool _skipUselessFiles = true;
    [ObservableProperty] private string _revision = "";
    [ObservableProperty] private string _targetFolder = "";
    [ObservableProperty] private string _repoFilesStatus = "";
    [ObservableProperty] private string _downloadStatus = "";
    [ObservableProperty] private bool _hasGgufVariants;
    [ObservableProperty] private bool _ggufNeedsLlama;
    [ObservableProperty] private string _formatWarning = "";
    [ObservableProperty] private string _formatWarningClass = "error";
    [ObservableProperty] private bool _isLoadingFiles;
    [ObservableProperty] private string _downloadSizeText = "";
    [ObservableProperty] private string _downloadSizeClass = "muted";
    /// <summary> Verdict « tiendra en VRAM ? » avant téléchargement (estimation sur la taille des fichiers). </summary>
    [ObservableProperty] private string _downloadFitText = "";
    [ObservableProperty] private string _downloadFitClass = "muted";
    [ObservableProperty] private string _downloadFitIcon = Icons.Info;
    [ObservableProperty] private bool _hasDownloadFit;

    // Token
    [ObservableProperty] private string _token;
    [ObservableProperty] private bool _showToken;
    [ObservableProperty] private string _tokenStatus = "";
    [ObservableProperty] private string _tokenStatusClass = "muted";

    private string? _filesLoadedFor;     // dépôt dont la liste de fichiers est affichée
    private bool _updatingVariants;

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly ISystemMonitorService _monitor;
    private readonly IUiServices _ui;
    private readonly IDownloadStore _downloads;
    private readonly IHfService _hfService;
    private readonly ISingleFileConverter _converter;
    private readonly ITrayService _tray;
    private readonly IModelCompat _compat;

    // config.json du dépôt en cours (lu avant téléchargement) ; ignoré si le dépôt ou la révision a changé
    private RemoteConfig? _remote;
    private string? _remoteKey;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    /// <param name="appState"> État partagé entre les pages. </param>
    /// <param name="monitor"> Mesures du système. </param>
    /// <param name="ui"> Services de l'interface (fichiers, presse-papiers, liens). </param>
    /// <param name="downloads"> Téléchargements mémorisés. </param>
    /// <param name="hfService"> Accès à Hugging Face. </param>
    /// <param name="converter"> Conversion des modèles « fichier unique ». </param>
    /// <param name="tray"> Arrêt des téléchargements avant de quitter. </param>
    /// <param name="compat"> Compatibilité vLLM (aussi avant téléchargement, d'après le config.json du dépôt). </param>
    public ModelsViewModel(ISettingsService settings, IAppState appState, ISystemMonitorService monitor, IUiServices ui, IDownloadStore downloads, IHfService hfService, ISingleFileConverter converter, ITrayService tray, IModelCompat compat)
    {
        ArgumentNullException.ThrowIfNull(compat);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(hfService);
        ArgumentNullException.ThrowIfNull(converter);
        ArgumentNullException.ThrowIfNull(tray);
        _settings = settings;
        _appState = appState;
        _monitor = monitor;
        _ui = ui;
        _downloads = downloads;
        _hfService = hfService;
        _converter = converter;
        _tray = tray;
        _compat = compat;
        _modelsDir = _settings.Current.ModelsDir;
        _token = _settings.Current.HfToken ?? "";
        _appState.ModelsChanged += Rebuild;
        Loc.Instance.LanguageChanged += Rebuild;
        Downloads.CollectionChanged += (_, _) => Rebuild();
        _tray.BeforeQuit += StopDownloadsForQuitAsync;
        // Téléchargements interrompus par la fermeture de l'application : repris là où ils s'étaient arrêtés
        // (sauf ceux mis en pause, qui restent en pause)
        foreach (var d in _downloads.Load())
            Dispatcher.UIThread.Post(() => StartJob(new DownloadJob(_hfService, _ui)
            {
                Repo = d.Repo, LocalDir = d.LocalDir, Include = d.Include, Exclude = d.Exclude,
                Revision = d.Revision, Variant = d.Variant,
            }, paused: d.Paused));
        TokenStatus = string.IsNullOrWhiteSpace(Token) ? Loc.T("Hf_NoToken") : Loc.T("Hf_TokenSaved");
    }

    /// <inheritdoc />
    public override void OnNavigatedTo()
    {
        ModelsDir = _settings.Current.ModelsDir;
        Rebuild();
    }

    private void Rebuild()
    {
        var gpus = _monitor.Latest?.Gpus ?? [];
        // Les modèles en cours de téléchargement sont représentés par leur carte de téléchargement
        var downloading = Downloads.Where(d => d.IsRunning || d.IsPaused).ToList();
        Items.Clear();
        foreach (var m in _appState.Models)
        {
            if (downloading.Any(d => d.Covers(m))) continue;
            if (string.IsNullOrWhiteSpace(Filter) || m.Repo.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase))
                Items.Add(new ModelItemViewModel(m, gpus, ArgsPlanner.ContextCapOf(_settings.Current), _appState, _ui, StartConversion));
        }
        HasDownloads = Downloads.Count > 0;
        IsEmpty = _appState.Models.Count == 0 && Downloads.Count == 0;
        var running = Downloads.Count(d => d.IsRunning);
        DownloadsChanged?.Invoke();
        LocalTabTitle = running > 0 ? $"{Loc.T("Models_Tab_Local")} ({running} ↓)" : Loc.T("Models_Tab_Local");
        UpdateDisk();
    }

    partial void OnFilterChanged(string value) => Rebuild();

    private void UpdateDisk()
    {
        try
        {
            var dir = _settings.ModelsDir;
            var probe = Directory.Exists(dir) ? dir : SettingsService.Home;
            var drive = new DriveInfo(probe);
            var total = _appState.Models.Sum(m => m.DiskBytes);
            DiskText = Loc.F("Models_Disk", _appState.Models.Count, Format.Bytes(total),
                Format.Bytes(drive.AvailableFreeSpace), Format.Bytes(drive.TotalSize));
        }
        catch { DiskText = ""; }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsScanning = true;
        await _appState.RefreshModelsAsync();
        IsScanning = false;
    }

    [RelayCommand]
    private async Task BrowseModelsDir()
    {
        var path = await _ui.PickFolderAsync(Loc.T("Models_ChooseDir"), _settings.ModelsDir);
        if (path == null) return;
        ModelsDir = path;
        await ApplyModelsDirAsync();
    }

    [RelayCommand]
    private async Task ApplyModelsDirAsync()
    {
        _settings.Current.ModelsDir = ModelsDir.Trim();
        _settings.Save();
        try { Directory.CreateDirectory(_settings.ModelsDir); } catch { /* dossier impossible à créer : signalé plus tard à l'utilisation */ }
        await RefreshAsync();
    }

    [RelayCommand]
    private void OpenModelsDir()
    {
        Directory.CreateDirectory(_settings.ModelsDir);
        _ui.Open(_settings.ModelsDir);
    }

    [RelayCommand]
    private void GoToDownload() => SelectedTab = 1;

    // ---------------------------------------------------------------- Recherche

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;
        IsSearching = true;
        SearchStatus = "";
        SearchResults.Clear();
        try
        {
            foreach (var r in await _hfService.SearchAsync(SearchQuery.Trim())) SearchResults.Add(r);
            SearchStatus = SearchResults.Count == 0 ? Loc.T("Hf_NoResult") : Loc.F("Hf_ResultCount", SearchResults.Count);
        }
        catch (Exception ex) { SearchStatus = "✘ " + ex.Message; }
        finally { IsSearching = false; }
    }

    partial void OnSelectedResultChanged(HfSearchResult? value)
    {
        if (value == null) return;
        RepoId = value.Id;
        _ = LoadFilesAsync();
    }

    partial void OnRepoIdChanged(string value)
    {
        TargetFolder = value.Trim().Split('/').LastOrDefault() ?? "";
        if (_filesLoadedFor != value.Trim())
        {
            // Nouveau dépôt : les filtres du précédent ne s'appliquent plus
            Include = "";
            Revision = "";
            DownloadStatus = "";
            RepoFiles.Clear();
            GgufVariants.Clear();
            HasGgufVariants = false;
            GgufNeedsLlama = false;
            _remote = null;
            FormatWarning = "";
            RepoFilesStatus = "";
            DownloadSizeText = "";
            _filesLoadedFor = null;
            _ = ScheduleFileListingAsync(value.Trim());
        }
    }

    private CancellationTokenSource? _listingDelay;

    /// <summary> Dépôt tapé à la main : liste ses fichiers (et donc la taille à télécharger) dès que la saisie s'arrête. </summary>
    /// <summary> Liste les fichiers du dépôt 800 ms après la dernière frappe (LoadFiles gère ses propres erreurs). </summary>
    private async Task ScheduleFileListingAsync(string repo)
    {
        _listingDelay?.Cancel();
        if (!Regex.IsMatch(repo, @"^[\w.\-]+/[\w.\-]+$")) return;
        var cts = _listingDelay = new CancellationTokenSource();
        try { await Task.Delay(800, cts.Token); } catch (OperationCanceledException) { return; }
        if (RepoId.Trim() == repo && _filesLoadedFor != repo && !IsLoadingFiles) await LoadFilesAsync();
    }

    [RelayCommand]
    private async Task LoadFilesAsync()
    {
        var repo = RepoId.Trim();
        RepoFiles.Clear();
        GgufVariants.Clear();
        HasGgufVariants = false;
        _filesLoadedFor = null;
        _remote = null;
        if (repo.Length == 0) return;
        RepoFilesStatus = Loc.T("Hf_ListingFiles");
        IsLoadingFiles = true;
        try
        {
            var files = await _hfService.ListFilesAsync(repo, Revision);
            foreach (var f in files.OrderByDescending(f => f.Size)) RepoFiles.Add(new HfFileViewModel(f, this));
            _filesLoadedFor = repo;
            FormatWarning = DetectRepoFormat(repo, files);
            FormatWarningClass = FormatWarning.StartsWith('ℹ') ? "warn" : "error";
            FormatWarning = FormatWarning.TrimStart('ℹ');
            BuildGgufVariants(files);
            UpdateSelectionSize();
            _ = LoadRemoteConfigAsync(repo, Revision, files);
        }
        catch (Exception ex) { RepoFilesStatus = "✘ " + ex.Message; }
        finally { IsLoadingFiles = false; }
    }

    /// <summary>
    /// Lit le config.json du dépôt avant téléchargement : architecture (cache KV, contexte) et compatibilité vLLM.
    /// Sans effet pour les GGUF et les dépôts sans config.json (l'estimation par taille reste affichée).
    /// </summary>
    private async Task LoadRemoteConfigAsync(string repo, string? revision, List<HfFile> files)
    {
        if (!RemotePreview.CanPreview(files)) return;
        var key = RemoteKey(repo, revision);
        try
        {
            var cfg = await _hfService.GetConfigAsync(repo, revision);
            if (cfg is not { } c || RemoteKey(RepoId.Trim(), Revision) != key) return;
            _remote = RemotePreview.Prepare(_compat, c, files);
            _remoteKey = key;
            // Le bandeau de format (devinette sur le nom du dépôt) fait doublon avec le verdict quand celui-ci
            // signale un problème bloquant : le verdict, plus précis, le remplace. Les informations (ℹ) restent.
            if (_remote.Compat.Problem != null && FormatWarningClass == "error") FormatWarning = "";
            UpdateSelectionSize();
        }
        catch (Exception)
        {
            // Config illisible (réseau, JSON) : l'estimation par taille reste affichée, sans bloquer la page
        }
    }

    private static string RemoteKey(string repo, string? revision) =>
        $"{repo}@{(string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim())}";

    /// <summary> Formats que vLLM ne charge pas, repérés avant le téléchargement (nom du dépôt, fichiers). </summary>
    private static string DetectRepoFormat(string repo, List<HfFile> files)
    {
        bool Has(string ext) => files.Any(f => f.Path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
        var name = repo.ToLowerInvariant();
        if (Regex.IsMatch(name, @"(^|[-_/.])mlx([-_/.]|$)") || name.StartsWith("mlx-community/") || Has(".npz"))
            return Loc.T("Compat_Mlx");
        if (Regex.IsMatch(name, @"exl[23]") || files.Any(f => f.Path.EndsWith("measurement.json")))
            return Loc.F("Compat_OtherEngine", "ExLlama (EXL2/EXL3)");
        if (Has(".onnx") && !Has(".safetensors") && !Has(".bin") && !Has(".gguf"))
            return Loc.F("Compat_OtherEngine", "ONNX");
        // Fichier unique pour ComfyUI / Automatic1111 (Comfy-Org/…, *.ckpt) : ni config.json ni model_index.json.
        // Modèle d'image connu (FLUX, SDXL, Z-Image) : convertible après le téléchargement
        if (!files.Any(f => f.Path is "config.json" or "model_index.json" || f.Path.EndsWith("/config.json")) && !Has(".gguf") && (Has(".safetensors") || Has(".ckpt")))
            return Regex.IsMatch(name + " " + string.Join(' ', files.Select(f => f.Path.ToLowerInvariant())), @"flux|sdxl|sd_xl|xl[-_]base|z[-_]?image")
                ? "ℹ" + Loc.T("Compat_SingleFileConvertible")
                : Loc.T("Compat_SingleFile");
        return "";
    }

    /// <summary> Dépôt GGUF : une variante par quantification (Q4_K_M, Q8_0…), à choisir avant le téléchargement. </summary>
    private void BuildGgufVariants(List<HfFile> files)
    {
        var split = new Regex(@"^(.*)-\d{5}-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);
        var groups = files
            .Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && !ModelScanner.IsAuxiliaryGguf(f.Path))
            .GroupBy(f => split.Match(f.Path) is { Success: true } m ? $"{m.Groups[1].Value}-*-of-{m.Groups[2].Value}.gguf" : f.Path)
            .OrderBy(g => g.Sum(f => f.Size))
            .ToList();

        _updatingVariants = true;
        var labels = ModelScanner.VariantLabels(groups.Select(g => g.First().Path).ToList());
        foreach (var g in groups)
        {
            var v = new GgufVariantViewModel
            {
                Quant = labels[g.First().Path],
                FileName = g.Count() > 1 ? g.Key : g.First().Path,
                Pattern = g.Key,
                Bytes = g.Sum(f => f.Size),
                Parts = g.Count(),
            };
            v.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(GgufVariantViewModel.IsSelected)) OnVariantSelectionChanged(); };
            GgufVariants.Add(v);
        }
        // Proposition par défaut : Q4_K_M (bon compromis), sinon la plus petite
        var def = GgufVariants.FirstOrDefault(v => v.Quant == "Q4_K_M") ?? GgufVariants.FirstOrDefault();
        if (def != null) def.IsSelected = true;
        _updatingVariants = false;
        HasGgufVariants = GgufVariants.Count > 1;
        GgufNeedsLlama = GgufVariants.Count > 0 && !LlamaCppService.Installed;
        if (GgufVariants.Count > 0) OnVariantSelectionChanged();
    }

    private void OnVariantSelectionChanged()
    {
        if (_updatingVariants) return;
        var sel = GgufVariants.Where(v => v.IsSelected).Select(v => v.Pattern).ToList();
        Include = string.Join(' ', sel);   // llama.cpp n'a besoin que du fichier GGUF (tokenizer et modèle de conversation inclus)
    }

    /// <summary> Fichiers ignorés : inutiles à vLLM, et poids en double de ce dépôt. </summary>
    private string ExcludePatterns()
    {
        var dup = HfService.DuplicateWeights(RepoFiles.Select(f => new HfFile(f.Path, f.Bytes)));
        return string.Join(' ', new[] { HfService.UselessFilesPatterns }.Concat(dup));
    }

    private void UpdateSelectionSize()
    {
        var filter = HfService.GlobFilter(Include);
        var useless = HfService.GlobFilter(ExcludePatterns());
        var sel = RepoFiles.Where(f => filter(f.Path) && !(SkipUselessFiles && useless(f.Path))).ToList();
        RepoFilesStatus = Loc.F("Hf_FilesSummary", RepoFiles.Count, sel.Count, Format.Bytes(sel.Sum(f => f.Bytes)));
        UpdateDownloadSize(sel);
        UpdateDownloadFit(sel);
    }

    /// <summary>
    /// Verdict VRAM pour les fichiers choisis, d'après les GPU actuels. Si le config.json du dépôt est lu
    /// (architecture, compatibilité vLLM), c'est le même calcul que sur la carte du modèle ; sinon l'estimation par taille (DownloadFit).
    /// </summary>
    private void UpdateDownloadFit(List<HfFileViewModel> sel)
    {
        DownloadFitText = "";
        HasDownloadFit = false;
        if (sel.Count == 0) return;
        HasDownloadFit = true;
        var gpus = _monitor.Latest?.Gpus ?? [];
        var repo = RepoId.Trim();
        if (_remote is { } rc && _remoteKey == RemoteKey(repo, Revision))
        {
            var model = RemotePreview.Model(repo, rc, sel.Sum(f => f.Bytes));
            var fit = ModelFit.Describe(model, gpus, ArgsPlanner.ContextCapOf(_settings.Current));
            var text = fit.Text;
            if (fit.Plan.Fits && fit.Budget != null && !model.IsBroken)
                text = Loc.F("Hf_Fit_Remote", text, Format.Bytes(fit.Plan.EstimatedBytes), fit.Plan.MaxModelLen.ToString("N0"));
            var cls = fit.Class;
            if (model.Warning is { } warning)
            {
                text += " " + warning;
                if (cls == "ok") cls = "warn";
            }
            (DownloadFitText, DownloadFitClass, DownloadFitIcon) = (text, cls, fit.Icon);
            return;
        }
        var verdict = DownloadFit.Evaluate(sel.Sum(f => f.Bytes), gpus);
        if (verdict == null)
        {
            (DownloadFitText, DownloadFitClass, DownloadFitIcon) = (Loc.T("Hf_Fit_NoGpu"), "muted", Icons.Info);
            return;
        }
        var need = Format.Bytes(verdict.NeededBytes);
        var free = Format.Bytes(verdict.FreeBytes);
        var total = Format.Bytes(verdict.TotalBytes);
        (DownloadFitText, DownloadFitClass, DownloadFitIcon) = verdict.Level switch
        {
            "ok" => (Loc.F("Hf_Fit_Ok", need, free), "ok", Icons.CheckCircle),
            "warn" => (Loc.F("Hf_Fit_Tight", need, free, total), "warn", Icons.Warning),
            _ => (Loc.F("Hf_Fit_No", need, total), "error", Icons.XCircle),
        };
    }

    /// <summary> Taille à télécharger (sans les fichiers déjà présents dans le dossier de destination) et place libre. </summary>
    private void UpdateDownloadSize(List<HfFileViewModel> sel)
    {
        DownloadSizeText = "";
        DownloadSizeClass = "muted";
        if (sel.Count == 0) return;
        try
        {
            var dir = _settings.ModelsDir;
            var folder = string.IsNullOrWhiteSpace(TargetFolder) ? RepoId.Trim().Split('/').Last() : TargetFolder.Trim();
            var localDir = Path.Combine(dir, folder);
            long needed = 0, present = 0;
            foreach (var f in sel)
            {
                var local = Path.Combine(localDir, f.Path);
                if (File.Exists(local) && new FileInfo(local).Length == f.Bytes) present += f.Bytes;
                else needed += f.Bytes;
            }
            var free = new DriveInfo(Directory.Exists(dir) ? dir : SettingsService.Home).AvailableFreeSpace;
            DownloadSizeText = Loc.F("Hf_DownloadSize", Format.Bytes(needed), Format.Bytes(free))
                               + (present > 0 ? " " + Loc.F("Hf_DownloadPresent", Format.Bytes(present)) : "");
            // Marge de 1 Gio : fichiers temporaires, métadonnées
            if (needed + (1L << 30) > free) { DownloadSizeText += " " + Loc.T("Hf_DownloadNoSpace"); DownloadSizeClass = "error"; }
        }
        catch { /* espace disque inconnu : pas d'avertissement */ }
    }

    partial void OnTargetFolderChanged(string value)
    {
        if (RepoFiles.Count > 0) UpdateSelectionSize();
    }

    /// <summary> Ajoute un fichier aux motifs inclus. </summary>
    public void AddInclude(string path)
    {
        var parts = HfService.SplitPatterns(Include).ToList();
        if (!parts.Contains(path)) parts.Add(path);
        Include = string.Join(' ', parts);
    }

    partial void OnIncludeChanged(string value)
    {
        if (RepoFiles.Count > 0) UpdateSelectionSize();
    }

    partial void OnSkipUselessFilesChanged(bool value)
    {
        if (RepoFiles.Count > 0) UpdateSelectionSize();
    }

    // ---------------------------------------------------------------- Téléchargement

    [RelayCommand]
    private async Task Download()
    {
        DownloadStatus = "";
        var repo = RepoId.Trim();
        if (repo.Length == 0) return;

        // La liste des fichiers permet de détecter un dépôt à plusieurs variantes GGUF
        if (_filesLoadedFor != repo) await LoadFilesAsync();
        if (HasGgufVariants && !GgufVariants.Any(v => v.IsSelected))
        {
            DownloadStatus = Loc.F("Hf_ChooseVariant", GgufVariants.Count);
            return;
        }

        var folder = string.IsNullOrWhiteSpace(TargetFolder) ? repo.Split('/').Last() : TargetFolder.Trim();
        var localDir = Path.Combine(_settings.ModelsDir, folder);
        var variant = HasGgufVariants ? string.Join(", ", GgufVariants.Where(v => v.IsSelected).Select(v => v.Quant)) : null;

        if (Downloads.Any(d => d.IsRunning && d.LocalDir == localDir && d.Include == Include))
        {
            DownloadStatus = Loc.T("Hf_AlreadyDownloading");
            return;
        }

        var job = new DownloadJob(_hfService, _ui)
        {
            Repo = repo, LocalDir = localDir, Include = Include, Revision = Revision, Variant = variant,
            Exclude = SkipUselessFiles ? ExcludePatterns() : null,
        };
        StartJob(job);
        DownloadStatus = Loc.T("Hf_Started");
        SelectedTab = 0;   // la progression est affichée dans "Mes modèles"
    }

    private void StartJob(DownloadJob job, bool paused = false)
    {
        job.Dismissed = j =>
        {
            Downloads.Remove(j);
            if (!j.IsConversion) _downloads.Remove(j.Pending);
        };
        job.Resumed = j =>
        {
            _downloads.Add(j.Pending with { Paused = false });
            _ = RunJobAsync(j);
        };
        job.Retried = j =>
        {
            Downloads.Remove(j);
            StartJob(new DownloadJob(_hfService, _ui)
            {
                Repo = j.Repo, LocalDir = j.LocalDir, Include = j.Include, Exclude = j.Exclude,
                Revision = j.Revision, Variant = j.Variant, Work = j.Work,
            });
        };
        job.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadJob.State)) Rebuild(); };
        Downloads.Insert(0, job);
        if (paused) { job.MarkPaused(); _downloads.Add(job.Pending); return; }
        if (!job.IsConversion) _downloads.Add(job.Pending);
        _ = RunJobAsync(job);
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        await job.RunAsync();
        // Fermeture de l'application : le téléchargement reste mémorisé pour reprendre au prochain lancement.
        // En pause : mémorisé comme tel (il ne repart pas tout seul).
        if (job.IsPaused) _downloads.Add(job.Pending);
        else if (!_tray.IsQuitting && !job.IsConversion) _downloads.Remove(job.Pending);
        await _appState.RefreshModelsAsync();
        // Réussi : la carte du modèle remplace la carte de téléchargement
        if (job.State == DownloadState.Done) Downloads.Remove(job);
    }

    private void StartConversion(ModelInfo m)
    {
        if (m.SingleFile is not { } file || Downloads.Any(d => d.IsRunning && d.LocalDir == m.Root)) return;
        StartJob(new DownloadJob(_hfService, _ui)
        {
            Repo = m.Repo, LocalDir = m.Root,
            Work = (progress, log, ct) => _converter.ConvertAsync(file, progress, log, ct),
        });
    }

    /// <summary> Fermeture : arrête les `hf download` en cours (ils reprendront au prochain lancement). </summary>
    private async Task StopDownloadsForQuitAsync()
    {
        foreach (var job in Downloads.Where(d => d.IsRunning && !d.IsConversion).ToList())
            await _hfService.KillOrphansAsync(job.LocalDir);
    }

    // ---------------------------------------------------------------- Token

    [RelayCommand]
    private void ToggleShowToken() => ShowToken = !ShowToken;

    [RelayCommand]
    private async Task SaveToken()
    {
        var t = Token.Trim();
        _settings.Current.HfToken = t.Length == 0 ? null : t;
        _settings.Save();
        if (t.Length == 0)
        {
            TokenStatus = Loc.T("Hf_TokenRemoved");
            TokenStatusClass = "muted";
            return;
        }
        await VerifyTokenAsync();
    }

    [RelayCommand]
    private async Task VerifyTokenAsync()
    {
        var t = Token.Trim();
        if (t.Length == 0) return;
        TokenStatus = Loc.T("Hf_TokenChecking");
        TokenStatusClass = "muted";
        try
        {
            var name = await _hfService.WhoAmIAsync(t);
            TokenStatus = Loc.F("Hf_TokenValid", name);
            TokenStatusClass = "ok";
        }
        catch (Exception ex)
        {
            TokenStatus = "✘ " + ex.Message;
            TokenStatusClass = "error";
        }
    }

    [RelayCommand]
    private void OpenTokenPage() => _ui.Open("https://huggingface.co/settings/tokens");

    [RelayCommand]
    private void OpenRepoPage()
    {
        if (!string.IsNullOrWhiteSpace(RepoId)) _ui.Open($"https://huggingface.co/{RepoId.Trim()}");
    }
}
