/*
    Fichier      :  MonitorViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Moniteur système intégré : CPU (total + cœurs), RAM / swap, réseau,
        GPU (utilisation, VRAM, température, puissance, ventilateur, fréquence),
        processus GPU (avec le modèle correspondant s'il a été lancé par l'application),
        disque du dossier des modèles. Historique de durée réglable ; panneau de réglages
        (fréquence, durée, arrondi, épaisseur, aire, grille, couleurs des courbes).
        Page modulable : la liste des widgets (Tiles) et leur disposition sont enregistrées dans les réglages ;
        le mode édition permet d'ajouter, supprimer, déplacer et redimensionner (TileBoard).
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class GpuCardViewModel : ObservableObject
{
    /// <summary> Index du GPU. </summary>
    public int Index { get; init; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private double _util;
    [ObservableProperty] private double _memPercent;
    [ObservableProperty] private string _memText = "";
    [ObservableProperty] private string _tempText = "—";
    [ObservableProperty] private string _powerText = "—";
    [ObservableProperty] private string _fanText = "—";
    [ObservableProperty] private string _clockText = "—";
    [ObservableProperty] private double _powerPercent;

    /// <summary> Crée la carte d'un GPU. </summary>
    /// <param name="historyPoints"> Nombre de points des courbes (durée et fréquence choisies). </param>
    public GpuCardViewModel(int historyPoints)
    {
        UtilHistory = new(historyPoints);
        MemHistory = new(historyPoints);
    }

    /// <summary> Historique de l'utilisation (%). </summary>
    public RingBuffer UtilHistory { get; }
    /// <summary> Historique de la mémoire utilisée (%). </summary>
    public RingBuffer MemHistory { get; }

    /// <summary> Met à jour la carte avec une nouvelle mesure. </summary>
    public void Update(GpuSample g)
    {
        Name = $"GPU {g.Index} · {g.Name}";
        Util = g.Util;
        MemPercent = g.MemTotal > 0 ? g.MemUsed / g.MemTotal * 100 : 0;
        MemText = $"{Format.Bytes(g.MemUsed)} / {Format.Bytes(g.MemTotal)}";
        TempText = g.Temp is { } t ? $"{t:0} °C" : "—";
        PowerText = g.Power is { } p ? g.PowerLimit is { } l ? $"{p:0} / {l:0} W" : $"{p:0} W" : "—";
        PowerPercent = g.Power is { } pp && g.PowerLimit is { } pl && pl > 0 ? pp / pl * 100 : 0;
        FanText = g.Fan is { } f ? $"{f:0} %" : "—";
        ClockText = g.ClockMhz is { } c ? $"{c:0} MHz" : "—";
        UtilHistory.Add(Util);
        MemHistory.Add(MemPercent);
    }
}

public sealed partial class CoreUsage : ObservableObject
{
    /// <summary> Libellé du cœur. </summary>
    public required string Label { get; init; }
    [ObservableProperty] private double _value;
}

/// <summary> Une entrée du catalogue « Ajouter un widget ». </summary>
public sealed record TileChoice(TileKind Kind, string Name, string Description, string Glyph);

/// <summary> Ligne du tableau des processus GPU. </summary>
/// <param name="Pid"> Identifiant du processus. </param>
/// <param name="Name"> Nom du programme. </param>
/// <param name="Memory"> Mémoire utilisée (texte). </param>
/// <param name="Gpu"> GPU utilisé. </param>
/// <param name="Owner"> Serveur de l'application, ou « processus externe ». </param>
public sealed record GpuProcessRow(int Pid, string Name, string Memory, string Gpu, string Owner);

public sealed partial class MonitorViewModel : ViewModelBase
{
    /// <summary> Cartes des GPU. </summary>
    public ObservableCollection<GpuCardViewModel> Gpus { get; } = new();
    /// <summary> Utilisation de chaque cœur. </summary>
    public ObservableCollection<CoreUsage> Cores { get; } = new();
    /// <summary> Processus sur les GPU. </summary>
    public ObservableCollection<GpuProcessRow> GpuProcesses { get; } = new();

    /// <summary> Historique du CPU (%). </summary>
    public RingBuffer CpuHistory { get; }
    /// <summary> Historique de la RAM (%). </summary>
    public RingBuffer RamHistory { get; }
    /// <summary> Historique du débit réseau reçu. </summary>
    public RingBuffer NetHistory { get; }

    /// <summary> Nombre de points pour couvrir la durée choisie à la fréquence choisie. </summary>
    /// <summary> Nombre de points des courbes avec les réglages actuels. </summary>
    private int HistoryPoints() => HistoryPoints(S);

    /// <summary> Nombre de points des courbes pour couvrir la durée choisie à la fréquence choisie. </summary>
    public static int HistoryPoints(AppSettings s)
    {
        var interval = Math.Clamp(s.MonitorIntervalMs, MonitorSettingsViewModel.MinIntervalMs, MonitorSettingsViewModel.MaxIntervalMs);
        return Math.Clamp(s.MonitorHistorySeconds * 1000 / interval, 10, 12000);
    }

    /// <summary> Nom du processeur. </summary>
    public string CpuName => _monitor.CpuName;
    /// <summary> Nombre de cœurs logiques affiché. </summary>
    public string CoreCountText => Loc.F("Mon_Threads", _monitor.CoreCount);

    [ObservableProperty] private double _cpu;
    [ObservableProperty] private string _cpuTemp = "—";
    [ObservableProperty] private string _cpuFreq = "—";
    [ObservableProperty] private string _loadAvg = "";
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private double _swapPercent;
    [ObservableProperty] private string _swapText = "";
    [ObservableProperty] private string _netRx = "";
    [ObservableProperty] private string _netTx = "";
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private double _diskPercent;
    [ObservableProperty] private string _diskText = "";
    [ObservableProperty] private bool _hasGpu;
    [ObservableProperty] private bool _hasGpuProcesses;

    // ---------------------------------------------------------------- Couleurs (réglages : MonitorSettingsViewModel)

    private AppSettings S => _settings.Current;

    [ObservableProperty] private IBrush _cpuBrush = Brushes.Gray;
    [ObservableProperty] private IBrush _ramBrush = Brushes.Gray;
    [ObservableProperty] private IBrush _gpuBrush = Brushes.Gray;
    [ObservableProperty] private IBrush _vramBrush = Brushes.Gray;
    [ObservableProperty] private IBrush _netBrush = Brushes.Gray;

    /// <summary> Couleur choisie dans les paramètres, sinon celle du thème actuel. </summary>
    private void RefreshColors()
    {
        IBrush Of(string key, string res) => _monitorSettings.BrushFor(key, res);
        CpuBrush = Of("cpu", "ChartCpu");
        RamBrush = Of("ram", "ChartRam");
        GpuBrush = Of("gpu", "ChartGpu");
        VramBrush = Of("vram", "ChartVram");
        NetBrush = Of("net", "ChartNet");
    }

    private void ResizeHistories()
    {
        var n = HistoryPoints();
        foreach (var h in new[] { CpuHistory, RamHistory, NetHistory }.Concat(Gpus.SelectMany(g => new[] { g.UtilHistory, g.MemHistory })))
            h.Resize(n);
    }

    // ---------------------------------------------------------------- Widgets modulables

    /// <summary> Widgets affichés. </summary>
    public ObservableCollection<MonitorTileViewModel> Tiles { get; } = new();
    /// <summary> Catalogue « Ajouter un widget ». </summary>
    public IReadOnlyList<TileChoice> Catalog { get; } = MonitorLayout.Kinds.Select(k =>
        new TileChoice(k, k.Name, k.Description, Application.Current?.TryGetResource(k.Icon, null, out var g) == true ? g as string ?? "" : "")).ToList();

    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private bool _isAddOpen;
    /// <summary> Aucun widget. </summary>
    public bool IsEmpty => Tiles.Count == 0;

    partial void OnIsEditingChanged(bool value) { if (!value) IsAddOpen = false; }

    [RelayCommand] private void ToggleEdit() => IsEditing = !IsEditing;
    [RelayCommand] private void ToggleAdd() => IsAddOpen = !IsAddOpen;

    private void LoadTiles()
    {
        var saved = S.MonitorTiles ?? MonitorLayout.Default();
        var tiles = saved.Where(t => MonitorLayout.KindOf(t.Kind) != null).ToList();
        foreach (var t in tiles)
        {
            var r = MonitorLayout.Clamp(new TileRect(t.Col, t.Row, t.W, t.H), MonitorLayout.KindOf(t.Kind));
            (t.Col, t.Row, t.W, t.H) = (r.Col, r.Row, r.W, r.H);
        }
        // Un fichier abîmé ne doit pas empiler des cartes : on repasse par le calcul de collisions
        var fixedRects = MonitorLayout.Compact(tiles.ToDictionary(t => t.Id, t => new TileRect(t.Col, t.Row, t.W, t.H)));
        foreach (var t in tiles) (t.Col, t.Row, t.W, t.H) = (fixedRects[t.Id].Col, fixedRects[t.Id].Row, fixedRects[t.Id].W, fixedRects[t.Id].H);
        Tiles.Clear();
        foreach (var t in tiles) Tiles.Add(new MonitorTileViewModel(this, t));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary> Enregistre la disposition des widgets dans les réglages. </summary>
    public void SaveTiles()
    {
        S.MonitorTiles = Tiles.Select(t => t.ToSetting()).ToList();
        _settings.Save();
    }

    /// <summary> Positions validées par le tableau après un déplacement ou un redimensionnement. </summary>
    public void ApplyLayout(IReadOnlyDictionary<string, TileRect> layout)
    {
        foreach (var t in Tiles)
            if (layout.TryGetValue(t.Id, out var r)) (t.Col, t.Row, t.W, t.H) = (r.Col, r.Row, r.W, r.H);
        SaveTiles();
    }

    [RelayCommand]
    private void AddTile(TileChoice choice)
    {
        var k = choice.Kind;
        var spot = MonitorLayout.FindFree(Tiles.Select(t => t.Rect), k.W, k.H);
        // Plusieurs widgets GPU : on propose d'abord un GPU qui n'est pas déjà affiché par un widget de ce type
        var used = Tiles.Where(t => t.Kind == k.Kind).Select(t => t.GpuIndex).ToHashSet();
        var gpu = k.NeedsGpu ? Gpus.Select(g => g.Index).FirstOrDefault(i => !used.Contains(i), Gpus.FirstOrDefault()?.Index ?? 0) : 0;
        Tiles.Add(new MonitorTileViewModel(this, new MonitorTileSetting { Kind = k.Kind, Col = spot.Col, Row = spot.Row, W = spot.W, H = spot.H, Gpu = gpu }));
        IsAddOpen = false;
        OnPropertyChanged(nameof(IsEmpty));
        SaveTiles();
    }

    [RelayCommand]
    private void RemoveTile(MonitorTileViewModel tile)
    {
        Tiles.Remove(tile);
        ApplyLayout(MonitorLayout.Compact(Tiles.ToDictionary(t => t.Id, t => t.Rect)));
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void ResetLayout()
    {
        S.MonitorTiles = null;
        LoadTiles();
        SaveTiles();
    }

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly ISystemMonitorService _monitor;
    private readonly MonitorSettingsViewModel _monitorSettings;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    /// <param name="appState"> État partagé entre les pages. </param>
    /// <param name="monitor"> Mesures du système. </param>
    /// <param name="monitorSettings"> Réglages des courbes (historique, couleurs). </param>
    public MonitorViewModel(ISettingsService settings, IAppState appState, ISystemMonitorService monitor, MonitorSettingsViewModel monitorSettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(monitorSettings);
        _settings = settings;
        _appState = appState;
        _monitor = monitor;
        _monitorSettings = monitorSettings;
        CpuHistory = new(HistoryPoints());
        RamHistory = new(HistoryPoints());
        NetHistory = new(HistoryPoints());
        LoadTiles();
        _monitor.Updated += Apply;
        var set = _monitorSettings;
        set.ColorsChanged += RefreshColors;
        set.HistoryChanged += ResizeHistories;
        RefreshColors();
    }

    private void Apply(SystemSample s)
    {
        Cpu = s.CpuPercent;
        CpuTemp = s.CpuTemp is { } t ? $"{t:0} °C" : "—";
        CpuFreq = s.CpuFreqMhz > 0 ? $"{s.CpuFreqMhz / 1000:0.00} GHz" : "—";
        LoadAvg = string.Join("  ", s.LoadAvg.Select(x => x.ToString("0.00")));
        CpuHistory.Add(Cpu);

        RamPercent = s.RamTotal > 0 ? s.RamUsed / s.RamTotal * 100 : 0;
        RamText = $"{Format.Bytes(s.RamUsed)} / {Format.Bytes(s.RamTotal)}";
        SwapPercent = s.SwapTotal > 0 ? s.SwapUsed / s.SwapTotal * 100 : 0;
        SwapText = s.SwapTotal > 0 ? $"{Format.Bytes(s.SwapUsed)} / {Format.Bytes(s.SwapTotal)}" : Loc.T("Mon_NoSwap");
        RamHistory.Add(RamPercent);

        NetRx = $"{Format.Bytes(s.NetRx)}/s";
        NetTx = $"{Format.Bytes(s.NetTx)}/s";
        NetHistory.Add(s.NetRx);
        Uptime = Loc.F("Mon_Uptime", Format.Duration(s.Uptime));

        // Cœurs : la collection n'est reconstruite que si le nombre change
        if (Cores.Count != s.CoreUsage.Length)
        {
            Cores.Clear();
            for (int i = 0; i < s.CoreUsage.Length; i++) Cores.Add(new CoreUsage { Label = $"{i}" });
        }
        for (int i = 0; i < s.CoreUsage.Length; i++) Cores[i].Value = s.CoreUsage[i];

        // GPU
        var before = Gpus.Select(g => (g.Index, g.Name)).ToList();
        while (Gpus.Count > s.Gpus.Count) Gpus.RemoveAt(Gpus.Count - 1);
        for (int i = 0; i < s.Gpus.Count; i++)
        {
            if (i >= Gpus.Count) Gpus.Add(new GpuCardViewModel(HistoryPoints()) { Index = s.Gpus[i].Index });
            Gpus[i].Update(s.Gpus[i]);
        }
        HasGpu = Gpus.Count > 0;
        // Les widgets GPU retrouvent leur carte quand la liste ou un nom change
        if (!before.SequenceEqual(Gpus.Select(g => (g.Index, g.Name)))) foreach (var tile in Tiles) tile.RefreshGpu();

        var rows = s.GpuProcesses.Select(p => new GpuProcessRow(p.Pid, Path.GetFileName(p.Name.Split(' ')[0]), Format.Bytes(p.Memory),
            $"GPU {p.GpuIndex}", OwnerOf(p.Pid))).ToList();
        if (!rows.SequenceEqual(GpuProcesses))
        {
            GpuProcesses.Clear();
            foreach (var r in rows) GpuProcesses.Add(r);
        }
        HasGpuProcesses = GpuProcesses.Count > 0;

        try
        {
            var dir = _settings.ModelsDir;
            var d = new DriveInfo(Directory.Exists(dir) ? dir : SettingsService.Home);
            DiskPercent = d.TotalSize > 0 ? 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize : 0;
            DiskText = Loc.F("Mon_DiskFree", Format.Bytes(d.AvailableFreeSpace), Format.Bytes(d.TotalSize));
        }
        catch { DiskText = ""; }
    }

    /// <summary> Modèle lancé par l'application auquel appartient ce processus (même groupe de processus). </summary>
    private string OwnerOf(int pid)
    {
        return _appState.InstanceOfProcess(pid)?.Title ?? Loc.T("Mon_ExternalProcess");
    }
}
