/*
    Fichier      :  AudioViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Audio : synthèse vocale avec un modèle TTS lancé (vLLM-Omni). Le travail est fait par TtsViewModel
        (texte, voix, aperçu, audios conservés) ; ce ViewModel ne fait que choisir le serveur, parmi ceux qui
        sont des modèles de synthèse vocale, et le transmettre à TtsViewModel.
*/

using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

public sealed partial class AudioViewModel : ViewModelBase
{
    private readonly IAppState _appState;

    /// <summary> Serveurs de synthèse vocale : cibles de la page. </summary>
    public ObservableCollection<InstanceViewModel> TtsTargets { get; } = new();

    /// <summary> Synthèse vocale (texte, voix, audios conservés). </summary>
    public TtsViewModel Tts { get; }

    /// <summary> Serveur choisi. </summary>
    [ObservableProperty] private InstanceViewModel? _target;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="appState"> État partagé : serveurs lancés. </param>
    /// <param name="tts"> Synthèse vocale. </param>
    public AudioViewModel(IAppState appState, TtsViewModel tts)
    {
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(tts);
        _appState = appState;
        Tts = tts;
        _appState.InstancesChanged += OnInstancesChanged;
        SyncTargets();
    }

    private bool _targetWasReady;

    private void OnInstancesChanged()
    {
        SyncTargets();
        if (Target is not { IsReady: true }) Target = TtsTargets.FirstOrDefault(i => i.IsReady) ?? Target;
        // Le serveur vient d'être prêt : ses voix peuvent maintenant être demandées
        Tts.OnTargetStateChanged();
        if (Target is { IsReady: true } && !_targetWasReady) Tts.LoadVoicesCommand.Execute(null);
        _targetWasReady = Target?.IsReady == true;
    }

    /// <summary> Liste des modèles de synthèse vocale, mise à jour quand les serveurs changent. </summary>
    private void SyncTargets()
    {
        var list = _appState.Instances.Where(i => i.IsTts).ToList();
        if (list.SequenceEqual(TtsTargets)) return;
        TtsTargets.Clear();
        foreach (var i in list) TtsTargets.Add(i);
    }

    /// <summary> Choisit le serveur (bouton « Tester » de la page Serveurs). </summary>
    public void SelectTarget(InstanceViewModel inst) => Target = inst;

    /// <summary> Un serveur est choisi. </summary>
    public bool HasTarget => Target != null;

    partial void OnTargetChanged(InstanceViewModel? value)
    {
        if (value != null) Tts.Target = value;
        _targetWasReady = value?.IsReady == true;
        OnPropertyChanged(nameof(HasTarget));
    }
}
