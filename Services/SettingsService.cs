/*
    Fichier      :  SettingsService.cs
    Projet       :  AlpenMux

    Description  :
        Persistance des préférences dans ~/.config/alpenmux/settings.json
        (permissions 600 : le fichier peut contenir le token Hugging Face).
        ISettingsService est enregistré comme singleton dans le conteneur (voir ServiceRegistration).
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Accès aux préférences de l'utilisateur et à leur sauvegarde. </summary>
public interface ISettingsService
{
    /// <summary> Préférences en cours (modifiées en place, puis enregistrées avec <see cref="Save"/>). </summary>
    AppSettings Current { get; }

    /// <summary> Levé après chaque sauvegarde (chemins, token…). </summary>
    event Action? Saved;

    /// <summary> Relit le fichier de réglages ; valeurs par défaut s'il est absent ou illisible. </summary>
    void Load();

    /// <summary> Écrit les réglages sur le disque (permissions 600 hors Windows) puis lève <see cref="Saved"/>. </summary>
    void Save();

    /// <summary> Venv de vLLM : chemin Linux (dans la distribution WSL sous Windows). </summary>
    string VenvPath { get; }

    /// <summary> Dossier des modèles, chemin local développé. </summary>
    string ModelsDir { get; }
}

/// <summary> Implémentation de <see cref="ISettingsService"/> : fichier JSON dans le dossier de configuration. </summary>
public sealed class SettingsService : ISettingsService
{
    /// <inheritdoc />
    public AppSettings Current { get; private set; } = new();

    /// <summary> Dossier personnel de l'utilisateur. </summary>
    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary> Dossier de configuration de l'application (~/.config/alpenmux). </summary>
    public static string ConfigDirectory =>
        AppPaths.ConfigDir;

    private static string SettingsFilePath => Path.Combine(ConfigDirectory, "settings.json");

    /// <inheritdoc />
    public event Action? Saved;

    private readonly ILogger<SettingsService> _logger;

    /// <summary> Crée le service et lit immédiatement le fichier de réglages. </summary>
    /// <param name="logger"> Journal des erreurs de lecture et d'écriture. </param>
    public SettingsService(ILogger<SettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Load();
    }

    /// <inheritdoc />
    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
                Current = JsonSerializer.Deserialize(File.ReadAllText(SettingsFilePath),
                              AppJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Settings] Lecture impossible : {Message}", ex.Message);
            Current = new AppSettings();
        }
    }

    /// <inheritdoc />
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(SettingsFilePath,
                JsonSerializer.Serialize(Current, AppJsonContext.Default.AppSettings));
            if (!OperatingSystem.IsWindows())   // sous Windows, %APPDATA% n'est lisible que par l'utilisateur
                File.SetUnixFileMode(SettingsFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Settings] Sauvegarde impossible : {Message}", ex.Message);
        }
        Saved?.Invoke();
    }

    /// <summary> Développe "~" en début de chemin et retourne un chemin absolu. </summary>
    /// <param name="path"> Chemin saisi par l'utilisateur (peut être vide). </param>
    /// <returns> Chemin absolu, ou chaîne vide si <paramref name="path"/> est vide. </returns>
    public static string ExpandPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        path = path.Trim();
        if (path == "~") return Home;
        if (path.StartsWith("~/")) return Path.Combine(Home, path[2..]);
        return Path.GetFullPath(path);
    }

    /// <inheritdoc />
    public string VenvPath  => LinuxHost.Expand(Current.VenvPath);

    /// <inheritdoc />
    public string ModelsDir => ExpandPath(Current.ModelsDir);
}

/// <summary> Préférences de l'utilisateur, sérialisées dans settings.json. </summary>
public sealed class AppSettings
{
    /// <summary> Code de la langue de l'interface (fr, en…). </summary>
    public string Language { get; set; } = Loc.DefaultLanguage;

    /// <summary> "System", "Light" ou "Dark". </summary>
    public string Theme { get; set; } = "System";

    /// <summary> Venv de vLLM (chemin Linux, ~ accepté). </summary>
    public string VenvPath  { get; set; } = "~/vllm-env/.venv";
    /// <summary> Dossier des modèles (~ accepté). </summary>
    public string ModelsDir { get; set; } = "~/models";

    /// <summary> Jeton Hugging Face (modèles protégés), null si absent. </summary>
    public string? HfToken { get; set; }

    /// <summary> Version de Python du venv créé par uv. </summary>
    public string PythonVersion   { get; set; } = "3.12";
    /// <summary> Spécification pip de vLLM ("vllm", "vllm==0.11.0"…). </summary>
    public string VllmPackageSpec { get; set; } = "vllm";

    /// <summary> Adresse d'écoute proposée pour un nouveau serveur. </summary>
    public string DefaultHost { get; set; } = "0.0.0.0";
    /// <summary> Port proposé pour un nouveau serveur. </summary>
    public int    DefaultPort { get; set; } = 8000;

    /// <summary> Intervalle entre deux mesures du moniteur (ms). </summary>
    public int MonitorIntervalMs { get; set; } = 1000;
    /// <summary> Durée affichée par les courbes du moniteur (secondes). </summary>
    public int MonitorHistorySeconds { get; set; } = 120;
    /// <summary> Arrondi des courbes : 0 = segments droits, 1 = très lissé. </summary>
    public double MonitorSmoothing { get; set; }
    /// <summary> Épaisseur des courbes du moniteur (px). </summary>
    public double MonitorLineWidth { get; set; } = 1.8;
    /// <summary> Remplissage sous les courbes du moniteur. </summary>
    public bool MonitorFill { get; set; } = true;
    /// <summary> Lignes de repère derrière les courbes du moniteur. </summary>
    public bool MonitorGrid { get; set; } = true;
    /// <summary> Couleurs des courbes (cpu, ram, gpu, vram, net) en #RRGGBB ; absent = couleur du thème. </summary>
    public Dictionary<string, string> MonitorColors { get; set; } = new();
    /// <summary> Widgets de la page Moniteur et leur disposition ; absent = disposition d'origine. </summary>
    public List<MonitorTileSetting>? MonitorTiles { get; set; }

    /// <summary> Plafond du contexte (--max-model-len) calculé automatiquement ; 0 = le maximum du modèle. </summary>
    public int AutoContextCap { get; set; } = ArgsPlanner.DefaultContextCap;

    /// <summary> Arrêter les serveurs vLLM quand on quitte l'application (sinon ils continuent et sont retrouvés au prochain lancement). </summary>
    public bool StopServersOnExit { get; set; } = true;
    /// <summary> Diagramme Mermaid invalide dans une réponse : renvoyé au modèle avec l'erreur pour correction (2 essais). </summary>
    public bool MermaidAutoFix { get; set; }

    /// <summary> La croix de la fenêtre garde l'application en arrière-plan (icône dans la zone de notification). </summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary> Adresse API unique pour tous les serveurs (routeur intégré, voir ApiRouter). </summary>
    public bool RouterEnabled { get; set; } = true;
    /// <summary> Adresse d'écoute du routeur. </summary>
    public string RouterHost { get; set; } = "127.0.0.1";
    /// <summary> Port du routeur. </summary>
    public int RouterPort { get; set; } = 9000;
    /// <summary> Clé API exigée par le routeur (null = aucune). </summary>
    public string? RouterApiKey { get; set; }

    /// <summary> Avant de réveiller un modèle, mettre en veille les autres modèles du même GPU (mode veille). </summary>
    public bool RouterAutoSleep { get; set; } = true;

    /// <summary> Configurations de lancement enregistrées par l'utilisateur. </summary>
    public List<FavoriteConfig> Favorites { get; set; } = new();

    /// <summary> Menu latéral replié. </summary>
    public bool SidebarCollapsed { get; set; }

    /// <summary> Paramètres de lancement mémorisés par modèle (clé = chemin du modèle). </summary>
    public Dictionary<string, LaunchProfile> Profiles { get; set; } = new();
}

/// <summary> Valeurs choisies par l'utilisateur pour un modèle (null = automatique). </summary>
public sealed class LaunchProfile
{
    /// <summary> Adresse d'écoute (--host). </summary>
    public string? Host { get; set; }
    /// <summary> Port du serveur (--port). </summary>
    public int? Port { get; set; }
    /// <summary> Nom exposé par l'API (--served-model-name). </summary>
    public string? ServedName { get; set; }
    /// <summary> Part de la VRAM réservée (--gpu-memory-utilization). </summary>
    public double? GpuMemoryUtilization { get; set; }
    /// <summary> Contexte maximal (--max-model-len). </summary>
    public int? MaxModelLen { get; set; }
    /// <summary> Type du cache KV (--kv-cache-dtype). </summary>
    public string? KvCacheDtype { get; set; }
    /// <summary> Désactive les graphes CUDA (--enforce-eager). </summary>
    public bool? EnforceEager { get; set; }
    /// <summary> Nombre de GPU en parallélisme de tenseurs (--tensor-parallel-size). </summary>
    public int? TensorParallel { get; set; }
    /// <summary> Nombre maximal de requêtes simultanées (--max-num-seqs). </summary>
    public int? MaxNumSeqs { get; set; }
    /// <summary> Type des poids (--dtype). </summary>
    public string? Dtype { get; set; }
    /// <summary> Méthode de quantification (--quantization). </summary>
    public string? Quantization { get; set; }
    /// <summary> Autorise le code Python du dépôt (--trust-remote-code). </summary>
    public bool? TrustRemoteCode { get; set; }
    /// <summary> Cache des préfixes activé. </summary>
    public bool? PrefixCaching { get; set; }
    /// <summary> Clé API exigée par le serveur (--api-key). </summary>
    public string? ApiKey { get; set; }
    /// <summary> GPU utilisés (liste d'index séparés par des virgules). </summary>
    public string? GpuIds { get; set; }
    /// <summary> Lancement avec vLLM-Omni. </summary>
    public bool? Omni { get; set; }
    /// <summary> Tokenizer à utiliser (--tokenizer). </summary>
    public string? Tokenizer { get; set; }
    /// <summary> Dossier ou dépôt de la configuration (--hf-config-path). </summary>
    public string? HfConfigPath { get; set; }
    /// <summary> Arguments supplémentaires ajoutés tels quels. </summary>
    public string? ExtraArgs { get; set; }

    /// <summary> Parsers : null = détection automatique, "" = aucun. </summary>
    public string? ReasoningParser { get; set; }
    /// <summary> Parser d'appels d'outils : null = détection automatique, "" = aucun. </summary>
    public string? ToolCallParser { get; set; }
    /// <summary> Modèle multimodal lancé en texte seul. </summary>
    public bool? TextOnly { get; set; }
    /// <summary> Modèle converti : lancé avec stable-diffusion.cpp (fichier d'origine) plutôt que vLLM-Omni. </summary>
    public bool? UseSdCpp { get; set; }
    /// <summary> Transformer seul avec stable-diffusion.cpp : chemin du VAE (--vae). </summary>
    public string? SdVae { get; set; }
    /// <summary> Transformer seul avec stable-diffusion.cpp : chemin de l'encodeur de texte (--llm). </summary>
    public string? SdLlm { get; set; }
    /// <summary> Part du modèle sur le GPU (%), le reste en RAM (vLLM : --cpu-offload-gb ; llama.cpp : -ngl). </summary>
    public int? GpuPercent { get; set; }
    /// <summary> Encodeurs de texte en RAM (pipelines d'image). </summary>
    public bool? CpuOffload { get; set; }
    /// <summary> Mode veille activé (--enable-sleep-mode). </summary>
    public bool? SleepMode { get; set; }
    /// <summary> Nombre de jetons prédits par MTP (décodage spéculatif). </summary>
    public int? MtpTokens { get; set; }
    /// <summary> Adaptateurs LoRA choisis (chemins). </summary>
    public List<string>? Loras { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(DownloadMetadata))]
[JsonSerializable(typeof(InstanceRecord))]
[JsonSerializable(typeof(SmokeResult))]
[JsonSerializable(typeof(VllmOptionsCache))]
[JsonSerializable(typeof(List<PendingDownload>))]
internal partial class AppJsonContext : JsonSerializerContext { }
