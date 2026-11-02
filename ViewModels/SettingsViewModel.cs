/*
    Fichier      :  SettingsViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Paramètres : langue, thème, chemins (venv, modèles), installation de vLLM,
        valeurs par défaut du serveur, fréquence du moniteur, comportement à la fermeture.
        Chaque modification est enregistrée immédiatement.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private AppSettings S => _settings.Current;

    /// <summary> Langues disponibles. </summary>
    public IReadOnlyList<LanguageInfo> Languages { get; } = Loc.Instance.AvailableLanguages();

    [ObservableProperty] private LanguageInfo? _selectedLanguage;
    [ObservableProperty] private string _selectedTheme;
    [ObservableProperty] private string _venvPath;
    [ObservableProperty] private string _modelsDir;
    [ObservableProperty] private string _pythonVersion;
    [ObservableProperty] private string _vllmSpec;
    [ObservableProperty] private string _defaultHost;
    [ObservableProperty] private string _defaultPort;
    [ObservableProperty] private bool _stopServersOnExit;
    [ObservableProperty] private bool _mermaidAutoFix;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autostart = AutostartService.IsEnabled;
    [ObservableProperty] private string _savedText = "";

    // Contexte automatique
    [ObservableProperty] private string _contextCap;

    // Adresse API unique (routeur)
    [ObservableProperty] private bool _routerEnabled;
    [ObservableProperty] private string _routerHost;
    [ObservableProperty] private string _routerPort;
    [ObservableProperty] private string _routerApiKey;
    [ObservableProperty] private bool _routerAutoSleep;
    /// <summary> État du routeur affiché. </summary>
    public string RouterStatus => _router.IsRunning ? Loc.F("Router_Running", _router.BaseUrl)
        : _router.Error is { } e ? "✘ " + e : Loc.T("Router_Stopped");
    /// <summary> Classe de style de l'état du routeur. </summary>
    public string RouterStatusClass => _router.IsRunning ? "ok" : _router.Error != null ? "error" : "muted";

    /// <summary> Thème du système choisi. </summary>
    public bool IsSystemTheme { get => SelectedTheme == "System"; set { if (value) SelectedTheme = "System"; } }
    /// <summary> Thème clair choisi. </summary>
    public bool IsLightTheme  { get => SelectedTheme == "Light";  set { if (value) SelectedTheme = "Light"; } }
    /// <summary> Thème sombre choisi. </summary>
    public bool IsDarkTheme   { get => SelectedTheme == "Dark";   set { if (value) SelectedTheme = "Dark"; } }

    /// <summary> Réglages du moniteur (carte de la page). </summary>
    public MonitorSettingsViewModel MonitorSettings => _monitorSettings;
    /// <summary> Dossier de configuration. </summary>
    public string ConfigDir => SettingsService.ConfigDirectory;
    /// <summary> Version affichée dans « À propos ». </summary>
    public string AboutVersion => $"{AppPaths.Version} — Avalonia UI · .NET 10";
    /// <summary> Dossier des traductions ajoutées par l'utilisateur. </summary>
    public string LangDir => Loc.UserLangDirectory;

    private readonly ISettingsService _settings;
    private readonly IAppState _appState;
    private readonly IApiRouter _router;
    private readonly IUiServices _ui;
    private readonly MonitorSettingsViewModel _monitorSettings;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    /// <param name="appState"> État partagé entre les pages. </param>
    /// <param name="router"> Routeur intégré. </param>
    /// <param name="ui"> Services de l'interface (fichiers, presse-papiers, liens). </param>
    /// <param name="monitorSettings"> Carte « Moniteur » des paramètres. </param>
    public SettingsViewModel(ISettingsService settings, IAppState appState, IApiRouter router, IUiServices ui, MonitorSettingsViewModel monitorSettings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(router);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(monitorSettings);
        _settings = settings;
        _appState = appState;
        _router = router;
        _ui = ui;
        _monitorSettings = monitorSettings;
        _minimizeToTray = S.MinimizeToTray;
        _selectedTheme = S.Theme;
        _venvPath = S.VenvPath;
        _modelsDir = S.ModelsDir;
        _pythonVersion = S.PythonVersion;
        _vllmSpec = S.VllmPackageSpec;
        _defaultHost = S.DefaultHost;
        _defaultPort = S.DefaultPort.ToString();
        _stopServersOnExit = S.StopServersOnExit;
        _mermaidAutoFix = S.MermaidAutoFix;
        _contextCap = S.AutoContextCap > 0 ? S.AutoContextCap.ToString() : "";
        _routerEnabled = S.RouterEnabled;
        _routerHost = S.RouterHost;
        _routerPort = S.RouterPort.ToString();
        _routerApiKey = S.RouterApiKey ?? "";
        _routerAutoSleep = S.RouterAutoSleep;
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == S.Language) ?? Languages.First();
        _router.Changed += () =>
        {
            OnPropertyChanged(nameof(RouterStatus));
            OnPropertyChanged(nameof(RouterStatusClass));
        };
    }

    partial void OnContextCapChanged(string value)
    {
        var v = value.Trim();
        if (v.Length == 0) { S.AutoContextCap = 0; Save(); }
        else if (int.TryParse(v, out var c) && c >= 512) { S.AutoContextCap = c; Save(); }
    }

    partial void OnRouterEnabledChanged(bool value) { S.RouterEnabled = value; Save(); _router.Apply(); }
    partial void OnRouterAutoSleepChanged(bool value) { S.RouterAutoSleep = value; Save(); }
    partial void OnRouterApiKeyChanged(string value) { S.RouterApiKey = value.Trim().Length == 0 ? null : value.Trim(); Save(); }

    /// <summary> Adresse et port : appliqués au clic (le routeur redémarre). </summary>
    [RelayCommand]
    private void ApplyRouter()
    {
        S.RouterHost = RouterHost.Trim().Length == 0 ? "127.0.0.1" : RouterHost.Trim();
        if (int.TryParse(RouterPort, out var p) && p is > 0 and < 65536) S.RouterPort = p;
        Save();
        _router.Apply();
    }

    [RelayCommand]
    private Task CopyRouterUrl() => _ui.CopyAsync(_router.BaseUrl);

    partial void OnSelectedLanguageChanged(LanguageInfo? value)
    {
        if (value == null) return;
        S.Language = value.Code;
        Save();
        Loc.Instance.SetLanguage(value.Code);
    }

    partial void OnSelectedThemeChanged(string value)
    {
        S.Theme = value;
        Save();
        _ui.ApplyTheme(value);
        OnPropertyChanged(nameof(IsSystemTheme));
        OnPropertyChanged(nameof(IsLightTheme));
        OnPropertyChanged(nameof(IsDarkTheme));
    }

    partial void OnPythonVersionChanged(string value) { S.PythonVersion = value.Trim(); Save(); }
    partial void OnVllmSpecChanged(string value) { if (value.Trim().Length > 0) { S.VllmPackageSpec = value.Trim(); Save(); } }
    partial void OnDefaultHostChanged(string value) { S.DefaultHost = value.Trim(); Save(); }
    partial void OnDefaultPortChanged(string value)
    {
        if (int.TryParse(value, out var p) && p is > 0 and < 65536) { S.DefaultPort = p; Save(); }
    }
    partial void OnStopServersOnExitChanged(bool value) { S.StopServersOnExit = value; Save(); }
    partial void OnMermaidAutoFixChanged(bool value) { S.MermaidAutoFix = value; Save(); }
    partial void OnMinimizeToTrayChanged(bool value) { S.MinimizeToTray = value; Save(); }
    partial void OnAutostartChanged(bool value) { AutostartService.SetEnabled(value); SavedText = Loc.T("Settings_Saved"); }

    private void Save()
    {
        _settings.Save();
        SavedText = Loc.T("Settings_Saved");
    }

    [RelayCommand]
    private void ApplyVenv() { S.VenvPath = LinuxHost.ToLinux(VenvPath.Trim()); VenvPath = S.VenvPath; Save(); }

    [RelayCommand]
    private async Task ApplyModelsDirAsync()
    {
        S.ModelsDir = ModelsDir.Trim();
        Save();
        await _appState.RefreshModelsAsync();
    }

    [RelayCommand]
    private async Task BrowseVenv()
    {
        var p = await _ui.PickFolderAsync(Loc.T("Dep_Venv_Choose"), _settings.VenvPath);
        if (p != null) { VenvPath = p; ApplyVenv(); }
    }

    [RelayCommand]
    private async Task BrowseModels()
    {
        var p = await _ui.PickFolderAsync(Loc.T("Models_ChooseDir"), _settings.ModelsDir);
        if (p != null) { ModelsDir = p; await ApplyModelsDirAsync(); }
    }

    [RelayCommand]
    private void OpenConfigDir()
    {
        Directory.CreateDirectory(ConfigDir);
        _ui.Open(ConfigDir);
    }

    /// <summary> Crée le dossier des traductions avec une copie du français à traduire. </summary>
    [RelayCommand]
    private void OpenLangDir()
    {
        Directory.CreateDirectory(LangDir);
        var template = Path.Combine(LangDir, "template.json.example");
        if (!File.Exists(template))
        {
            using var s = Avalonia.Platform.AssetLoader.Open(new System.Uri("avares://alpenmux/Assets/Lang/fr.json"));
            using var f = File.Create(template);
            s.CopyTo(f);
        }
        _ui.Open(LangDir);
    }
}
