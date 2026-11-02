/*
    Fichier      :  TtsViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Tester, pour un modèle de synthèse vocale (vLLM-Omni) :
            - texte à dire, voix, langue et consignes de style (si le modèle les accepte) ;
            - aperçu d'une voix (phrase courte, mise en cache) ;
            - audios générés, conservés de façon permanente avec leurs informations de génération :
              écoute, téléchargement (choix de la destination) et suppression.

        API utilisée (vLLM-Omni, compatible OpenAI) :
            GET  /v1/audio/voices  → {"voices": [...], "uploaded_voices": [{"name": ...}]}
            POST /v1/audio/speech  → audio WAV
        Si le serveur ne liste pas ses voix, celles du config.json du modèle sont proposées.
*/

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> Un audio généré (liste de la page). </summary>
public sealed partial class TtsClipViewModel : ObservableObject
{
    private readonly TtsViewModel _owner;
    /// <summary> Audio enregistré. </summary>
    public TtsClip Clip { get; }

    private readonly ITtsStore _ttsStore;

    /// <summary> Crée la ligne d'un audio. </summary>
    /// <param name="clip"> Audio enregistré. </param>
    /// <param name="owner"> Onglet TTS (lecture, suppression). </param>
    /// <param name="ttsStore"> Emplacement des fichiers audio. </param>
    public TtsClipViewModel(TtsClip clip, TtsViewModel owner, ITtsStore ttsStore)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(ttsStore);
        Clip = clip;
        _owner = owner;
        _ttsStore = ttsStore;
    }

    /// <summary> Texte lu. </summary>
    public string Text => Clip.Text;

    /// <summary> Texte long : 4 lignes affichées, le reste au clic sur « Afficher tout ». </summary>
    public bool IsLongText => Clip.Text.Length > 400 || Clip.Text.Count(c => c == '\n') >= 4;
    [ObservableProperty] private bool _isExpanded;
    /// <summary> Nombre de lignes affichées (0 = toutes). </summary>
    public int TextMaxLines => IsExpanded || !IsLongText ? 0 : 4;
    /// <summary> Libellé du lien « Afficher tout / moins ». </summary>
    public string ExpandText => Loc.T(IsExpanded ? "Tts_ShowLess" : "Tts_ShowAll");
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(TextMaxLines));
        OnPropertyChanged(nameof(ExpandText));
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
    /// <summary> Fichier WAV. </summary>
    public string AudioPath => _ttsStore.AudioOf(Clip.Id);
    /// <summary> Des instructions de style ont été données. </summary>
    public bool HasInstructions => !string.IsNullOrWhiteSpace(Clip.Instructions);
    /// <summary> Instructions de style. </summary>
    public string Instructions => Clip.Instructions ?? "";

    /// <summary> Voix · langue · modèle </summary>
    public string Voice => string.IsNullOrEmpty(Clip.Language) ? Clip.Voice : $"{Clip.Voice} · {Clip.Language}";
    /// <summary> Modèle utilisé. </summary>
    public string Model => Clip.Model;

    /// <summary> Durée, taille et temps de génération affichés. </summary>
    public string Details => Loc.F("Tts_ClipDetails",
        Clip.CreatedAt.ToString("g", CultureInfo.CurrentCulture), Clip.DurationSeconds, Clip.GenerationSeconds,
        Clip.GenerationSeconds > 0 ? Clip.DurationSeconds / Clip.GenerationSeconds : 0, Format.Bytes(Clip.SizeBytes));

    [ObservableProperty] private bool _isPlaying;
    /// <summary> Icône du bouton de lecture. </summary>
    public string PlayIcon => IsPlaying ? Icons.Stop : Icons.Play;
    partial void OnIsPlayingChanged(bool value) => OnPropertyChanged(nameof(PlayIcon));

    [RelayCommand]
    private void Play() => _owner.TogglePlay(AudioPath);

    [RelayCommand]
    private Task Download() => _owner.DownloadAsync(this);

    [RelayCommand]
    private void Delete() => _owner.Delete(this);
}

