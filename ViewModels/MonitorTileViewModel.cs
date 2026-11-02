/*
    Fichier      :  MonitorTileViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Un widget de la page Moniteur : type, position et taille sur la grille, titre choisi et GPU affiché.
        Les widgets lisent leurs mesures dans Monitor (CPU, RAM…) ou dans Gpu (le GPU choisi pour ce widget).
*/

using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class MonitorTileViewModel : ObservableObject
{
    /// <summary> Identifiant du widget. </summary>
    public string Id { get; }
    /// <summary> Type de widget. </summary>
    public string Kind { get; }
    /// <summary> Description du type (null si inconnu). </summary>
    public TileKind? Info { get; }
    /// <summary> Page Moniteur (mesures affichées). </summary>
    public MonitorViewModel Monitor { get; }

    [ObservableProperty] private int _col;
    [ObservableProperty] private int _row;
    [ObservableProperty] private int _w;
    [ObservableProperty] private int _h;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private int _gpuIndex;

    /// <summary> Crée le widget à partir de sa disposition enregistrée. </summary>
    /// <param name="monitor"> Page Moniteur. </param>
    /// <param name="s"> Disposition enregistrée. </param>
    public MonitorTileViewModel(MonitorViewModel monitor, MonitorTileSetting s)
    {
        Monitor = monitor;
        Id = s.Id;
        Kind = s.Kind;
        Info = MonitorLayout.KindOf(s.Kind);
        (_col, _row, _w, _h) = (s.Col, s.Row, s.W, s.H);
        _title = s.Title ?? "";
        _gpuIndex = s.Gpu;
    }

    /// <summary> Disposition à enregistrer. </summary>
    public MonitorTileSetting ToSetting() => new()
    {
        Id = Id, Kind = Kind, Col = Col, Row = Row, W = W, H = H,
        Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(), Gpu = GpuIndex,
    };

    /// <summary> Rectangle occupé dans la grille. </summary>
    public TileRect Rect => new(Col, Row, W, H);
    /// <summary> Le widget affiche un GPU. </summary>
    public bool NeedsGpu => Info?.NeedsGpu == true;
    /// <summary> Carte du GPU affiché, ou null. </summary>
    public GpuCardViewModel? Gpu => NeedsGpu ? Monitor.Gpus.FirstOrDefault(g => g.Index == GpuIndex) : null;
    /// <summary> Le GPU affiché existe. </summary>
    public bool HasGpu => Gpu != null;
    /// <summary> Le GPU affiché n'existe pas (ou plus). </summary>
    public bool NoGpu => NeedsGpu && Gpu == null;

    /// <summary> Choix dans la liste des GPU (modifier le widget). </summary>
    public GpuCardViewModel? SelectedGpu
    {
        get => Gpu;
        set { if (value != null) GpuIndex = value.Index; }
    }

    /// <summary> Titre choisi, sinon le titre par défaut du widget. </summary>
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title.Trim() : DefaultTitle;

    /// <summary> Titre par défaut selon le type. </summary>
    public string DefaultTitle => Kind switch
    {
        "gpu" => Gpu?.Name ?? Info!.Name,
        _ when NeedsGpu => Monitor.Gpus.Count > 1 ? $"{Info!.Name} · GPU {GpuIndex}" : Info!.Name,
        _ => Info?.Name ?? Kind,
    };

    partial void OnTitleChanged(string value) { OnPropertyChanged(nameof(DisplayTitle)); Monitor.SaveTiles(); }
    partial void OnGpuIndexChanged(int value) { RefreshGpu(); Monitor.SaveTiles(); }
    partial void OnColChanged(int value) => OnPropertyChanged(nameof(Rect));
    partial void OnRowChanged(int value) => OnPropertyChanged(nameof(Rect));
    partial void OnWChanged(int value) => OnPropertyChanged(nameof(Rect));
    partial void OnHChanged(int value) => OnPropertyChanged(nameof(Rect));

    /// <summary> La liste des GPU ou leur nom a changé. </summary>
    public void RefreshGpu()
    {
        OnPropertyChanged(nameof(Gpu));
        OnPropertyChanged(nameof(SelectedGpu));
        OnPropertyChanged(nameof(HasGpu));
        OnPropertyChanged(nameof(NoGpu));
        OnPropertyChanged(nameof(DefaultTitle));
        OnPropertyChanged(nameof(DisplayTitle));
    }
}
