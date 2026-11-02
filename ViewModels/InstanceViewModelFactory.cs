/*
    Fichier      :  InstanceViewModelFactory.cs
    Projet       :  AlpenMux

    Description  :
        Fabrique des serveurs (InstanceViewModel) : un serveur est créé à l'exécution, à partir d'une
        configuration (lancement) ou d'un enregistrement de l'historique (démarrage de l'application).
        Les services sont fournis par le conteneur, seul l'enregistrement est passé à la main.
*/

using System;
using Microsoft.Extensions.DependencyInjection;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> Création des <see cref="InstanceViewModel"/> avec leurs services. </summary>
public interface IInstanceViewModelFactory
{
    /// <summary> Nouveau serveur à partir d'une configuration (pas encore démarré). </summary>
    /// <param name="config"> Configuration de lancement (copiée). </param>
    InstanceViewModel Create(LaunchConfig config);

    /// <summary> Serveur de l'historique : journal rechargé, serveur retrouvé s'il tourne encore. </summary>
    /// <param name="record"> Enregistrement lu dans l'historique. </param>
    InstanceViewModel Restore(InstanceRecord record);
}

/// <summary> Implémentation de <see cref="IInstanceViewModelFactory"/> à partir du conteneur. </summary>
public sealed class InstanceViewModelFactory : IInstanceViewModelFactory
{
    // Le fournisseur est lu à la création (et non au constructeur) : AppState dépend de cette fabrique
    // et chaque serveur dépend d'AppState.
    private readonly IServiceProvider _services;
    private readonly IVllmOptions _vllmOptions;

    /// <summary> Crée la fabrique. </summary>
    /// <param name="services"> Conteneur qui fournit les services de chaque serveur. </param>
    /// <param name="vllmOptions"> Options du vLLM installé (arguments de lancement). </param>
    public InstanceViewModelFactory(IServiceProvider services, IVllmOptions vllmOptions)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(vllmOptions);
        _services = services;
        _vllmOptions = vllmOptions;
    }

    /// <inheritdoc />
    public InstanceViewModel Create(LaunchConfig config) => New(new InstanceRecord
    {
        Config = config.Clone(),
        Arguments = config.BuildArguments(_vllmOptions),
        Environment = config.BuildEnvironment(),
    });

    /// <inheritdoc />
    public InstanceViewModel Restore(InstanceRecord record)
    {
        var vm = New(record);
        vm.RestoreFromHistory();
        return vm;
    }

    private InstanceViewModel New(InstanceRecord record) =>
        ActivatorUtilities.CreateInstance<InstanceViewModel>(_services, record);
}
