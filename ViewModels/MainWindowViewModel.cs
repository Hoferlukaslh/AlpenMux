/*
    Fichier      :  MainWindowViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Navigation entre les pages (menu latéral) :
            Accueil · Installation · Modèles · Serveurs · Moniteur · Chat · Images · Audio · Paramètres
        Les ViewModels des pages sont créés une seule fois et gardés en mémoire.
        Au démarrage, si une dépendance manque, la page Installation s'ouvre directement.
*/

using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class NavItem : ObservableObject
{
    /// <summary> Clé de la page. </summary>
    public required string Key { get; init; }
    /// <summary> Glyphe de l'icône. </summary>
    public required string Icon { get; init; }
    /// <summary> Clé de traduction du libellé. </summary>
    public required string LabelKey { get; init; }
    /// <summary> ViewModel de la page. </summary>
    public required ViewModelBase Page { get; init; }

    [ObservableProperty] private string? _badge;

    /// <summary> Libellé traduit. </summary>
    public string Label => Loc.T(LabelKey);
    /// <summary> Une pastille est affichée. </summary>
    public bool HasBadge => Badge != null;

    /// <summary> Crée l'entrée (le libellé suit la langue). </summary>
    public NavItem() => Loc.Instance.LanguageChanged += () => OnPropertyChanged(nameof(Label));

    partial void OnBadgeChanged(string? value) => OnPropertyChanged(nameof(HasBadge));
}

public sealed partial class MainWindowViewModel : ViewModelBase
{
    /// <summary> Page Accueil. </summary>
    public DashboardViewModel Dashboard { get; }
    /// <summary> Page Installation. </summary>
    public SetupViewModel     Setup     { get; }
    /// <summary> Page Modèles. </summary>
    public ModelsViewModel    Models    { get; }
    /// <summary> Page Serveurs. </summary>
    public LaunchViewModel    Launch    { get; }
    /// <summary> Page Moniteur. </summary>
    public MonitorViewModel   Monitor   { get; }
    /// <summary> Page Chat (texte). </summary>
    public ChatViewModel      Chat      { get; }
    /// <summary> Page Images (génération d'images). </summary>
    public ImagesViewModel    Images    { get; }
    /// <summary> Page Audio (synthèse vocale). </summary>
    public AudioViewModel     Audio     { get; }
    /// <summary> Page Connaissances (bases RAG). </summary>
    public KnowledgeViewModel Knowledge { get; }
    /// <summary> Page Paramètres. </summary>
    public SettingsViewModel  Settings  { get; }

    /// <summary> Entrées du haut du menu. </summary>
    public ObservableCollection<NavItem> NavItems { get; }
    /// <summary> Entrées du bas du menu (Paramètres). </summary>
    public ObservableCollection<NavItem> BottomNavItems { get; }

