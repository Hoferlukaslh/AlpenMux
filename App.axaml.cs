/*
    Fichier      :  App.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Démarrage de l'application : construction du conteneur d'injection de dépendances (racine de composition,
        voir ServiceRegistration), langue et thème, fenêtre principale, icône de la zone de notification,
        instance unique et arrêt propre sur SIGTERM.
*/

using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using AlpenMux.Services;
using AlpenMux.ViewModels;
using AlpenMux.Views;

namespace AlpenMux;

/// <summary> Application Avalonia : racine de composition des services. </summary>
public partial class App : Application
{
    // Garde l'abonnement à SIGTERM en vie (il est annulé quand l'objet est libéré)
    private PosixSignalRegistration? _sigterm;
    private static ServiceProvider? _services;

    /// <summary>
    /// Conteneur des services. À n'utiliser que là où l'injection par constructeur est impossible
    /// (contrôles créés par le XAML) ; partout ailleurs, les services sont passés au constructeur.
    /// </summary>
    public static IServiceProvider Services =>
        _services ?? throw new InvalidOperationException("Le conteneur de services n'est pas encore construit.");

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        _services ??= new ServiceCollection().AddAlpenMuxServices().BuildServiceProvider();

        var settings = Services.GetRequiredService<ISettingsService>().Current;
        Loc.Instance.SetLanguage(settings.Language);
        Services.GetRequiredService<IUiServices>().ApplyTheme(settings.Theme);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var tray = Services.GetRequiredService<ITrayService>();
            // L'application continue en arrière-plan quand la fenêtre est fermée : on quitte explicitement
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var window = new MainWindow(Services.GetRequiredService<ISettingsService>(), tray)
            {
                DataContext = Services.GetRequiredService<MainWindowViewModel>(),
            };
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => _services?.Dispose();
            tray.Init();
            SingleInstance.Listen(() => Dispatcher.UIThread.Post(tray.ShowMainWindow));
            // SIGTERM (fermeture de session, kill) : arrêt propre, comme "Quitter"
            try
            {
                _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    ctx.Cancel = true;
                    Dispatcher.UIThread.Post(() => _ = tray.QuitAsync());
                });
            }
            catch (PlatformNotSupportedException) { /* signal absent sur ce système */ }
            if (!Program.StartMinimized) window.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
