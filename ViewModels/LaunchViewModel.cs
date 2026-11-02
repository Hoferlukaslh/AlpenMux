/*
    Fichier      :  LaunchViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Serveurs :
            - "Nouveau serveur" : choix du modèle, des GPU et des paramètres vLLM.
              Les valeurs par défaut sont calculées automatiquement (ArgsPlanner) ;
              les valeurs modifiées sont mémorisées par modèle (profil).
            - favoris : configurations complètes enregistrées, à recharger ou lancer directement
            - un élément par serveur (en cours ou passé) : état, progression, métriques,
              configuration, console. L'historique est conservé jusqu'à sa suppression.
        Plusieurs serveurs peuvent tourner en même temps (ports différents, part de VRAM, GPU différents).
        Multi-GPU : les GPU sont choisis automatiquement (le moins possible), le tensor parallel suit.
        Moteur selon le modèle : vLLM / vLLM-Omni, llama.cpp (GGUF), stable-diffusion.cpp (fichier unique
        d'images complet : SD 1.x / 2.x, SDXL) ; les réglages de vLLM sont masqués pour les moteurs natifs.
        Fonctions de vLLM proposées selon le modèle : parsers de raisonnement et d'appels d'outils (détectés
        d'après le modèle de conversation), mode texte seul (multimodal), mise en veille, adaptateurs LoRA,
        décodage spéculatif MTP, recette officielle vLLM. Les options inconnues du vLLM installé sont refusées
        avant le lancement.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Media;
using AlpenMux.Controls;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class GpuChoice : ObservableObject
{
    /// <summary> Index du GPU. </summary>
    public int Index { get; init; }
    /// <summary> Nom du GPU. </summary>
    public string Name { get; init; } = "";
    /// <summary> Fabricant du GPU. </summary>
    public string Vendor { get; init; } = "NVIDIA";
    [ObservableProperty] private string _memory = "";
    [ObservableProperty] private bool _isSelected = true;
    /// <summary> Libellé affiché. </summary>
    public string Label => $"GPU {Index} · {Name}";
}

/// <summary> Adaptateur LoRA proposé pour le modèle choisi. </summary>
public sealed partial class LoraChoice : ObservableObject
{
    /// <summary> Adaptateur. </summary>
    public required LoraAdapterInfo Adapter { get; init; }
    /// <summary> Libellé affiché (nom et rang). </summary>
    public string Label => $"{Adapter.Name}  (r = {Adapter.Rank})";
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class LaunchViewModel : ViewModelBase
{
    /// <summary> Choix « aucun parser » dans les listes. </summary>
    public static string NoParser => Loc.T("Launch_ParserNone");
    /// <summary> Modèles installés. </summary>
    public ObservableCollection<ModelInfo> Models => _appState.Models;
    /// <summary> Serveurs lancés. </summary>
    public ObservableCollection<InstanceViewModel> Instances => _appState.Instances;
    /// <summary> Favoris. </summary>
    public ObservableCollection<FavoriteConfig> Favorites => _appState.Favorites;
    /// <summary> GPU proposés. </summary>
    public ObservableCollection<GpuChoice> Gpus { get; } = new();

    /// <summary> Types de cache KV proposés (vLLM). </summary>
    public string[] KvCacheDtypes { get; } = ["auto", "fp8", "fp8_e4m3", "fp8_e5m2"];
    /// <summary> Types de cache KV proposés (llama.cpp). </summary>
    public string[] LlamaKvTypes { get; } = ["f16", "q8_0", "q4_0"];
    [ObservableProperty] private string _llamaKvType = "f16";
    [ObservableProperty] private string _llamaGpuLayers = "";
    /// <summary> Serveur d'embedding (RAG) : llama-server --embedding, listé dans la page Connaissances. </summary>
    [ObservableProperty] private bool _llamaEmbedding;
    partial void OnLlamaKvTypeChanged(string value) => UpdatePreview();
    partial void OnLlamaEmbeddingChanged(bool value) => UpdatePreview();
    partial void OnLlamaGpuLayersChanged(string value)
    {
        // -ngl saisi à la main : le curseur suit
        if (!_syncingNgl && !_loading && SelectedModel?.NumLayers is { } layers and > 0)
        {
            _loading = true;
            GpuPercent = int.TryParse(value.Trim(), out var ngl) ? Math.Clamp(Math.Round(100.0 * ngl / layers), 0, 100) : 100;
            _loading = false;
            OnPropertyChanged(nameof(GpuPercentText));
        }
        UpdatePreview();
    }
    /// <summary> Types des poids proposés. </summary>
    public string[] Dtypes { get; } = ["auto", "bfloat16", "float16", "float32"];

    [ObservableProperty] private ModelInfo? _selectedModel;
    [ObservableProperty] private InstanceViewModel? _selectedInstance;
    [ObservableProperty] private bool _isNewServerView = true;

    // Paramètres
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _servedName = "";
    [ObservableProperty] private double _gpuMemoryUtilization = 0.9;
    [ObservableProperty] private string _maxModelLen = "";
    [ObservableProperty] private string _kvCacheDtype = "auto";
    [ObservableProperty] private bool _enforceEager;
    [ObservableProperty] private string _tensorParallel = "1";
    [ObservableProperty] private string _pipelineParallel = "1";
    [ObservableProperty] private string _maxNumSeqs = "";
    [ObservableProperty] private string _dtype = "auto";
    [ObservableProperty] private bool _trustRemoteCode;
    [ObservableProperty] private bool _prefixCaching = true;
    [ObservableProperty] private string _apiKey = "";
    [ObservableProperty] private bool _omni;
    [ObservableProperty] private string _tokenizer = "";
    [ObservableProperty] private string _hfConfigPath = "";
    [ObservableProperty] private string _extraArgs = "";
    [ObservableProperty] private double _vramShare = 100;
    [ObservableProperty] private bool _showAdvanced;

    // Fonctions de vLLM selon le modèle
    /// <summary> Parsers de raisonnement proposés. </summary>
    public ObservableCollection<string> ReasoningParsers { get; } = new();
    /// <summary> Parsers d'appels d'outils proposés. </summary>
    public ObservableCollection<string> ToolParsers { get; } = new();
    /// <summary> Adaptateurs LoRA compatibles. </summary>
    public ObservableCollection<LoraChoice> Loras { get; } = new();
    [ObservableProperty] private string _reasoningParser = "";
    [ObservableProperty] private string _toolCallParser = "";
    [ObservableProperty] private string _parserHint = "";
    [ObservableProperty] private bool _textOnly;
    [ObservableProperty] private bool _sleepMode;
    [ObservableProperty] private bool _mtpEnabled;
    [ObservableProperty] private bool _cpuOffload;
    [ObservableProperty] private string _mtpTokens = "1";
    private ParserChoice _detected = new(null, null);
    partial void OnReasoningParserChanged(string value) => UpdatePreview();
    partial void OnToolCallParserChanged(string value) => UpdatePreview();
    partial void OnTextOnlyChanged(bool value) { OnPropertyChanged(nameof(TextOnlyHelp)); Recalculate(); }
    partial void OnSleepModeChanged(bool value) => UpdatePreview();
    partial void OnCpuOffloadChanged(bool value)
    {
        if (Omni && !_loading && SelectedModel is { } m)
            ApplyOmniText(m, ArgsPlanner.Budget(_monitor.Latest?.Gpus ?? [], GpuIds));
        UpdatePreview();
    }
    partial void OnMtpEnabledChanged(bool value) => UpdatePreview();
    partial void OnMtpTokensChanged(string value) => UpdatePreview();

    /// <summary> L'option « texte seul » est proposée. </summary>
    public bool ShowTextOnly => SelectedModel is { IsMultimodal: true, IsGguf: false } && !Omni;
    /// <summary> L'option « encodeurs en RAM » est proposée. </summary>
    public bool ShowCpuOffload => SelectedModel is { TextEncoderBytes: > 0 } && Omni && !IsSdCpp;
    /// <summary> Aide de l'option « encodeurs en RAM ». </summary>
    public string CpuOffloadHelp => SelectedModel is { TextEncoderBytes: > 0 } m ? Loc.F("Launch_CpuOffload_Help", Format.Bytes(m.TextEncoderBytes)) : "";
    /// <summary> L'option MTP est proposée. </summary>
    public bool ShowMtp => SelectedModel is { MtpLayers: > 0, IsGguf: false } && !Omni;
    /// <summary> Les adaptateurs LoRA sont proposés. </summary>
    public bool ShowLoras => Loras.Count > 0 && !IsNativeEngine;
    /// <summary> Les parsers sont proposés. </summary>
    public bool ShowParsers => !IsNativeEngine && !Omni;
    /// <summary> Aide de l'option « texte seul ». </summary>
    public string TextOnlyHelp => SelectedModel is { MultimodalBytes: > 0 } m
        ? Loc.F("Launch_TextOnly_HelpBytes", Format.Bytes(m.MultimodalBytes)) : Loc.T("Launch_TextOnly_Help");

    // Recette officielle vLLM (vllm-project/recipes)
    [ObservableProperty] private string _recipeTitle = "";
    [ObservableProperty] private string _recipeUrl = "";
    [ObservableProperty] private string _recipeArgs = "";
    [ObservableProperty] private string _recipeSummary = "";
    /// <summary> Résultat du dernier « Appliquer » : ce qui a changé, ou le fait que tout était déjà en place. </summary>
    [ObservableProperty] private string _recipeStatus = "";
    private VllmRecipe? _recipe;
    /// <summary> Une recette officielle existe pour ce modèle. </summary>
    public bool HasRecipe => RecipeUrl.Length > 0;
    /// <summary> La recette ajoute des arguments. </summary>
    public bool HasRecipeArgs => RecipeSummary.Length > 0;
    partial void OnRecipeSummaryChanged(string value) => OnPropertyChanged(nameof(HasRecipeArgs));
    partial void OnRecipeUrlChanged(string value) => OnPropertyChanged(nameof(HasRecipe));
    /// <summary> Vrai quand le résultat du dernier « Appliquer » est à afficher. </summary>
    public bool HasRecipeStatus => RecipeStatus.Length > 0;
    partial void OnRecipeStatusChanged(string value) => OnPropertyChanged(nameof(HasRecipeStatus));


    // Résultat de l'estimation
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _planClass = "muted";
    [ObservableProperty] private string _planIcon = Icons.Info;
    [ObservableProperty] private string _estimateText = "";
    [ObservableProperty] private string _gpuText = "";
    [ObservableProperty] private string _commandPreview = "";
    [ObservableProperty] private string _launchError = "";
    [ObservableProperty] private bool _hasProfile;

    // Favoris
    [ObservableProperty] private string _favoriteName = "";
    [ObservableProperty] private string _favoriteStatus = "";

    /// <summary> Au moins un modèle est installé. </summary>
    public bool HasModels => Models.Count > 0;
    /// <summary> Au moins un favori. </summary>
    public bool HasFavorites => Favorites.Count > 0;
    /// <summary> Le lancement est possible. </summary>
    public bool CanLaunch => SelectedModel is { IsBroken: false } &&
        (IsLlama ? LlamaCppService.Installed : IsSdCpp ? StableDiffusionCppService.Installed && SdComponentsReady : _python.VllmInstalled);
    /// <summary> vLLM est requis mais pas installé. </summary>
    public bool VllmMissing => !IsNativeEngine && !_python.VllmInstalled;

    /// <summary> Fichier unique d'images complet : lancé avec stable-diffusion.cpp (sd-server). </summary>
    public bool IsSdCpp => SelectedModel is { } sm && (sm.SdCppRunnable || sm.SdCppWithComponents || sm.HasSdCppAlternative && UseSdCpp);

    /// <summary> Transformer seul (FLUX.2, Z-Image) : le VAE et l'encodeur de texte sont à fournir. </summary>
    public bool SdComponentsNeeded => IsSdCpp && SelectedModel?.SdCppWithComponents == true;

    /// <summary> Composants saisis (ou téléchargés) et présents sur le disque. </summary>
    public bool SdComponentsReady => !SdComponentsNeeded || File.Exists(SdVae) && File.Exists(SdLlm);

    /// <summary> Le modèle est FLUX.2 klein 4B : les composants peuvent être téléchargés automatiquement. </summary>
    public bool SdAutoAvailable => SdComponentsNeeded && !SdBusy && _sdComponents.CanDownload(SelectedModel?.SdCppFile);

    /// <summary> Chemin du VAE (--vae), saisi à la main ou rempli par le téléchargement. </summary>
    [ObservableProperty] private string _sdVae = "";
    /// <summary> Chemin de l'encodeur de texte Qwen3 (--llm). </summary>
    [ObservableProperty] private string _sdLlm = "";
    /// <summary> Téléchargement des composants en cours. </summary>
    [ObservableProperty] private bool _sdBusy;
    /// <summary> Suivi du téléchargement des composants, affiché sur la page Serveurs. </summary>
    public ProgressTracker SdProgress { get; } = new();

    partial void OnSdVaeChanged(string value) => ComponentsChanged();
    partial void OnSdLlmChanged(string value) => ComponentsChanged();
    partial void OnSdBusyChanged(bool value) => ComponentsChanged();

    private void ComponentsChanged()
    {
        OnPropertyChanged(nameof(SdComponentsReady));
        OnPropertyChanged(nameof(SdAutoAvailable));
        OnPropertyChanged(nameof(CanLaunch));
        UpdatePreview();
    }

    /// <summary> Choisit le VAE avec le sélecteur de fichiers. </summary>
    [RelayCommand]
    private async Task PickSdVaeAsync()
    {
        var files = await _ui.PickFilesAsync(Loc.T("Launch_SdPickVae"), Loc.T("Launch_SdModelFiles"), "*.safetensors", "*.gguf");
        if (files.Count > 0) SdVae = files[0];
    }

    /// <summary> Choisit l'encodeur de texte avec le sélecteur de fichiers. </summary>
    [RelayCommand]
    private async Task PickSdLlmAsync()
    {
        var files = await _ui.PickFilesAsync(Loc.T("Launch_SdPickLlm"), Loc.T("Launch_SdModelFiles"), "*.safetensors", "*.gguf");
        if (files.Count > 0) SdLlm = files[0];
    }

    /// <summary> Télécharge le VAE et l'encodeur de texte de FLUX.2 klein 4B (dépôt Comfy-Org) et les renseigne. </summary>
    [RelayCommand]
    private async Task DownloadSdComponentsAsync()
    {
        if (!SdAutoAvailable) return;
        SdBusy = true;
        try
        {
            // Pas d'annulation depuis l'interface : le téléchargement va jusqu'au bout ou échoue avec un message
            var files = await _sdComponents.DownloadAsync(SdProgress, _ => { }, CancellationToken.None);
            if (files != null) { SdVae = files.Vae; SdLlm = files.Llm; }
        }
        finally { SdBusy = false; }
    }

    /// <summary> Modèle converti avec son fichier d'origine : moteur au choix (vLLM-Omni ou stable-diffusion.cpp). </summary>
    public bool ShowEngineChoice => SelectedModel?.HasSdCppAlternative == true;
    [ObservableProperty] private bool _useSdCpp;
    partial void OnUseSdCppChanged(bool value)
    {
        foreach (var n in new[] { nameof(IsSdCpp), nameof(SdCppMissing), nameof(IsNativeEngine), nameof(ShowVllmAdvanced), nameof(ShowParsers),
                                  nameof(ShowLoras), nameof(ShowVllmMemory), nameof(ShowMemorySliders), nameof(IsDiffusers), nameof(ShowCpuOffload),
                                  nameof(IsOmniMode), nameof(CanLaunch) })
            OnPropertyChanged(n);
        UpdatePreview();
    }
    /// <summary> stable-diffusion.cpp est requis mais pas installé. </summary>
    public bool SdCppMissing => IsSdCpp && !StableDiffusionCppService.Installed;
    /// <summary> Moteur en binaire natif : les réglages de vLLM sont masqués. </summary>
    public bool IsNativeEngine => IsLlama || IsSdCpp;
    [ObservableProperty] private bool _sdOffload;

    /// <summary>
    /// Part du modèle sur le GPU (%), le reste en RAM, comme dans LM Studio :
    /// llama.cpp → nombre de couches sur le GPU (-ngl) ; vLLM → Gio de poids gardés en RAM (--cpu-offload-gb).
    /// Plus lent, mais permet de lancer un modèle plus gros que la VRAM, ou de garder plus de place pour le contexte.
    /// </summary>
    [ObservableProperty] private double _gpuPercent = 100;
    /// <summary> La part du modèle sur le GPU est réglable. </summary>
    public bool ShowGpuPercent => IsLlama || !IsNativeEngine && !Omni;
    /// <summary> vLLM : fraction des poids sur le GPU (1 = tout). </summary>
    private double VllmGpuFraction => IsNativeEngine || Omni ? 1.0 : Math.Clamp(GpuPercent / 100.0, 0.05, 1.0);
    /// <summary> Part du modèle sur le GPU affichée. </summary>
    public string GpuPercentText
    {
        get
        {
            if (SelectedModel is not { } m) return $"{GpuPercent:0} %";
            if (IsLlama)
                return m.NumLayers is { } layers ? $"{GpuPercent:0} % · " + Loc.F("Launch_GpuPercent_Layers", LayersForPercent(layers), layers) : $"{GpuPercent:0} %";
            var ram = ArgsPlanner.LoadedWeights(m, TextOnly && ShowTextOnly) * (1 - VllmGpuFraction);
            return ram > 0 ? $"{GpuPercent:0} % · " + Loc.F("Launch_GpuPercent_Ram", Format.Bytes(ram)) : $"{GpuPercent:0} %";
        }
    }
    private int LayersForPercent(int layers) => (int)Math.Round(layers * GpuPercent / 100.0);

    partial void OnGpuPercentChanged(double value)
    {
        OnPropertyChanged(nameof(GpuPercentText));
        if (_loading) return;
        if (IsLlama && SelectedModel?.NumLayers is { } layers)
        {
            // -ngl suit le curseur (vide = toutes les couches)
            _syncingNgl = true;
            LlamaGpuLayers = value >= 100 ? "" : LayersForPercent(layers).ToString();
            _syncingNgl = false;
        }
        else Recalculate();   // vLLM : moins de poids sur le GPU = plus de place pour le cache KV
    }
    private bool _syncingNgl;
    partial void OnSdOffloadChanged(bool value) => UpdatePreview();

    /// <summary> Fichier GGUF : lancé avec llama.cpp (réglages propres, ceux de vLLM masqués). </summary>
    public bool IsLlama => SelectedModel?.IsGguf == true;
    /// <summary> llama.cpp est requis mais pas installé. </summary>
    public bool LlamaMissing => IsLlama && !LlamaCppService.Installed;
    /// <summary> Les réglages de mémoire de vLLM sont affichés. </summary>
    public bool ShowVllmMemory => !IsNativeEngine && !Omni;
    /// <summary> Pipeline d'image (diffusers) : vLLM-Omni le charge en entier, sans réserve de VRAM réglable. </summary>
    public bool IsDiffusers => Omni && !IsSdCpp && SelectedModel?.ModelType == "diffusers";
    /// <summary> Curseurs « part de la VRAM libre » et --gpu-memory-utilization : sans effet sur un pipeline d'image. </summary>
    public bool ShowMemorySliders => !IsNativeEngine && !IsDiffusers;
    /// <summary> Les réglages avancés de vLLM sont affichés. </summary>
    public bool ShowVllmAdvanced => HasModels && !IsNativeEngine;
    /// <summary> L'option vLLM-Omni est proposée. </summary>
    public bool ShowOmniOption => SelectedModel?.IsOmni == true;
    /// <summary> Lancement avec vLLM-Omni. </summary>
    public bool IsOmniMode => Omni && !IsSdCpp;
    [ObservableProperty] private string _omniStagesText = "";
    private List<OmniStage> _omniStages = new();
    private double? _omniMainMin;
    /// <summary> Le modèle choisi est un GGUF. </summary>
    public bool IsGguf => SelectedModel?.IsGguf == true;
    /// <summary> Problème du modèle choisi. </summary>
    public string ModelProblem => SelectedModel?.Problem ?? "";
    /// <summary> Avertissement du modèle choisi. </summary>
    public string ModelWarning => SelectedModel is not { } m ? ""
        : string.Join(" ", new[] { m.Warning, _compat.KnownHfOverrides(m) != null ? Loc.T("Compat_AutoFix") : null }.Where(x => !string.IsNullOrEmpty(x)));
    /// <summary> Plusieurs GPU sont présents. </summary>
    public bool IsMultiGpu => Gpus.Count > 1;
    /// <summary> Part de VRAM réservée affichée. </summary>
    public string UtilText => $"{GpuMemoryUtilization * 100:0} %";
    /// <summary> Part de VRAM libre utilisée affichée. </summary>
    public string ShareText => $"{VramShare:0} %";

    private bool _loading;           // évite de recalculer pendant le chargement d'un profil
    private bool _applyingConfig;    // chargement d'un favori : pas de profil automatique

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly ISystemMonitorService _monitor;
    private readonly IPythonEnvironment _python;
    private readonly IUiServices _ui;
    private readonly IEnvironmentChecker _environmentChecker;
    private readonly IChatParsers _chatParsers;
    private readonly IModelCompat _compat;
    private readonly IOmniDeploy _omniDeploy;
    private readonly IVllmOptions _vllmOptions;
    private readonly IVllmRecipes _recipes;
    private readonly IInstanceViewModelFactory _instanceFactory;
    private readonly ISdComponentService _sdComponents;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    /// <param name="appState"> État partagé entre les pages. </param>
    /// <param name="monitor"> Mesures du système. </param>
    /// <param name="python"> Lecture du venv. </param>
    /// <param name="ui"> Services de l'interface (fichiers, presse-papiers, liens). </param>
    /// <param name="environmentChecker"> Vérification du venv avant lancement. </param>
    /// <param name="chatParsers"> Parsers de raisonnement et d'outils. </param>
    /// <param name="compat"> Correctifs connus des modèles. </param>
    /// <param name="omniDeploy"> Étapes des modèles vLLM-Omni. </param>
    /// <param name="vllmOptions"> Options du vLLM installé. </param>
    /// <param name="recipes"> Recettes officielles de vLLM. </param>
    /// <param name="instanceFactory"> Création des serveurs. </param>
    /// <param name="sdComponents"> Téléchargement du VAE et de l'encodeur de texte (FLUX.2 klein). </param>
    public LaunchViewModel(ISettingsService settings, IAppState appState, ISystemMonitorService monitor, IPythonEnvironment python, IUiServices ui, IEnvironmentChecker environmentChecker, IChatParsers chatParsers, IModelCompat compat, IOmniDeploy omniDeploy, IVllmOptions vllmOptions, IVllmRecipes recipes, IInstanceViewModelFactory instanceFactory, ISdComponentService sdComponents)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(environmentChecker);
        ArgumentNullException.ThrowIfNull(chatParsers);
        ArgumentNullException.ThrowIfNull(compat);
        ArgumentNullException.ThrowIfNull(omniDeploy);
        ArgumentNullException.ThrowIfNull(vllmOptions);
        ArgumentNullException.ThrowIfNull(recipes);
        ArgumentNullException.ThrowIfNull(instanceFactory);
        ArgumentNullException.ThrowIfNull(sdComponents);
        _settings = settings;
        _appState = appState;
        _monitor = monitor;
        _python = python;
        _ui = ui;
        _environmentChecker = environmentChecker;
        _chatParsers = chatParsers;
        _compat = compat;
        _omniDeploy = omniDeploy;
        _vllmOptions = vllmOptions;
        _recipes = recipes;
        _instanceFactory = instanceFactory;
        _sdComponents = sdComponents;
        _host = _settings.Current.DefaultHost;
        _port = _settings.Current.DefaultPort.ToString();
        _appState.ModelsChanged += OnModelsChanged;
        _appState.LaunchRequested += m =>
        {
            ShowNewServer();
            SelectedModel = Models.FirstOrDefault(x => x.Target == m.Target) ?? m;
        };
        _appState.EditRequested += ApplyConfig;
        Favorites.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFavorites));
        _monitor.Updated += OnSample;
        // Liste des options du vLLM installé lue : parsers exacts et vérification de la commande
        _vllmOptions.Loaded += () =>
        {
            if (SelectedModel is { } m && !_applyingConfig) RefreshParsers(m, keepSelection: true);
            UpdatePreview();
        };
    }

    /// <inheritdoc />
    public override void OnNavigatedTo()
    {
        OnPropertyChanged(nameof(VllmMissing));
        OnPropertyChanged(nameof(LlamaMissing));
        OnPropertyChanged(nameof(CanLaunch));
        _ = _vllmOptions.EnsureAsync();
    }

    private void OnSample(SystemSample s)
    {
        // Liste des GPU construite à la première mesure, mémoire libre mise à jour ensuite
        if (Gpus.Count != s.Gpus.Count)
        {
            Gpus.Clear();
            foreach (var g in s.Gpus)
            {
                var c = new GpuChoice { Index = g.Index, Name = g.Name, Vendor = g.Vendor };
                c.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName != nameof(GpuChoice.IsSelected) || _loading) return;
                    TensorParallel = Math.Max(1, Gpus.Count(x => x.IsSelected)).ToString();
                    Recalculate();
                };
                Gpus.Add(c);
            }
            OnPropertyChanged(nameof(IsMultiGpu));
            if (SelectedModel != null && !_applyingConfig) LoadProfile(SelectedModel);
        }
        foreach (var g in s.Gpus)
            if (Gpus.FirstOrDefault(c => c.Index == g.Index) is { } c)
                c.Memory = Loc.F("Launch_GpuFree", Format.Bytes(g.MemFree), Format.Bytes(g.MemTotal));
        if (IsNewServerView) UpdateVram();
    }

    private void OnModelsChanged()
    {
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(ShowVllmAdvanced));
        if (SelectedModel != null)
            SelectedModel = Models.FirstOrDefault(m => m.Target == SelectedModel.Target);
        if (SelectedModel != null) return;
        var budget = ArgsPlanner.Budget(_monitor.Latest?.Gpus ?? [], null);
        SelectedModel = Models.FirstOrDefault(m => !m.IsBroken && ArgsPlanner.Plan(m, budget, ArgsPlanner.ContextCapOf(_settings.Current)).Fits)
                        ?? Models.FirstOrDefault(m => !m.IsBroken);
    }

    partial void OnSelectedModelChanged(ModelInfo? value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(ShowOmniOption));
        OnPropertyChanged(nameof(IsGguf));
        OnPropertyChanged(nameof(IsLlama));
        OnPropertyChanged(nameof(LlamaMissing));
        OnPropertyChanged(nameof(IsSdCpp));
        OnPropertyChanged(nameof(SdCppMissing));
        OnPropertyChanged(nameof(ShowEngineChoice));
        OnPropertyChanged(nameof(ShowGpuPercent));
        OnPropertyChanged(nameof(GpuPercentText));
        OnPropertyChanged(nameof(IsNativeEngine));
        OnPropertyChanged(nameof(ShowVllmAdvanced));
        OnPropertyChanged(nameof(ShowParsers));
        OnPropertyChanged(nameof(ShowLoras));
        OnPropertyChanged(nameof(VllmMissing));
        OnPropertyChanged(nameof(ShowVllmMemory));
        OnPropertyChanged(nameof(ShowGpuPercent));
        OnPropertyChanged(nameof(IsDiffusers));
        OnPropertyChanged(nameof(ShowMemorySliders));
        OnPropertyChanged(nameof(ShowVllmAdvanced));
        OnPropertyChanged(nameof(ModelProblem));
        OnPropertyChanged(nameof(ModelWarning));
        OnPropertyChanged(nameof(ShowTextOnly));
        OnPropertyChanged(nameof(ShowCpuOffload));
        OnPropertyChanged(nameof(CpuOffloadHelp));
        OnPropertyChanged(nameof(ShowMtp));
        OnPropertyChanged(nameof(ShowParsers));
        OnPropertyChanged(nameof(TextOnlyHelp));
        _ = LoadRecipeAsync(value);
        if (value == null || _applyingConfig) return;
        LoadProfile(value);
    }

    /// <summary> Listes des parsers du vLLM installé et choix détecté pour ce modèle. </summary>
    private void RefreshParsers(ModelInfo m, bool keepSelection = false)
    {
        var (r, t) = (ReasoningParser, ToolCallParser);
        _detected = _chatParsers.Detect(m);
        ReasoningParsers.Clear();
        ToolParsers.Clear();
        ReasoningParsers.Add(NoParser);
        ToolParsers.Add(NoParser);
        foreach (var x in _chatParsers.ReasoningParsers) ReasoningParsers.Add(x);
        foreach (var x in _chatParsers.ToolParsers) ToolParsers.Add(x);
        ParserHint = _detected.Reasoning == null && _detected.Tool == null ? Loc.T("Launch_Parsers_NoneDetected")
            : Loc.F("Launch_Parsers_Detected", _detected.Reasoning ?? NoParser, _detected.Tool ?? NoParser);
        if (keepSelection) { ReasoningParser = ReasoningParsers.Contains(r) ? r : NoParser; ToolCallParser = ToolParsers.Contains(t) ? t : NoParser; }
    }

    /// <summary> Valeur d'un profil (null = automatique, "" = aucun) -> élément de la liste. </summary>
    private static string ParserItem(string? saved, string? detected, ObservableCollection<string> list) =>
        saved == null ? (detected != null && list.Contains(detected) ? detected : NoParser)
        : saved.Length == 0 || !list.Contains(saved) ? NoParser : saved;

    // La liste vidée (changement de modèle) peut remettre la sélection à null
    private static string? ParserValue(string? item) => string.IsNullOrEmpty(item) || item == NoParser ? null : item;

    /// <summary> Adaptateurs LoRA entraînés pour ce modèle. </summary>
    private void RefreshLoras(ModelInfo? m, IEnumerable<string>? selected)
    {
        Loras.Clear();
        if (m is { IsGguf: false })
            foreach (var a in _appState.Adapters.Where(a => a.Fits(m)))
            {
                var c = new LoraChoice { Adapter = a, IsSelected = selected?.Contains(a.Name) == true };
                c.PropertyChanged += (_, _) => UpdatePreview();
                Loras.Add(c);
            }
        OnPropertyChanged(nameof(ShowLoras));
    }

    private async Task LoadRecipeAsync(ModelInfo? m)
    {
        RecipeTitle = RecipeUrl = RecipeArgs = RecipeSummary = RecipeStatus = "";
        _recipe = null;
        if (m == null || m.IsGguf) return;
        var recipe = await _recipes.FindAsync(m);
        if (SelectedModel != m || recipe == null) return;
        _recipe = recipe;
        RecipeTitle = Loc.F("Launch_Recipe", recipe.Title);
        RecipeUrl = recipe.Url;
        RecipeArgs = string.Join(' ', recipe.ExtraArgs.Select(LaunchConfig.Quote));
        var parts = new List<string>();
        if (recipe.ReasoningParser != null) parts.Add($"--reasoning-parser {recipe.ReasoningParser}");
        if (recipe.ToolParser != null) parts.Add($"--tool-call-parser {recipe.ToolParser}");
        if (recipe.TrustRemoteCode) parts.Add("--trust-remote-code");
        if (RecipeArgs.Length > 0) parts.Add(RecipeArgs);
        RecipeSummary = string.Join("  ", parts);
    }

    [RelayCommand]
    private void OpenRecipe()
    {
        if (RecipeUrl.Length > 0) _ui.Open(RecipeUrl);
    }

    /// <summary>
    /// Ajoute les options recommandées par la recette aux arguments supplémentaires (sans doublon).
    /// Le résultat est écrit dans <see cref="RecipeStatus"/> : sans lui, un clic qui ne change rien ne se voit pas
    /// (le parser est souvent déjà détecté automatiquement).
    /// </summary>
    [RelayCommand]
    private void ApplyRecipe()
    {
        if (_recipe is not { } recipe) return;
        var changes = new List<string>();
        // Parsers recommandés (s'ils existent dans le vLLM installé) : la recette fait foi sur la détection
        if (recipe.ReasoningParser is { } rp && ReasoningParsers.Contains(rp) && ReasoningParser != rp)
        {
            ReasoningParser = rp;
            changes.Add($"--reasoning-parser {rp}");
        }
        if (recipe.ToolParser is { } tp && ToolParsers.Contains(tp) && ToolCallParser != tp)
        {
            ToolCallParser = tp;
            changes.Add($"--tool-call-parser {tp}");
        }
        if (recipe.TrustRemoteCode && !TrustRemoteCode)
        {
            TrustRemoteCode = true;
            changes.Add("--trust-remote-code");
        }
        var current = UiServices.SplitArgs(ExtraArgs).ToList();
        var add = UiServices.SplitArgs(RecipeArgs).ToList();
        // Option par option (avec sa valeur éventuelle) : celles déjà présentes sont gardées telles quelles
        for (int i = 0; i < add.Count; i++)
        {
            if (!add[i].StartsWith("--")) continue;
            var opt = add[i];
            var values = add.Skip(i + 1).TakeWhile(x => !x.StartsWith("--")).ToList();
            if (!current.Contains(opt))
            {
                current.Add(opt);
                current.AddRange(values);
                changes.Add(string.Join(' ', new[] { opt }.Concat(values)));
            }
        }
        ExtraArgs = string.Join(' ', current.Select(LaunchConfig.Quote));
        ShowAdvanced = true;
        RecipeStatus = changes.Count == 0 ? Loc.T("Launch_Recipe_AlreadyApplied") : Loc.F("Launch_Recipe_Applied", string.Join("  ", changes));
    }

    partial void OnSelectedInstanceChanged(InstanceViewModel? value)
    {
        if (value != null) IsNewServerView = false;
    }

    partial void OnGpuMemoryUtilizationChanged(double value)
    {
        OnPropertyChanged(nameof(UtilText));
        // Curseur --gpu-memory-utilization déplacé à la main : la part de la VRAM libre suit (sans recalcul)
        if (!_loading && ArgsPlanner.Budget(_monitor.Latest?.Gpus ?? [], GpuIds) is { } b
            && ArgsPlanner.LaunchableBytes(b.Free) is var launchable and > 0)
        {
            _loading = true;
            VramShare = Math.Clamp(Math.Round(value * b.Usable / launchable * 100 / 5) * 5, 10, 100);
            _loading = false;
        }
        if (Omni && !_loading && SelectedModel is { } m)
            ApplyOmniText(m, ArgsPlanner.Budget(_monitor.Latest?.Gpus ?? [], GpuIds));
        UpdatePreview();
    }
    partial void OnVramShareChanged(double value) { OnPropertyChanged(nameof(ShareText)); Recalculate(); }
    partial void OnTensorParallelChanged(string value) => Recalculate();
    partial void OnPipelineParallelChanged(string value) => UpdatePreview();
    partial void OnHostChanged(string value) => UpdatePreview();
    partial void OnPortChanged(string value) => UpdatePreview();
    partial void OnServedNameChanged(string value) => UpdatePreview();
    partial void OnMaxModelLenChanged(string value) => UpdatePreview();
    partial void OnKvCacheDtypeChanged(string value) => UpdatePreview();
    partial void OnEnforceEagerChanged(bool value) => UpdatePreview();
    partial void OnMaxNumSeqsChanged(string value) => UpdatePreview();
    partial void OnDtypeChanged(string value) => UpdatePreview();
    partial void OnTrustRemoteCodeChanged(bool value) => UpdatePreview();
    partial void OnPrefixCachingChanged(bool value) => UpdatePreview();
    partial void OnApiKeyChanged(string value) => UpdatePreview();
    partial void OnOmniChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOmniMode));
        OnPropertyChanged(nameof(ShowVllmMemory));
        OnPropertyChanged(nameof(ShowGpuPercent));
        OnPropertyChanged(nameof(IsDiffusers));
        OnPropertyChanged(nameof(ShowMemorySliders));
        OnPropertyChanged(nameof(ShowTextOnly));
        OnPropertyChanged(nameof(ShowCpuOffload));
        OnPropertyChanged(nameof(ShowMtp));
        OnPropertyChanged(nameof(ShowParsers));
        Recalculate();
    }
    partial void OnTokenizerChanged(string value) => UpdatePreview();
    partial void OnHfConfigPathChanged(string value) => UpdatePreview();
    partial void OnExtraArgsChanged(string value) => UpdatePreview();

    [RelayCommand]
    private void ShowNewServer()
    {
        SelectedInstance = null;
        IsNewServerView = true;
        LaunchError = "";
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(VllmMissing));
        Recalculate();
    }

    private System.Collections.Generic.List<GpuChoice> SelectedGpus => Gpus.Where(g => g.IsSelected).ToList();

    private string? GpuIds =>
        Gpus.Count <= 1 || Gpus.All(g => g.IsSelected) ? null
        : string.Join(',', SelectedGpus.Select(g => g.Index));

    private static int? ParseInt(string s) => int.TryParse(s.Trim(), out var v) && v > 0 ? v : null;

    // ---------------------------------------------------------------- Valeurs automatiques

    /// <summary> Recalcule les valeurs automatiques (mémoire, contexte) à partir de la VRAM libre actuelle. </summary>
    [RelayCommand]
    private void Recalculate()
    {
        if (_loading || SelectedModel is not { } m) { UpdatePreview(); return; }

        var gpus = _monitor.Latest?.Gpus ?? [];
        var budget = ArgsPlanner.Budget(gpus, GpuIds);
        if (Omni) { RecalculateOmni(m, budget); return; }
        var plan = ArgsPlanner.Plan(m, budget, ArgsPlanner.ContextCapOf(_settings.Current), VramShare / 100.0, ParseInt(TensorParallel) ?? 1, TextOnly && ShowTextOnly, gpuFraction: VllmGpuFraction);

        _loading = true;
        GpuMemoryUtilization = plan.GpuMemoryUtilization;
        MaxModelLen = plan.MaxModelLen.ToString();
        KvCacheDtype = plan.Fp8KvCache ? "fp8" : "auto";
        EnforceEager = plan.EnforceEager;
        LlamaKvType = plan.Fp8KvCache ? "q8_0" : "f16";
        _loading = false;

        ApplyPlanText(plan, budget);
        UpdatePreview();
    }

    /// <summary>
    /// vLLM-Omni : la VRAM libre (selon la part choisie) est donnée au serveur, puis répartie entre
    /// ses étapes dans les proportions prévues par vLLM-Omni (les valeurs d'origine visent de gros GPU).
    /// </summary>
    private void RecalculateOmni(ModelInfo m, GpuBudget? budget)
    {
        _omniStages = _omniDeploy.Stages(m.ModelType);
        var total = budget == null ? 0.9
            : Math.Clamp(Math.Floor(ArgsPlanner.LaunchableBytes(budget.Free) * (VramShare / 100.0) / budget.Usable * 100) / 100, 0.05, ArgsPlanner.MaxUtil);

        _loading = true;
        GpuMemoryUtilization = total;
        _loading = false;
        ApplyOmniText(m, budget);
        UpdatePreview();
    }

    private void ApplyOmniText(ModelInfo m, GpuBudget? budget)
    {
        if (_omniStages.Count == 0 && m.ModelType == "diffusers")
        {
            // Pipeline d'image : une seule étape, chargée en entier ; la barre de VRAM dit si elle tient
            OmniStagesText = Loc.T("Omni_Diffusers");
            var fits = budget == null || ArgsPlanner.Plan(m, budget, ArgsPlanner.ContextCapOf(_settings.Current), cpuOffload: CpuOffload).Fits;
            (PlanText, PlanClass, PlanIcon) = fits ? (OmniStagesText, "ok", Icons.CheckCircle) : (Loc.T("Omni_DiffusersTooBig"), "error", Icons.XCircle);
            EstimateText = "";
            return;
        }
        if (_omniStages.Count == 0)
        {
            OmniStagesText = Loc.T("Omni_NoStages");
            (PlanText, PlanClass, PlanIcon) = (Loc.T("Omni_NoStages"), "warn", Icons.Warning);
            EstimateText = "";
            return;
        }
        // L'étape la plus dotée porte le modèle principal : poids + surcoût + un minimum de cache KV
        // Même base que vLLM et que la barre : la VRAM totale vue par PyTorch (≈ 97 % de celle de nvidia-smi)
        var usable = budget?.Usable ?? 0;
        var need = OmniDeploy.MainNeedBytes(m);
        _omniMainMin = usable > 0 ? Math.Ceiling(need / usable * 100) / 100 : null;

        var split = OmniDeploy.Split(_omniStages.ToDictionary(s => s.Id.ToString(), s => s.GpuMemoryUtilization),
                                     _omniStages.ToDictionary(s => s.Id.ToString(), s => s.Devices), GpuMemoryUtilization, _omniMainMin);
        var parts = split.OrderBy(kv => int.Parse(kv.Key)).Select(kv => Loc.F("Omni_Stage", kv.Key, kv.Value * 100)).ToList();
        OmniStagesText = Loc.F("Omni_Stages", _omniStages.Count, string.Join(" · ", parts));

        var main = _omniStages.OrderByDescending(s => s.GpuMemoryUtilization).ThenBy(s => s.Id).First().Id.ToString();
        var mainShare = split.GetValueOrDefault(main);
        if (budget == null) (PlanText, PlanClass, PlanIcon) = (OmniStagesText, "muted", Icons.Info);
        else if (mainShare * usable < need * 0.9)
            (PlanText, PlanClass, PlanIcon) = (Loc.F("Omni_TooSmall", Format.Bytes(mainShare * usable), Format.Bytes(need)), "error", Icons.XCircle);
        else if (mainShare * usable < need)
            (PlanText, PlanClass, PlanIcon) = (Loc.F("Omni_Tight", Format.Bytes(mainShare * usable), Format.Bytes(need)), "warn", Icons.Warning);
        else (PlanText, PlanClass, PlanIcon) = (OmniStagesText, "ok", Icons.CheckCircle);
        EstimateText = budget == null ? "" : Loc.F("Omni_Estimate", Format.Bytes(GpuMemoryUtilization * usable), Format.Bytes(budget.Free), budget.Name);
    }

    /// <summary> Choisit automatiquement les GPU (le moins possible) et le tensor parallel. </summary>
    [RelayCommand]
    private void AutoSelectGpus()
    {
        if (SelectedModel is not { } m || Gpus.Count <= 1) return;
        var gpus = _monitor.Latest?.Gpus ?? [];
        var (ids, tp) = ArgsPlanner.AutoSelectGpus(m, gpus, ArgsPlanner.ContextCapOf(_settings.Current), VramShare / 100.0);
        _loading = true;
        foreach (var g in Gpus) g.IsSelected = ids.Contains(g.Index);
        TensorParallel = tp.ToString();
        _loading = false;
        Recalculate();
    }

    private void ApplyPlanText(PlannedArgs plan, GpuBudget? budget)
    {
        PlanText = plan.Explanation;
        (PlanClass, PlanIcon) = budget == null ? ("muted", Icons.Info)
            : !plan.Fits ? ("error", Icons.XCircle)
            : plan.Tight ? ("warn", Icons.Warning)
            : ("ok", Icons.CheckCircle);
        EstimateText = budget == null
            ? Loc.F("Launch_EstimateNoGpu", Format.Bytes(plan.EstimatedBytes))
            : Loc.F("Launch_Estimate", Format.Bytes(plan.EstimatedBytes), Format.Bytes(budget.Free), Format.Bytes(budget.Total), budget.Name);
        var tp = ParseInt(TensorParallel) ?? 1;
        GpuText = Gpus.Count == 0 ? "" : Loc.F("Launch_GpuSummary", SelectedGpus.Count, Gpus.Count, tp);
    }

    private void LoadProfile(ModelInfo m)
    {
        var profiles = _settings.Current.Profiles;
        HasProfile = profiles.TryGetValue(m.Target, out var p);

        _loading = true;
        Host = p?.Host ?? _settings.Current.DefaultHost;
        ServedName = p?.ServedName ?? m.Name;
        MaxNumSeqs = p?.MaxNumSeqs?.ToString() ?? "";
        Dtype = p?.Dtype ?? DefaultDtype(m);
        TrustRemoteCode = p?.TrustRemoteCode ?? false;
        PrefixCaching = p?.PrefixCaching ?? true;
        ApiKey = p?.ApiKey ?? "";
        Omni = !m.IsGguf && (p?.Omni ?? m.IsOmni);
        PipelineParallel = "1";
        Tokenizer = p?.Tokenizer ?? "";
        HfConfigPath = p?.HfConfigPath ?? "";
        ExtraArgs = p?.ExtraArgs ?? "";
        RefreshParsers(m);
        ReasoningParser = ParserItem(p?.ReasoningParser, _detected.Reasoning, ReasoningParsers);
        ToolCallParser = ParserItem(p?.ToolCallParser, _detected.Tool, ToolParsers);
        TextOnly = p?.TextOnly ?? false;
        UseSdCpp = m.HasSdCppAlternative && (p?.UseSdCpp ?? false);
        SdVae = p?.SdVae ?? "";
        SdLlm = p?.SdLlm ?? "";
        GpuPercent = p?.GpuPercent ?? 100;
        if (m.IsGguf && m.NumLayers is { } nlayers)
            LlamaGpuLayers = GpuPercent >= 100 ? "" : LayersForPercent(nlayers).ToString();
        CpuOffload = p?.CpuOffload ?? ArgsPlanner.NeedsCpuOffload(m, ArgsPlanner.Budget(_monitor.Latest?.Gpus ?? [], GpuIds), ArgsPlanner.ContextCapOf(_settings.Current));
        SleepMode = p?.SleepMode ?? false;
        MtpEnabled = p?.MtpTokens is > 0;
        MtpTokens = (p?.MtpTokens ?? 1).ToString();
        RefreshLoras(m, p?.Loras);
        _loading = false;

        // GPU : ceux du profil, sinon sélection automatique
        if (p?.GpuIds is { } ids)
        {
            var set = ids.Split(',').Select(x => int.TryParse(x, out var i) ? i : -1).ToHashSet();
            _loading = true;
            foreach (var g in Gpus) g.IsSelected = set.Contains(g.Index);
            TensorParallel = (p.TensorParallel ?? Math.Max(1, SelectedGpus.Count)).ToString();
            _loading = false;
        }
        else if (Gpus.Count > 1) AutoSelectGpus();
        else
        {
            _loading = true;
            foreach (var g in Gpus) g.IsSelected = true;
            TensorParallel = (p?.TensorParallel ?? 1).ToString();
            _loading = false;
        }

        // Port : premier port libre à partir du port préféré
        Port = _appState.NextFreePort(p?.Port ?? _settings.Current.DefaultPort).ToString();

        // Mémoire et contexte : toujours recalculés (la VRAM libre change), sauf valeurs forcées dans le profil
        Recalculate();
        _loading = true;
        if (p?.GpuMemoryUtilization is { } u) GpuMemoryUtilization = u;
        if (p?.MaxModelLen is { } l) MaxModelLen = l.ToString();
        if (p?.KvCacheDtype is { } kv) KvCacheDtype = kv;
        if (p?.EnforceEager is { } ee) EnforceEager = ee;
        _loading = false;
        UpdatePreview();
    }

    /// <summary> bfloat16 n'existe pas sur les GPU de capacité &lt; 8.0 (avant RTX 30xx) : float16 à la place. </summary>
    private string DefaultDtype(ModelInfo m)
    {
        var caps = _monitor.ComputeCaps;
        var selected = Gpus.Where(g => g.IsSelected).Select(g => g.Index).ToList();
        var relevant = caps.Where(c => selected.Count == 0 || selected.Contains(c.Key)).Select(c => c.Value).ToList();
        if (m.IsGguf || m.TorchDtype is not ("bfloat16" or "auto" or "?" or null) || relevant.Count == 0) return "auto";
        return relevant.Min() < 8.0 ? "float16" : "auto";
    }

    [RelayCommand]
    private void ResetProfile()
    {
        if (SelectedModel is not { } m) return;
        _settings.Current.Profiles.Remove(m.Target);
        _settings.Save();
        LoadProfile(m);
    }

    /// <summary> Mémorise les valeurs qui diffèrent des valeurs automatiques. </summary>
    private void SaveProfile(ModelInfo m)
    {
        var gpus = _monitor.Latest?.Gpus ?? [];
        var plan = ArgsPlanner.Plan(m, ArgsPlanner.Budget(gpus, GpuIds), ArgsPlanner.ContextCapOf(_settings.Current), VramShare / 100.0, ParseInt(TensorParallel) ?? 1, TextOnly && ShowTextOnly, gpuFraction: VllmGpuFraction);
        var autoTokenizer = "";
        // Parsers : mémorisés seulement s'ils diffèrent de la détection (null = automatique, "" = aucun)
        string? SavedParser(string? item, string? detected) => ParserValue(item) == detected ? null : ParserValue(item) ?? "";

        var p = new LaunchProfile
        {
            Host = Host != _settings.Current.DefaultHost ? Host : null,
            Port = ParseInt(Port) is { } port && port != _settings.Current.DefaultPort ? port : null,
            ServedName = ServedName != m.Name ? ServedName : null,
            GpuMemoryUtilization = Math.Abs(GpuMemoryUtilization - plan.GpuMemoryUtilization) > 0.005 ? GpuMemoryUtilization : null,
            MaxModelLen = ParseInt(MaxModelLen) is { } len && len != plan.MaxModelLen ? len : null,
            KvCacheDtype = KvCacheDtype != (plan.Fp8KvCache ? "fp8" : "auto") ? KvCacheDtype : null,
            EnforceEager = EnforceEager != plan.EnforceEager ? EnforceEager : null,
            TensorParallel = GpuIds != null ? ParseInt(TensorParallel) : null,
            MaxNumSeqs = ParseInt(MaxNumSeqs),
            Dtype = Dtype != "auto" ? Dtype : null,
            TrustRemoteCode = TrustRemoteCode ? true : null,
            PrefixCaching = PrefixCaching ? null : false,
            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
            GpuIds = GpuIds,
            Omni = Omni != m.IsOmni ? Omni : null,
            Tokenizer = Tokenizer.Trim() != autoTokenizer ? Tokenizer.Trim() : null,
            HfConfigPath = string.IsNullOrWhiteSpace(HfConfigPath) ? null : HfConfigPath.Trim(),
            ExtraArgs = string.IsNullOrWhiteSpace(ExtraArgs) ? null : ExtraArgs.Trim(),
            ReasoningParser = SavedParser(ReasoningParser, _detected.Reasoning),
            ToolCallParser = SavedParser(ToolCallParser, _detected.Tool),
            TextOnly = TextOnly ? true : null,
            UseSdCpp = UseSdCpp && m.HasSdCppAlternative ? true : null,
            SdVae = SdComponentsNeeded && SdVae.Trim() is { Length: > 0 } sv ? sv : null,
            SdLlm = SdComponentsNeeded && SdLlm.Trim() is { Length: > 0 } sl ? sl : null,
            GpuPercent = GpuPercent < 100 ? (int)GpuPercent : null,
            CpuOffload = CpuOffload != ArgsPlanner.NeedsCpuOffload(m, ArgsPlanner.Budget(gpus, GpuIds), ArgsPlanner.ContextCapOf(_settings.Current)) ? CpuOffload : null,
            SleepMode = SleepMode ? true : null,
            MtpTokens = MtpEnabled && ParseInt(MtpTokens) is { } mtp ? mtp : null,
            Loras = Loras.Where(l => l.IsSelected).Select(l => l.Adapter.Name).ToList() is { Count: > 0 } ls ? ls : null,
        };
        _settings.Current.Profiles[m.Target] = p;
        _settings.Save();
        HasProfile = true;
    }

    // ---------------------------------------------------------------- Configuration

    /// <summary> Configuration correspondant au formulaire. </summary>
    private LaunchConfig? ToConfig()
    {
        if (SelectedModel is not { } m) return null;
        return new LaunchConfig
        {
            ModelTarget = m.Target,
            ModelRepo = m.Repo,
            ModelIsGguf = m.IsGguf,
            Engine = m.IsGguf ? LaunchConfig.EngineLlama : IsSdCpp ? LaunchConfig.EngineSdCpp : LaunchConfig.EngineVllm,
            SdModelFile = IsSdCpp ? m.SdCppFile : null,
            SdOffload = IsSdCpp && SdOffload,
            SdVae = SdComponentsNeeded && SdVae.Trim() is { Length: > 0 } cv ? cv : null,
            SdLlm = SdComponentsNeeded && SdLlm.Trim() is { Length: > 0 } cl ? cl : null,
            CpuOffloadGb = VllmGpuFraction < 1
                ? Math.Round(ArgsPlanner.LoadedWeights(m, TextOnly && ShowTextOnly) * (1 - VllmGpuFraction) / ArgsPlanner.Gib / Math.Max(1, ParseInt(TensorParallel) ?? 1), 1)
                : null,
            LlamaKvType = LlamaKvType,
            LlamaGpuLayers = int.TryParse(LlamaGpuLayers.Trim(), out var ngl) && ngl >= 0 ? ngl : null,
            Embedding = IsLlama && LlamaEmbedding,
            Host = Host.Trim(),
            Port = ParseInt(Port) ?? 0,
            ServedName = string.IsNullOrWhiteSpace(ServedName) ? m.Name : ServedName.Trim(),
            GpuMemoryUtilization = Math.Round(GpuMemoryUtilization, 2),
            MaxModelLen = ParseInt(MaxModelLen),
            KvCacheDtype = KvCacheDtype,
            EnforceEager = EnforceEager,
            TensorParallel = ParseInt(TensorParallel) ?? 1,
            PipelineParallel = ParseInt(PipelineParallel) ?? 1,
            MaxNumSeqs = ParseInt(MaxNumSeqs),
            Dtype = Dtype,
            TrustRemoteCode = TrustRemoteCode,
            PrefixCaching = PrefixCaching,
            ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim(),
            GpuIds = GpuIds,
            GpuVendor = Gpus.FirstOrDefault()?.Vendor ?? "NVIDIA",
            Omni = Omni && !IsSdCpp,
            OmniStageWeights = Omni && _omniStages.Count > 0 ? _omniStages.ToDictionary(s => s.Id.ToString(), s => s.GpuMemoryUtilization) : null,
            OmniStageDevices = Omni && _omniStages.Count > 0 ? _omniStages.ToDictionary(s => s.Id.ToString(), s => s.Devices) : null,
            OmniMainMin = Omni ? _omniMainMin : null,
            Tokenizer = string.IsNullOrWhiteSpace(Tokenizer) ? null : Tokenizer.Trim(),
            HfConfigPath = string.IsNullOrWhiteSpace(HfConfigPath) ? null : HfConfigPath.Trim(),
            HfOverrides = m.IsGguf ? null : _compat.KnownHfOverrides(m),
            ExtraArgs = string.IsNullOrWhiteSpace(ExtraArgs) ? null : ExtraArgs.Trim(),
            VramShare = VramShare,
            ReasoningParser = ShowParsers ? ParserValue(ReasoningParser) : null,
            ToolCallParser = ShowParsers ? ParserValue(ToolCallParser) : null,
            TextOnly = TextOnly && ShowTextOnly,
            CpuOffload = CpuOffload && ShowCpuOffload,
            VaeTiling = Omni && m.ModelType == "diffusers",
            SleepMode = SleepMode && !m.IsGguf && !Omni,
            Loras = !m.IsGguf && Loras.Where(l => l.IsSelected).Select(l => new LoraModule { Name = l.Adapter.Name, Path = l.Adapter.Path }).ToList() is { Count: > 0 } lm ? lm : null,
            MaxLoraRank = Loras.Where(l => l.IsSelected).Select(l => l.Adapter.Rank).DefaultIfEmpty(0).Max() is var r and > 0 ? LoraRank(r) : null,
            MtpTokens = MtpEnabled && ShowMtp ? Math.Clamp(ParseInt(MtpTokens) ?? 1, 1, 8) : 0,
            MtpMethod = MtpEnabled && ShowMtp ? MtpMethodFor(m) : null,
        };
    }

    /// <summary> --max-lora-rank n'accepte que certaines valeurs : la plus petite qui couvre le rang. </summary>
    private static int LoraRank(int r) => new[] { 8, 16, 32, 64, 128, 256, 320, 512 }.FirstOrDefault(x => x >= r, 512);

    /// <summary>
    /// Méthode MTP : « mtp » sur les vLLM récents (≥ 0.10.2) qui choisissent eux-mêmes le module selon le modèle ;
    /// avant, un nom par famille (table de correspondance de Local-LLM-Launcher-GUI, engines/mtp.py).
    /// </summary>
    private string MtpMethodFor(ModelInfo m)
    {
        if (_python.VllmVersion is { } v && Version.TryParse(v.Split('+')[0].Split(".dev")[0], out var ver) && ver >= new Version(0, 10, 2))
            return "mtp";
        return (m.ModelType ?? "") switch
        {
            var t when t.StartsWith("deepseek") => "deepseek_mtp",
            var t when t.StartsWith("qwen3_next") => "qwen3_next_mtp",
            var t when t.StartsWith("glm4_moe") => "glm4_moe_mtp",
            var t when t.StartsWith("mimo") => "mimo_mtp",
            var t when t.StartsWith("ernie") => "ernie_mtp",
            _ => "mtp",
        };
    }

    /// <summary> Charge une configuration complète (favori, "modifier et relancer") dans le formulaire. </summary>
    public void ApplyConfig(LaunchConfig c)
    {
        SelectedInstance = null;
        IsNewServerView = true;
        LaunchError = "";

        _applyingConfig = true;
        SelectedModel = Models.FirstOrDefault(m => m.Target == c.ModelTarget);
        _applyingConfig = false;
        if (SelectedModel == null)
        {
            LaunchError = Loc.F("Fav_ModelMissing", c.ModelRepo);
            return;
        }
        HasProfile = _settings.Current.Profiles.ContainsKey(c.ModelTarget);

        _loading = true;
        Host = c.Host;
        ServedName = c.ServedName;
        GpuMemoryUtilization = c.GpuMemoryUtilization;
        MaxModelLen = c.MaxModelLen?.ToString() ?? "";
        KvCacheDtype = c.KvCacheDtype;
        LlamaKvType = c.LlamaKvType;
        LlamaGpuLayers = c.LlamaGpuLayers?.ToString() ?? "";
        LlamaEmbedding = c.Embedding;
        EnforceEager = c.EnforceEager;
        TensorParallel = c.TensorParallel.ToString();
        PipelineParallel = c.PipelineParallel.ToString();
        MaxNumSeqs = c.MaxNumSeqs?.ToString() ?? "";
        Dtype = c.Dtype;
        TrustRemoteCode = c.TrustRemoteCode;
        PrefixCaching = c.PrefixCaching;
        ApiKey = c.ApiKey ?? "";
        Omni = c.Omni;
        _omniStages = c.OmniStageWeights?.Select(kv => new OmniStage(int.Parse(kv.Key), kv.Value,
            c.OmniStageDevices?.GetValueOrDefault(kv.Key) ?? "0")).ToList() ?? _omniDeploy.Stages(SelectedModel.ModelType);
        _omniMainMin = c.OmniMainMin;
        Tokenizer = c.Tokenizer ?? "";
        HfConfigPath = c.HfConfigPath ?? "";
        ExtraArgs = c.ExtraArgs ?? "";
        VramShare = c.VramShare;
        RefreshParsers(SelectedModel);
        ReasoningParser = ParserItem(c.ReasoningParser ?? "", null, ReasoningParsers);
        ToolCallParser = ParserItem(c.ToolCallParser ?? "", null, ToolParsers);
        TextOnly = c.TextOnly;
        UseSdCpp = c.IsSdCpp && SelectedModel?.HasSdCppAlternative == true;
        SdVae = c.SdVae ?? "";
        SdLlm = c.SdLlm ?? "";
        GpuPercent = SelectedModel is not { } sel ? 100
            : c.IsLlama ? (c.LlamaGpuLayers is { } cn && sel.NumLayers is { } nl and > 0 ? Math.Clamp(Math.Round(100.0 * cn / nl), 0, 100) : 100)
            : c.CpuOffloadGb is > 0 and var off && ArgsPlanner.LoadedWeights(sel, c.TextOnly) is var lw and > 0
                ? Math.Clamp(Math.Round(100 * (1 - off * ArgsPlanner.Gib * Math.Max(1, c.TensorParallel) / lw) / 5) * 5, 5, 100) : 100;
        CpuOffload = c.CpuOffload;
        SleepMode = c.SleepMode;
        MtpEnabled = c.MtpTokens > 0;
        MtpTokens = Math.Max(1, c.MtpTokens).ToString();
        RefreshLoras(SelectedModel, c.Loras?.Select(l => l.Name));
        var set = c.GpuIds?.Split(',').Select(x => int.TryParse(x, out var i) ? i : -1).ToHashSet();
        foreach (var g in Gpus) g.IsSelected = set == null || set.Contains(g.Index);
        _loading = false;

        // Port de la configuration, ou le suivant s'il est déjà pris
        Port = (_appState.IsPortTaken(c.Port) ? _appState.NextFreePort(c.Port) : c.Port).ToString();

        var gpus = _monitor.Latest?.Gpus ?? [];
        var budget = ArgsPlanner.Budget(gpus, GpuIds);
        if (SelectedModel is { } planned)
            ApplyPlanText(ArgsPlanner.Plan(planned, budget, ArgsPlanner.ContextCapOf(_settings.Current), VramShare / 100.0, c.TensorParallel, c.TextOnly, gpuFraction: VllmGpuFraction), budget);
        UpdatePreview();
    }

    private void UpdatePreview()
    {
        OnPropertyChanged(nameof(CanLaunch));
        if (_loading) return;
        CommandPreview = ToConfig()?.CommandLine(_vllmOptions) ?? "";
        UpdateVram();
    }

    // ---------------------------------------------------------------- Barre de VRAM

    [ObservableProperty] private IReadOnlyList<VramSegment> _vramSegments = [];
    [ObservableProperty] private double _vramTotal;
    [ObservableProperty] private string _vramTitle = "";
    [ObservableProperty] private string _vramStatus = "";
    [ObservableProperty] private string _vramStatusClass = "muted";
    [ObservableProperty] private string _vramStatusIcon = Icons.Info;
    /// <summary> Une barre de VRAM est affichée. </summary>
    public bool HasVram => VramTotal > 0;
    partial void OnVramTotalChanged(double value) => OnPropertyChanged(nameof(HasVram));

    /// <summary> llama.cpp : tampons de calcul + contexte CUDA (ordre de grandeur). </summary>
    private const double LlamaOverheadGib = 0.8;

    private static IBrush Brush(string key) => VramUsage.Brush(key);

    /// <summary> Correction proposée par le bouton « Ajuster automatiquement » (null = aucune). </summary>
    private Action? _vramFix;
    [ObservableProperty] private string _vramFixText = "";
    /// <summary> Une correction de la VRAM est proposée. </summary>
    public bool HasVramFix => VramFixText.Length > 0;
    partial void OnVramFixTextChanged(string value) => OnPropertyChanged(nameof(HasVramFix));

    [RelayCommand]
    private void ApplyVramFix() => _vramFix?.Invoke();

    private void SetFix(string? text, Action? apply)
    {
        _vramFix = apply;
        VramFixText = text ?? "";
    }

    private static string Pct(double v) => (v * 100).ToString("0", CultureInfo.CurrentCulture) + " %";
    private static string Tok(double v) => ((long)v).ToString("N0", CultureInfo.CurrentCulture);

    // ---------------------------------------------------------------- Barre de RAM

    [ObservableProperty] private IReadOnlyList<VramSegment> _ramSegments = [];
    [ObservableProperty] private double _ramTotal;
    [ObservableProperty] private string _ramTitle = "";
    [ObservableProperty] private string _ramStatus = "";
    [ObservableProperty] private string _ramStatusClass = "muted";
    [ObservableProperty] private string _ramStatusIcon = Icons.Info;
    /// <summary> Une barre de RAM est affichée. </summary>
    public bool HasRam => RamTotal > 0;
    partial void OnRamTotalChanged(double value) => OnPropertyChanged(nameof(HasRam));

    /// <summary> RAM du processus lui-même (Python, PyTorch, bibliothèques CUDA côté processeur), ordres de grandeur mesurés. </summary>
    private const double VllmProcessGib = 2.5, OmniProcessGib = 3.0, NativeProcessGib = 0.5;

    /// <summary>
    /// Prévision de la RAM, comme pour la VRAM : déjà utilisée, ce que le lancement y mettra (processus,
    /// poids gardés en RAM : --cpu-offload-gb, couches hors GPU de llama.cpp, encodeur de texte, poids en veille…),
    /// libre ensuite. Au-delà, le système utilise le fichier d'échange (tout devient très lent).
    /// </summary>
    private void UpdateRam(SystemSample? sample, ModelInfo m, LaunchConfig c)
    {
        if (sample == null || sample.RamTotal <= 0) { RamTotal = 0; return; }
        var gib = ArgsPlanner.Gib;
        var segs = new List<VramSegment>
        {
            new(Loc.T("Ram_Used"), Format.Bytes(sample.RamUsed), sample.RamUsed, Brush("VramOther"), false, Loc.T("Ram_Used_Detail")),
        };
        void Add(string label, string shortLabel, double bytes, string brush, string? detail = null)
        {
            if (bytes > 0) segs.Add(new VramSegment(label, shortLabel, bytes, Brush(brush), true, detail));
        }

        if (c.IsLlama)
        {
            Add(Loc.T("Ram_Process"), Loc.T("Ram_ProcessShort"), NativeProcessGib * gib, "VramOverhead");
            var layers = m.NumLayers ?? 0;
            var onGpu = layers > 0 ? Math.Clamp((double)(c.LlamaGpuLayers ?? layers) / layers, 0, 1) : 1;
            Add(Loc.T("Ram_WeightsCpu"), Loc.T("Vram_WeightsShort"), m.WeightsBytes * (1 - onGpu), "VramWeights",
                layers > 0 ? Loc.F("Ram_LayersCpu", layers - Math.Min(c.LlamaGpuLayers ?? layers, layers), layers) : null);
        }
        else if (c.IsSdCpp)
        {
            Add(Loc.T("Ram_Process"), Loc.T("Ram_ProcessShort"), NativeProcessGib * gib, "VramOverhead");
            if (c.SdOffload) Add(Loc.T("Ram_WeightsCpu"), Loc.T("Vram_WeightsShort"), m.DiskBytes, "VramWeights", Loc.T("Vram_SdInRam"));
        }
        else
        {
            Add(Loc.T("Ram_Process"), Loc.T("Ram_ProcessShort"), (c.Omni ? OmniProcessGib : VllmProcessGib) * gib, "VramOverhead",
                Loc.T("Ram_Process_Detail"));
            if (c.CpuOffloadGb is > 0 and var off)
                Add(Loc.T("Ram_WeightsCpu"), Loc.T("Vram_WeightsShort"), off * gib * Math.Max(1, c.TensorParallel), "VramWeights", "--cpu-offload-gb");
            if (c.CpuOffload && m.TextEncoderBytes > 0)
                Add(Loc.T("Ram_TextEncoder"), Loc.T("Vram_WeightsShort"), m.TextEncoderBytes, "VramWeights", "--enable-cpu-offload");
            if (c.SleepMode)
                Add(Loc.T("Ram_Sleep"), Loc.T("Ram_SleepShort"), ArgsPlanner.LoadedWeights(m, c.TextOnly), "VramKvSpare", Loc.T("Ram_Sleep_Detail"));
        }

        var newBytes = segs.Where(x => x.IsNew).Sum(x => x.Bytes);
        var freeAfter = sample.RamTotal - sample.RamUsed - newBytes;
        if (freeAfter > 0) segs.Add(VramUsage.Free(freeAfter, "Vram_Free"));
        RamTitle = Loc.F("Ram_Title", Format.Bytes(sample.RamTotal))
                   + (sample.SwapTotal > 0 ? " · " + Loc.F("Ram_Swap", Format.Bytes(sample.SwapUsed), Format.Bytes(sample.SwapTotal)) : "");
        (RamStatus, RamStatusClass, RamStatusIcon) =
            freeAfter < 0 ? (Loc.F("Ram_Missing", Format.Bytes(-freeAfter)), "error", Icons.XCircle)
            : freeAfter < 2 * gib ? (Loc.F("Ram_Tight", Format.Bytes(freeAfter)), "warn", Icons.Warning)
            : (Loc.F("Ram_Ok", Format.Bytes(newBytes), Format.Bytes(freeAfter)), "ok", Icons.CheckCircle);
        RamSegments = segs;
        RamTotal = sample.RamTotal;
    }

    /// <summary>
    /// Répartition de la VRAM du GPU le plus chargé parmi ceux choisis : déjà occupée (autres applications,
    /// serveurs de l'application), réservée par le lancement en préparation (selon le moteur), libre ensuite.
    /// Si la configuration ne peut pas marcher, explique pourquoi et calcule une correction quand elle existe.
    /// </summary>
    private void UpdateVram()
    {
        var sample = _monitor.Latest;
        if (SelectedModel is { } rm && ToConfig() is { } rc) UpdateRam(sample, rm, rc);
        else RamTotal = 0;
        if (sample == null || sample.Gpus.Count == 0 || SelectedModel is not { } m || ToConfig() is not { } c) { VramTotal = 0; SetFix(null, null); return; }
        var ids = c.GpuIds?.Split(',').Select(x => int.TryParse(x, out var i) ? i : -1).ToHashSet();
        var selected = sample.Gpus.Where(g => ids == null || ids.Contains(g.Index)).ToList();
        if (selected.Count == 0) { VramTotal = 0; SetFix(null, null); return; }
        var gpu = selected.MinBy(g => g.MemFree)!;
        var gib = ArgsPlanner.Gib;
        var total = gpu.MemTotal;
        // vLLM calcule --gpu-memory-utilization sur la VRAM vue par PyTorch (≈ 97 % de celle de nvidia-smi)
        var vllmTotal = total * ArgsPlanner.UsableFrac;
        // Ce que ce lancement peut prendre (vLLM : moins son propre contexte CUDA ; llama.cpp : compté dans son surcoût)
        var available = c.IsLlama ? Math.Max(0, total - gpu.MemUsed - ArgsPlanner.SafetyGib * gib)
                                  : ArgsPlanner.LaunchableBytes(total - gpu.MemUsed);

        // Déjà occupé : autres applications, serveurs de l'application
        var segs = VramUsage.Occupied(_appState, sample, gpu, out var servers);
        var hasRunning = servers > 0;

        // Ce que le lancement va réserver
        var tp = Math.Max(1, c.TensorParallel);
        string? problem = null;
        var problemIsEstimate = false;   // écart faible : nos chiffres sont des estimations, ça peut passer
        string? fixText = null;
        Action? fix = null;
        void Missing(string label, double bytes) =>
            segs.Add(new VramSegment(label, Loc.T("Vram_KvMissingShort"), bytes, Brush("ErrorBrush"), true));

        if (c.IsSdCpp)
        {
            // stable-diffusion.cpp : poids du fichier (quantification gardée) + calcul de l'image ;
            // s'ils ne tiennent pas, sd-server en place une partie en RAM de lui-même (--auto-fit) : plus lent, mais ça marche
            var w = c.SdOffload ? 0 : m.DiskBytes;
            var o = 0.4 * gib + DiffusionWorkGib * gib;
            segs.Add(new VramSegment(Loc.T("Vram_Weights"), Loc.T("Vram_WeightsShort"), w, Brush("VramWeights"), true,
                c.SdOffload ? Loc.T("Vram_SdInRam") : null));
            segs.Add(new VramSegment(Loc.T("Vram_ImageWork"), "CUDA", o, Brush("VramOverhead"), true, Loc.T("Vram_ImageWork_Detail")));
            if (w + o > available)
            {
                problem = Loc.F("Vram_SdAutoFit", Format.Bytes(w + o), Format.Bytes(available));
                problemIsEstimate = true;   // pas bloquant : avertissement
            }
        }
        else if (c.IsLlama)
        {
            var layers = m.NumLayers ?? 0;
            var ngl = c.LlamaGpuLayers ?? int.MaxValue;
            var frac = layers > 0 ? Math.Clamp((double)ngl / layers, 0, 1) : 1;
            var w = m.WeightsBytes * frac;
            var o = LlamaOverheadGib * gib;
            segs.Add(new VramSegment(Loc.T("Vram_Weights"), Loc.T("Vram_WeightsShort"), w, Brush("VramWeights"), true,
                frac < 1 ? Loc.F("Vram_LayersOnGpu", Math.Min(ngl, layers), layers) : null));
            segs.Add(new VramSegment(Loc.T("Vram_Overhead"), "CUDA", o, Brush("VramOverhead"), true, Loc.T("Vram_Overhead_Detail")));
            var kvBytes = 0.0;
            if (m.Kv is { } kvInfo)
            {
                var factor = c.LlamaKvType switch { "q8_0" => 0.53, "q4_0" => 0.28, _ => 1.0 };
                var ctxLen = c.MaxModelLen ?? LaunchConfig.LlamaDefaultContext;
                var kvGpu = kvInfo.Scale(factor * frac);
                kvBytes = kvGpu.Bytes(ctxLen);
                segs.Add(new VramSegment(Loc.T("Vram_Kv"), "KV", kvBytes, Brush("VramKv"), true,
                    Loc.F("Vram_KvContext", Tok(ctxLen)) + (kvInfo.HasSliding ? " · " + Loc.F("Vram_KvSliding", Tok(kvInfo.Window)) : "")));

                // Trop gros : d'abord réduire le contexte, sinon mettre moins de couches sur le GPU
                if (w + o + kvBytes > available && kvBytes > 0)
                {
                    var ctxFit = kvGpu.MaxTokens(available - w - o) / 256 * 256;
                    if (ctxFit >= 1024)
                        (fixText, fix) = (Loc.F("Fix_LlamaCtx", Tok(ctxFit)), () => MaxModelLen = ctxFit.ToString());
                    else if (layers > 0)
                    {
                        var perLayer = m.WeightsBytes / (double)layers;
                        var kv2k = kvInfo.Scale(factor).Bytes(2048);
                        var nglFit = (int)((available - o - kv2k) / (perLayer + kv2k / layers));
                        if (nglFit >= 1)
                            (fixText, fix) = (Loc.F("Fix_LlamaNgl", nglFit, layers), () => { _loading = true; MaxModelLen = "2048"; _loading = false; LlamaGpuLayers = nglFit.ToString(); });
                    }
                }
            }
        }
        else
        {
            var reserved = c.GpuMemoryUtilization * vllmTotal;   // vLLM : part de la VRAM totale
            if (c.Omni && m.ModelType == "diffusers")
            {
                // Pipeline d'image : pas de cache KV ni de réserve ; poids chargés (sans les encodeurs de texte gardés en RAM)
                // + calcul de l'image (activations, décodage ≈ 1 Gio en 1024×1024)
                var w = ArgsPlanner.LoadedWeights(m, false, c.CpuOffload);
                var o = ArgsPlanner.OverheadBytes(w) + DiffusionWorkGib * gib;
                segs.Add(new VramSegment(Loc.T("Vram_Weights"), Loc.T("Vram_WeightsShort"), w, Brush("VramWeights"), true,
                    c.CpuOffload && m.TextEncoderBytes > 0 ? Loc.F("Vram_TextEncoderInRam", Format.Bytes(m.TextEncoderBytes)) : null));
                segs.Add(new VramSegment(Loc.T("Vram_ImageWork"), "CUDA", o, Brush("VramOverhead"), true, Loc.T("Vram_ImageWork_Detail")));
                if (w + o > available)
                {
                    problem = Loc.F("Vram_ImageTooBig", Format.Bytes(w + o), Format.Bytes(available));
                    var wOff = ArgsPlanner.LoadedWeights(m, false, true);
                    if (!c.CpuOffload && m.TextEncoderBytes > 0 && wOff + ArgsPlanner.OverheadBytes(wOff) + DiffusionWorkGib * gib <= available)
                        (fixText, fix) = (Loc.T("Fix_CpuOffload"), () => CpuOffload = true);
                }
            }
            else if (c.Omni && c.OmniSplit() is { Count: > 0 } split && c.OmniStageWeights is { } weights)
            {
                // vLLM-Omni : l'étape la plus dotée porte le modèle principal (poids + surcoût + un peu de cache KV)
                var main = weights.OrderByDescending(kv => kv.Value).ThenBy(kv => int.Parse(kv.Key)).First().Key;
                var need = OmniDeploy.MainNeedBytes(m);
                foreach (var (stage, share) in split.OrderBy(kv => int.Parse(kv.Key)))
                {
                    var bytes = share * vllmTotal;
                    var isMain = stage == main;
                    segs.Add(new VramSegment(Loc.F("Vram_Stage", stage), Loc.F("Vram_StageShort", stage), bytes,
                        isMain ? Brush("VramWeights") : Brush("VramOverhead"), true,
                        isMain ? Loc.F("Vram_StageNeed", Format.Bytes(need)) : null));
                    if (isMain && bytes < need)
                    {
                        Missing(Loc.F("Vram_StageMissing", stage), need - bytes);
                        problemIsEstimate = bytes >= need * 0.9;
                        problem = Loc.F(problemIsEstimate ? "Vram_OmniMainTight" : "Vram_OmniMainShort", stage, Format.Bytes(bytes), Format.Bytes(need));
                    }
                }
                if (problem != null)
                {
                    // Plus petite part totale qui donne assez à l'étape principale
                    var minMain = c.OmniMainMin ?? Math.Ceiling(need / vllmTotal * 100) / 100;
                    for (var t = c.GpuMemoryUtilization; t <= ArgsPlanner.MaxUtil + 1e-9; t += 0.01)
                    {
                        var tt = Math.Round(t, 2);
                        if (OmniDeploy.Split(weights, c.OmniStageDevices, tt, minMain).GetValueOrDefault(main) * vllmTotal < need) continue;
                        if (tt * vllmTotal <= available)
                            (fixText, fix) = (Loc.F("Fix_OmniTotal", Pct(tt), Format.Bytes(tt * vllmTotal)), () => SetServerShare(tt, available, vllmTotal));
                        break;
                    }
                }
            }
            else
            {
                // --cpu-offload-gb : cette part des poids reste en RAM
                var w = Math.Max(0, ArgsPlanner.LoadedWeights(m, c.TextOnly) * ArgsPlanner.WeightMargin / tp - (c.CpuOffloadGb ?? 0) * gib);
                var o = ArgsPlanner.OverheadBytes(w, m.VocabSize);
                var kv = reserved - w - o;
                // Cache KV de ce lancement : type choisi, réparti sur les GPU, couches glissantes plafonnées à la fenêtre
                var kvc = m.Kv?.Scale((c.KvCacheDtype.StartsWith("fp8") ? 0.5 : 1.0) / tp);
                int? maxLen = c.MaxModelLen ?? m.MaxPositions;

                // Plus petite réservation qui marche : poids + surcoût + une requête de la longueur maximale.
                // Une seule valeur, utilisée par le message ET par le bouton « Ajuster ».
                var needCtx = kvc != null && maxLen is { } l1 ? kvc.Bytes(l1) : 0.05 * vllmTotal;
                var minUtil = Math.Ceiling((w + o + needCtx) / vllmTotal * 100 + 1) / 100;

                if (kv <= 0)
                {
                    // La réservation ne contient pas les poids + le surcoût : elle les remplit jusqu'à sa limite,
                    // et seule la partie qui dépasse est en rouge (la barre ne grandit pas quand on baisse la réservation)
                    var wIn = Math.Min(w, reserved);
                    var oIn = Math.Clamp(reserved - w, 0, o);
                    segs.Add(new VramSegment(Loc.T("Vram_Weights"), Loc.T("Vram_WeightsShort"), wIn, Brush("VramWeights"), true,
                        tp > 1 ? Loc.F("Vram_WeightsSplit", tp) : null));
                    if (oIn > 0) segs.Add(new VramSegment(Loc.T("Vram_Overhead"), "CUDA", oIn, Brush("VramOverhead"), true, Loc.T("Vram_Overhead_Detail")));
                    // + le cache KV d'une requête, qui manque aussi (le cadre garde la même taille d'un cas à l'autre)
                    Missing(Loc.T("Vram_ReservationMissing"), (w - wIn) + (o - oIn) + (kvc != null && maxLen != null ? needCtx : 0));
                    problem = Loc.F("Vram_ReservationTooSmall", Format.Bytes(reserved), Format.Bytes(w + o), Pct(minUtil));
                }
                else
                {
                    segs.Add(new VramSegment(Loc.T("Vram_Weights"), Loc.T("Vram_WeightsShort"), w, Brush("VramWeights"), true,
                        tp > 1 ? Loc.F("Vram_WeightsSplit", tp) : null));
                    segs.Add(new VramSegment(Loc.T("Vram_Overhead"), "CUDA", o, Brush("VramOverhead"), true, Loc.T("Vram_Overhead_Detail")));
                    if (kvc != null && maxLen is { } ml)
                    {
                        // vLLM réserve toujours la même VRAM ; le contexte décide de la part du cache KV
                        // qu'une seule requête peut occuper (le reste sert aux requêtes en parallèle)
                        var tokens = kvc.MaxTokens(kv);
                        var oneRequest = kvc.Bytes(ml);
                        var sliding = kvc.HasSliding ? Loc.F("Vram_KvSliding", Tok(kvc.Window)) : null;
                        if (oneRequest <= kv)
                        {
                            segs.Add(new VramSegment(Loc.F("Vram_KvOne", Tok(ml)), "KV", oneRequest, Brush("VramKv"), true, sliding));
                            segs.Add(new VramSegment(Loc.T("Vram_KvSpare"), Loc.T("Vram_KvSpareShort"), kv - oneRequest, Brush("VramKvSpare"), true,
                                Loc.F("Vram_KvParallel", kv / oneRequest, Tok(Math.Min(tokens, 99_999_999)))));
                        }
                        else
                        {
                            // Le cache KV prend toute la réservation restante ; le rouge est ce qu'il faudrait EN PLUS
                            segs.Add(new VramSegment(Loc.F("Vram_KvOne", Tok(ml)), "KV", kv, Brush("VramKv"), true, Loc.F("Vram_KvTokens", Tok(tokens))));
                            Missing(Loc.T("Vram_KvMissing"), oneRequest - kv);
                            problem = Loc.F("Vram_KvTooSmall", Tok(tokens), Tok(ml));
                        }
                    }
                    else
                        segs.Add(new VramSegment(Loc.T("Vram_Kv"), "KV", kv, Brush("VramKv"), true,
                            m.Kv == null ? Loc.T("Vram_KvUnknown") : null));
                }

                // Correction : la réservation minimale si la VRAM libre le permet, sinon le maximum possible + un contexte plus court
                if (problem != null || reserved > available)
                {
                    var maxUtil = Math.Floor(Math.Min(available / vllmTotal, ArgsPlanner.MaxUtil) * 100) / 100;
                    if (minUtil <= maxUtil && (problem != null || minUtil < c.GpuMemoryUtilization))
                        (fixText, fix) = (Loc.F("Fix_Util", Pct(minUtil)), () => GpuMemoryUtilization = minUtil);
                    else if (kvc != null && maxUtil * vllmTotal - w - o is var kvMax && kvMax > 0)
                    {
                        var ctxFit = Math.Min(kvc.MaxTokens(kvMax), m.MaxPositions ?? int.MaxValue) / 256 * 256;
                        if (ctxFit >= 1024)
                            (fixText, fix) = (Loc.F("Fix_UtilCtx", Pct(maxUtil), Tok(ctxFit)),
                                () => { _loading = true; MaxModelLen = ctxFit.ToString(); _loading = false; GpuMemoryUtilization = maxUtil; });
                    }
                }
            }
        }

        var newBytes = segs.Where(x => x.IsNew).Sum(x => x.Bytes);
        var freeAfter = total - gpu.MemUsed - newBytes;
        if (freeAfter > 0) segs.Add(VramUsage.Free(freeAfter, "Vram_Free"));

        VramTitle = Loc.F("Vram_Title", gpu.Index, gpu.Name, Format.Bytes(total))
                    + (selected.Count > 1 ? " · " + Loc.F("Vram_MostLoaded", selected.Count) : "");
        if (problem != null)
        {
            // Le problème vient de la configuration, pas forcément du manque de VRAM : le dire clairement
            var hint = fix != null ? Loc.F("Vram_CanFix", Format.Bytes(available)) : hasRunning ? Loc.T("Vram_StopServer") : "";
            (VramStatus, VramStatusClass, VramStatusIcon) = problemIsEstimate
                ? ((problem + " " + hint).Trim(), "warn", Icons.Warning)
                : ((problem + " " + hint).Trim(), "error", Icons.XCircle);
        }
        else if (freeAfter < 0)
            (VramStatus, VramStatusClass, VramStatusIcon) = (Loc.F(c.IsLlama ? "Vram_MissingLlama" : "Vram_Missing", Format.Bytes(-freeAfter)), "error", Icons.XCircle);
        else if (freeAfter < ArgsPlanner.SafetyGib * gib)
            (VramStatus, VramStatusClass, VramStatusIcon) = (Loc.F("Vram_Tight", Format.Bytes(freeAfter)), "warn", Icons.Warning);
        else (VramStatus, VramStatusClass, VramStatusIcon) = (Loc.F("Vram_Ok", Format.Bytes(newBytes), Format.Bytes(freeAfter)), "ok", Icons.CheckCircle);

        SetFix(VramStatusClass == "ok" ? null : fixText, fix);
        VramSegments = segs;
        VramTotal = total;
    }

    /// <summary> Calcul d'une image (activations, décodage par le VAE), en plus des poids. </summary>
    private const double DiffusionWorkGib = 1.0;

    /// <summary> Fixe la part de VRAM du serveur (vLLM-Omni) et aligne le curseur « part de la VRAM libre ». </summary>
    private void SetServerShare(double util, double available, double total)
    {
        _loading = true;
        if (available > 0) VramShare = Math.Clamp(Math.Ceiling(util * total / available * 100 / 5) * 5, 10, 100);
        _loading = false;
        GpuMemoryUtilization = util;
    }

    [RelayCommand]
    private Task CopyCommand() => _ui.CopyAsync(CommandPreview);

    /// <summary> Vérifie la configuration ; retourne un message d'erreur ou null. </summary>
    private string? Validate(LaunchConfig c, ModelInfo m)
    {
        if (c.IsSdCpp)
        {
            if (!StableDiffusionCppService.Installed) return Loc.T("Launch_NoSdCpp");
            if (m.Problem != null) return m.Problem;
            if (c.Port is <= 0 or > 65535) return Loc.T("Launch_BadPort");
            if (_appState.IsPortTaken(c.Port)) return Loc.F("Launch_PortTaken", c.Port);
            return null;
        }
        if (c.IsLlama)
        {
            if (!LlamaCppService.Installed) return Loc.T("Launch_NoLlama");
            if (m.Problem != null) return m.Problem;
            if (c.Port is <= 0 or > 65535) return Loc.T("Launch_BadPort");
            if (_appState.IsPortTaken(c.Port)) return Loc.F("Launch_PortTaken", c.Port);
            if (Gpus.Count > 0 && SelectedGpus.Count == 0) return Loc.T("Launch_NoGpuSelected");
            return null;
        }
        if (!_python.VllmInstalled) return Loc.T("Launch_NoVllm");
        // Environnement Python : incohérence connue -> on n'essaie même pas
        var env = _environmentChecker.StaticCheck();
        if (!env.Ok) return Loc.F("Launch_EnvBroken", env.Problems[0]);
        if (_environmentChecker.CachedSmoke() is { Ok: false } smoke) return Loc.F("Launch_EnvBroken", smoke.Error ?? "?");
        if (m.Problem != null) return m.Problem;
        if (c.Port is <= 0 or > 65535) return Loc.T("Launch_BadPort");
        if (_appState.IsPortTaken(c.Port)) return Loc.F("Launch_PortTaken", c.Port);
        if (c.Omni && _python.OmniVersion == null) return Loc.T("Launch_NoOmni");

        var gpuCount = Gpus.Count == 0 ? 1 : SelectedGpus.Count;
        if (Gpus.Count > 0 && gpuCount == 0) return Loc.T("Launch_NoGpuSelected");
        if (c.TensorParallel * c.PipelineParallel > gpuCount)
            return Loc.F("Launch_TooFewGpus", c.TensorParallel, c.PipelineParallel, gpuCount);
        if (m.AttentionHeads is { } heads && heads % c.TensorParallel != 0)
            return Loc.F("Launch_TpHeads", heads, c.TensorParallel);
        // Options que ce vLLM ne connaît pas (vLLM-Omni ajoute les siennes : pas de vérification)
        if (!c.Omni && _vllmOptions.Unknown(c.BuildArguments(_vllmOptions).Skip(2)) is { Count: > 0 } unknown)
            return Loc.F("Launch_UnknownOptions", string.Join(", ", unknown), _python.VllmVersion ?? "?");
        return null;
    }

    [RelayCommand]
    private void Launch()
    {
        LaunchError = "";
        if (SelectedModel is not { IsBroken: false } m || ToConfig() is not { } config) return;
        if (Validate(config, m) is { } error) { LaunchError = error; return; }

        SaveProfile(m);
        StartServer(config);
        // Le prochain lancement propose le port suivant
        Port = _appState.NextFreePort(config.Port + 1).ToString();
    }

    private void StartServer(LaunchConfig config)
    {
        var inst = _instanceFactory.Create(config);
        _appState.AddInstance(inst);
        inst.Start();
        SelectedInstance = inst;
    }

    // ---------------------------------------------------------------- Favoris

    [RelayCommand]
    private void SaveFavorite()
    {
        if (ToConfig() is not { } c) return;
        var name = string.IsNullOrWhiteSpace(FavoriteName) ? $"{c.ServedName} :{c.Port}" : FavoriteName.Trim();
        _appState.AddFavorite(name, c);
        FavoriteName = "";
        FavoriteStatus = Loc.F("Fav_Added", name);
    }

    [RelayCommand]
    private void LoadFavorite(FavoriteConfig? fav)
    {
        if (fav != null) ApplyConfig(fav.Config.Clone());
    }

    /// <summary> Lance directement un favori (port suivant si le sien est pris). </summary>
    [RelayCommand]
    private void LaunchFavorite(FavoriteConfig? fav)
    {
        if (fav == null) return;
        ApplyConfig(fav.Config.Clone());
        if (LaunchError.Length > 0) return;
        if (SelectedModel is not { IsBroken: false } m || ToConfig() is not { } config) return;
        if (Validate(config, m) is { } error) { LaunchError = error; return; }
        StartServer(config);
    }

    [RelayCommand]
    private void DeleteFavorite(FavoriteConfig? fav)
    {
        if (fav != null) _appState.RemoveFavorite(fav);
    }

    // ---------------------------------------------------------------- Divers

    [RelayCommand]
    private void ToggleAdvanced() => ShowAdvanced = !ShowAdvanced;

    [RelayCommand]
    private void GoToModels() => _appState.Navigate("Models");

    [RelayCommand]
    private void GoToSetup() => _appState.Navigate("Setup");

    /// <summary> L'erreur de lancement vient de l'environnement Python (bouton vers Installation). </summary>
    public bool LaunchErrorIsEnv => LaunchError.StartsWith(Loc.F("Launch_EnvBroken", "").TrimEnd());
    partial void OnLaunchErrorChanged(string value) => OnPropertyChanged(nameof(LaunchErrorIsEnv));

    [RelayCommand]
    private Task CopyUrl(InstanceViewModel? inst) => inst == null ? Task.CompletedTask : _ui.CopyAsync(inst.ApiUrl);

    [RelayCommand]
    private Task CopyLogs(InstanceViewModel? inst) => inst == null ? Task.CompletedTask : _ui.CopyAsync(inst.AllLogs());

    [RelayCommand]
    private Task CopyInstanceCommand(InstanceViewModel? inst) => inst == null ? Task.CompletedTask : _ui.CopyAsync(inst.CommandLine);

    [RelayCommand]
    private void OpenDocs(InstanceViewModel? inst)
    {
        if (inst != null) _ui.Open($"{inst.LocalUrl}/docs");
    }

    [RelayCommand]
    private void TestInChat(InstanceViewModel? inst)
    {
        if (inst == null) return;
        ChatTarget?.Invoke(inst);   // la fenêtre choisit la page (Chat, Images ou Audio) et y navigue
    }

    /// <summary> Branché par MainWindowViewModel pour pré-sélectionner l'instance dans la page Tester. </summary>
    public Action<InstanceViewModel>? ChatTarget { get; set; }
}
