/*
    Fichier      :  ImagesViewModel.cs
    Projet       :  AlpenMux

    Description  :
        Page Images : génération d'images avec un modèle de diffusion lancé.
            - vLLM-Omni : POST /v1/images/generations (réponse en base64) ;
            - stable-diffusion.cpp : POST /sdapi/v1/txt2img (format Automatic1111).
        Les images forment une galerie conservée sur le disque jusqu'à suppression par l'utilisateur (ImageGalleryStore) ;
        un clic ouvre le carrousel, où l'on passe d'une image à l'autre et où le bas affiche les informations de la
        génération (prompt, réglages, serveur, durée, fichier) et le bouton de suppression.
        Le prompt est saisi en bas.
        Anciennement dans la page Tester ; le fonctionnement n'a pas changé.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> Une image générée et ce qui a servi à la produire : affichée dans la galerie et le carrousel. </summary>
public sealed partial class GeneratedImage : ObservableObject
{
    /// <summary> Miniature affichée dans la galerie. </summary>
    public required Bitmap Thumbnail { get; init; }

    /// <summary> Image en pleine résolution : chargée à l'ouverture du carrousel, libérée en le quittant. </summary>
    [ObservableProperty] private Bitmap? _full;
    /// <summary> Fichier PNG enregistré dans le dossier des images. </summary>
    public required string FilePath { get; init; }
    /// <summary> Prompt positif. </summary>
    public required string Prompt { get; init; }
    /// <summary> Prompt négatif (vide si aucun). </summary>
    public required string NegativePrompt { get; init; }
    /// <summary> Taille, par exemple « 1024x1024 ». </summary>
    public required string Size { get; init; }
    /// <summary> Étapes de diffusion, ou « par défaut ». </summary>
    public required string Steps { get; init; }
    /// <summary> Guidance (CFG) envoyée, ou « par défaut ». </summary>
    public required string Guidance { get; init; }
    /// <summary> Nom du modèle servi. </summary>
    public required string Model { get; init; }
    /// <summary> Nom du serveur qui a généré l'image. </summary>
    public required string Server { get; init; }
    /// <summary> Durée de la génération (envoi de la requête jusqu'à la réponse). </summary>
    public required TimeSpan Duration { get; init; }
    /// <summary> Heure de la génération. </summary>
    public required DateTime Created { get; init; }

    /// <summary> Il existe un prompt négatif à afficher. </summary>
    public bool HasNegativePrompt => NegativePrompt.Length > 0;
    /// <summary> Nom du fichier, sans le dossier. </summary>
    public string FileName => Path.GetFileName(FilePath);
    /// <summary> Durée en secondes, lisible. </summary>
    public string DurationText => Duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s";
    /// <summary> Date de la génération, lisible. </summary>
    public string CreatedText => Created.ToString("g", CultureInfo.CurrentCulture);
}

public sealed partial class ImagesViewModel : ViewModelBase
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary> Serveurs de génération d'images : cibles de la page. </summary>
    public ObservableCollection<InstanceViewModel> ImageTargets { get; } = new();
    /// <summary> Images de la galerie, de la plus ancienne à la plus récente (galerie et carrousel). </summary>
    public ObservableCollection<GeneratedImage> Gallery { get; } = new();

    /// <summary> Image ouverte dans le carrousel (null = carrousel fermé). </summary>
    [ObservableProperty] private GeneratedImage? _viewed;

    /// <summary> Le carrousel est ouvert. </summary>
    public bool IsViewerOpen => Viewed != null;

    /// <summary> Position dans la galerie, en texte (« 3 / 12 »). </summary>
    public string ViewedPosition => Viewed == null ? "" : $"{Gallery.IndexOf(Viewed) + 1} / {Gallery.Count}";

    /// <summary> Texte d'accueil de la galerie : affiché tant qu'aucune image n'existe et qu'il n'y a pas de génération. </summary>
    public bool ShowEmptyHint => Gallery.Count == 0 && !IsGenerating;

    /// <summary> Tailles d'image proposées. </summary>
    public string[] ImageSizes { get; } = ["512x512", "768x768", "1024x1024", "1024x768", "768x1024", "1328x1328"];
    [ObservableProperty] private string _imageSize = "1024x1024";
    [ObservableProperty] private string _imageSteps = "";
    [ObservableProperty] private string _imageGuidance = "";
    [ObservableProperty] private string _imageNegative = "";
    [ObservableProperty] private InstanceViewModel? _target;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _isGenerating;
    [ObservableProperty] private string _error = "";

    /// <summary> Texte d'invite de la zone de saisie. </summary>
    public string InputPlaceholder => Loc.T("Chat_ImagePlaceholder");

    /// <summary> Suppression d'une image demandée : attend la confirmation (carrousel). </summary>
    [ObservableProperty] private bool _isConfirmingDelete;

    /// <summary> Effacement de toute la galerie demandé : attend la confirmation. </summary>
    [ObservableProperty] private bool _isConfirmingClear;

    /// <summary> Maj ou Ctrl maintenu : les vignettes affichent une corbeille (suppression sans confirmation). </summary>
    [ObservableProperty] private bool _quickDeleteArmed;

    private CancellationTokenSource? _cts;
    private readonly IAppState _appState;
    private readonly IUiServices _ui;
    private readonly IImageGalleryStore _store;

    /// <summary> Crée le ViewModel. </summary>
    /// <param name="appState"> État partagé : serveurs lancés. </param>
    /// <param name="ui"> Services de l'interface (dossiers, ouverture des images, presse-papiers). </param>
    /// <param name="store"> Galerie conservée sur le disque. </param>
    public ImagesViewModel(IAppState appState, IUiServices ui, IImageGalleryStore store)
    {
        ArgumentNullException.ThrowIfNull(appState);
        ArgumentNullException.ThrowIfNull(ui);
        ArgumentNullException.ThrowIfNull(store);
        _appState = appState;
        _ui = ui;
        _store = store;
        _appState.InstancesChanged += OnInstancesChanged;
        Gallery.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowEmptyHint));
            OnPropertyChanged(nameof(ViewedPosition));
        };
        SyncTargets();
        _ = LoadGalleryAsync();
    }

    /// <summary> Charge les images enregistrées ; elles passent avant celles générées depuis le lancement. </summary>
    private async Task LoadGalleryAsync()
    {
        var stored = await _store.LoadAllAsync();
        var loaded = stored.Select(ToGenerated);
        var all = Gallery.Concat(loaded).OrderBy(i => i.Created).ToList();
        Gallery.Clear();
        foreach (var image in all) Gallery.Add(image);
    }

    /// <summary> Image du carrousel en pleine résolution : lue à l'ouverture, libérée dès qu'on la quitte. </summary>
    private GeneratedImage? _fullLoaded;

    partial void OnViewedChanged(GeneratedImage? value)
    {
        OnPropertyChanged(nameof(IsViewerOpen));
        OnPropertyChanged(nameof(ViewedPosition));
        IsConfirmingDelete = false;
        if (_fullLoaded != null && _fullLoaded != value) _fullLoaded.Full = null;
        _fullLoaded = value;
        if (value is { Full: null })
        {
            try
            {
                value.Full = _store.LoadFull(value.FilePath);
            }
            catch (IOException ex)
            {
                // Fichier supprimé ou illisible hors de l'application : on n'affiche que la miniature
                Error = ex.Message;
            }
        }
    }

    /// <summary> Image de la galerie à partir de ce qui est enregistré sur le disque. </summary>
    private static GeneratedImage ToGenerated(StoredImage stored)
    {
        var info = stored.Info;
        return new GeneratedImage
        {
            Thumbnail = stored.Thumbnail,
            FilePath = stored.FilePath,
            Prompt = info?.Prompt ?? "",
            NegativePrompt = info?.NegativePrompt ?? "",
            Size = info?.Size ?? "",
            Steps = info?.Steps ?? "",
            Guidance = info?.Guidance ?? "",
            Model = info?.Model ?? "",
            Server = info?.Server ?? "",
            Duration = TimeSpan.FromSeconds(info?.DurationSeconds ?? 0),
            Created = stored.Created,
        };
    }

    partial void OnIsGeneratingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyHint));

    private void OnInstancesChanged()
    {
        SyncTargets();
        if (Target is not { IsReady: true }) Target = ImageTargets.FirstOrDefault(i => i.IsReady) ?? Target;
    }

    /// <summary> Liste des serveurs de génération d'images, mise à jour quand les serveurs changent. </summary>
    private void SyncTargets()
    {
        var list = _appState.Instances.Where(i => i.IsImageGen).ToList();
        if (list.SequenceEqual(ImageTargets)) return;
        ImageTargets.Clear();
        foreach (var i in list) ImageTargets.Add(i);
    }

    /// <summary> Choisit le serveur (bouton « Tester » de la page Serveurs). </summary>
    public void SelectTarget(InstanceViewModel inst) => Target = inst;

    /// <summary> Le serveur choisi génère des images. </summary>
    public bool HasTarget => Target != null;

    partial void OnTargetChanged(InstanceViewModel? value)
    {
        Error = "";
        if (value is { IsImageGen: true }) ImageGuidance = DefaultGuidance(value);
        OnPropertyChanged(nameof(HasTarget));
    }

    /// <summary> Requête /sdapi/v1/txt2img de stable-diffusion.cpp (format Automatic1111). </summary>
    private void WriteSdApiRequest(Utf8JsonWriter w, string prompt)
    {
        w.WriteString("prompt", prompt);
        w.WriteString("negative_prompt", ImageNegative.Trim());
        var size = ImageSize.Split('x');
        if (size.Length == 2 && int.TryParse(size[0], out var width) && int.TryParse(size[1], out var height))
        {
            w.WriteNumber("width", width);
            w.WriteNumber("height", height);
        }
        if (int.TryParse(ImageSteps, out var steps) && steps > 0) w.WriteNumber("steps", steps);
        if (double.TryParse(ImageGuidance.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cfg) && cfg >= 0)
            w.WriteNumber("cfg_scale", Math.Min(cfg, 20));
        w.WriteNumber("seed", -1);
        w.WriteNumber("batch_size", 1);
    }

    /// <summary>
    /// Guidance (CFG) conseillée pour le pipeline. Sans valeur, vLLM-Omni prend 1,0 (pas de guidage) :
    /// SDXL et SD3 donnent alors des images délavées, au style « peinture », qui suivent mal le prompt.
    /// Vide pour les modèles distillés (Turbo, schnell…) qui n'utilisent pas de guidage.
    /// </summary>
    private static string DefaultGuidance(InstanceViewModel t)
    {
        // stable-diffusion.cpp : fichiers SD 1.x / SDXL complets, guidance classique
        // Transformer seul (FLUX.2 klein, Z-Image Turbo) : modèles distillés, sans guidage
        if (t.Config.IsSdCpp && t.Config.SdLlm != null) return "1";
        if (t.Config.IsSdCpp) return "7";
        string? cls = null;
        try
        {
            var index = Path.Combine(t.Config.ModelTarget, "model_index.json");
            if (File.Exists(index))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(index));
                cls = doc.RootElement.TryGetProperty("_class_name", out var c) ? c.GetString() : null;
            }
        }
        catch { /* model_index.json illisible : type de pipeline inconnu */ }
        var name = (cls ?? "") + " " + t.Config.ModelRepo;
        if (name.Contains("turbo", StringComparison.OrdinalIgnoreCase) || name.Contains("schnell", StringComparison.OrdinalIgnoreCase)
            || name.Contains("lightning", StringComparison.OrdinalIgnoreCase)) return "";
        return cls switch
        {
            "StableDiffusionXLPipeline" or "StableDiffusion3Pipeline" => "7",
            "FluxPipeline" => "3.5",
            _ => "",
        };
    }

    [RelayCommand]
    private void OpenImagesFolder()
    {
        Directory.CreateDirectory(_store.Directory);
        _ui.Open(_store.Directory);
    }

    /// <summary> Demande l'effacement de toute la galerie ; la confirmation suit. </summary>
    [RelayCommand]
    private void BeginClear() => IsConfirmingClear = true;

    [RelayCommand]
    private void CancelClear() => IsConfirmingClear = false;

    /// <summary> Supprime définitivement toutes les images de la galerie (fichiers compris). </summary>
    [RelayCommand]
    private void ConfirmClear()
    {
        IsConfirmingClear = false;
        if (!DeleteFiles(Gallery.ToList())) return;
        Gallery.Clear();
        Viewed = null;
    }

    /// <summary> Demande la suppression de l'image ouverte ; la confirmation suit. </summary>
    [RelayCommand]
    private void BeginDeleteViewed()
    {
        if (Viewed != null) IsConfirmingDelete = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    /// <summary> Supprime définitivement l'image ouverte, et affiche la suivante (ou la précédente). </summary>
    [RelayCommand]
    private void ConfirmDeleteViewed()
    {
        if (Viewed is not { } image) return;
        IsConfirmingDelete = false;
        if (DeleteFiles([image])) RemoveFromGallery(image);
    }

    /// <summary> Suppression rapide (Maj ou Ctrl maintenu) : l'image disparaît sans confirmation. </summary>
    [RelayCommand]
    private void DeleteNow(GeneratedImage? image)
    {
        if (image != null && DeleteFiles([image])) RemoveFromGallery(image);
    }

    /// <summary> Retire une image de la galerie ; si elle était ouverte, la voisine prend sa place. </summary>
    private void RemoveFromGallery(GeneratedImage image)
    {
        var index = Gallery.IndexOf(image);
        Gallery.Remove(image);
        if (Viewed == image) Viewed = Gallery.Count == 0 ? null : Gallery[Math.Min(index, Gallery.Count - 1)];
    }

    /// <summary> Supprime les fichiers ; en cas d'échec, l'erreur est affichée et la galerie reste intacte. </summary>
    private bool DeleteFiles(IEnumerable<GeneratedImage> images)
    {
        try
        {
            foreach (var image in images) _store.Delete(image.FilePath);
            return true;
        }
        catch (IOException ex)
        {
            Error = ex.Message;
            return false;
        }
    }

    /// <summary> Ouvre une image de la galerie dans le carrousel. </summary>
    [RelayCommand]
    private void OpenImage(GeneratedImage? image)
    {
        if (image != null) Viewed = image;
    }

    /// <summary> Ferme le carrousel. </summary>
    [RelayCommand]
    private void CloseViewer() => Viewed = null;

    /// <summary> Image précédente du carrousel (la première revient après la dernière). </summary>
    [RelayCommand]
    private void Previous() => Move(-1);

    /// <summary> Image suivante du carrousel (la dernière revient après la première). </summary>
    [RelayCommand]
    private void Next() => Move(1);

    private void Move(int delta)
    {
        if (Viewed == null || Gallery.Count == 0) return;
        var index = Gallery.IndexOf(Viewed);
        if (index < 0) return;
        Viewed = Gallery[(index + delta + Gallery.Count) % Gallery.Count];
    }

    /// <summary> Ouvre le fichier PNG de l'image affichée, avec l'application du système. </summary>
    [RelayCommand]
    private void OpenViewedFile()
    {
        if (Viewed is { } image) _ui.Open(image.FilePath);
    }

    /// <summary> Copie le prompt de l'image affichée dans le presse-papiers. </summary>
    [RelayCommand]
    private async Task CopyPromptAsync()
    {
        if (Viewed is { } image) await _ui.CopyAsync(image.Prompt);
    }

    [RelayCommand]
    private void StopGeneration() => _cts?.Cancel();

    [RelayCommand]
    private async Task SendAsync()
    {
        Error = "";
        if (IsGenerating || string.IsNullOrWhiteSpace(Input)) return;
        if (Target is not { IsReady: true } t) { Error = Loc.T("Images_NoServer"); return; }
        // Serveur en veille : réveil avant la requête
        if (t.IsSleeping && !await t.WakeUpAsync()) { Error = Loc.F("Router_WakeFailed", t.ServedName); return; }
        var prompt = Input.Trim();
        Input = "";
        await GenerateImageAsync(t, prompt);
    }

    /// <summary>
    /// Génération d'image, réponse en base64 : vLLM-Omni par POST /v1/images/generations,
    /// stable-diffusion.cpp par POST /sdapi/v1/txt2img (format Automatic1111).
    /// Les réglages sont lus au lancement : chaque image garde ceux avec lesquels elle a été faite.
    /// </summary>
    private async Task GenerateImageAsync(InstanceViewModel t, string prompt)
    {
        IsGenerating = true;
        _cts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var negative = ImageNegative.Trim();
        var size = ImageSize;
        var steps = int.TryParse(ImageSteps, out var stepCount) && stepCount > 0
            ? stepCount.ToString(CultureInfo.CurrentCulture) : Loc.T("Images_Info_Default");
        var guidance = double.TryParse(ImageGuidance.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cfgValue) && cfgValue >= 0
            ? Math.Min(cfgValue, 20).ToString(CultureInfo.CurrentCulture) : Loc.T("Images_Info_Default");
        try
        {
            var sdcpp = t.Config.IsSdCpp;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                if (sdcpp) WriteSdApiRequest(w, prompt);
                else
                {
                    w.WriteString("model", t.ServedName);
                    w.WriteString("prompt", prompt);
                    w.WriteNumber("n", 1);
                    w.WriteString("size", ImageSize);
                    w.WriteString("response_format", "b64_json");
                    if (int.TryParse(ImageSteps, out var steps0) && steps0 > 0) w.WriteNumber("num_inference_steps", steps0);
                    if (double.TryParse(ImageGuidance.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var cfg) && cfg >= 0)
                        w.WriteNumber("guidance_scale", Math.Min(cfg, 20));
                    if (negative.Length > 0) w.WriteString("negative_prompt", negative);
                }
                w.WriteEndObject();
            }
            // stable-diffusion.cpp : API d'Automatic1111 (étapes, guidance et prompt négatif en champs simples)
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{t.LocalUrl}{(sdcpp ? "/sdapi/v1/txt2img" : "/v1/images/generations")}")
            {
                Content = new ByteArrayContent(ms.ToArray()) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
            };
            if (t.ApiKey != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", t.ApiKey);
            using var resp = await Http.SendAsync(req, _cts.Token);
            var text = await resp.Content.ReadAsStringAsync(_cts.Token);
            if (!resp.IsSuccessStatusCode) { Error = $"HTTP {(int)resp.StatusCode} : {text}"; return; }
            var duration = DateTime.UtcNow - started;

            using var doc = JsonDocument.Parse(text);
            // Réponses : { "data": [ { "b64_json": … } ] } (OpenAI) ou { "images": [ "…" ] } (Automatic1111)
            var images = sdcpp
                ? doc.RootElement.GetProperty("images").EnumerateArray().Select(e => e.GetString()).ToList()
                : doc.RootElement.GetProperty("data").EnumerateArray()
                      .Select(e => e.TryGetProperty("b64_json", out var b) ? b.GetString() : null).ToList();
            if (images.Count == 0) { Error = Loc.T("Chat_ImageNone"); return; }
            var created = DateTime.Now;
            foreach (var data in images)
            {
                if (data == null) continue;
                var bytes = Convert.FromBase64String(data);
                var record = new GenerationRecord(prompt, negative, size, steps, guidance, t.ServedName, t.Title,
                    duration.TotalSeconds, created);
                Gallery.Add(ToGenerated(_store.Save(bytes, record)));
            }
        }
        catch (OperationCanceledException) { /* arrêt demandé : rien à afficher, le bouton Stop disparaît */ }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsGenerating = false;
            _cts = null;
        }
    }
}