public sealed partial class TtsViewModel : ObservableObject
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private const string DefaultVoice = "default";

    /// <summary> Voix proposées par le serveur ou le modèle. </summary>
    public ObservableCollection<string> Voices { get; } = new();
    /// <summary> Langues proposées. </summary>
    public ObservableCollection<string> Languages { get; } = new();
    /// <summary> Audios générés. </summary>
    public ObservableCollection<TtsClipViewModel> Clips { get; } = new();

    [ObservableProperty] private InstanceViewModel? _target;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string? _selectedVoice;
    [ObservableProperty] private string? _selectedLanguage;
    [ObservableProperty] private string _instructions = "";
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private bool _isLoadingVoices;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _voicesInfo = "";

    /// <summary> Le modèle prend une langue en paramètre. </summary>
    public bool HasLanguages => Languages.Count > 0;
    /// <summary> Au moins un audio généré. </summary>
    public bool HasClips => Clips.Count > 0;
    /// <summary> La génération est possible (serveur prêt, texte saisi). </summary>
    public bool CanGenerate => !IsGenerating && Target is { IsReady: true } && !string.IsNullOrWhiteSpace(Text);
    /// <summary> Nombre de caractères affiché. </summary>
    public string CharCount => Loc.F("Tts_CharCount", Text.Length);
    /// <summary> L'aperçu de la voix est en cours de lecture. </summary>
    public bool PreviewPlaying => _previewPath != null && _audio.Current == _previewPath;
    /// <summary> Icône du bouton d'aperçu. </summary>
    public string PreviewIcon => PreviewPlaying ? Icons.Stop : Icons.SpeakerHigh;
    /// <summary> Message affiché si aucun lecteur audio n'est installé. </summary>
    public string PlayerMissing => AudioPlayer.PlayerName == null ? Loc.T("Tts_NoPlayer") : "";

    private CancellationTokenSource? _cts;
    private string? _previewPath;

    private readonly IAudioPlayer _audio;
    private readonly ITtsStore _ttsStore;
    private readonly IUiServices _ui;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="audio"> Lecture des audios. </param>
    /// <param name="ttsStore"> Audios enregistrés. </param>
    /// <param name="ui"> Services de l'interface (fichiers, presse-papiers, liens). </param>
    public TtsViewModel(IAudioPlayer audio, ITtsStore ttsStore, IUiServices ui)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(ttsStore);
        ArgumentNullException.ThrowIfNull(ui);
        _audio = audio;
        _ttsStore = ttsStore;
        _ui = ui;
        foreach (var c in _ttsStore.LoadAll()) Clips.Add(new TtsClipViewModel(c, this, _ttsStore));
        Clips.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasClips));
        _audio.CurrentChanged += OnPlayerChanged;
    }

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(CharCount));
        OnPropertyChanged(nameof(CanGenerate));
    }
    partial void OnIsGeneratingChanged(bool value) => OnPropertyChanged(nameof(CanGenerate));

    partial void OnTargetChanged(InstanceViewModel? value)
    {
        OnPropertyChanged(nameof(CanGenerate));
        Error = "";
        _ = LoadVoicesAsync();
    }

    /// <summary> À appeler quand l'état du serveur change (prêt / arrêté). </summary>
    public void OnTargetStateChanged() => OnPropertyChanged(nameof(CanGenerate));

    private void OnPlayerChanged()
    {
        var current = _audio.Current;
        foreach (var c in Clips) c.IsPlaying = c.AudioPath == current;
        OnPropertyChanged(nameof(PreviewPlaying));
        OnPropertyChanged(nameof(PreviewIcon));
    }

    // ---------------------------------------------------------------- Voix et langues

    [RelayCommand]
    private async Task LoadVoicesAsync()
    {
        var t = Target;
        var keep = SelectedVoice;
        Voices.Clear();
        Languages.Clear();
        VoicesInfo = "";
        if (t == null) { OnPropertyChanged(nameof(HasLanguages)); return; }

        // Langues : seulement d'après le modèle (l'API ne les liste pas)
        var langs = _ttsStore.ConfigLanguages(t.Config.ModelTarget);
        if (langs.Count > 0)
        {
            Languages.Add("Auto");
            foreach (var l in langs) Languages.Add(l);
            var ui = Loc.Instance.CurrentLanguage == "fr" ? "French" : "English";
            SelectedLanguage = Languages.Contains(SelectedLanguage ?? "") ? SelectedLanguage : Languages.Contains(ui) ? ui : "Auto";
        }
        else SelectedLanguage = null;
        OnPropertyChanged(nameof(HasLanguages));

        // Voix : d'abord le serveur, sinon le config.json du modèle
        IsLoadingVoices = true;
        try
        {
            var fromServer = t.IsReady ? await FetchServerVoicesAsync(t) : null;
            if (Target != t) return;
            var voices = fromServer ?? _ttsStore.ConfigVoices(t.Config.ModelTarget);
            if (voices.Count > 1) voices.Remove(DefaultVoice);   // "default" = voix par défaut du modèle, redondante
            if (voices.Count == 0) voices.Add(DefaultVoice);
            foreach (var v in voices) Voices.Add(v);
            VoicesInfo = fromServer != null ? Loc.F("Tts_VoicesFromServer", Voices.Count)
                : t.IsReady ? Loc.F("Tts_VoicesFromConfig", Voices.Count) : Loc.T("Tts_ServerNotReady");
            SelectedVoice = keep != null && Voices.Contains(keep) ? keep : Voices.FirstOrDefault();
        }
        finally { IsLoadingVoices = false; }
    }

    private static async Task<System.Collections.Generic.List<string>?> FetchServerVoicesAsync(InstanceViewModel t)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{t.LocalUrl}/v1/audio/voices");
            Authorize(req, t);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            using var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            var res = new System.Collections.Generic.List<string>();
            if (doc.RootElement.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array)
                res.AddRange(v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            if (doc.RootElement.TryGetProperty("uploaded_voices", out var u) && u.ValueKind == JsonValueKind.Array)
                foreach (var x in u.EnumerateArray())
                    if (x.TryGetProperty("name", out var n) && n.GetString() is { } name && !res.Contains(name)) res.Add(name);
            return res.Count > 0 ? res : null;
        }
        catch { return null; }
    }

    private static void Authorize(HttpRequestMessage req, InstanceViewModel t)
    {
        if (t.ApiKey != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.ApiKey);
    }

    // ---------------------------------------------------------------- Génération

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task Generate()
    {
        Error = "";
        if (!CanGenerate || Target is not { } t) { if (Target is not { IsReady: true }) Error = Loc.T("Chat_NoServer"); return; }

        IsGenerating = true;
        _cts = new CancellationTokenSource();
        try
        {
            var text = Text.Trim();
            var voice = SelectedVoice ?? DefaultVoice;
            var lang = HasLanguages ? SelectedLanguage : null;
            var instr = string.IsNullOrWhiteSpace(Instructions) ? null : Instructions.Trim();

            var started = DateTime.UtcNow;
            var wav = await SynthesizeAsync(t, text, voice, lang, instr, _cts.Token);
            var clip = _ttsStore.Save(new TtsClip
            {
                Text = text, Voice = voice, Language = lang, Instructions = instr,
                Model = t.ModelRepo, ServedName = t.ServedName,
                GenerationSeconds = (DateTime.UtcNow - started).TotalSeconds,
            }, wav);

            var vm = new TtsClipViewModel(clip, this, _ttsStore);
            Clips.Insert(0, vm);
            TogglePlay(vm.AudioPath);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsGenerating = false;
            _cts = null;
        }
    }

    /// <summary> Appel de /v1/audio/speech. Lève une exception avec le message d'erreur du serveur. </summary>
    private static async Task<byte[]> SynthesizeAsync(InstanceViewModel t, string text, string voice, string? language,
        string? instructions, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", t.ServedName);
            w.WriteString("input", text);
            w.WriteString("voice", voice);
            if (language != null) w.WriteString("language", language);
            if (instructions != null) w.WriteString("instructions", instructions);
            w.WriteString("response_format", "wav");
            w.WriteEndObject();
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{t.LocalUrl}/v1/audio/speech")
        {
            Content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json"),
        };
        Authorize(req, t);
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsByteArrayAsync(ct);
        var type = resp.Content.Headers.ContentType?.MediaType ?? "";
        if (resp.IsSuccessStatusCode && !type.Contains("json") && body.Length > 44) return body;

        // Erreur au format OpenAI : {"error": {"message": ...}}
        var msg = Encoding.UTF8.GetString(body);
        try
        {
            using var doc = JsonDocument.Parse(msg);
            if (doc.RootElement.TryGetProperty("error", out var e))
                msg = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString() ?? msg : e.ToString();
            else if (doc.RootElement.TryGetProperty("detail", out var d)) msg = d.ToString();
        }
        catch { /* réponse d'erreur non JSON : message brut conservé */ }
        throw new InvalidOperationException(Loc.F("Tts_Failed", (int)resp.StatusCode, msg.Length > 400 ? msg[..400] : msg));
    }

    // ---------------------------------------------------------------- Aperçu d'une voix

    [RelayCommand]
    private async Task PreviewVoice()
    {
        Error = "";
        if (PreviewPlaying) { _audio.Stop(); return; }
        if (IsPreviewing || Target is not { IsReady: true } t || SelectedVoice is not { } voice) return;

        var lang = HasLanguages ? SelectedLanguage : null;
        var path = _ttsStore.PreviewPath(t.ServedName, voice, lang);
        try
        {
            if (!File.Exists(path))
            {
                IsPreviewing = true;
                var sentence = _ttsStore.PreviewSentence(lang is null or "Auto" ? (Loc.Instance.CurrentLanguage == "fr" ? "French" : "English") : lang);
                var wav = await SynthesizeAsync(t, sentence, voice, lang, null, CancellationToken.None);
                Directory.CreateDirectory(_ttsStore.PreviewDir);
                await File.WriteAllBytesAsync(path, wav);
            }
            _previewPath = path;
            if (!_audio.Play(path)) Error = Loc.T("Tts_NoPlayer");
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsPreviewing = false; }
    }

    partial void OnSelectedVoiceChanged(string? value)
    {
        if (PreviewPlaying) _audio.Stop();
    }

    // ---------------------------------------------------------------- Audios générés

    /// <summary> Lit ce fichier, ou arrête la lecture s'il est déjà en cours. </summary>
    public void TogglePlay(string path)
    {
        if (_audio.Current == path) { _audio.Stop(); return; }
        if (!_audio.Play(path)) Error = Loc.T("Tts_NoPlayer");
    }

    /// <summary> Enregistre une copie de l'audio là où l'utilisateur le choisit. </summary>
    public async Task DownloadAsync(TtsClipViewModel c)
    {
        var dest = await _ui.PickSaveFileAsync(Loc.T("Tts_SaveTitle"), _ttsStore.SuggestedFileName(c.Clip), Loc.T("Tts_WavFiles"), "*.wav");
        if (dest == null) return;
        try { File.Copy(c.AudioPath, dest, overwrite: true); }
        catch (Exception ex) { Error = ex.Message; }
    }

    /// <summary> Supprime un audio. </summary>
    public void Delete(TtsClipViewModel c)
    {
        if (c.IsPlaying) _audio.Stop();
        _ttsStore.Delete(c.Clip.Id);
        Clips.Remove(c);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(_ttsStore.Root);
        _ui.Open(_ttsStore.Root);
    }
}
