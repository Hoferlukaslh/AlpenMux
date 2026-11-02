/*
    Fichier      :  KnowledgeViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page « Connaissances » (RAG) : création des bases de connaissances (dossiers et fichiers), choix du serveur
        d'embedding (un serveur lancé, ou un modèle GGUF lancé ici avec --embedding), indexation en tâche de fond
        avec progression et annulation, et zone de test (question → passages trouvés et leurs scores).
        Les calculs sont dans RagIndexer / RagSearch ; ce ViewModel n'accède à aucune vue.
*/

using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> Une base dans la liste de la page (réglages et résumé de l'index). </summary>
public sealed partial class KnowledgeBaseItem : ObservableObject
{
    /// <summary> Réglages de la base. </summary>
    public RagBaseConfig Config { get; }

    /// <summary> Résumé affiché sous le nom (fichiers, passages, modèle). </summary>
    [ObservableProperty] private string _summary = "";

    /// <summary> Dossiers et fichiers, observables pour la liste (recopiés dans Config à chaque changement). </summary>
    public ObservableCollection<string> Sources { get; }

    /// <summary> Crée l'élément. </summary>
    /// <param name="config"> Réglages de la base. </param>
    public KnowledgeBaseItem(RagBaseConfig config)
    {
        Config = config;
        Sources = new ObservableCollection<string>(config.Sources);
    }

    /// <summary> Recopie la liste observable dans les réglages. </summary>
    public void SyncSources()
    {
        Config.Sources.Clear();
        Config.Sources.AddRange(Sources);
    }

    /// <summary> Nom affiché. </summary>
    public string Name => Config.Name;
}

/// <summary> Passage trouvé par la zone de test (ou une question du chat), prêt à afficher. </summary>
public sealed class KnowledgeResultView
{
    /// <summary> Chemin du fichier source (ouvert au clic). </summary>
    public string Path { get; init; } = "";
    /// <summary> Nom du fichier. </summary>
    public string File { get; init; } = "";
    /// <summary> Chemin de titres et page, sur une ligne. </summary>
    public string Location { get; init; } = "";
    /// <summary> Texte du passage. </summary>
    public string Text { get; init; } = "";
    /// <summary> Scores lisibles (similarité, BM25). </summary>
    public string Scores { get; init; } = "";
}

/// <summary> ViewModel de la page Connaissances. </summary>
public sealed partial class KnowledgeViewModel : ViewModelBase
{
    private readonly IRagStore _store;
    private readonly IRagIndexer _indexer;
    private readonly IRagSearch _search;
    private readonly IAppState _appState;
    private readonly IUiServices _ui;
    private readonly IInstanceViewModelFactory _instanceFactory;
    private CancellationTokenSource? _indexCts;

    /// <summary> Bases de connaissances, de la plus récente à la plus ancienne. </summary>
    public ObservableCollection<KnowledgeBaseItem> Bases { get; } = new();
    /// <summary> Base ouverte dans le volet de droite. </summary>
    [ObservableProperty] private KnowledgeBaseItem? _selected;
    /// <summary> Dossiers et fichiers de la base en cours de création. </summary>
    public ObservableCollection<string> NewSources { get; } = new();
    /// <summary> Nom de la base en cours de création. </summary>
    [ObservableProperty] private string _newName = "";
    /// <summary> Suppression demandée : un second clic confirme. </summary>
    [ObservableProperty] private bool _deleteArmed;
    /// <summary> Erreur affichée (création, lancement, recherche). </summary>
    [ObservableProperty] private string _error = "";

    /// <summary> Serveurs d'embedding lancés. </summary>
    public ObservableCollection<InstanceViewModel> EmbeddingServers { get; } = new();
    /// <summary> Serveur d'embedding utilisé pour indexer et chercher. </summary>
    [ObservableProperty] private InstanceViewModel? _server;
    /// <summary> Modèles GGUF qui ressemblent à un modèle d'embedding (pour les lancer ici). </summary>
    public ObservableCollection<ModelInfo> EmbeddingModels { get; } = new();
    /// <summary> Modèle choisi pour le lancement. </summary>
    [ObservableProperty] private ModelInfo? _model;

    /// <summary> Indexation en cours. </summary>
    [ObservableProperty] private bool _isIndexing;
    /// <summary> Ligne d'état de l'indexation (progression, bilan). </summary>
    [ObservableProperty] private string _indexStatus = "";
    /// <summary> Avancement de l'indexation (0 à 100). </summary>
    [ObservableProperty] private double _indexPercent;
    /// <summary> Indexation en erreur (serveur, modèle…). </summary>
    [ObservableProperty] private string _indexError = "";
    /// <summary> Fichiers de la base en erreur (raison lisible). </summary>
    public ObservableCollection<string> FileErrors { get; } = new();

    /// <summary> Question de la zone de test. </summary>
    [ObservableProperty] private string _query = "";
    /// <summary> Recherche en cours. </summary>
    [ObservableProperty] private bool _isSearching;
    /// <summary> Passages trouvés par la dernière recherche. </summary>
    public ObservableCollection<KnowledgeResultView> Results { get; } = new();
    /// <summary> Message de la zone de test (rien trouvé, erreur). </summary>
    [ObservableProperty] private string _queryMessage = "";

    /// <summary> Crée le ViewModel. </summary>
    public KnowledgeViewModel(IRagStore store, IRagIndexer indexer, IRagSearch search, IAppState appState, IUiServices ui, IInstanceViewModelFactory instanceFactory)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(instanceFactory);
        _store = store;
        _indexer = indexer;
        _search = search;
        _appState = appState;
        _ui = ui;
        _instanceFactory = instanceFactory;
        _appState.InstancesChanged += SyncServers;
        _appState.ModelsChanged += SyncModels;
        Reload();
        SyncServers();
        SyncModels();
    }

    // ---------------------------------------------------------------- Bases

    /// <summary> Recharge la liste des bases depuis le disque. </summary>
    public void Reload()
    {
        var keep = Selected?.Config.Id;
        Bases.Clear();
        foreach (var cfg in _store.List()) Bases.Add(NewItem(cfg));
        Selected = Bases.FirstOrDefault(b => b.Config.Id == keep) ?? Bases.FirstOrDefault();
    }

    private KnowledgeBaseItem NewItem(RagBaseConfig cfg)
    {
        var item = new KnowledgeBaseItem(cfg);
        item.Summary = SummaryOf(cfg);
        return item;
    }

    private string SummaryOf(RagBaseConfig cfg)
    {
        using var db = _store.Open(cfg.Id);
        var s = db.Stats();
        if (s.Chunks == 0 && s.Errors == 0 && string.IsNullOrEmpty(cfg.EmbeddingModel)) return Loc.T("Kb_NotIndexed");
        return Loc.F("Kb_Status", s.Documents, s.Chunks, s.Errors) + (cfg.EmbeddingModel.Length > 0 ? " · " + cfg.EmbeddingModel : "");
    }

    /// <summary> Une base est ouverte. </summary>
    public bool HasSelected => Selected != null;
    /// <summary> Aucun serveur d'embedding lancé. </summary>
    public bool NoServers => EmbeddingServers.Count == 0;
    /// <summary> Aucun modèle d'embedding GGUF trouvé. </summary>
    public bool NoEmbeddingModels => EmbeddingModels.Count == 0;
    /// <summary> Des fichiers sont en erreur. </summary>
    public bool HasFileErrors => FileErrors.Count > 0;

    partial void OnSelectedChanged(KnowledgeBaseItem? value)
    {
        OnPropertyChanged(nameof(HasSelected));
        DeleteArmed = false;
        IndexStatus = "";
        IndexError = "";
        Results.Clear();
        QueryMessage = "";
        RefreshFileErrors();
        // Le serveur de la base : celui qui a servi à l'index, sinon le premier prêt
        if (value != null && EmbeddingServers.Count > 0)
        {
            Server = EmbeddingServers.FirstOrDefault(s => s.ServedName == value.Config.EmbeddingModel && s.IsReady)
                  ?? EmbeddingServers.FirstOrDefault(s => s.ServedName == value.Config.EmbeddingModel)
                  ?? EmbeddingServers.FirstOrDefault(s => s.IsReady) ?? EmbeddingServers[0];
        }
    }

    private void RefreshFileErrors()
    {
        FileErrors.Clear();
        if (Selected is { } item)
        {
            using var db = _store.Open(item.Config.Id);
            foreach (var d in db.Documents().Where(d => d.Error != null))
                FileErrors.Add($"{Path.GetFileName(d.Path)} : {d.Error}");
        }
        OnPropertyChanged(nameof(HasFileErrors));
    }

    /// <summary> Met à jour le résumé et la liste des erreurs de la base ouverte. </summary>
    private void RefreshSelected()
    {
        if (Selected is not { } item) return;
        item.Summary = SummaryOf(item.Config);
        RefreshFileErrors();
    }

    /// <summary> Ouvre une base dans le volet de droite (clic dans la liste). </summary>
    [RelayCommand]
    private void SelectBase(KnowledgeBaseItem? item)
    {
        if (item != null) Selected = item;
    }

    [RelayCommand]
    private async Task AddNewFolderAsync()
    {
        var folder = await _ui.PickFolderAsync(Loc.T("Kb_PickFolder"));
        if (!string.IsNullOrEmpty(folder) && !NewSources.Contains(folder)) NewSources.Add(folder);
    }

    [RelayCommand]
    private async Task AddNewFilesAsync()
    {
        var files = await _ui.PickFilesAsync(Loc.T("Kb_PickFiles"), Loc.T("Chat_Documents"), MarkItDownService.Patterns);
        foreach (var f in files.Where(f => !NewSources.Contains(f))) NewSources.Add(f);
    }

    [RelayCommand]
    private void RemoveNewSource(string? path)
    {
        if (path != null) NewSources.Remove(path);
    }

    [RelayCommand]
    private void Create()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(NewName)) { Error = Loc.T("Kb_NeedName"); return; }
        if (NewSources.Count == 0) { Error = Loc.T("Kb_NeedSource"); return; }
        var cfg = _store.Create(NewName, NewSources);
        NewName = "";
        NewSources.Clear();
        var item = NewItem(cfg);
        Bases.Insert(0, item);
        Selected = item;
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is not { } item) return;
        if (!DeleteArmed) { DeleteArmed = true; return; }
        if (IsIndexing) { Error = Loc.T("Kb_BusyIndexing"); DeleteArmed = false; return; }
        _store.Delete(item.Config.Id);
        Bases.Remove(item);
        Selected = Bases.FirstOrDefault();
    }

    [RelayCommand]
    private async Task AddSourceAsync()
    {
        if (Selected is not { } item) return;
        var folder = await _ui.PickFolderAsync(Loc.T("Kb_PickFolder"));
        if (string.IsNullOrEmpty(folder) || item.Sources.Contains(folder)) return;
        item.Sources.Add(folder);
        item.SyncSources();
        _store.SaveConfig(item.Config);
    }

    [RelayCommand]
    private void RemoveSource(string? path)
    {
        if (Selected is not { } item || path is null) return;
        item.Sources.Remove(path);
        item.SyncSources();
        _store.SaveConfig(item.Config);
    }

    // ---------------------------------------------------------------- Serveurs d'embedding

    private void SyncServers()
    {
        var list = _appState.Instances.Where(i => i.IsEmbedding).ToList();
        if (!list.SequenceEqual(EmbeddingServers))
        {
            EmbeddingServers.Clear();
            foreach (var i in list) EmbeddingServers.Add(i);
        }
        OnPropertyChanged(nameof(NoServers));
        if (Server is null || !EmbeddingServers.Contains(Server)) Server = EmbeddingServers.FirstOrDefault(s => s.IsReady) ?? EmbeddingServers.FirstOrDefault();
    }

    private void SyncModels()
    {
        var list = _appState.Models
            .Where(m => m.Layout == "gguf" && EmbeddingProfiles.LooksLikeEmbedding(m.Repo + " " + m.Name))
            .ToList();
        if (!list.SequenceEqual(EmbeddingModels))
        {
            EmbeddingModels.Clear();
            foreach (var m in list) EmbeddingModels.Add(m);
        }
        OnPropertyChanged(nameof(NoEmbeddingModels));
        if (Model is null || !EmbeddingModels.Contains(Model)) Model = EmbeddingModels.FirstOrDefault();
    }

    /// <summary> Lance le modèle d'embedding choisi (llama.cpp en mode --embedding) et l'affiche comme serveur. </summary>
    [RelayCommand]
    private void LaunchEmbedding()
    {
        Error = "";
        if (Model is not { } m) { Error = Loc.T("Kb_NoEmbeddingModel"); return; }
        var config = new LaunchConfig
        {
            Engine = LaunchConfig.EngineLlama,
            ModelTarget = m.Target,
            ModelRepo = m.Repo,
            ModelIsGguf = true,
            // Le nom servi reprend le nom du modèle : c'est lui qui désigne le profil (préfixes) à l'indexation
            ServedName = m.Repo.Split('/').Last(),
            Port = _appState.NextFreePort(8011),
            MaxModelLen = 8192,
            Embedding = true,
            // Sur le processeur : un petit modèle d'embedding ne prend pas de VRAM au modèle de chat (plan RAG, § 3.1)
            LlamaGpuLayers = 0,
        };
        var inst = _instanceFactory.Create(config);
        _appState.AddInstance(inst);
        inst.Start();
        Server = inst;
    }

    // ---------------------------------------------------------------- Indexation

    [RelayCommand]
    private Task IndexAsync() => RunIndexAsync(rebuild: false);

    [RelayCommand]
    private Task RebuildAsync() => RunIndexAsync(rebuild: true);

    [RelayCommand]
    private void CancelIndex() => _indexCts?.Cancel();

    private async Task RunIndexAsync(bool rebuild)
    {
        Error = "";
        IndexError = "";
        if (Selected is not { } item) return;
        if (Server is not { IsReady: true } server) { IndexError = Loc.T("Kb_ServerNotReady"); return; }
        if (IsIndexing) return;

        var endpoint = new EmbeddingEndpoint(server.LocalUrl, server.ServedName, server.ApiKey);
        var id = item.Config.Id;
        _indexCts = new CancellationTokenSource();
        var ct = _indexCts.Token;
        IsIndexing = true;
        IndexPercent = 0;
        IndexStatus = Loc.T("Kb_Indexing_Scanning");
        // Progress<T> rappelle sur le thread de l'interface : l'indexation elle-même reste hors de lui
        var progress = new Progress<RagIndexProgress>(p =>
        {
            IndexStatus = p.Stage switch
            {
                RagIndexStage.Scanning => Loc.T("Kb_Indexing_Scanning"),
                RagIndexStage.Converting => Loc.F("Kb_Indexing_Converting", p.FilesTotal),
                _ => Loc.F("Kb_Indexing_Embedding", p.FilesDone, p.FilesTotal, p.ChunksDone) + (p.Current is { } c ? " · " + c : ""),
            };
            IndexPercent = p.FilesTotal == 0 ? 0 : 100.0 * p.FilesDone / p.FilesTotal;
        });
        try
        {
            var report = await Task.Run(() => _indexer.IndexAsync(id, endpoint, rebuild, progress, ct));
            IndexStatus = Loc.F("Kb_Done", report.Indexed, report.Unchanged, report.Removed, report.Failed);
            IndexPercent = 100;
        }
        catch (OperationCanceledException)
        {
            IndexStatus = Loc.T("Kb_Cancelled");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or HttpRequestException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            IndexError = ex.Message;
        }
        finally
        {
            IsIndexing = false;
            _indexCts?.Dispose();
            _indexCts = null;
            RefreshSelected();
        }
    }

    // ---------------------------------------------------------------- Zone de test

    [RelayCommand]
    private async Task SearchAsync()
    {
        QueryMessage = "";
        Results.Clear();
        if (Selected is not { } item || string.IsNullOrWhiteSpace(Query)) return;
        if (Server is not { IsReady: true } server) { QueryMessage = Loc.T("Kb_ServerNotReady"); return; }
        var endpoint = new EmbeddingEndpoint(server.LocalUrl, server.ServedName, server.ApiKey);
        IsSearching = true;
        try
        {
            var hits = await Task.Run(() => _search.SearchAsync(item.Config.Id, endpoint, Query, 8, CancellationToken.None));
            foreach (var h in hits) Results.Add(ToView(h));
            QueryMessage = hits.Count == 0 ? Loc.T("Kb_NoResult") : "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            QueryMessage = ex.Message;
        }
        finally { IsSearching = false; }
    }

    /// <summary> Affichage d'un passage : chemin de titres et page, scores lisibles. </summary>
    public static KnowledgeResultView ToView(RagHit h)
    {
        var parts = new List<string>();
        if (h.Heading.Length > 0) parts.Add(h.Heading);
        if (h.Page is { } page) parts.Add(Loc.F("Kb_PageShort", page));
        var loc = string.Join(" · ", parts);
        var scores = Loc.F("Kb_Scores", h.Cosine.ToString("0.00"), h.Bm25 is { } b ? b.ToString("0.00") : "–");
        return new KnowledgeResultView
        {
            Path = h.Path,
            File = Path.GetFileName(h.Path),
            Location = loc,
            Text = h.Text.Trim(),
            Scores = scores,
        };
    }

    [RelayCommand]
    private void OpenSource(KnowledgeResultView? view)
    {
        if (view is { Path.Length: > 0 } && File.Exists(view.Path)) _ui.Open(view.Path);
    }
}
