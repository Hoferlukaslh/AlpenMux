/*
    Fichier      :  MonitorSettingsViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Réglages du moniteur système (page Paramètres) : fréquence d'actualisation, durée d'historique,
        arrondi / épaisseur des courbes, aire, grille et couleur de chaque courbe.
        Instance unique : le moniteur écoute HistoryChanged et ColorsChanged.
        Les courbes d'exemple défilent à la fréquence choisie tant que la page est visible (StartPreview / StopPreview).
*/

using System;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> Une couleur proposée pour une courbe (Hex null = couleur du thème). </summary>
public sealed partial class ColorSwatch : ObservableObject
{
    /// <summary> Clé de la courbe (cpu, ram…). </summary>
    public required string Key { get; init; }
    /// <summary> Couleur #RRGGBB, ou null pour la couleur du thème. </summary>
    public string? Hex { get; init; }
    /// <summary> Commande de choix de cette couleur. </summary>
    public required IRelayCommand<ColorSwatch> Pick { get; init; }
    [ObservableProperty] private IBrush _brush = Brushes.Gray;
    [ObservableProperty] private bool _isSelected;
}

public sealed partial class ChartColorRow : ObservableObject
{
    /// <summary> Clé de la courbe. </summary>
    public required string Key { get; init; }
    /// <summary> Libellé affiché. </summary>
    public required string Label { get; init; }
    /// <summary> Couleurs proposées. </summary>
    public required ColorSwatch[] Swatches { get; init; }
    /// <summary> Couleur actuelle de la courbe et courbe d'exemple qui la montre. </summary>
    [ObservableProperty] private IBrush _brush = Brushes.Gray;
    /// <summary> Données de la courbe d'exemple. </summary>
    public required RingBuffer Demo { get; init; }
}

