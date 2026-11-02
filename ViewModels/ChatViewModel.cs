/*
    Fichier      :  ChatViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Chat : discussion avec un modèle de texte lancé, via l'API compatible OpenAI
        (/v1/chat/completions en streaming). Les conversations sont rangées dans des dossiers et gardées
        entre les sessions (ChatStore). Réflexion du modèle activable quand le modèle sait la désactiver
        (chat_template avec enable_thinking). Affiche la vitesse de génération et, en direct, l'occupation du
        contexte (tokens de la discussion / taille maximale du contexte du serveur).
        Modèle vision : images jointes aux messages (data URI, format OpenAI image_url).
        Outils : tableau JSON déclaré dans les réglages (envoyé en « tools ») ; les appels reçus sont affichés, pas exécutés.
        Images (vLLM-Omni, stable-diffusion.cpp) et synthèse vocale ont leur propre page : ImagesViewModel, AudioViewModel.
        Base de connaissances (RAG) : si la conversation en a une, les passages pertinents pour le message sont cherchés
        avant l'envoi, passés au modèle dans un bloc <context> (ce message seulement) et listés sous la réponse.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class ChatMessage : ObservableObject
{
    /// <summary> Rôle : "user" ou "assistant". </summary>
    public required string Role { get; init; }
    [ObservableProperty] private string _content = "";
    [ObservableProperty] private string _reasoning = "";
    [ObservableProperty] private string _stats = "";

    /// <summary> Images du message : data URI envoyées au modèle, et vignettes affichées. </summary>
    public List<string> ImageUrls { get; } = new();
    /// <summary> Images jointes ou générées. </summary>
    public ObservableCollection<Bitmap> Images { get; } = new();
    /// <summary> Le message contient des images. </summary>
    public bool HasImages => Images.Count > 0;

    /// <summary> Image générée enregistrée sur le disque (page Images). </summary>
    public string? SavedPath { get; set; }

    /// <summary> Documents joints, déjà convertis en Markdown (envoyés au modèle avant le texte du message). </summary>
    public List<(string Name, string Markdown)> Documents { get; } = new();
    /// <summary> Noms des documents joints. </summary>
    public List<string> DocumentNames => Documents.Select(d => d.Name).ToList();
    /// <summary> Le message contient des documents. </summary>
    public bool HasDocuments => Documents.Count > 0;

    /// <summary> Bloc &lt;context&gt; du RAG, envoyé avec ce message seulement (jamais enregistré, jamais renvoyé au tour suivant). </summary>
    public string? ContextBlock { get; set; }

    /// <summary> Passages de la base citée (enregistrés avec la réponse). </summary>
    public List<RagHit> Sources { get; set; } = new();
    /// <summary> Passages affichés sous la réponse. </summary>
    public ObservableCollection<KnowledgeResultView> SourceViews { get; } = new();
    /// <summary> La réponse cite des passages. </summary>
    public bool HasSources => SourceViews.Count > 0;

    /// <summary> Remplace les passages du message (affichage et enregistrement). </summary>
    public void SetSources(IEnumerable<RagHit> hits)
    {
        Sources = hits.ToList();
        SourceViews.Clear();
        foreach (var h in Sources) SourceViews.Add(KnowledgeViewModel.ToView(h));
        OnPropertyChanged(nameof(HasSources));
    }

    /// <summary> Contenu envoyé au modèle : documents entre balises, contexte RAG, puis le message. </summary>
    public string ApiContent
    {
        get
        {
            var parts = new List<string>();
            if (Documents.Count > 0)
                parts.Add(string.Join("\n\n", Documents.Select(d => $"<document name=\"{d.Name}\">\n{d.Markdown.Trim()}\n</document>")));
            if (!string.IsNullOrEmpty(ContextBlock)) parts.Add(ContextBlock);
            if (Content.Length > 0) parts.Add(Content);
            return string.Join("\n\n", parts);
        }
    }

    private readonly IUiServices _ui;

    /// <summary> Crée un message vide. </summary>
    /// <param name="ui"> Presse-papiers (copie) et ouverture des images. </param>
    public ChatMessage(IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(ui);
        _ui = ui;
        Images.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasImages));
    }

    /// <summary> Réflexion du modèle : repliée par défaut, dépliée au clic. </summary>
    [ObservableProperty] private bool _reasoningExpanded;

    /// <summary> En-tête de la réflexion : « en cours » tant que la réponse n'a pas commencé, puis sa longueur. </summary>
    public string ReasoningHeader => Content.Length == 0
        ? Loc.T("Chat_ReasoningRunning")
        : Loc.F("Chat_ReasoningDone", Reasoning.Split((char[])[' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries).Length);
    /// <summary> Glyphe de la flèche du raisonnement (ouvert / fermé). </summary>
    public string ReasoningCaret => ReasoningExpanded ? "" : "";
    partial void OnReasoningExpandedChanged(bool value) => OnPropertyChanged(nameof(ReasoningCaret));
    partial void OnContentChanged(string value)
    {
        OnPropertyChanged(nameof(ReasoningHeader));
        RefreshDisplay();
    }

    /// <summary>
    /// Texte affiché : le Markdown de la réponse, où les blocs ```mermaid sont remplacés par l'image du diagramme
    /// dès qu'elle est prête (bloc fermé, même si la réponse continue). Content garde le texte d'origine (envoyé au modèle, copié).
    /// </summary>
    [ObservableProperty] private string _displayContent = "";
    [ObservableProperty] private string _diagramStatus = "";

    /// <summary> Diagrammes dessinés (code → image PNG). </summary>
    public Dictionary<string, string> Diagrams { get; } = new();
    /// <summary> Codes déjà envoyés au dessin pendant la réponse (un diagramme invalide n'est pas relancé à chaque token). </summary>
    public HashSet<string> DiagramsTried { get; } = new();
    /// <summary> Dessin en cours pendant la réponse (un seul lancement à la fois). </summary>
    public Task DiagramsTask { get; set; } = Task.CompletedTask;

    /// <summary> Formules LaTeX dessinées (formule → image PNG). </summary>
    public Dictionary<LatexService.Formula, string> Formulas { get; } = new();

    /// <summary> Recalcule le texte affiché (diagrammes et formules déjà dessinés remplacés par leur image). </summary>
    public void RefreshDisplay()
    {
        var text = Diagrams.Count == 0 ? Content
            : ChatViewModel.MermaidBlockRegex().Replace(Content, m => Diagrams.TryGetValue(m.Groups[1].Value, out var png)
                ? $"![{Loc.T("Mermaid_Alt")}]({new Uri(png).AbsoluteUri})" : m.Value);
        DisplayContent = Formulas.Count == 0 ? text : LatexService.Substitute(text, f => Formulas.GetValueOrDefault(f));
    }
    partial void OnReasoningChanged(string value) { OnPropertyChanged(nameof(HasReasoning)); OnPropertyChanged(nameof(ReasoningHeader)); }

    [RelayCommand]
    private void ToggleReasoning() => ReasoningExpanded = !ReasoningExpanded;
    [ObservableProperty] private bool _copied;

    /// <summary> Copie la réponse (texte Markdown d'origine) dans le presse-papiers. </summary>
    [RelayCommand]
    private async Task CopyAsync()
    {
        await _ui.CopyAsync(Content);
        Copied = true;
        await Task.Delay(1500);
        Copied = false;
    }

    [RelayCommand]
    private void OpenImage()
    {
        if (SavedPath != null && File.Exists(SavedPath)) _ui.Open(SavedPath);
    }

    /// <summary> Message de l'utilisateur. </summary>
    public bool IsUser => Role == "user";
    /// <summary> Réponse du modèle. </summary>
    public bool IsAssistant => Role == "assistant";
    /// <summary> La réponse contient un raisonnement. </summary>
    public bool HasReasoning => Reasoning.Length > 0;
}

public sealed partial class ChatViewModel : ViewModelBase
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary> Serveurs de texte (ni synthèse vocale, ni génération d'images) : cibles de la page Chat. </summary>
    public ObservableCollection<InstanceViewModel> ChatTargets { get; } = new();
    /// <summary> Messages de la conversation ouverte. </summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary> Images jointes au prochain message. </summary>
    public ObservableCollection<PendingImage> PendingImages { get; } = new();
    /// <summary> Des images attendent d'être envoyées. </summary>
    public bool HasPendingImages => PendingImages.Count > 0;
    /// <summary> Le serveur choisi accepte des images. </summary>
    public bool CanAttachImages => Target?.AcceptsImages == true;

    /// <summary> Documents joints au prochain message (txt, md, ou convertis en Markdown par markitdown). </summary>
    public ObservableCollection<PendingDocument> PendingDocuments { get; } = new();
    /// <summary> Des documents attendent d'être envoyés. </summary>
    public bool HasPendingDocuments => PendingDocuments.Count > 0;
    /// <summary> Documents joints : un serveur de texte est choisi. </summary>
    public bool CanAttachDocuments => Target != null;

    /// <summary> Bases de connaissances proposées (première entrée : aucune). </summary>
    public ObservableCollection<KnowledgeOption> KnowledgeOptions { get; } = new();
    /// <summary> Base interrogée pour cette conversation. </summary>
    [ObservableProperty] private KnowledgeOption? _knowledge;

    /// <summary> Le modèle sait activer et désactiver sa réflexion (chat_template avec enable_thinking). </summary>
    [ObservableProperty] private bool _thinkSupported;
    /// <summary> Réflexion demandée pour cette conversation. </summary>
    [ObservableProperty] private bool _thinkEnabled = true;

    [ObservableProperty] private InstanceViewModel? _target;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private string _systemPrompt = "";
    [ObservableProperty] private double _temperature = 0.7;
    [ObservableProperty] private double _topP = 1.0;
    [ObservableProperty] private double _topK = -1.0;
    [ObservableProperty] private double _minP = 0.0;
    [ObservableProperty] private double _frequencyPenalty = 0.0;
    [ObservableProperty] private double _presencePenalty = 0.0;
    [ObservableProperty] private double _repeatPenalty = 1.0;
    [ObservableProperty] private string _maxTokens = "1024";
    /// <summary> Outils déclarés (tableau JSON au format OpenAI). Vide = aucun outil envoyé. </summary>
    [ObservableProperty] private string _toolsJson = "";
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _error = "";

    // ---------------------------------------------------------------- Conversations

    /// <summary> Groupes du menu : dossiers, puis conversations sans dossier. </summary>
    public ObservableCollection<ChatGroupViewModel> Groups { get; } = new();
    /// <summary> Choix des dossiers pour « Déplacer » (sans dossier en premier). </summary>
    public ObservableCollection<ChatFolderOption> FolderOptions { get; } = new();
    /// <summary> Filtre du menu (titre). </summary>
    [ObservableProperty] private string _searchText = "";
    /// <summary> Titre de la conversation ouverte. </summary>
    [ObservableProperty] private string _currentTitle = "";
    /// <summary> Menu des conversations affiché. </summary>
    [ObservableProperty] private bool _isSidebarOpen = true;

    /// <summary> Maj ou Ctrl maintenu : les lignes du menu affichent une corbeille (suppression sans confirmation). </summary>
    [ObservableProperty] private bool _quickDeleteArmed;
    /// <summary> Réglages (température, tokens, prompt système) affichés. </summary>
    [ObservableProperty] private bool _isSettingsOpen;

    /// <summary> Au moins un serveur est prêt. </summary>
    public bool HasReadyInstance => Instances.Any(i => i.IsReady);

    /// <summary> Serveurs disponibles (tous). </summary>
    public ObservableCollection<InstanceViewModel> Instances => _appState.Instances;

    // ---------------------------------------------------------------- Contexte en direct

    /// <summary> Taille maximale du contexte du serveur (null = inconnue). </summary>
    [ObservableProperty] private int? _contextMax;
    /// <summary> Tokens de la discussion (prompt + réponses), d'après le serveur. </summary>
    [ObservableProperty] private int _contextUsed;
    [ObservableProperty] private string _contextText = "";
    [ObservableProperty] private string _contextHint = "";
    [ObservableProperty] private string _contextClass = "ok";
    /// <summary> Part du contexte occupée par la conversation (%). </summary>
    public double ContextPercent => ContextMax is > 0 and var max ? Math.Min(100, 100.0 * ContextUsed / max) : 0;
    /// <summary> Part du contexte occupée avec le message en cours de saisie (%). </summary>
    public double DraftPercent => ContextMax is > 0 and var max ? Math.Min(100, 100.0 * (ContextUsed + DraftTokens) / max) : 0;
    /// <summary> La jauge de contexte est affichée. </summary>
    public bool HasContext => Target is { IsReady: true };

    /// <summary>
    /// Message en cours d'écriture : texte (≈ 4 caractères par token) + documents joints (compte exact du
    /// tokenizer du serveur quand il est connu, sinon estimation) + balises qui les entourent.
    /// </summary>
    private int DraftTokens =>
        (string.IsNullOrWhiteSpace(Input) ? 0 : Input.Length / 4 + 8)
        + PendingDocuments.Where(d => d.Markdown != null).Sum(d => d.Tokens + 12);

    /// <summary> Texte sous la zone de saisie : tokens du message en cours (vide si rien). </summary>
    public string DraftText => DraftTokens > 0 ? Loc.F("Chat_CtxDraft", DraftTokens) : "";

    partial void OnContextUsedChanged(int value) => UpdateContext();
    partial void OnContextMaxChanged(int? value) => UpdateContext();
    partial void OnInputChanged(string value) => UpdateContext();
    partial void OnMaxTokensChanged(string value) => UpdateContext();

    private void UpdateContext()
    {
        OnPropertyChanged(nameof(ContextPercent));
        OnPropertyChanged(nameof(DraftPercent));
        OnPropertyChanged(nameof(HasContext));
        OnPropertyChanged(nameof(DraftText));
        var used = ContextUsed.ToString("N0", CultureInfo.CurrentCulture);
        if (ContextMax is not { } max)
        {
            ContextText = Loc.F("Chat_CtxUnknown", used);
            (ContextHint, ContextClass) = ("", "ok");
            return;
        }
        var pct = 100.0 * ContextUsed / max;
        ContextText = Loc.F("Chat_Ctx", used, max.ToString("N0", CultureInfo.CurrentCulture), pct);
        var left = max - ContextUsed - DraftTokens;
        var answer = int.TryParse(MaxTokens, out var mt) && mt > 0 ? mt : 0;
        // Pas d'avertissement tant que la discussion + le message en cours restent sous 75 % du contexte
        if (ContextUsed + DraftTokens < 0.75 * max && answer <= left)
            (ContextHint, ContextClass) = ("", "ok");
        else if (left <= 0)
            (ContextHint, ContextClass) = (Loc.T("Chat_CtxFull"), "error");
        else if (answer > left)
            (ContextHint, ContextClass) = (Loc.F("Chat_CtxCut", left.ToString("N0", CultureInfo.CurrentCulture), answer), "warn");
        else if (100.0 * (ContextUsed + DraftTokens) / max >= 75)
            (ContextHint, ContextClass) = (Loc.T("Chat_CtxAlmost") + (DraftTokens > 0 ? " " + Loc.F("Chat_CtxDraft", DraftTokens) : ""), "warn");
        else
            (ContextHint, ContextClass) = (DraftTokens > 0 ? Loc.F("Chat_CtxDraft", DraftTokens) : "", "ok");
    }

    /// <summary>
    /// Taille du contexte, et capacité de réflexion, du serveur : /props (llama.cpp) ou /v1/models (vLLM) pour la taille,
    /// chat_template du modèle pour la réflexion (enable_thinking).
    /// </summary>
    private async Task LoadTargetInfoAsync(InstanceViewModel t)
    {
        int? max = t.Config.IsLlama ? null : t.Config.MaxModelLen;
        bool think = false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (t.Config.IsLlama)
            {
                // llama-server : contexte d'une requête (le contexte total est partagé entre les "slots")
                using var doc = JsonDocument.Parse(await GetAsync(t, "/props", cts.Token));
                if (doc.RootElement.TryGetProperty("default_generation_settings", out var g) && g.TryGetProperty("n_ctx", out var n))
                    max = n.GetInt32();
                if (doc.RootElement.TryGetProperty("chat_template", out var tpl) && tpl.ValueKind == JsonValueKind.String)
                    think = tpl.GetString()!.Contains("enable_thinking", StringComparison.Ordinal);
            }
            else
            {
                if (max == null)
                {
                    using var doc = JsonDocument.Parse(await GetAsync(t, "/v1/models", cts.Token));
                    foreach (var m in doc.RootElement.GetProperty("data").EnumerateArray())
                        if (m.TryGetProperty("max_model_len", out var l) && l.ValueKind == JsonValueKind.Number) { max = l.GetInt32(); break; }
                }
                // vLLM : le modèle est sur le disque (son chat_template y est écrit)
                var root = t.Config.ModelTarget;
                think = await Task.Run(() => ChatParsers.ReadChatTemplate(root).Contains("enable_thinking", StringComparison.Ordinal));
            }
        }
        catch { /* serveur pas prêt : on garde la valeur de la configuration */ }
        if (max is null && t.Config.IsLlama) max = t.Config.MaxModelLen;
        if (Target == t)
        {
            ContextMax = max;
            ThinkSupported = think;
            // Réponse limitée par défaut à la moitié du contexte maximal
            if (max is > 0) MaxTokens = (max.Value / 2).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static async Task<string> GetAsync(InstanceViewModel t, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, t.LocalUrl + path);
        if (t.ApiKey != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.ApiKey);
        using var resp = await Http.SendAsync(req, ct);
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary> Température affichée. </summary>
    public string TemperatureText => Temperature.ToString("0.00");

    private CancellationTokenSource? _cts;

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly IRagStore _ragStore;
    private readonly IRagSearch _ragSearch;
    private readonly IMarkItDownService _markItDownService;
    private readonly IMermaidService _mermaidService;
    private readonly ILatexService _latexService;
    private readonly IUiServices _ui;
    private readonly IChatStore _store;
    private readonly ChatIndex _index;
    private readonly Dictionary<string, ConversationItemViewModel> _items = new();
    private readonly Avalonia.Threading.DispatcherTimer _saveTimer;
    private ConversationItemViewModel? _current;
    private bool _loadingConversation;
    private bool _targetWasReady;

    /// <summary> Crée le ViewModel et ouvre la dernière conversation (ou une nouvelle). </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    /// <param name="appState"> État partagé entre les pages. </param>
    /// <param name="markItDownService"> Conversion des documents joints. </param>
    /// <param name="mermaidService"> Rendu des diagrammes. </param>
    /// <param name="latexService"> Rendu des formules LaTeX. </param>
    /// <param name="ui"> Services de l'interface (fichiers, presse-papiers, liens). </param>
    /// <param name="store"> Conversations enregistrées. </param>
    /// <param name="ragStore"> Bases de connaissances (choix dans le chat). </param>
    /// <param name="ragSearch"> Recherche dans les bases avant chaque message. </param>
    public ChatViewModel(ISettingsService settings, IAppState appState, IMarkItDownService markItDownService, IMermaidService mermaidService,
        ILatexService latexService, IUiServices ui, IChatStore store, IRagStore ragStore, IRagSearch ragSearch)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(ragStore);
        ArgumentNullException.ThrowIfNull(ragSearch);
        _ragStore = ragStore;
        _ragSearch = ragSearch;
        // Une base créée ou indexée ailleurs (page Connaissances, tâche de fond) apparaît tout de suite dans le chat
        _ragStore.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshKnowledgeOptions);
        ArgumentNullException.ThrowIfNull(markItDownService);
        ArgumentNullException.ThrowIfNull(mermaidService);
        ArgumentNullException.ThrowIfNull(latexService);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(store);
        _settings = settings;
        _appState = appState;
        _markItDownService = markItDownService;
        _mermaidService = mermaidService;
        _latexService = latexService;
        _ui = ui;
        _store = store;
        _index = store.LoadIndex();

        _saveTimer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveCurrent();

        _appState.InstancesChanged += OnInstancesChanged;
        // Diagrammes : redessinés dans le nouveau thème
        if (Avalonia.Application.Current is { } app) app.ActualThemeVariantChanged += (_, _) => OnThemeChanged();
        PendingImages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPendingImages));
        PendingDocuments.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(HasPendingDocuments)); UpdateContext(); };

        // Conversations : index chargé, puis la dernière ouverte
        foreach (var m in _index.Conversations) _items[m.Id] = new ConversationItemViewModel(m, this);
        RebuildFolderOptions();
        SyncTargets();
        var last = _index.OpenId is { } openId && _items.TryGetValue(openId, out var o) ? o
                 : _items.Values.OrderByDescending(i => i.Meta.UpdatedAt).FirstOrDefault();
        if (last != null) OpenConversation(last);
        else NewConversation(null);
        RebuildGroups();
    }

    private void OnInstancesChanged()
    {
        SyncTargets();
        OnPropertyChanged(nameof(HasReadyInstance));
        if (Target is not { IsReady: true }) Target = ChatTargets.FirstOrDefault(i => i.IsReady) ?? Target;
        if (Target is { IsReady: true } t && !_targetWasReady) _ = LoadTargetInfoAsync(t);
        OnPropertyChanged(nameof(HasContext));
        _targetWasReady = Target?.IsReady == true;
    }

    /// <summary> Liste des serveurs de texte, mise à jour quand les serveurs changent. </summary>
    private void SyncTargets()
    {
        var list = Instances.Where(i => !i.IsTts && !i.IsImageGen && !i.IsEmbedding).ToList();
        if (list.SequenceEqual(ChatTargets)) return;
        ChatTargets.Clear();
        foreach (var i in list) ChatTargets.Add(i);
    }

    partial void OnTargetChanged(InstanceViewModel? value)
    {
        ContextMax = null;
        ThinkSupported = false;
        ContextUsed = EstimateTokens();
        if (value is { IsReady: true }) _ = LoadTargetInfoAsync(value);
        _targetWasReady = value?.IsReady == true;
        OnPropertyChanged(nameof(CanAttachImages));
        OnPropertyChanged(nameof(CanAttachDocuments));
        OnPropertyChanged(nameof(HasContext));
        if (!CanAttachImages) PendingImages.Clear();
    }

    /// <summary> Le choix de base est enregistré avec la conversation. </summary>
    partial void OnKnowledgeChanged(KnowledgeOption? value)
    {
        if (_loadingConversation || _current == null) return;
        _current.Meta.KnowledgeBaseId = value?.Id;
        SaveIndex();
    }

    /// <summary> Liste des bases (recharge depuis le disque) ; la base de la conversation ouverte est sélectionnée. </summary>
    public void RefreshKnowledgeOptions()
    {
        var wanted = _current?.Meta.KnowledgeBaseId;
        var options = new List<KnowledgeOption> { new(null, Loc.T("Rag_NoBaseOption")) };
        options.AddRange(_ragStore.List().Select(b => new KnowledgeOption(b.Id, b.Name)));
        _loadingConversation = true;
        try
        {
            KnowledgeOptions.Clear();
            foreach (var o in options) KnowledgeOptions.Add(o);
            Knowledge = KnowledgeOptions.FirstOrDefault(o => o.Id == wanted) ?? KnowledgeOptions[0];
        }
        finally { _loadingConversation = false; }
    }

    partial void OnSearchTextChanged(string value) => RebuildGroups();
    partial void OnSystemPromptChanged(string value)
    {
        if (_loadingConversation || _current == null) return;
        _current.Meta.SystemPrompt = value;
        ScheduleSave();
    }

    /// <summary> Le bouton Réflexion est un choix : enregistré tout de suite. </summary>
    partial void OnThinkEnabledChanged(bool value)
    {
        if (_loadingConversation || _current == null) return;
        _current.Meta.Think = value;
        SaveIndex();
    }
    partial void OnTemperatureChanged(double value) => OnPropertyChanged(nameof(TemperatureText));

    /// <summary> Choisit le serveur avec lequel discuter (bouton « Tester » de la page Serveurs). </summary>
    public void SelectTarget(InstanceViewModel inst) => Target = inst;

    /// <summary> Tokens de la discussion, estimés (≈ 4 caractères par token) tant que le serveur n'a pas compté. </summary>
    private int EstimateTokens() => Messages.Sum(m => (m.Content.Length + m.Reasoning.Length) / 4 + 8)
                                   + (string.IsNullOrEmpty(SystemPrompt) ? 0 : SystemPrompt.Length / 4 + 8);

    // ---------------------------------------------------------------- Conversations : ouverture, création, enregistrement

    /// <summary> Ouvre une conversation du menu (la conversation précédente est enregistrée, ou supprimée si elle est vide). </summary>
    internal void OpenConversation(ConversationItemViewModel item)
    {
        if (_current == item || IsGenerating) return;
        LeaveCurrent();
        _current = item;
        foreach (var i in _items.Values) i.IsCurrent = i == item;
        CurrentTitle = item.Title;

        _loadingConversation = true;
        try
        {
            SystemPrompt = item.Meta.SystemPrompt;
            ThinkEnabled = item.Meta.Think;
        }
        finally { _loadingConversation = false; }

        Messages.Clear();
        PendingImages.Clear();
        PendingDocuments.Clear();
        Input = "";
        Error = "";
        RefreshKnowledgeOptions();
        foreach (var r in _store.LoadRecord(item.Id)?.Messages ?? []) Messages.Add(MessageFrom(r));
        // Diagrammes déjà fermés : redessinés (sans appel au modèle)
        foreach (var m in Messages.Where(m => m.IsAssistant))
        {
            RenderReadyDiagrams(m);
            _ = RenderFormulasAsync(m, Task.CompletedTask, install: false);
        }
        ContextUsed = EstimateTokens();
        SaveIndex();
    }

    /// <summary> Crée une conversation (dans ce dossier, ou sans dossier). Une conversation vide déjà ouverte est réutilisée. </summary>
    /// <param name="folderId"> Dossier (null = sans dossier). </param>
    public void NewConversation(string? folderId)
    {
        if (IsGenerating) return;
        if (_current is { } cur && Messages.Count == 0 && cur.Meta.FolderId == folderId) return;
        LeaveCurrent();
        var meta = new ChatMeta { Title = Loc.T("Chat_NewConversation"), FolderId = folderId, Think = true, UpdatedAt = DateTime.UtcNow };
        var item = new ConversationItemViewModel(meta, this);
        _items[meta.Id] = item;
        OpenConversation(item);
        RebuildGroups();
    }

    /// <summary> Quitte la conversation ouverte : enregistrée, ou supprimée si elle n'a aucun message. </summary>
    private void LeaveCurrent()
    {
        if (_current == null) return;
        if (Messages.Count == 0)
        {
            var empty = _current;
            _current = null;
            RemoveConversation(empty);
            return;
        }
        SaveCurrent();
        _current = null;
    }

    /// <summary> Enregistre la conversation ouverte (messages, réglages, index). </summary>
    /// <param name="touch"> Met la date de modification à maintenant (tri du menu). </param>
    private void SaveCurrent(bool touch = false)
    {
        _saveTimer.Stop();
        if (_current == null || _loadingConversation) return;
        var record = new ChatRecord { Messages = Messages.Select(ToRecord).ToList() };
        _store.SaveRecord(_current.Id, record);
        _current.Meta.SystemPrompt = SystemPrompt;
        _current.Meta.Think = ThinkEnabled;
        if (touch) _current.Touch();
        SaveIndex();
    }

    /// <summary> Réglages modifiés sans nouveau message : enregistrement différé (frappe au clavier). </summary>
    private void ScheduleSave()
    {
        if (_loadingConversation) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary> Écrit l'index (dossiers, titres, dates, conversation ouverte). </summary>
    private void SaveIndex()
    {
        _index.Conversations = _items.Values.Select(i => i.Meta).ToList();
        _index.OpenId = _current?.Id;
        _store.SaveIndex(_index);
    }

    private void RemoveConversation(ConversationItemViewModel item)
    {
        _items.Remove(item.Id);
        _store.DeleteRecord(item.Id);
        SaveIndex();
        RebuildGroups();
    }

    internal void RenameConversation(ConversationItemViewModel item, string name)
    {
        item.Title = name;
        item.Meta.TitleSet = true;
        if (item == _current) CurrentTitle = name;
        SaveIndex();
    }

    internal void MoveConversation(ConversationItemViewModel item, string? folderId)
    {
        item.Meta.FolderId = folderId;
        SaveIndex();
        RebuildGroups();
    }

    /// <summary>
    /// Dépôt par glisser-déposer : la conversation rejoint le dossier (null = sans dossier).
    /// Un dossier replié est déplié, pour que la conversation déposée reste visible.
    /// </summary>
    internal void MoveConversationTo(string id, string? folderId)
    {
        if (!_items.TryGetValue(id, out var item) || item.Meta.FolderId == folderId) return;
        if (folderId != null) SetFolderExpanded(folderId, true);
        MoveConversation(item, folderId);
    }

    /// <summary> Met en évidence le groupe visé par le glisser-déposer (null = aucun). </summary>
    internal void SetDropTarget(ChatGroupViewModel? target)
    {
        foreach (var g in Groups) g.IsDropTarget = g == target;
    }

    partial void OnQuickDeleteArmedChanged(bool value)
    {
        foreach (var g in Groups)
        {
            g.RefreshQuickDelete();
            foreach (var i in g.Items) i.RefreshQuickDelete();
        }
    }

    /// <summary> Supprime une conversation et ses messages. Si elle est ouverte, la plus récente prend sa place. </summary>
    internal void DeleteConversation(ConversationItemViewModel item)
    {
        var wasCurrent = _current == item;
        // Pendant une réponse, la conversation ouverte reste en place (sinon la réponse irait dans la mauvaise)
        if (wasCurrent && IsGenerating) { Error = Loc.T("Chat_BusyDelete"); return; }
        if (wasCurrent)
        {
            _current = null;
            Messages.Clear();
        }
        _items.Remove(item.Id);
        _store.DeleteRecord(item.Id);
        if (wasCurrent)
        {
            var next = _items.Values.OrderByDescending(i => i.Meta.UpdatedAt).FirstOrDefault();
            SaveIndex();
            RebuildGroups();
            if (next != null) OpenConversation(next);
            else NewConversation(null);
            return;
        }
        SaveIndex();
        RebuildGroups();
    }

    /// <summary> Crée un dossier, nommé tout de suite par l'utilisateur. </summary>
    [RelayCommand]
    private void NewFolder()
    {
        var folder = new ChatFolder { Name = Loc.T("Chat_NewFolder") };
        _index.Folders.Add(folder);
        RebuildFolderOptions();
        SaveIndex();
        RebuildGroups();
        Groups.FirstOrDefault(g => g.FolderId == folder.Id)?.BeginRenameCommand.Execute(null);
    }

    internal void RenameFolder(string id, string name)
    {
        if (_index.Folders.FirstOrDefault(f => f.Id == id) is not { } folder) return;
        folder.Name = name;
        RebuildFolderOptions();
        SaveIndex();
        RebuildGroups();
    }

    /// <summary> Supprime un dossier ; ses conversations restent, sans dossier. </summary>
    internal void DeleteFolder(string id)
    {
        foreach (var i in _items.Values.Where(i => i.Meta.FolderId == id)) i.Meta.FolderId = null;
        _index.Folders.RemoveAll(f => f.Id == id);
        RebuildFolderOptions();
        SaveIndex();
        RebuildGroups();
    }

    internal void SetFolderExpanded(string id, bool expanded)
    {
        if (_index.Folders.FirstOrDefault(f => f.Id == id) is not { } folder || folder.Expanded == expanded) return;
        folder.Expanded = expanded;
        SaveIndex();
    }

    /// <summary> Reconstruit le menu : dossiers (leurs conversations, les plus récentes d'abord), puis sans dossier. </summary>
    private void RebuildGroups()
    {
        var filter = SearchText.Trim();
        var searching = filter.Length > 0;
        var visible = _items.Values
            .Where(i => !searching || i.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.Meta.UpdatedAt)
            .ToList();

        Groups.Clear();
        foreach (var f in _index.Folders)
        {
            var g = new ChatGroupViewModel(f.Id, f.Name, f.Expanded, this);
            foreach (var i in visible.Where(i => i.Meta.FolderId == f.Id)) g.Items.Add(i);
            if (!searching || g.Items.Count > 0) Groups.Add(g);
        }
        var unfiled = new ChatGroupViewModel(null, Loc.T("Chat_Unfiled"), true, this, showLabel: _index.Folders.Count > 0);
        foreach (var i in visible.Where(i => i.Meta.FolderId is null || !_index.Folders.Any(f => f.Id == i.Meta.FolderId))) unfiled.Items.Add(i);
        Groups.Add(unfiled);
    }

    private void RebuildFolderOptions()
    {
        FolderOptions.Clear();
        FolderOptions.Add(new ChatFolderOption(null, Loc.T("Chat_Unfiled")));
        foreach (var f in _index.Folders) FolderOptions.Add(new ChatFolderOption(f.Id, f.Name));
    }

    /// <summary> Message enregistré → message affiché (miniatures des images recréées). </summary>
    private ChatMessage MessageFrom(ChatMessageRecord r)
    {
        var m = new ChatMessage(_ui) { Role = r.Role, Content = r.Content, Reasoning = r.Reasoning, Stats = r.Stats };
        if (r.Sources.Count > 0) m.SetSources(r.Sources);
        foreach (var a in r.Attachments)
        {
            if (a.DataUri.Length > 0)
            {
                m.ImageUrls.Add(a.DataUri);
                if (Thumbnail(a.DataUri) is { } thumb) m.Images.Add(thumb);
            }
            else if (a.Name.Length > 0) m.Documents.Add((a.Name, a.Markdown));
        }
        return m;
    }

    private static ChatMessageRecord ToRecord(ChatMessage m)
    {
        var r = new ChatMessageRecord { Role = m.Role, Content = m.Content, Reasoning = m.Reasoning, Stats = m.Stats, Sources = m.Sources.ToList() };
        r.Attachments.AddRange(m.ImageUrls.Select(u => new ChatAttachment { DataUri = u }));
        r.Attachments.AddRange(m.Documents.Select(d => new ChatAttachment { Name = d.Name, Markdown = d.Markdown }));
        return r;
    }

    /// <summary> Miniature d'une image enregistrée en data URI (null si illisible). </summary>
    private static Bitmap? Thumbnail(string dataUri)
    {
        try
        {
            var bytes = Convert.FromBase64String(dataUri[(dataUri.IndexOf(',') + 1)..]);
            using var ms = new MemoryStream(bytes);
            return Bitmap.DecodeToWidth(ms, 160);
        }
        catch (Exception ex) when (ex is FormatException or IOException or ArgumentException)
        {
            return null;   // image illisible : le message reste, sans vignette
        }
    }

    /// <summary> Titre automatique : première ligne du premier message (ou nom du document). </summary>
    private void SetAutoTitle(string text, IEnumerable<string> documentNames)
    {
        if (_current is not { } cur || cur.Meta.TitleSet) return;
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? documentNames.FirstOrDefault() ?? "";
        if (line.Length == 0) return;
        if (line.Length > 60) line = line[..60].TrimEnd() + "…";
        cur.Title = line;
        CurrentTitle = line;
    }

    // ---------------------------------------------------------------- Base de connaissances

    /// <summary>
    /// Passages pertinents de la base pour la question, et leur bloc &lt;context&gt;. Le budget (≈ 30 % du contexte libre)
    /// limite la taille ; sans question, rien n'est ajouté.
    /// </summary>
    private async Task<(List<RagHit>? Hits, string? Block)> SearchKnowledgeAsync(string kbId, string query)
    {
        var cfg = _ragStore.Get(kbId) ?? throw new InvalidOperationException(Loc.T("Rag_NoBase"));
        if (string.IsNullOrEmpty(cfg.EmbeddingModel)) throw new InvalidOperationException(Loc.T("Rag_NotIndexed"));
        if (string.IsNullOrWhiteSpace(query)) return (null, null);
        // Plusieurs serveurs du même modèle (un arrêté, un prêt) : celui qui répond
        var server = Instances.Where(i => i.IsEmbedding && i.ServedName == cfg.EmbeddingModel).OrderByDescending(i => i.IsReady).FirstOrDefault();
        if (server is not { IsReady: true }) throw new InvalidOperationException(Loc.F("Rag_EmbeddingNotRunning", cfg.EmbeddingModel));
        var endpoint = new EmbeddingEndpoint(server.LocalUrl, server.ServedName, server.ApiKey);

        var hits = await Task.Run(() => _ragSearch.SearchAsync(kbId, endpoint, query, 8, CancellationToken.None));
        int budget = ContextMax is { } max ? Math.Clamp((int)((max - ContextUsed) * 0.3), 300, 6000) : 2000;
        var kept = RagContext.WithinBudget(hits, budget);
        return (kept, RagContext.Build(kept, Loc.T("Rag_Instruction"), Loc.T("Rag_NoSource")));
    }

    /// <summary> Ouvre le fichier d'une source (clic sur un passage cité). </summary>
    [RelayCommand]
    private void OpenSource(KnowledgeResultView? source)
    {
        if (source is { Path.Length: > 0 } && File.Exists(source.Path)) _ui.Open(source.Path);
    }

    // ---------------------------------------------------------------- Images et documents joints

    [RelayCommand]
    private async Task AttachImage()
    {
        var files = await _ui.PickFilesAsync(Loc.T("Chat_AttachImage"), "Images", "*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp");
        foreach (var f in files)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(f);
                var mime = Path.GetExtension(f).ToLowerInvariant() switch
                {
                    ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", ".bmp" => "image/bmp", _ => "image/jpeg",
                };
                using var ms = new MemoryStream(bytes);
                PendingImages.Add(new PendingImage(Path.GetFileName(f), $"data:{mime};base64,{Convert.ToBase64String(bytes)}", Bitmap.DecodeToWidth(ms, 160)));
            }
            catch (Exception ex) { Error = $"{Path.GetFileName(f)} : {ex.Message}"; }
        }
    }

    [RelayCommand]
    private async Task AttachDocument()
    {
        var files = await _ui.PickFilesAsync(Loc.T("Chat_AttachDocument"), Loc.T("Chat_Documents"), MarkItDownService.Patterns);
        foreach (var f in files)
        {
            var doc = new PendingDocument(Path.GetFileName(f));
            PendingDocuments.Add(doc);
            _ = ConvertDocumentAsync(doc, f);
        }
    }

    /// <summary> Conversion en arrière-plan (markitdown installé à la première utilisation). </summary>
    private async Task ConvertDocumentAsync(PendingDocument doc, string file)
    {
        try
        {
            var md = await _markItDownService.ConvertAsync(file, s => Avalonia.Threading.Dispatcher.UIThread.Post(() => doc.Status = s), CancellationToken.None);
            Avalonia.Threading.Dispatcher.UIThread.Post(() => { doc.SetMarkdown(md); UpdateContext(); });
            // Compte exact avec le tokenizer du modèle (/tokenize de vLLM et llama.cpp)
            if (Target is { IsReady: true } t && await CountTokensAsync(t, md) is { } exact)
                Avalonia.Threading.Dispatcher.UIThread.Post(() => { doc.SetTokens(exact); UpdateContext(); });
        }
        catch (Exception ex)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() => doc.SetError(ex.Message));
        }
    }

    /// <summary> Nombre de tokens d'un texte selon le serveur (POST /tokenize), null si indisponible. </summary>
    private static async Task<int?> CountTokensAsync(InstanceViewModel t, string text)
    {
        try
        {
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("model", t.ServedName);
                // vLLM lit "prompt", llama-server lit "content"
                w.WriteString("prompt", text);
                w.WriteString("content", text);
                w.WriteBoolean("add_special_tokens", false);
                w.WriteEndObject();
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{t.LocalUrl}/tokenize")
            {
                Content = new ByteArrayContent(ms.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
            };
            if (t.ApiKey != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.ApiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            if (doc.RootElement.TryGetProperty("count", out var c) && c.TryGetInt32(out var n)) return n;
            if (doc.RootElement.TryGetProperty("tokens", out var toks) && toks.ValueKind == JsonValueKind.Array) return toks.GetArrayLength();
            return null;
        }
        catch { return null; }
    }

    [RelayCommand]
    private void RemoveDocument(PendingDocument? doc)
    {
        if (doc != null) PendingDocuments.Remove(doc);
    }

    [RelayCommand]
    private void RemoveImage(PendingImage? img)
    {
        if (img != null) PendingImages.Remove(img);
    }

    [RelayCommand]
    private void Clear()
    {
        Messages.Clear();
        ContextUsed = 0;
        Error = "";
        SaveCurrent(touch: true);
        RebuildGroups();
    }

    [RelayCommand]
    private void StopGeneration() => _cts?.Cancel();

    [RelayCommand]
    private void NewChat() => NewConversation(null);

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    /// <summary> Image ouverte en grand depuis le chat (clic sur une image ou un diagramme) ; null = fermée. </summary>
    [ObservableProperty] private Bitmap? _previewImage;

    /// <summary> La visionneuse est ouverte. </summary>
    public bool IsImagePreviewOpen => PreviewImage != null;

    partial void OnPreviewImageChanged(Bitmap? value) => OnPropertyChanged(nameof(IsImagePreviewOpen));

    /// <summary> Ferme la visionneuse. </summary>
    [RelayCommand]
    private void ClosePreview() => PreviewImage = null;

    [RelayCommand]
    private void ToggleSettings() => IsSettingsOpen = !IsSettingsOpen;

    [RelayCommand]
    private async Task SendAsync()
    {
        Error = "";
        if (IsGenerating || string.IsNullOrWhiteSpace(Input) && PendingImages.Count == 0 && PendingDocuments.Count == 0) return;
        if (PendingDocuments.Any(d => d.IsConverting)) { Error = Loc.T("Chat_DocWait"); return; }
        if (Target is not { IsReady: true } t) { Error = Loc.T("Chat_NoServer"); return; }
        if (!string.IsNullOrWhiteSpace(ToolsJson) && ParseTools(ToolsJson) == null) { Error = Loc.T("Chat_Tools_Invalid"); return; }
        // Serveur en veille : réveil avant la requête
        if (t.IsSleeping && !await t.WakeUpAsync()) { Error = Loc.F("Router_WakeFailed", t.ServedName); return; }
        if (_current == null) NewConversation(null);

        // Base de connaissances : les passages pertinents sont cherchés avant d'écrire le message
        List<RagHit>? sources = null;
        string? contextBlock = null;
        if (Knowledge?.Id is { } kbId)
        {
            IsGenerating = true;
            try { (sources, contextBlock) = await SearchKnowledgeAsync(kbId, Input.Trim()); }
            catch (Exception ex) when (ex is InvalidOperationException or System.Net.Http.HttpRequestException)
            {
                Error = ex.Message;
                return;
            }
            finally { IsGenerating = false; }
        }

        var userMsg = new ChatMessage(_ui) { Role = "user", Content = Input.Trim(), ContextBlock = contextBlock };
        foreach (var img in PendingImages) { userMsg.ImageUrls.Add(img.DataUri); userMsg.Images.Add(img.Thumbnail); }
        PendingImages.Clear();
        foreach (var d in PendingDocuments.Where(d => d.Markdown != null)) userMsg.Documents.Add((d.Name, d.Markdown!));
        PendingDocuments.Clear();
        if (!Messages.Any(m => m.IsUser)) SetAutoTitle(userMsg.Content, userMsg.DocumentNames);
        Messages.Add(userMsg);
        Input = "";
        var answer = new ChatMessage(_ui) { Role = "assistant" };
        if (sources is { Count: > 0 }) answer.SetSources(sources);
        Messages.Add(answer);
        // Le message de l'utilisateur est enregistré tout de suite : il survit à une fermeture pendant la réponse
        SaveCurrent(touch: true);
        RebuildGroups();

        IsGenerating = true;
        _cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        DateTime? firstToken = null;
        int chunks = 0;
        int? completionTokens = null;
        int? promptTokens = null;
        // Estimation tant que le serveur n'a pas donné le compte exact : discussion + message envoyé
        var before = ContextUsed + Messages[^2].Content.Length / 4 + 8;
        // Appels d'outils : les morceaux arrivent par index, le nom et les arguments en plusieurs fois
        var toolCalls = new SortedDictionary<int, (StringBuilder Name, StringBuilder Args)>();

        try
        {
            var body = BuildBody(t);
            // Le contexte RAG n'est envoyé qu'avec ce message : les tours suivants font une nouvelle recherche
            userMsg.ContextBlock = null;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{t.LocalUrl}/v1/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (t.ApiKey != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.ApiKey);

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Error = $"HTTP {(int)resp.StatusCode} : {await resp.Content.ReadAsStringAsync(_cts.Token)}";
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(_cts.Token);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(_cts.Token) is { } line)
            {
                if (!line.StartsWith("data:")) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;

                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("completion_tokens", out var ct)) completionTokens = ct.GetInt32();
                    if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                if (!choices[0].TryGetProperty("delta", out var delta)) continue;

                foreach (var key in new[] { "reasoning_content", "reasoning" })
                    if (delta.TryGetProperty(key, out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        answer.Reasoning += r.GetString();
                        firstToken ??= DateTime.UtcNow;
                        chunks++;
                    }
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    answer.Content += c.GetString();
                    firstToken ??= DateTime.UtcNow;
                    chunks++;
                    RenderReadyDiagrams(answer);
                }
                if (delta.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
                    foreach (var call in calls.EnumerateArray())
                    {
                        var index = call.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : 0;
                        if (!toolCalls.TryGetValue(index, out var entry))
                            toolCalls[index] = entry = (new StringBuilder(), new StringBuilder());
                        if (call.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            if (fn.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String) entry.Name.Append(nm.GetString());
                            if (fn.TryGetProperty("arguments", out var ar) && ar.ValueKind == JsonValueKind.String) entry.Args.Append(ar.GetString());
                        }
                        firstToken ??= DateTime.UtcNow;
                        chunks++;
                    }

                // Contexte en direct : compte exact du serveur s'il l'envoie à chaque morceau, sinon estimation
                ContextUsed = promptTokens is { } p ? p + (completionTokens ?? chunks) : before + chunks;
            }
        }
        catch (OperationCanceledException) { answer.Content += " […]"; }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            // Les appels d'outils sont affichés dans la réponse (rien n'est exécuté)
            foreach (var (name, args) in toolCalls.Values)
                answer.Content += (answer.Content.Length > 0 ? "\n\n" : "")
                    + $"**{Loc.F("Chat_ToolCall", name.ToString())}**\n```json\n{args}\n```";
            IsGenerating = false;
            _cts = null;
            var tokens = completionTokens ?? chunks;
            if (promptTokens is { } pt) ContextUsed = pt + tokens;
            if (firstToken is { } ft && tokens > 0)
            {
                var gen = (DateTime.UtcNow - ft).TotalSeconds;
                answer.Stats = Loc.F("Chat_Stats", tokens, gen > 0 ? tokens / gen : 0, (ft - started).TotalMilliseconds);
            }
            SaveCurrent(touch: true);
            RebuildGroups();
            // Formules après les diagrammes : une seule installation de paquet à la fois dans le venv
            _ = RenderFormulasAsync(answer, RenderDiagramsAsync(answer, t), install: true);
        }
    }

    [GeneratedRegex(@"```mermaid[ \t]*\r?\n(.*?)\r?\n[ \t]*```", RegexOptions.Singleline)]
    internal static partial Regex MermaidBlockRegex();

    /// <summary>
    /// Pendant la réponse : diagrammes dont le bloc est fermé (ligne ``` suivie d'un retour à la ligne) dessinés
    /// tout de suite et affichés à la place de leur code. Un lancement à la fois ; les blocs fermés entre-temps
    /// sont pris au token suivant, le dernier par RenderDiagramsAsync à la fin de la réponse.
    /// </summary>
    private void RenderReadyDiagrams(ChatMessage msg)
    {
        if (!msg.DiagramsTask.IsCompleted || !_mermaidService.Installed) return;
        var content = msg.Content;
        var codes = MermaidBlockRegex().Matches(content)
            .Where(m => content.AsSpan(m.Index + m.Length).TrimStart(" \t\r").StartsWith("\n"))
            .Select(m => m.Groups[1].Value)
            .Where(code => !msg.Diagrams.ContainsKey(code) && msg.DiagramsTried.Add(code))
            .ToList();
        if (codes.Count == 0) return;
        msg.DiagramsTask = Run();

        async Task Run()
        {
            msg.DiagramStatus = Loc.T("Mermaid_Rendering");
            try
            {
                var dark = DarkTheme;
                var results = await _mermaidService.RenderAsync(codes, dark, CancellationToken.None);
                if (dark != DarkTheme) { Avalonia.Threading.Dispatcher.UIThread.Post(() => ReRender(msg)); return; }
                for (int i = 0; i < codes.Count; i++)
                    if (results[i].Png is { } png) msg.Diagrams[codes[i]] = png;
            }
            catch { /* reprise à la fin de la réponse */ }
            msg.DiagramStatus = "";
            msg.RefreshDisplay();
        }
    }

    private static bool DarkTheme => Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

    /// <summary> Changement de thème clair / sombre : les diagrammes de toutes les réponses sont redessinés. </summary>
    private void OnThemeChanged()
    {
        foreach (var m in Messages.Where(m => m.IsAssistant && MermaidBlockRegex().IsMatch(m.Content))) ReRender(m);
        foreach (var m in Messages.Where(m => m.IsAssistant && LatexService.Find(m.Content).Count > 0))
        {
            m.Formulas.Clear();
            m.RefreshDisplay();
            _ = RenderFormulasAsync(m, Task.CompletedTask, install: false);
        }
    }

    /// <summary> Efface les diagrammes dessinés (autre thème) et les redessine dans le thème actuel. </summary>
    private void ReRender(ChatMessage msg)
    {
        msg.Diagrams.Clear();
        msg.DiagramsTried.Clear();
        msg.RefreshDisplay();
        RenderReadyDiagrams(msg);
    }

    /// <summary>
    /// Formules LaTeX de la réponse dessinées en local (ziamath dans le venv, installé à la première utilisation)
    /// et affichées à la place de leur code. Attend <paramref name="after"/> (les diagrammes) pour ne pas installer
    /// deux paquets à la fois. Une formule invalide reste affichée telle quelle. Rien n'est envoyé au modèle.
    /// <paramref name="install"/> : installer ziamath s'il manque (fin de réponse seulement, pas à l'ouverture d'une conversation).
    /// </summary>
    private async Task RenderFormulasAsync(ChatMessage msg, Task after, bool install)
    {
        try { await after; } catch { /* erreur déjà affichée par le rendu des diagrammes */ }
        var formulas = LatexService.Find(msg.Content).Select(s => s.Formula)
            .Where(f => !msg.Formulas.ContainsKey(f)).Distinct().ToList();
        if (formulas.Count == 0) return;
        try
        {
            if (!_latexService.Installed)
            {
                if (!install) return;
                msg.DiagramStatus = Loc.T("Latex_Installing");
                if (!await _latexService.InstallAsync(new ProgressTracker(), _ => { }, CancellationToken.None))
                { msg.DiagramStatus = Loc.T("Latex_InstallFailed"); return; }
            }
            var dark = DarkTheme;
            var results = await _latexService.RenderAsync(formulas, dark, CancellationToken.None);
            // Thème changé pendant le dessin : OnThemeChanged redessine dans le nouveau thème
            if (dark != DarkTheme) return;
            for (int i = 0; i < formulas.Count; i++)
                if (results[i].Png is { } png) msg.Formulas[formulas[i]] = png;
                else msg.DiagramStatus = Loc.F("Latex_Failed", results[i].Error ?? "?");
            msg.RefreshDisplay();
        }
        catch (Exception ex) { msg.DiagramStatus = Loc.F("Latex_Failed", ex.Message); }
    }

    /// <summary>
    /// Diagrammes Mermaid de la réponse dessinés en local (mermaidx dans le venv, installé à la première
    /// utilisation) et affichés à la place de leur code ; ceux déjà dessinés pendant la réponse viennent du cache.
    /// Diagramme invalide : si l'option est active, le modèle reçoit l'erreur et corrige (2 essais au plus,
    /// chacun vérifié) ; sinon le code reste affiché avec la raison.
    /// </summary>
    private async Task RenderDiagramsAsync(ChatMessage msg, InstanceViewModel t)
    {
        try { await msg.DiagramsTask; } catch { /* erreur déjà affichée par le rendu des diagrammes */ }
        var blocks = MermaidBlockRegex().Matches(msg.Content).Cast<Match>().ToList();
        if (blocks.Count == 0) return;
        var dark = DarkTheme;
        void Status(string s) => Avalonia.Threading.Dispatcher.UIThread.Post(() => msg.DiagramStatus = s);
        try
        {
            if (!_mermaidService.Installed)
            {
                Status(Loc.T("Mermaid_Installing"));
                if (!await _mermaidService.InstallAsync(new ProgressTracker(), _ => { }, CancellationToken.None))
                { Status(Loc.T("Mermaid_InstallFailed")); return; }
            }
            var codes = blocks.Select(m => m.Groups[1].Value).ToList();
            if (codes.Any(c => !msg.Diagrams.ContainsKey(c))) Status(Loc.T("Mermaid_Rendering"));
            var results = await _mermaidService.RenderAsync(codes, dark, CancellationToken.None);

            // Correction par le modèle : l'erreur exacte de Mermaid lui est renvoyée
            var fixedCount = 0;
            if (_settings.Current.MermaidAutoFix && t.IsReady)
                for (int i = 0; i < codes.Count; i++)
                {
                    for (int attempt = 1; attempt <= 2 && results[i].Png == null; attempt++)
                    {
                        Status(Loc.F("Mermaid_Fixing", attempt));
                        if (await AskDiagramFixAsync(t, codes[i], results[i].Error ?? "?") is not { } fixedCode) break;
                        var r = (await _mermaidService.RenderAsync([fixedCode], dark, CancellationToken.None))[0];
                        if (r.Png != null) fixedCount++;
                        results[i] = r.Png != null ? r : new MermaidService.Result(null, r.Error);
                        codes[i] = fixedCode;   // l'essai suivant part de la dernière version
                    }
                }

            // Texte d'origine mis à jour avec les diagrammes corrigés ; affichage : images à la place du code
            var fence = "```";
            var content = msg.Content;
            if (fixedCount > 0)
                for (int i = blocks.Count - 1; i >= 0; i--)
                    content = content[..blocks[i].Index] + $"{fence}mermaid\n{codes[i]}\n{fence}" + content[(blocks[i].Index + blocks[i].Length)..];
            var failure = results.FirstOrDefault(r => r.Png == null)?.Error;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (dark != DarkTheme) { ReRender(msg); return; }   // thème changé pendant le rendu : refait dans le nouveau
                for (int i = 0; i < codes.Count; i++)
                    if (results[i].Png is { } png) msg.Diagrams[codes[i]] = png;
                if (fixedCount > 0) msg.Content = content;
                msg.RefreshDisplay();
                msg.DiagramStatus = failure != null ? Loc.F("Mermaid_Failed", MermaidService.ShortError(failure))
                    : fixedCount > 0 ? Loc.F("Mermaid_Fixed", fixedCount) : "";
                if (fixedCount > 0 && Messages.Contains(msg)) SaveCurrent();
            });
        }
        catch (Exception ex) { Status(Loc.F("Mermaid_Failed", ex.Message)); }
    }

    /// <summary> Demande au modèle de corriger un diagramme (réponse non diffusée), null si rien d'exploitable. </summary>
    private static async Task<string?> AskDiagramFixAsync(InstanceViewModel t, string code, string error)
    {
        try
        {
            var fence = "```";
            var prompt = Loc.F("Mermaid_FixPrompt", error.Trim(), $"{fence}mermaid\n{code}\n{fence}");
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                w.WriteString("model", t.ServedName);
                w.WriteNumber("temperature", 0.2);
                w.WriteNumber("max_tokens", 4096);
                // Sans réflexion : inutile ici, et elle épuiserait les tokens avant le diagramme (Qwen3, Gemma…)
                w.WriteStartObject("chat_template_kwargs");
                w.WriteBoolean("enable_thinking", false);
                w.WriteEndObject();
                w.WriteStartArray("messages");
                w.WriteStartObject();
                w.WriteString("role", "user");
                w.WriteString("content", prompt);
                w.WriteEndObject();
                w.WriteEndArray();
                w.WriteEndObject();
            }
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{t.LocalUrl}/v1/chat/completions")
            {
                Content = new ByteArrayContent(ms.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
            };
            if (t.ApiKey != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.ApiKey);
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var answer = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            // Le bloc ```mermaid de la réponse, sinon la réponse entière si elle commence par un type de diagramme
            if (MermaidBlockRegex().Match(answer) is { Success: true } m) return m.Groups[1].Value;
            var trimmed = answer.Trim();
            return Regex.IsMatch(trimmed, @"^(graph|flowchart|sequenceDiagram|classDiagram|stateDiagram|erDiagram|gantt|pie|mindmap|journey|timeline|gitGraph|quadrantChart|xychart)", RegexOptions.IgnoreCase)
                ? trimmed : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Vérifie le champ « Outils » : tableau JSON non vide. Renvoie le JSON compact, ou null si invalide ou vide.
    /// </summary>
    internal static string? ParseTools(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return null;
            return doc.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            // JSON mal formé : le message d'erreur est affiché à l'envoi
            return null;
        }
    }

    private string BuildBody(InstanceViewModel t)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", t.ServedName);
            w.WriteBoolean("stream", true);
            w.WriteStartObject("stream_options");
            w.WriteBoolean("include_usage", true);
            if (!t.Config.IsLlama) w.WriteBoolean("continuous_usage_stats", true);   // vLLM : compte de tokens à chaque morceau
            w.WriteEndObject();
            w.WriteNumber("temperature", Math.Round(Temperature, 2));
            if (TopP < 1.0) w.WriteNumber("top_p", Math.Round(TopP, 2));
            if (TopK > 0) w.WriteNumber("top_k", (int)TopK);
            if (MinP > 0) w.WriteNumber("min_p", Math.Round(MinP, 2));
            if (FrequencyPenalty != 0) w.WriteNumber("frequency_penalty", Math.Round(FrequencyPenalty, 2));
            if (PresencePenalty != 0) w.WriteNumber("presence_penalty", Math.Round(PresencePenalty, 2));
            // Même réglage, nom différent : llama-server attend repeat_penalty, vLLM repetition_penalty
            if (RepeatPenalty != 1.0) w.WriteNumber(t.Config.IsLlama ? "repeat_penalty" : "repetition_penalty", Math.Round(RepeatPenalty, 2));
            if (int.TryParse(MaxTokens, out var mt) && mt > 0) w.WriteNumber("max_tokens", mt);
            if (ParseTools(ToolsJson) is { } tools)
            {
                w.WritePropertyName("tools");
                w.WriteRawValue(tools, skipInputValidation: true);
            }
            // Réflexion : seulement si le modèle sait la désactiver (sinon on ne touche à rien)
            if (ThinkSupported)
            {
                w.WriteStartObject("chat_template_kwargs");
                w.WriteBoolean("enable_thinking", ThinkEnabled);
                w.WriteEndObject();
            }
            w.WriteStartArray("messages");
            if (!string.IsNullOrWhiteSpace(SystemPrompt))
            {
                w.WriteStartObject();
                w.WriteString("role", "system");
                w.WriteString("content", SystemPrompt.Trim());
                w.WriteEndObject();
            }
            foreach (var m in Messages.Where(m => m.Content.Length > 0 || m.ImageUrls.Count > 0 || m.HasDocuments))
            {
                w.WriteStartObject();
                w.WriteString("role", m.Role);
                if (m.ImageUrls.Count == 0) w.WriteString("content", m.ApiContent);
                else
                {
                    // Message avec images : contenu en plusieurs parties (texte + image_url)
                    w.WriteStartArray("content");
                    if (m.ApiContent.Length > 0)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "text");
                        w.WriteString("text", m.ApiContent);
                        w.WriteEndObject();
                    }
                    foreach (var url in m.ImageUrls)
                    {
                        w.WriteStartObject();
                        w.WriteString("type", "image_url");
                        w.WriteStartObject("image_url");
                        w.WriteString("url", url);
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary> Personas (prompts système prédéfinis) proposés dans les réglages. </summary>
    public IReadOnlyList<ChatPersona> AvailablePersonas { get; } =
        new[] { "None", "Coder", "Writer", "Analyst", "Teacher", "Translator", "Brainstorm" }
            .Select(n => new ChatPersona("Chat_Persona_" + n, n == "None" ? null : "Chat_PersonaPrompt_" + n)).ToArray();

    /// <summary> Persona choisi : son prompt remplace le prompt système de la conversation. </summary>
    [ObservableProperty] private ChatPersona? _selectedPersona;

    partial void OnSelectedPersonaChanged(ChatPersona? value)
    {
        if (value != null) SystemPrompt = value.SystemPrompt;
    }

    /// <summary>
    /// Copie dans le presse-papiers la commande cURL qui rejoue la conversation avec les réglages actuels :
    /// syntaxe bash sous Linux, PowerShell (curl.exe) sous Windows.
    /// </summary>
    [RelayCommand]
    private async Task CopyCurlAsync()
    {
        if (Target is not { IsReady: true } t) { Error = Loc.T("Chat_NoServer"); return; }
        await _ui.CopyAsync(BuildCurl(t, BuildBody(t), OperatingSystem.IsWindows()));
        CurlCopied = true;
        await Task.Delay(1500);
        CurlCopied = false;
    }

    /// <summary> Enregistre la conversation ouverte dans un fichier Markdown, à l'emplacement choisi. </summary>
    [RelayCommand]
    private async Task ExportMarkdownAsync()
    {
        if (Messages.Count == 0) { Error = Loc.T("Chat_ExportEmpty"); return; }
        var dest = await _ui.PickSaveFileAsync(Loc.T("Chat_ExportTitle"), SafeFileName(CurrentTitle) + ".md",
            Loc.T("Chat_MarkdownFiles"), "*.md");
        if (dest == null) return;
        try { await File.WriteAllTextAsync(dest, BuildMarkdown(), new UTF8Encoding(false)); }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>
    /// Texte Markdown de la conversation : le texte d'origine des messages (sans rendu des diagrammes),
    /// les documents joints listés sous leur message, la réflexion absente (elle n'est pas une réponse).
    /// </summary>
    internal string BuildMarkdown()
    {
        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(string.IsNullOrWhiteSpace(CurrentTitle) ? Loc.T("Chat_NewConversation") : CurrentTitle.Trim());
        foreach (var m in Messages)
        {
            sb.AppendLine().Append("## ").AppendLine(m.IsUser ? Loc.T("Chat_ExportYou") : Loc.T("Chat_ExportAssistant"));
            sb.AppendLine();
            foreach (var d in m.DocumentNames) sb.AppendLine(Loc.F("Chat_ExportAttached", d)).AppendLine();
            sb.AppendLine(m.Content.Trim());
        }
        return sb.ToString();
    }

    /// <summary> Nom de fichier utilisable : caractères interdits remplacés, sans nom vide. </summary>
    private static string SafeFileName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(title.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return name.Length == 0 ? "conversation" : name.Length > 60 ? name[..60] : name;
    }

    /// <summary>
    /// Exporte la conversation en page HTML et l'ouvre dans le navigateur par défaut : sa boîte d'impression
    /// enregistre le PDF (Chrome, Firefox…). Les diagrammes sont redessinés en thème clair (fond blanc) pour l'impression.
    /// </summary>
    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (Messages.Count == 0) { Error = Loc.T("Chat_ExportEmpty"); return; }
        try
        {
            var snapshot = Messages.ToList();
            var light = await LightDiagramsAsync(snapshot);
            var lightFormulas = await LightFormulasAsync(snapshot);
            var messages = snapshot.Select(m => new ChatExportMessage(
                m.IsUser,
                m.IsAssistant ? LightText(m.Content, light, lightFormulas) : m.Content,
                m.ImageUrls.ToList(),
                m.DocumentNames.Select(d => Loc.F("Chat_ExportAttached", d)).ToList())).ToList();
            var labels = new ChatExportLabels(Loc.T("Chat_ExportYou"), Loc.T("Chat_ExportAssistant"), Loc.T("Chat_ExportPrint"));
            var html = ChatHtmlExport.Build(CurrentTitle, messages, Loc.Instance.CurrentLanguage, labels, ChatHtmlExport.ReadMarkedScript());
            var dir = Path.Combine(AppPaths.DataDir, "exports");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{SafeFileName(CurrentTitle)}-{DateTime.Now:yyyyMMdd-HHmmss}.html");
            await File.WriteAllTextAsync(path, html, new UTF8Encoding(false));
            _ui.Open(path);
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary>
    /// Diagrammes des réponses dessinés en thème clair (code → PNG). Vide si Mermaid n'est pas installé ;
    /// un diagramme invalide reste en bloc de code.
    /// </summary>
    private async Task<Dictionary<string, string>> LightDiagramsAsync(IReadOnlyList<ChatMessage> messages)
    {
        var png = new Dictionary<string, string>();
        var codes = messages.Where(m => m.IsAssistant)
            .SelectMany(m => MermaidBlockRegex().Matches(m.Content).Select(x => x.Groups[1].Value))
            .Distinct().ToList();
        if (codes.Count == 0 || !_mermaidService.Installed) return png;
        var results = await _mermaidService.RenderAsync(codes, dark: false, CancellationToken.None);
        for (int i = 0; i < codes.Count; i++)
            if (results[i].Png is { } file) png[codes[i]] = file;
        return png;
    }

    /// <summary> Formules LaTeX des réponses dessinées en thème clair (fond blanc de l'impression). </summary>
    private async Task<Dictionary<LatexService.Formula, string>> LightFormulasAsync(IReadOnlyList<ChatMessage> messages)
    {
        var png = new Dictionary<LatexService.Formula, string>();
        var formulas = messages.Where(m => m.IsAssistant)
            .SelectMany(m => LatexService.Find(m.Content).Select(s => s.Formula))
            .Distinct().ToList();
        if (formulas.Count == 0 || !_latexService.Installed) return png;
        var results = await _latexService.RenderAsync(formulas, dark: false, CancellationToken.None);
        for (int i = 0; i < formulas.Count; i++)
            if (results[i].Png is { } file) png[formulas[i]] = file;
        return png;
    }

    /// <summary> Texte d'une réponse avec les blocs Mermaid et les formules remplacés par leur image claire quand elle existe. </summary>
    private static string LightText(string content, IReadOnlyDictionary<string, string> light, IReadOnlyDictionary<LatexService.Formula, string> lightFormulas)
    {
        var text = MermaidBlockRegex().Replace(content, m => light.TryGetValue(m.Groups[1].Value, out var file)
            ? $"![{Loc.T("Mermaid_Alt")}]({new Uri(file).AbsoluteUri})" : m.Value);
        return LatexService.Substitute(text, f => lightFormulas.GetValueOrDefault(f));
    }

    /// <summary>
    /// Commande cURL pour une requête de chat. Le JSON de Utf8JsonWriter est en ASCII (accents et apostrophes
    /// échappés en \uXXXX) et sur une ligne : il passe tel quel entre guillemets simples (bash) ou dans une
    /// here-string PowerShell envoyée à curl.exe (« curl » seul y est un alias d'Invoke-WebRequest).
    /// </summary>
    private static string BuildCurl(InstanceViewModel t, string body, bool windows)
    {
        var url = $"{t.LocalUrl}/v1/chat/completions";
        var auth = t.ApiKey != null ? $" -H \"Authorization: Bearer {t.ApiKey}\"" : "";
        if (windows)
            return $"@'\r\n{body}\r\n'@ | curl.exe -s \"{url}\" -H \"Content-Type: application/json\"{auth} --data-binary \"@-\"";
        return $"curl -s \"{url}\" -H \"Content-Type: application/json\"{auth} \\\n  -d '{body}'";
    }

    /// <summary> La commande cURL vient d'être copiée (coche affichée sur le bouton). </summary>
    [ObservableProperty] private bool _curlCopied;
}

/// <summary> Persona du chat : nom affiché et prompt système, tous deux traduits. </summary>
/// <param name="NameKey"> Clé de traduction du nom. </param>
/// <param name="PromptKey"> Clé de traduction du prompt système (null : aucun prompt). </param>
public sealed record ChatPersona(string NameKey, string? PromptKey)
{
    /// <summary> Prompt système dans la langue de l'interface. </summary>
    public string SystemPrompt => PromptKey == null ? "" : Loc.T(PromptKey);

    /// <summary> Nom affiché dans la liste. </summary>
    public override string ToString() => Loc.T(NameKey);
}

/// <summary> Image jointe au prochain message. </summary>
public sealed record PendingImage(string Name, string DataUri, Bitmap Thumbnail);

/// <summary> Document joint au prochain message : converti en Markdown en arrière-plan. </summary>
/// <summary> Base de connaissances proposée dans le chat ; Id null = aucune. </summary>
public sealed record KnowledgeOption(string? Id, string Name);

public sealed partial class PendingDocument(string name) : ObservableObject
{
    /// <summary> Nom du fichier. </summary>
    public string Name { get; } = name;
    /// <summary> Texte Markdown obtenu, ou null pendant la conversion. </summary>
    public string? Markdown { get; private set; }
    [ObservableProperty] private string _status = Loc.T("Chat_DocConverting");
    [ObservableProperty] private bool _isConverting = true;
    [ObservableProperty] private bool _isError;

    /// <summary> Tokens du document : estimation (~4 caractères par token), puis compte exact du serveur. </summary>
    public int Tokens { get; private set; }
    private bool _exact;

    /// <summary> Enregistre le texte converti et estime son nombre de jetons. </summary>
    public void SetMarkdown(string md)
    {
        Markdown = md;
        IsConverting = false;
        Tokens = md.Length / 4;
        UpdateStatus();
    }

    /// <summary> Remplace l'estimation par le nombre exact de jetons. </summary>
    public void SetTokens(int exact)
    {
        Tokens = exact;
        _exact = true;
        UpdateStatus();
    }

    private void UpdateStatus() =>
        Status = Loc.F(_exact ? "Chat_DocReadyExact" : "Chat_DocReady", Format.Bytes(Encoding.UTF8.GetByteCount(Markdown ?? "")),
                       Tokens.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary> Marque la conversion comme échouée. </summary>
    public void SetError(string message)
    {
        IsConverting = false;
        IsError = true;
        Status = message;
    }
}
