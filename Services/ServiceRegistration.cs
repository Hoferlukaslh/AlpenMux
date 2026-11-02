/*
    Fichier      :  ServiceRegistration.cs
    Projet       :  AlpenMux

    Description  :
        Enregistrement des services et des ViewModels dans le conteneur d'injection de dépendances
        (Microsoft.Extensions.DependencyInjection). Le conteneur est construit au démarrage par App.

        Durées de vie :
            - singletons : tous les services (un seul état partagé par l'application) et les ViewModels
              de page (créés une seule fois et gardés en mémoire, comme avant) ;
            - les serveurs (InstanceViewModel) sont créés à l'exécution par IInstanceViewModelFactory.

        Restent statiques (pas de dépendance, ou nécessaires avant la création du conteneur) :
        AppPaths, LinuxHost, ProcessRunner, WslService, AutostartService, SingleInstance, WindowsSystemInfo,
        les fonctions de calcul pures (ArgsPlanner, GgufReader, SafetensorsReader, MonitorLayout, VramUsage…)
        et Loc (utilisé par l'extension de balisage {l:Tr} du XAML, aussi enregistré ici).
*/

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AlpenMux.ViewModels;

namespace AlpenMux.Services;

/// <summary> Enregistrement des services de l'application dans le conteneur. </summary>
public static class ServiceRegistration
{
    /// <summary> Ajoute tous les services et ViewModels d'AlpenMux. </summary>
    /// <param name="services"> Collection à compléter. </param>
    /// <returns> La même collection, pour enchaîner les appels. </returns>
    public static IServiceCollection AddAlpenMuxServices(this IServiceCollection services)
    {
        // Debug : fenêtre du débogueur ; avertissements et erreurs aussi sur la sortie d'erreur, comme avant
        services.AddLogging(b => b.AddDebug().AddProvider(new StderrLoggerProvider()).SetMinimumLevel(LogLevel.Debug));

        // Réglages, état partagé, interface
        services.AddSingleton(Loc.Instance);
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<IAppState, AppState>();
        services.AddSingleton<IUiServices, UiServices>();
        services.AddSingleton<IImageGalleryStore, ImageGalleryStore>();
        services.AddSingleton<ITrayService, TrayService>();

        // Système, routeur, audio
        services.AddSingleton<ISystemMonitorService, SystemMonitorService>();
        services.AddSingleton<IApiRouter, ApiRouter>();
        services.AddSingleton<IAudioPlayer, AudioPlayer>();

        // Environnement Python, installation
        services.AddSingleton<IPythonEnvironment, PythonEnvironment>();
        services.AddSingleton<IUvPipInstaller, UvPipInstaller>();
        services.AddSingleton<IEnvironmentChecker, EnvironmentChecker>();
        services.AddSingleton<IDependencyService, DependencyService>();
        services.AddSingleton<ILlamaCppService, LlamaCppService>();
        services.AddSingleton<IStableDiffusionCppService, StableDiffusionCppService>();
        services.AddSingleton<IMermaidService, MermaidService>();
        services.AddSingleton<ILatexService, LatexService>();
        services.AddSingleton<IMarkItDownService, MarkItDownService>();
        // RAG : embeddings par le serveur choisi, index SQLite, indexation et recherche
        services.AddSingleton<IEmbeddingClient, EmbeddingClient>();
        services.AddSingleton<IRagIndexer, RagIndexer>();
        services.AddSingleton<IRagSearch, RagSearch>();

        // vLLM : options, parsers, compatibilité, recettes
        services.AddSingleton<IVllmOptions, VllmOptions>();
        services.AddSingleton<IChatParsers, ChatParsers>();
        services.AddSingleton<IModelCompat, ModelCompat>();
        services.AddSingleton<IOmniDeploy, OmniDeploy>();
        services.AddSingleton<IVllmRecipes, VllmRecipes>();

        // Modèles et données
        services.AddSingleton<IHfService, HfService>();
        services.AddSingleton<IModelScanner, ModelScanner>();
        services.AddSingleton<ISingleFileConverter, SingleFileConverter>();
        services.AddSingleton<ISdComponentService, SdComponentService>();
        services.AddSingleton<IInstanceStore, InstanceStore>();
        services.AddSingleton<IDownloadStore, DownloadStore>();
        services.AddSingleton<ITtsStore, TtsStore>();
        services.AddSingleton<IChatStore, ChatStore>();
        services.AddSingleton<IRagStore, RagStore>();

        // ViewModels
        services.AddSingleton<IInstanceViewModelFactory, InstanceViewModelFactory>();
        services.AddSingleton<MonitorSettingsViewModel>();
        services.AddSingleton<TtsViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SetupViewModel>();
        services.AddSingleton<ModelsViewModel>();
        services.AddSingleton<LaunchViewModel>();
        services.AddSingleton<MonitorViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<ImagesViewModel>();
        services.AddSingleton<AudioViewModel>();
        services.AddSingleton<KnowledgeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        return services;
    }
}