    [ObservableProperty] private NavItem? _selectedNav;
    [ObservableProperty] private NavItem? _selectedBottomNav;
    [ObservableProperty] private ViewModelBase? _currentPage;
    [ObservableProperty] private bool _isSidebarCollapsed;
    [ObservableProperty] private bool _isClosing;

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly IApiRouter _router;
    private readonly ISystemMonitorService _monitor;
    private readonly IVllmOptions _vllmOptions;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="dashboard"> Page Accueil. </param>
    /// <param name="setup"> Page Installation. </param>
    /// <param name="models"> Page Modèles. </param>
    /// <param name="launch"> Page Serveurs. </param>
    /// <param name="monitorPage"> Page Moniteur. </param>
    /// <param name="chat"> Page Chat. </param>
    /// <param name="images"> Page Images. </param>
    /// <param name="audio"> Page Audio. </param>
    /// <param name="knowledge"> Page Connaissances (RAG). </param>
    /// <param name="settingsPage"> Page Paramètres. </param>
    /// <param name="settings"> Réglages (menu replié). </param>
    /// <param name="appState"> État partagé (navigation, serveurs). </param>
    /// <param name="router"> Routeur démarré au lancement. </param>
    /// <param name="monitor"> Mesures démarrées au lancement. </param>
    /// <param name="vllmOptions"> Options du vLLM installé, lues en arrière-plan. </param>
    public MainWindowViewModel(DashboardViewModel dashboard, SetupViewModel setup, ModelsViewModel models, LaunchViewModel launch,
        MonitorViewModel monitorPage, ChatViewModel chat, ImagesViewModel images, AudioViewModel audio, KnowledgeViewModel knowledge, SettingsViewModel settingsPage, ISettingsService settings, IAppState appState, IApiRouter router, ISystemMonitorService monitor, IVllmOptions vllmOptions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(vllmOptions);
        _settings = settings;
        _appState = appState;
        _router = router;
        _monitor = monitor;
        _vllmOptions = vllmOptions;
        _isSidebarCollapsed = settings.Current.SidebarCollapsed;
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(monitorPage);
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentNullException.ThrowIfNull(images);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(settingsPage);
        Dashboard = dashboard;
        Setup = setup;
        Models = models;
        Launch = launch;
        Monitor = monitorPage;
        Chat = chat;
        Images = images;
        Audio = audio;
        Knowledge = knowledge;
        Settings = settingsPage;
        NavItems =
        [
            new NavItem { Key = "Home",    Icon = "", LabelKey = "Nav_Home",    Page = Dashboard },
            new NavItem { Key = "Setup",   Icon = "\ue5d4", LabelKey = "Nav_Setup",   Page = Setup },
            new NavItem { Key = "Models",  Icon = "", LabelKey = "Nav_Models",  Page = Models },
            new NavItem { Key = "Launch",  Icon = "", LabelKey = "Nav_Servers", Page = Launch },
            new NavItem { Key = "Monitor", Icon = "", LabelKey = "Nav_Monitor", Page = Monitor },
            new NavItem { Key = "Chat",    Icon = "", LabelKey = "Nav_Chat",    Page = Chat },
            new NavItem { Key = "Images",  Icon = "\ue2ca", LabelKey = "Nav_Images", Page = Images },
            new NavItem { Key = "Audio",   Icon = "\ue802", LabelKey = "Nav_Audio",  Page = Audio },
            new NavItem { Key = "Knowledge", Icon = "\ue1de", LabelKey = "Nav_Knowledge", Page = Knowledge },
        ];
        BottomNavItems =
        [
            new NavItem { Key = "Settings", Icon = "", LabelKey = "Nav_Settings", Page = Settings },
        ];

        Launch.ChatTarget = OpenInTester;
        _appState.NavigationRequested += Navigate;
        _appState.InstancesChanged += UpdateBadges;
        Setup.StatusChanged += UpdateBadges;
        Models.DownloadsChanged += UpdateBadges;

        _monitor.Start();
        if (LinuxHost.IsWsl && WslService.DistroInstalled)
            _ = System.Threading.Tasks.Task.Run(() => LinuxHost.Exec("true", [], 60000));   // démarre la distribution WSL en avance
        _ = _appState.RefreshModelsAsync();
        _appState.LoadHistory();
        _router.Apply();
        _ = _vllmOptions.EnsureAsync();   // options du vLLM installé, lues en arrière-plan

        Setup.Refresh();
        Navigate(Setup.AllRequiredOk ? "Home" : "Setup");
        UpdateBadges();
#if DEBUG
        DebugAutomation();
#endif
    }

#if DEBUG
    /// <summary>
    /// Aides aux tests manuels (build Debug uniquement) : ALPENMUX_PAGE=Launch ouvre une page,
    /// ALPENMUX_LAUNCH=nom lance le favori de ce nom, ALPENMUX_SERVER=1 affiche le premier serveur.
    /// </summary>
    private void DebugAutomation()
    {
        if (Environment.GetEnvironmentVariable("ALPENMUX_LAUNCH") is { Length: > 0 } fav &&
            _appState.Favorites.FirstOrDefault(f => f.Name == fav) is { } f)
            Avalonia.Threading.DispatcherTimer.RunOnce(() => Launch.LaunchFavoriteCommand.Execute(f), TimeSpan.FromSeconds(4));
        if (Environment.GetEnvironmentVariable("ALPENMUX_PAGE") is { Length: > 0 } page)
            Avalonia.Threading.DispatcherTimer.RunOnce(() => Navigate(page), TimeSpan.FromSeconds(3));
        if (Environment.GetEnvironmentVariable("ALPENMUX_SERVER") == "1")
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (_appState.Instances.FirstOrDefault() is not { } i) return;
                Navigate("Launch");
                Launch.SelectedInstance = i;
                if (Environment.GetEnvironmentVariable("ALPENMUX_TAB") is { } tab && int.TryParse(tab, out var t)) i.SelectedTab = t;
            }, TimeSpan.FromSeconds(6));
    }
#endif

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value == null) return;
        SelectedBottomNav = null;
        CurrentPage = value.Page;
        value.Page.OnNavigatedTo();
    }

    partial void OnSelectedBottomNavChanged(NavItem? value)
    {
        if (value == null) return;
        SelectedNav = null;
        CurrentPage = value.Page;
        value.Page.OnNavigatedTo();
    }

    partial void OnIsSidebarCollapsedChanged(bool value)
    {
        _settings.Current.SidebarCollapsed = value;
        _settings.Save();
    }

    /// <summary>
    /// Bouton « Tester » d'un serveur : chaque type de modèle a sa page (texte, image, voix).
    /// </summary>
    private void OpenInTester(InstanceViewModel inst)
    {
        if (inst.IsTts) { Audio.SelectTarget(inst); Navigate("Audio"); }
        else if (inst.IsImageGen) { Images.SelectTarget(inst); Navigate("Images"); }
        else { Chat.SelectTarget(inst); Navigate("Chat"); }
    }

    /// <summary> Affiche la page de cette clé ("Home", "Setup", "Models", "Launch", "Monitor", "Chat", "Images", "Audio", "Knowledge", "Settings"). </summary>
    public void Navigate(string key)
    {
        var item = NavItems.FirstOrDefault(n => n.Key == key);
        if (item != null) { SelectedNav = item; return; }
        var bottom = BottomNavItems.FirstOrDefault(n => n.Key == key);
        if (bottom != null) SelectedBottomNav = bottom;
    }

    private void UpdateBadges()
    {
        var running = _appState.RunningCount;
        NavItems.First(n => n.Key == "Launch").Badge = running > 0 ? running.ToString() : null;
        NavItems.First(n => n.Key == "Setup").Badge = Setup.AllRequiredOk ? null : "!";
        var downloads = Models.RunningDownloads;
        NavItems.First(n => n.Key == "Models").Badge = downloads > 0 ? $"{downloads} ↓" : null;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    /// <summary> Bascule rapide clair / sombre depuis le menu latéral. </summary>
    [RelayCommand]
    private void ToggleTheme()
    {
        var isDark = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        Settings.SelectedTheme = isDark ? "Light" : "Dark";
    }
}