public sealed partial class MonitorSettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    public MonitorSettingsViewModel(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _interval = Math.Clamp(S.MonitorIntervalMs, MinIntervalMs, MaxIntervalMs);
        _intervalPos = PosOf(S.MonitorIntervalMs);
        _historySeconds = S.MonitorHistorySeconds;
        _smoothing = S.MonitorSmoothing * 100;
        _lineWidth = S.MonitorLineWidth;
        _fill = S.MonitorFill;
        _grid = S.MonitorGrid;
        InitializeColorRows();
    }

    private AppSettings S => _settings.Current;

    /// <summary> Clé du réglage, libellé (clé de traduction si elle commence par « Mon_ ») et ressource de couleur du thème. </summary>
    public static readonly (string Key, string Label, string Resource)[] ChartKeys =
    [
        ("cpu", "CPU", "ChartCpu"), ("ram", "RAM", "ChartRam"), ("gpu", "GPU", "ChartGpu"),
        ("vram", "VRAM", "ChartVram"), ("net", "Mon_Network", "ChartNet"),
    ];
    private static readonly string[] Palette =
        ["#4F8CFF", "#A371F7", "#3FB950", "#F0883E", "#39C5CF", "#F85149", "#E3B341", "#FF7EB6", "#8B949E"];

    // Après les tableaux ci-dessus : le constructeur s'en sert

    /// <summary> Fréquence ou durée modifiée : les historiques doivent changer de taille. </summary>
    public event Action? HistoryChanged;
    /// <summary> Couleur choisie ou thème changé. </summary>
    public event Action? ColorsChanged;

    /// <summary> Fréquence la plus rapide (ms). </summary>
    public const int MinIntervalMs = 50;
    /// <summary> Fréquence la plus lente (ms). </summary>
    public const int MaxIntervalMs = 600_000;

    /// <summary> Position du curseur (0–1000) : échelle logarithmique de 50 ms à 600 s. </summary>
    private static double PosOf(double ms) =>
        1000 * Math.Log(Math.Clamp(ms, MinIntervalMs, MaxIntervalMs) / MinIntervalMs) / Math.Log((double)MaxIntervalMs / MinIntervalMs);

    /// <summary> Valeur en ms pour une position du curseur, arrondie (50 ms, puis 100 ms dès 1 s, puis 1 s dès 10 s). </summary>
    private static double MsOf(double pos)
    {
        var ms = MinIntervalMs * Math.Pow((double)MaxIntervalMs / MinIntervalMs, pos / 1000);
        var step = ms < 1000 ? 50 : ms < 10_000 ? 100 : 1000;
        return Math.Clamp(Math.Round(ms / step) * step, MinIntervalMs, MaxIntervalMs);
    }

    private bool _syncing;
    [ObservableProperty] private double _interval;
    [ObservableProperty] private double _intervalPos;
    [ObservableProperty] private double _historySeconds;
    [ObservableProperty] private double _smoothing;
    [ObservableProperty] private double _lineWidth;
    [ObservableProperty] private bool _fill;
    [ObservableProperty] private bool _grid;

    /// <summary> Couleurs des courbes, une ligne par courbe. </summary>
    public ChartColorRow[] ColorRows { get; private set; } = [];

    /// <summary> Fréquence des mesures affichée. </summary>
    public string IntervalText => Interval < 10_000 ? $"{Interval:0} ms" : $"{Interval / 1000:0} s";
    /// <summary> Durée de l'historique affichée. </summary>
    public string HistoryText => HistorySeconds >= 120 ? $"{HistorySeconds / 60:0.#} min" : $"{HistorySeconds:0} s";
    /// <summary> Lissage affiché. </summary>
    public string SmoothingText => $"{Smoothing:0} %";
    /// <summary> Épaisseur affichée. </summary>
    public string LineWidthText => $"{LineWidth:0.0} px";

    /// <summary> Couleur choisie pour cette courbe, sinon celle du thème actuel. </summary>
    public IBrush BrushFor(string key, string resource) =>
        S.MonitorColors.TryGetValue(key, out var hex) && Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : VramUsage.Brush(resource);

    // ---------------------------------------------------------------- Aperçu animé (données fictives)

    private readonly DispatcherTimer _previewTimer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <summary> Valeur fictive d'une courbe à l'instant t (secondes) : signal fonction du temps, comme une vraie mesure. </summary>
    private static double DemoValue(int seed, double t) =>
        Math.Clamp(50 + 28 * Math.Sin(t * 0.9 + seed) + 14 * Math.Sin(t * 3.1 + seed * 2.1) + 6 * Math.Sin(t * 8.3 + seed), 3, 97);

    /// <summary> Même durée et même fréquence que les vraies courbes (plafonné : l'aperçu est petit). </summary>
    private int DemoPoints() => Math.Min(MonitorViewModel.HistoryPoints(S), 600);

    /// <summary> Remplit l'aperçu avec le passé du signal : la courbe est pleine dès l'ouverture. </summary>
    private void FillDemo()
    {
        var n = DemoPoints();
        var dt = Math.Clamp(S.MonitorIntervalMs, MinIntervalMs, MaxIntervalMs) / 1000.0;
        var now = _clock.Elapsed.TotalSeconds;
        for (var r = 0; r < ColorRows.Length; r++)
        {
            var b = ColorRows[r].Demo;
            b.Resize(n);
            b.Clear();
            for (var i = 0; i < n; i++) b.Add(DemoValue(r + 1, now - (n - 1 - i) * dt));
        }
        _previewTimer.Interval = TimeSpan.FromSeconds(dt);
    }

    private void Tick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        for (var r = 0; r < ColorRows.Length; r++) ColorRows[r].Demo.Add(DemoValue(r + 1, now));
    }

    /// <summary> À appeler quand la page devient visible (et StopPreview quand elle disparaît). </summary>
    public void StartPreview() { FillDemo(); _previewTimer.Start(); }
    /// <summary> Arrête l'aperçu animé. </summary>
    public void StopPreview() => _previewTimer.Stop();

    private void InitializeColorRows()
    {
        ColorRows = ChartKeys.Select((k, n) => new ChartColorRow
        {
            Key = k.Key,
            Demo = new RingBuffer(DemoPoints()),
            Label = k.Label.StartsWith("Mon_") ? Loc.T(k.Label) : k.Label,
            Swatches = new[] { Swatch(k.Key, null, VramUsage.Brush(k.Resource)) }
                .Concat(Palette.Select(h => Swatch(k.Key, h, new SolidColorBrush(Color.Parse(h))))).ToArray(),
        }).ToArray();
        MarkSelected();
        _previewTimer.Tick += (_, _) => Tick();
        // Thème clair / sombre : la couleur par défaut change
        if (Application.Current is { } app)
            app.ActualThemeVariantChanged += (_, _) =>
            {
                foreach (var (r, k) in ColorRows.Zip(ChartKeys)) r.Swatches[0].Brush = VramUsage.Brush(k.Resource);
                MarkSelected();
                ColorsChanged?.Invoke();
            };
    }

    private ColorSwatch Swatch(string key, string? hex, IBrush brush) =>
        new() { Key = key, Hex = hex, Brush = brush, Pick = PickColorCommand };

    partial void OnIntervalPosChanged(double value)
    {
        if (_syncing) return;
        _syncing = true;
        Interval = MsOf(value);
        _syncing = false;
    }
    partial void OnIntervalChanged(double value)
    {
        if (!_syncing) { _syncing = true; IntervalPos = PosOf(value); _syncing = false; }
        S.MonitorIntervalMs = (int)value;
        OnPropertyChanged(nameof(IntervalText));
        Apply(history: true);
    }
    partial void OnHistorySecondsChanged(double value)
    {
        S.MonitorHistorySeconds = (int)value;
        OnPropertyChanged(nameof(HistoryText));
        Apply(history: true);
    }
    partial void OnSmoothingChanged(double value) { S.MonitorSmoothing = value / 100; OnPropertyChanged(nameof(SmoothingText)); Apply(); }
    partial void OnLineWidthChanged(double value) { S.MonitorLineWidth = value; OnPropertyChanged(nameof(LineWidthText)); Apply(); }
    partial void OnFillChanged(bool value) { S.MonitorFill = value; Apply(); }
    partial void OnGridChanged(bool value) { S.MonitorGrid = value; Apply(); }

    private void Apply(bool history = false)
    {
        _settings.Save();   // redessine aussi les courbes (Sparkline écoute Saved)
        if (history) { HistoryChanged?.Invoke(); if (_previewTimer.IsEnabled) FillDemo(); }
    }

    [RelayCommand]
    private void PickColor(ColorSwatch swatch)
    {
        if (swatch.Hex == null) S.MonitorColors.Remove(swatch.Key); else S.MonitorColors[swatch.Key] = swatch.Hex;
        MarkSelected();
        _settings.Save();
        ColorsChanged?.Invoke();
    }

    [RelayCommand]
    private void Reset()
    {
        S.MonitorColors.Clear();
        var d = new AppSettings();
        (Interval, HistorySeconds, Smoothing, LineWidth, Fill, Grid) =
            (d.MonitorIntervalMs, d.MonitorHistorySeconds, d.MonitorSmoothing * 100, d.MonitorLineWidth, d.MonitorFill, d.MonitorGrid);
        MarkSelected();
        _settings.Save();
        ColorsChanged?.Invoke();
    }

    private void MarkSelected()
    {
        foreach (var (r, k) in ColorRows.Zip(ChartKeys))
        {
            r.Brush = BrushFor(k.Key, k.Resource);
            S.MonitorColors.TryGetValue(r.Key, out var cur);
            foreach (var sw in r.Swatches) sw.IsSelected = string.Equals(sw.Hex, cur, StringComparison.OrdinalIgnoreCase);
        }
    }
}
