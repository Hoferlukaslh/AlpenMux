/*
    Fichier      :  AppState.cs
    Projet       :  AlpenMux

    Description  :
        État partagé entre les pages :
            - liste des modèles détectés (rafraîchie à la demande)
            - serveurs vLLM lancés
            - demandes de navigation (ex. "Lancer ce modèle" depuis la page Modèles)
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using AlpenMux.ViewModels;

namespace AlpenMux.Services;

/// <summary> État partagé entre les pages : modèles détectés, serveurs lancés, favoris et demandes de navigation. </summary>
public interface IAppState
{
    /// <summary>
    /// Serveur de l'application auquel appartient un processus (GPU) : chaque serveur est lancé dans
    /// sa propre session (setsid), ses processus enfants ont donc son PID comme groupe ou session.
    /// </summary>
    ViewModels.InstanceViewModel? InstanceOfProcess(int pid);

    /// <summary> Modèles détectés dans le dossier des modèles. </summary>
    ObservableCollection<ModelInfo> Models { get; }

    /// <summary> Adaptateurs LoRA trouvés dans le dossier des modèles. </summary>
    List<LoraAdapterInfo> Adapters { get; }

    /// <summary> Serveurs lancés et historique, les plus récents en premier. </summary>
    ObservableCollection<InstanceViewModel> Instances { get; }

    /// <summary> Configurations enregistrées en favoris. </summary>
    ObservableCollection<FavoriteConfig> Favorites { get; }

    /// <summary> Levé après chaque recherche des modèles. </summary>
    event Action? ModelsChanged;

    /// <summary> Levé quand la liste ou l'état des serveurs change. </summary>
    event Action? InstancesChanged;

    /// <summary> Navigation demandée vers une page ("Setup", "Models", "Launch", …). </summary>
    event Action<string>? NavigationRequested;

    /// <summary> Un modèle a été choisi pour le lancement depuis une autre page. </summary>
    event Action<ModelInfo>? LaunchRequested;

    /// <summary> Une configuration (favori, serveur existant) doit être chargée dans le formulaire. </summary>
    event Action<LaunchConfig>? EditRequested;

    /// <summary> Une recherche des modèles est en cours. </summary>
    bool IsScanning { get; }

    /// <summary> Recherche les modèles et adaptateurs LoRA (en arrière-plan) puis met la liste à jour. </summary>
    Task RefreshModelsAsync();

    /// <summary> Demande l'affichage d'une page ("Setup", "Models", "Launch"…). </summary>
    void Navigate(string page);

    /// <summary> Ouvre la page Serveurs avec ce modèle sélectionné. </summary>
    void RequestLaunch(ModelInfo m);

    /// <summary> Ouvre la page Serveurs avec cette configuration chargée dans le formulaire. </summary>
    void RequestEdit(LaunchConfig config);

    /// <summary> Recharge l'historique des serveurs (et retrouve ceux qui tournent encore). </summary>
    void LoadHistory();

    /// <summary> Les serveurs les plus récents en haut de la liste. </summary>
    void AddInstance(InstanceViewModel vm);

    /// <summary> Supprime le serveur de la liste et son historique (journal compris). </summary>
    void RemoveInstance(InstanceViewModel vm);

    /// <summary> Enregistre une copie de la configuration en favori. </summary>
    void AddFavorite(string name, LaunchConfig config);

    /// <summary> Supprime un favori. </summary>
    void RemoveFavorite(FavoriteConfig fav);

    /// <summary> Lève <see cref="InstancesChanged"/> (changement d'état d'un serveur). </summary>
    void NotifyInstancesChanged();

    /// <summary> Nombre de serveurs en marche. </summary>
    int RunningCount { get; }

    /// <summary> Premier port libre à partir de start (ports des instances et ports système exclus). </summary>
    int NextFreePort(int start);

    /// <summary> Le port est-il déjà utilisé (serveur de l'application ou autre programme) ? </summary>
    bool IsPortTaken(int port);

    /// <summary> Arrête tous les serveurs (fermeture de l'application). </summary>
    Task StopAllAsync();
}

/// <summary> Implémentation de <see cref="IAppState"/>. </summary>
public sealed class AppState : IAppState
{
    private readonly ISettingsService _settings;
    private readonly IInstanceStore _instanceStore;
    private readonly IModelScanner _scanner;
    private readonly IInstanceViewModelFactory _instanceFactory;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (dossier des modèles, favoris). </param>
    /// <param name="instanceStore"> Historique des serveurs. </param>
    /// <param name="scanner"> Recherche des modèles. </param>
    /// <param name="instanceFactory"> Création des serveurs retrouvés dans l'historique. </param>
    public AppState(ISettingsService settings, IInstanceStore instanceStore, IModelScanner scanner, IInstanceViewModelFactory instanceFactory)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(instanceStore);
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(instanceFactory);
        _settings = settings;
        _instanceStore = instanceStore;
        _scanner = scanner;
        _instanceFactory = instanceFactory;
        Favorites = new(settings.Current.Favorites);
    }


    /// <inheritdoc />
    public ViewModels.InstanceViewModel? InstanceOfProcess(int pid)
    {
        try
        {
            var stat = System.IO.File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var pgid = int.Parse(fields[2]);
            var sid  = int.Parse(fields[3]);
            return Instances.FirstOrDefault(i => i.Pid is { } p && (p == pgid || p == sid || p == pid));
        }
        catch { return null; }
    }

    /// <inheritdoc />
    public ObservableCollection<ModelInfo> Models { get; } = new();

    /// <inheritdoc />
    public List<LoraAdapterInfo> Adapters { get; private set; } = new();
    /// <inheritdoc />
    public ObservableCollection<InstanceViewModel> Instances { get; } = new();
    /// <inheritdoc />
    public ObservableCollection<FavoriteConfig> Favorites { get; }

    /// <inheritdoc />
    public event Action? ModelsChanged;
    /// <inheritdoc />
    public event Action? InstancesChanged;

    /// <inheritdoc />
    public event Action<string>? NavigationRequested;

    /// <inheritdoc />
    public event Action<ModelInfo>? LaunchRequested;

    /// <inheritdoc />
    public event Action<LaunchConfig>? EditRequested;

    /// <inheritdoc />
    public bool IsScanning { get; private set; }

    /// <inheritdoc />
    public async Task RefreshModelsAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var dir = _settings.ModelsDir;
            var list = await Task.Run(() => _scanner.Scan(dir));
            Adapters = await Task.Run(() => _scanner.ScanAdapters(dir));
            Models.Clear();
            foreach (var m in list) Models.Add(m);
        }
        finally { IsScanning = false; }
        ModelsChanged?.Invoke();
    }

    /// <inheritdoc />
    public void Navigate(string page) => NavigationRequested?.Invoke(page);

    /// <inheritdoc />
    public void RequestLaunch(ModelInfo m)
    {
        Navigate("Launch");
        LaunchRequested?.Invoke(m);
    }

    /// <inheritdoc />
    public void RequestEdit(LaunchConfig config)
    {
        Navigate("Launch");
        EditRequested?.Invoke(config);
    }

    /// <inheritdoc />
    public void LoadHistory()
    {
        foreach (var r in _instanceStore.LoadAll())
            Instances.Insert(0, _instanceFactory.Restore(r));
        InstancesChanged?.Invoke();
    }

    /// <inheritdoc />
    public void AddInstance(InstanceViewModel vm)
    {
        Instances.Insert(0, vm);
        InstancesChanged?.Invoke();
    }

    /// <inheritdoc />
    public void RemoveInstance(InstanceViewModel vm)
    {
        if (vm.IsRunning) return;
        Instances.Remove(vm);
        _instanceStore.Delete(vm.Id);
        InstancesChanged?.Invoke();
    }

    // ---------------------------------------------------------------- Favoris

    /// <inheritdoc />
    public void AddFavorite(string name, LaunchConfig config)
    {
        var fav = new FavoriteConfig { Name = name, Config = config.Clone() };
        Favorites.Insert(0, fav);
        SaveFavorites();
    }

    /// <inheritdoc />
    public void RemoveFavorite(FavoriteConfig fav)
    {
        Favorites.Remove(fav);
        SaveFavorites();
    }

    private void SaveFavorites()
    {
        _settings.Current.Favorites = Favorites.ToList();
        _settings.Save();
    }

    /// <inheritdoc />
    public void NotifyInstancesChanged() => InstancesChanged?.Invoke();

    /// <inheritdoc />
    public int RunningCount => Instances.Count(i => i.IsRunning);

    /// <inheritdoc />
    public int NextFreePort(int start)
    {
        var used = Instances.Where(i => i.IsRunning).Select(i => i.Port).ToHashSet();
        try
        {
            foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
                used.Add(ep.Port);
        }
        catch { /* liste des ports système indisponible : seuls ceux des serveurs comptent */ }
        var p = start;
        while (used.Contains(p) && p < 65535) p++;
        return p;
    }

    /// <inheritdoc />
    public bool IsPortTaken(int port) =>
        Instances.Any(i => i.IsRunning && i.Port == port) ||
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    /// <inheritdoc />
    public Task StopAllAsync() => Task.WhenAll(Instances.Where(i => i.IsRunning).Select(i => i.StopAsync()));
}
