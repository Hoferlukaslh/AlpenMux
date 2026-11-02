/*
    Fichier      :  MainWindow.axaml.cs
    Projet       :  AlpenMux

    Description  :
        La croix masque la fenêtre : l'application continue en arrière-plan (icône de la zone
        de notification), les serveurs continuent de tourner. Si l'option est désactivée,
        la croix quitte l'application (les serveurs sont arrêtés si l'option correspondante est active).
*/

using System;
using System.Linq;
using Avalonia.Controls;
using AlpenMux.Services;
using AlpenMux.ViewModels;

namespace AlpenMux.Views;

/// <summary> Fenêtre principale : menu latéral et page affichée. </summary>
public partial class MainWindow : Window
{
    private readonly ISettingsService? _settings;
    private readonly ITrayService? _tray;

    /// <summary> Constructeur réservé à l'aperçu du concepteur XAML (aucun service). </summary>
    public MainWindow() => InitializeComponent();

    /// <summary> Crée la fenêtre. </summary>
    /// <param name="settings"> Réglages (comportement de la croix). </param>
    /// <param name="tray"> Arrière-plan et fermeture réelle de l'application. </param>
    public MainWindow(ISettingsService settings, ITrayService tray)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tray);
        _settings = settings;
        _tray = tray;
        InitializeComponent();
        Closing += OnClosing;
        _tray.StoppingServers = closing =>
        {
            if (DataContext is MainWindowViewModel vm) vm.IsClosing = closing;
        };
#if DEBUG
        // Tests manuels : ALPENMUX_MAXIMIZE=1, ALPENMUX_SCROLL=<pixels> (défile les pages visibles après 8 s)
        if (System.Environment.GetEnvironmentVariable("ALPENMUX_MAXIMIZE") == "1") WindowState = WindowState.Maximized;
        if (double.TryParse(System.Environment.GetEnvironmentVariable("ALPENMUX_SCROLL"), out var y))
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                foreach (var sv in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(this).OfType<ScrollViewer>().Where(v => v.IsEffectivelyVisible))
                    sv.Offset = new Avalonia.Vector(0, y);
            }, System.TimeSpan.FromSeconds(8));
#endif
    }

    // async void : gestionnaire d'événement (seul cas où c'est permis) ; les erreurs sont donc interceptées ici
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_tray == null || _settings == null) return;   // fenêtre du concepteur
        if (_tray.IsQuitting || e.CloseReason == WindowCloseReason.OSShutdown) return;

        e.Cancel = true;
        try
        {
            if (_settings.Current.MinimizeToTray)
                Hide();
            else
                await _tray.QuitAsync();
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Fermeture] {ex.Message}"); }
    }
}
