/*
    Fichier      :  TrayService.cs
    Projet       :  AlpenMux

    Description  :
        Fonctionnement en arrière-plan (comme Teams) :
            - icône dans la zone de notification (barre des tâches, à côté de l'horloge)
            - clic gauche sur l'icône : afficher / masquer la fenêtre
            - clic droit : menu (afficher, nombre de serveurs actifs, arrêter tous les serveurs, quitter)
              - Windows : icône Shell_NotifyIcon (WindowsTrayIcon) + fenêtre de menu dédiée (TrayMenuWindow),
                dont la taille suit le contenu (le menu natif d'Avalonia affichait une flèche de défilement)
              - autres systèmes : TrayIcon d'Avalonia avec son menu natif
        La croix de la fenêtre masque l'application au lieu de la quitter (option dans les paramètres).
*/

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using AlpenMux.Views;

namespace AlpenMux.Services;

/// <summary> Fonctionnement en arrière-plan : icône de la zone de notification, affichage de la fenêtre et fermeture réelle. </summary>
public interface ITrayService
{
    /// <summary> Vrai pendant la fermeture réelle de l'application. </summary>
    bool IsQuitting { get; }

    /// <summary> Appelé pendant la fermeture si des serveurs doivent être arrêtés (affichage d'un message). </summary>
    Action<bool>? StoppingServers { get; set; }

    /// <summary> Travail à terminer avant de quitter (arrêt des téléchargements). </summary>
    event Func<Task>? BeforeQuit;

    /// <summary> Crée l'icône de la zone de notification et son menu. </summary>
    void Init();

    /// <summary> Affiche la fenêtre principale au premier plan. </summary>
    void ShowMainWindow();

    /// <summary> Fermeture réelle : arrête les serveurs si l'option est active, puis quitte. </summary>
    Task QuitAsync();
}

/// <summary> Implémentation de <see cref="ITrayService"/>. </summary>
public sealed class TrayService : ITrayService
{
    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly IApiRouter _router;
    private readonly ISystemMonitorService _monitor;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (arrêt des serveurs à la fermeture). </param>
    /// <param name="appState"> Serveurs lancés (menu, arrêt). </param>
    /// <param name="router"> Routeur arrêté à la fermeture. </param>
    /// <param name="monitor"> Mesures arrêtées à la fermeture. </param>
    public TrayService(ISettingsService settings, IAppState appState, IApiRouter router, ISystemMonitorService monitor)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(monitor);
        _settings = settings;
        _appState = appState;
        _router = router;
        _monitor = monitor;
    }

    // Avalonia (hors Windows)
    private TrayIcon? _tray;
    private NativeMenuItem? _show, _servers, _stopAll, _quit;

    // Windows
    private WindowsTrayIcon? _winTray;
    private TrayMenuWindow? _menu;

    /// <inheritdoc />
    public bool IsQuitting { get; private set; }

    /// <inheritdoc />
    public Action<bool>? StoppingServers { get; set; }

    /// <inheritdoc />
    public event Func<Task>? BeforeQuit;

    private static IClassicDesktopStyleApplicationLifetime? Desktop =>
        Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <inheritdoc />
    public void Init()
    {
        if (Application.Current is not { } app) return;

        Loc.Instance.LanguageChanged += Update;
        _appState.InstancesChanged += () => Dispatcher.UIThread.Post(Update);

        if (OperatingSystem.IsWindows()) InitWindows();
        else InitAvalonia(app);
        Update();
    }

    private void InitWindows()
    {
        _winTray = new WindowsTrayIcon(ReadIcon(), AppPaths.DisplayName);
        _winTray.LeftClick += ToggleMainWindow;
        _winTray.RightClick += OpenMenu;

        _menu = new TrayMenuWindow();
        _menu.ShowRequested += ShowMainWindow;
        _menu.StopAllRequested += () => _ = _appState.StopAllAsync();
        _menu.QuitRequested += () => _ = QuitAsync();
    }

    private void InitAvalonia(Application app)
    {
        _show = new NativeMenuItem();
        _show.Click += (_, _) => ShowMainWindow();
        _servers = new NativeMenuItem { IsEnabled = false };
        _stopAll = new NativeMenuItem();
        _stopAll.Click += async (_, _) => await _appState.StopAllAsync();
        _quit = new NativeMenuItem();
        _quit.Click += async (_, _) => await QuitAsync();

        var menu = new NativeMenu();
        menu.Items.Add(_show);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_servers);
        menu.Items.Add(_stopAll);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_quit);

        _tray = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://alpenmux/Assets/icon-64.png"))),
            Menu = menu,
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => ToggleMainWindow();
        TrayIcon.SetIcons(app, new TrayIcons { _tray });
    }

    private static byte[] ReadIcon()
    {
        using var stream = AssetLoader.Open(new Uri("avares://alpenmux/Assets/icon-64.png"));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private void Update()
    {
        var running = _appState.RunningCount;
        var tooltip = running == 0 ? AppPaths.DisplayName : $"{AppPaths.DisplayName} — {Loc.F("Dash_ServersCount", running)}";

        if (_winTray != null)
        {
            _winTray.SetTooltip(tooltip);
            return;
        }
        if (_tray == null) return;
        _show!.Header = Loc.T("Tray_Show");
        _servers!.Header = running == 0 ? Loc.T("Dash_NoServer") : Loc.F("Dash_ServersCount", running);
        _stopAll!.Header = Loc.T("Tray_StopAll");
        _stopAll.IsEnabled = running > 0;
        _quit!.Header = Loc.T("Tray_Quit");
        _tray.ToolTipText = tooltip;
    }

    private void OpenMenu(PixelPoint cursor)
    {
        var running = _appState.RunningCount;
        var serversText = running == 0 ? Loc.T("Dash_NoServer") : Loc.F("Dash_ServersCount", running);
        _menu?.OpenAt(cursor, serversText, running > 0);
    }

    /// <inheritdoc />
    public void ShowMainWindow()
    {
        if (Desktop?.MainWindow is not { } w) return;
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    private void ToggleMainWindow()
    {
        if (Desktop?.MainWindow is not { } w) return;
        if (w.IsVisible && w.WindowState != WindowState.Minimized) w.Hide();
        else ShowMainWindow();
    }

    /// <inheritdoc />
    public async Task QuitAsync()
    {
        if (IsQuitting) return;
        IsQuitting = true;
        _router.Stop();
        if (BeforeQuit != null)
            foreach (var f in BeforeQuit.GetInvocationList().Cast<Func<Task>>())
                try { await f(); } catch { /* un travail en échec ne doit pas empêcher de quitter */ }
        if (_settings.Current.StopServersOnExit && _appState.RunningCount > 0)
        {
            StoppingServers?.Invoke(true);
            await _appState.StopAllAsync();
        }
        _monitor.Stop();
        // Supprime l'icône tout de suite : sinon elle reste affichée jusqu'au survol de la barre des tâches
        _winTray?.Dispose();
        _menu?.Close();
        if (_tray != null) _tray.IsVisible = false;
        Desktop?.Shutdown();
    }
}
