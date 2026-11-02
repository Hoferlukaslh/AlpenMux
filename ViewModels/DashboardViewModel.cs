/*
    Fichier      :  DashboardViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Accueil : vue d'ensemble (état de l'installation, ressources, serveurs actifs)
        et raccourcis vers les actions courantes.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class DashboardViewModel : ViewModelBase
{
    /// <summary> Serveurs lancés. </summary>
    public ObservableCollection<InstanceViewModel> Instances => _appState.Instances;

    /// <summary> Une barre de VRAM par GPU : autres applications, serveurs de l'application, libre. </summary>
    public ObservableCollection<GpuVramView> GpuVram { get; } = new();
    /// <summary> Au moins une barre de VRAM à afficher. </summary>
    public bool HasGpuVram => GpuVram.Count > 0;

    [ObservableProperty] private string _gpuName = "—";
    [ObservableProperty] private double _gpuUtil;
    [ObservableProperty] private double _vramPercent;
    [ObservableProperty] private string _vramText = "";
    [ObservableProperty] private double _cpu;
    /// <summary> Nom du processeur. </summary>
    public string CpuName { get; }
    [ObservableProperty] private double _ramPercent;
    [ObservableProperty] private string _ramText = "";
    [ObservableProperty] private string _modelsText = "";
    [ObservableProperty] private string _serversText = "";
    [ObservableProperty] private bool _hasInstances;
    [ObservableProperty] private bool _setupOk = true;
    [ObservableProperty] private string _vllmText = "";

    // Adresse unique (routeur)
    [ObservableProperty] private bool _routerRunning;
    [ObservableProperty] private string _routerUrl = "";
    [ObservableProperty] private string _routerModels = "";

    [RelayCommand]
    private System.Threading.Tasks.Task CopyRouterUrl() => _ui.CopyAsync(RouterUrl);

    private readonly IAppState _appState;
    private readonly IApiRouter _router;
    private readonly IPythonEnvironment _python;
    private readonly ISystemMonitorService _monitor;
    private readonly IUiServices _ui;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="appState"> État partagé entre les pages. </param>
    /// <param name="router"> Routeur intégré. </param>
    /// <param name="python"> Lecture du venv. </param>
    /// <param name="monitor"> Mesures du système. </param>
    /// <param name="ui"> Services de l'interface (fichiers, presse-papiers, liens). </param>
    public DashboardViewModel(IAppState appState, IApiRouter router, IPythonEnvironment python, ISystemMonitorService monitor, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(ui);
        _appState = appState;
        _router = router;
        _python = python;
        _monitor = monitor;
        _ui = ui;
        CpuName = _monitor.CpuName;
        _monitor.Updated += s =>
        {
            Cpu = s.CpuPercent;
            RamPercent = s.RamTotal > 0 ? s.RamUsed / s.RamTotal * 100 : 0;
            RamText = $"{Format.Bytes(s.RamUsed)} / {Format.Bytes(s.RamTotal)}";
            if (s.Gpus.Count > 0)
            {
                var used = s.Gpus.Sum(g => g.MemUsed);
                var total = s.Gpus.Sum(g => g.MemTotal);
                GpuName = s.Gpus.Count == 1 ? s.Gpus[0].Name : $"{s.Gpus.Count} × {s.Gpus[0].Name}";
                GpuUtil = s.Gpus.Average(g => g.Util);
                VramPercent = total > 0 ? used / total * 100 : 0;
                VramText = $"{Format.Bytes(used)} / {Format.Bytes(total)}";
            }
            else GpuName = Loc.T("Dash_NoGpu");
            UpdateGpuVram(s);
        };
        _appState.ModelsChanged += Update;
        _appState.InstancesChanged += Update;
        _router.Changed += Update;
        Loc.Instance.LanguageChanged += Update;
        Update();
    }

    /// <inheritdoc />
    public override void OnNavigatedTo() => Update();

    private void UpdateGpuVram(SystemSample s)
    {
        // Mise à jour en place (pas de reconstruction à chaque mesure)
        while (GpuVram.Count > s.Gpus.Count) GpuVram.RemoveAt(GpuVram.Count - 1);
        while (GpuVram.Count < s.Gpus.Count) GpuVram.Add(new GpuVramView());
        for (int i = 0; i < s.Gpus.Count; i++)
        {
            var gpu = s.Gpus[i];
            var segs = VramUsage.Occupied(_appState, s, gpu, out var servers);
            var free = Math.Max(0, gpu.MemTotal - gpu.MemUsed);
            segs.Add(VramUsage.Free(free));
            var v = GpuVram[i];
            v.Title = Loc.F("Vram_Title", gpu.Index, gpu.Name, Format.Bytes(gpu.MemTotal));
            v.Status = servers > 0
                ? Loc.F("Dash_VramStatus", Format.Bytes(free), Format.Bytes(servers))
                : Loc.F("Dash_VramStatusNoServer", Format.Bytes(free));
            v.Segments = segs;
            v.Total = gpu.MemTotal;
        }
        OnPropertyChanged(nameof(HasGpuVram));
    }

    /// <summary> Met à jour les mesures affichées. </summary>
    public void Update()
    {
        ModelsText = Loc.F("Dash_ModelsCount", _appState.Models.Count);
        var running = _appState.RunningCount;
        ServersText = running == 0 ? Loc.T("Dash_NoServer") : Loc.F("Dash_ServersCount", running);
        HasInstances = Instances.Count > 0;
        SetupOk = _python.VllmInstalled;
        VllmText = _python.VllmVersion is { } v ? $"vLLM {v}" : Loc.T("Dash_VllmMissing");
        RouterRunning = _router.IsRunning;
        RouterUrl = _router.BaseUrl;
        var names = Instances.Where(i => i.IsReady).Select(i => i.IsSleeping ? $"{i.ServedName} ({Loc.T("Inst_Sleeping")})" : i.ServedName).ToList();
        RouterModels = names.Count == 0 ? Loc.T("Dash_Router_NoModel") : Loc.F("Dash_Router_Models", string.Join(", ", names));
    }

    [RelayCommand]
    private void Go(string page) => _appState.Navigate(page);

    [RelayCommand]
    private void OpenServer(InstanceViewModel inst) => _appState.Navigate("Launch");

    [RelayCommand]
    private System.Threading.Tasks.Task CopyUrl(InstanceViewModel inst) => _ui.CopyAsync(inst.ApiUrl);
}

public sealed partial class GpuVramView : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private IReadOnlyList<AlpenMux.Controls.VramSegment> _segments = [];
    [ObservableProperty] private double _total;
}
