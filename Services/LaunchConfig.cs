/*
    Fichier      :  LaunchConfig.cs
    Projet       :  AlpenMux

    Description  :
        Configuration complète d'un lancement de serveur : vLLM, ou llama.cpp (llama-server) pour les fichiers GGUF.
        Utilisée par le formulaire de lancement, les favoris et l'historique des serveurs :
            - BuildArguments() : arguments de `vllm serve` ou de `llama-server`
            - BuildEnvironment() : variables (choix des GPU, bibliothèques de llama.cpp)
            - Describe() : lignes (libellé, valeur) affichées dans l'onglet Configuration
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AlpenMux.Services;

/// <summary> Configuration complète d'un lancement (vLLM, llama.cpp ou stable-diffusion.cpp), enregistrée dans l'historique et les favoris. </summary>
public sealed class LaunchConfig
{
    /// <summary> Moteur vLLM (ou vLLM-Omni). </summary>
    public const string EngineVllm = "vllm";
    /// <summary> Moteur llama.cpp. </summary>
    public const string EngineLlama = "llama.cpp";
    /// <summary> Moteur stable-diffusion.cpp. </summary>
    public const string EngineSdCpp = "sd.cpp";

    /// <summary>
    /// Moteur : vLLM, llama.cpp pour les fichiers GGUF (lus directement, sans copie),
    /// ou stable-diffusion.cpp pour les fichiers uniques d'images (ComfyUI / Automatic1111).
    /// </summary>
    public string Engine { get; set; } = EngineVllm;
    /// <summary> Lancé avec llama.cpp. </summary>
    public bool IsLlama => Engine == EngineLlama;
    /// <summary> Lancé avec stable-diffusion.cpp. </summary>
    public bool IsSdCpp => Engine == EngineSdCpp;
    /// <summary> Moteur en binaire natif (pas dans le venv ni dans WSL) : llama.cpp ou stable-diffusion.cpp. </summary>
    public bool IsNative => IsLlama || IsSdCpp;

    /// <summary> stable-diffusion.cpp : fichier unique lancé (ModelTarget reste le dossier du modèle). </summary>
    public string? SdModelFile { get; set; }

    /// <summary> stable-diffusion.cpp : poids gardés en RAM et chargés sur le GPU au besoin (--offload-to-cpu). </summary>
    public bool SdOffload { get; set; }

    /// <summary>
    /// stable-diffusion.cpp, transformer seul : chemin du VAE (--vae). Avec <see cref="SdLlm"/>, le transformer
    /// est donné par --diffusion-model au lieu de -m.
    /// </summary>
    public string? SdVae { get; set; }

    /// <summary> stable-diffusion.cpp, transformer seul : chemin de l'encodeur de texte Qwen3 (--llm). </summary>
    public string? SdLlm { get; set; }

    /// <summary> llama.cpp : type du cache KV (f16, q8_0, q4_0) ; q8_0 / q4_0 activent l'attention flash. </summary>
    public string LlamaKvType { get; set; } = "f16";

    /// <summary> llama.cpp : couches sur le GPU (-ngl) ; null = toutes. </summary>
    public int? LlamaGpuLayers { get; set; }

    /// <summary> Argument du modèle (dossier, dépôt ou fichier .gguf). </summary>
    public string ModelTarget { get; set; } = "";
    /// <summary> Nom du modèle affiché. </summary>
    public string ModelRepo { get; set; } = "";
    /// <summary> Modèle GGUF. </summary>
    public bool ModelIsGguf { get; set; }

    /// <summary> Adresse d'écoute. </summary>
    public string Host { get; set; } = "0.0.0.0";
    /// <summary> Port. </summary>
    public int Port { get; set; } = 8000;
    /// <summary> Nom exposé par l'API. </summary>
    public string ServedName { get; set; } = "";

    /// <summary> Part de la VRAM réservée. </summary>
    public double GpuMemoryUtilization { get; set; } = 0.9;
    /// <summary> Contexte maximal (null = celui du modèle). </summary>
    public int? MaxModelLen { get; set; }
    /// <summary> Type du cache KV. </summary>
    public string KvCacheDtype { get; set; } = "auto";
    /// <summary> Graphes CUDA désactivés. </summary>
    public bool EnforceEager { get; set; }
    /// <summary> Parallélisme de tenseurs (nombre de GPU). </summary>
    public int TensorParallel { get; set; } = 1;
    /// <summary> Parallélisme de pipeline. </summary>
    public int PipelineParallel { get; set; } = 1;
    /// <summary> Nombre maximal de requêtes simultanées. </summary>
    public int? MaxNumSeqs { get; set; }
    /// <summary> Type des poids. </summary>
    public string Dtype { get; set; } = "auto";
    /// <summary> Code Python du dépôt autorisé. </summary>
    public bool TrustRemoteCode { get; set; }
    /// <summary> Cache des préfixes. </summary>
    public bool PrefixCaching { get; set; } = true;
    /// <summary> Clé API exigée par le serveur. </summary>
    public string? ApiKey { get; set; }

    /// <summary> Indices nvidia-smi séparés par des virgules ; null = tous les GPU. </summary>
    public string? GpuIds { get; set; }
    /// <summary> Fabricant des GPU (variable *_VISIBLE_DEVICES). </summary>
    public string GpuVendor { get; set; } = "NVIDIA";

    /// <summary> Lancé avec vLLM-Omni. </summary>
    public bool Omni { get; set; }

    /// <summary> vLLM-Omni : part de VRAM prévue par étape (clé = numéro d'étape) et GPU de chaque étape. </summary>
    public Dictionary<string, double>? OmniStageWeights { get; set; }
    /// <summary> GPU de chaque étape vLLM-Omni. </summary>
    public Dictionary<string, string>? OmniStageDevices { get; set; }

    /// <summary> vLLM-Omni : part de VRAM minimale de l'étape principale (poids + surcoût + un peu de cache KV). </summary>
    public double? OmniMainMin { get; set; }

    /// <summary> La VRAM du serveur répartie entre les étapes (clé = numéro d'étape). </summary>
    public Dictionary<string, double> OmniSplit() => OmniDeploy.Split(OmniStageWeights, OmniStageDevices, GpuMemoryUtilization, OmniMainMin);

    /// <summary> JSON --stage-overrides : la VRAM du serveur répartie entre les étapes. </summary>
    public string? OmniOverrides => Omni ? OmniDeploy.BuildOverrides(OmniSplit()) : null;
    /// <summary> Tokenizer à utiliser. </summary>
    public string? Tokenizer { get; set; }
    /// <summary> Configuration du modèle (dossier ou dépôt). </summary>
    public string? HfConfigPath { get; set; }

    /// <summary> JSON de --hf-overrides : correctif connu appliqué automatiquement (voir ModelCompat.KnownHfOverrides). </summary>
    public string? HfOverrides { get; set; }
    /// <summary> Arguments supplémentaires. </summary>
    public string? ExtraArgs { get; set; }

    /// <summary> Part de la VRAM libre utilisée pour le calcul automatique (information). </summary>
    public double VramShare { get; set; } = 100;

    /// <summary> --reasoning-parser : la réflexion du modèle va dans reasoning_content (null = aucun). </summary>
    public string? ReasoningParser { get; set; }

    /// <summary> --tool-call-parser + --enable-auto-tool-choice : appels d'outils au format OpenAI (null = aucun). </summary>
    public string? ToolCallParser { get; set; }

    /// <summary> Modèle multimodal en texte seul : l'encodeur vision / audio n'est pas chargé (moins de VRAM). </summary>
    public bool TextOnly { get; set; }

    /// <summary> Serveur d'embedding (RAG) : llama.cpp en mode --embedding, sans chat. </summary>
    public bool Embedding { get; set; }

    /// <summary> vLLM-Omni, pipeline d'image : encodeurs de texte gardés en RAM, passés sur le GPU le temps de lire le prompt. </summary>
    public bool CpuOffload { get; set; }

    /// <summary> vLLM : Gio de poids gardés en RAM par GPU (--cpu-offload-gb), selon « % du modèle sur le GPU ». </summary>
    public double? CpuOffloadGb { get; set; }

    /// <summary> vLLM-Omni, pipeline d'image : décodage de l'image par tuiles (--vae-use-tiling). </summary>
    public bool VaeTiling { get; set; }

    /// <summary>
    /// Mise en veille possible (--enable-sleep-mode) : le modèle libère la VRAM et se réveille en quelques secondes.
    /// Les routes /sleep et /wake_up demandent VLLM_SERVER_DEV_MODE=1 : le serveur n'écoute alors que sur 127.0.0.1.
    /// </summary>
    public bool SleepMode { get; set; }

    /// <summary> Adaptateurs LoRA servis avec le modèle (--enable-lora --lora-modules nom=chemin). </summary>
    public List<LoraModule>? Loras { get; set; }

    /// <summary> --max-lora-rank : au moins le rang (r) du plus grand adaptateur. </summary>
    public int? MaxLoraRank { get; set; }

    /// <summary> Décodage spéculatif MTP : tokens proposés par étape (0 = désactivé). </summary>
    public int MtpTokens { get; set; }

    /// <summary> Nom de la méthode MTP pour ce vLLM (« mtp », ou deepseek_mtp… sur les anciennes versions). </summary>
    public string? MtpMethod { get; set; }

    /// <summary> Adresse d'écoute réelle : 127.0.0.1 en mode veille (routes de développement non protégées). </summary>
    public string EffectiveHost => SleepMode && !IsNative ? "127.0.0.1" : Host;

    /// <summary> Copie indépendante (les listes et dictionnaires sont copiés). </summary>
    public LaunchConfig Clone()
    {
        var c = (LaunchConfig)MemberwiseClone();
        if (OmniStageWeights != null) c.OmniStageWeights = new(OmniStageWeights);
        if (OmniStageDevices != null) c.OmniStageDevices = new(OmniStageDevices);
        if (Loras != null) c.Loras = Loras.Select(l => new LoraModule { Name = l.Name, Path = l.Path }).ToList();
        return c;
    }

    /// <summary> llama.cpp : contexte par défaut si aucun n'est donné. </summary>
    public const int LlamaDefaultContext = 4096;

    /// <summary> Arguments de `vllm serve`, de `llama-server` ou de `sd-server` pour cette configuration. </summary>
    /// <param name="options"> Options acceptées par le vLLM installé (choix entre deux formes d'une option). </param>
    /// <returns> Programme puis arguments. </returns>
    public List<string> BuildArguments(IVllmOptions options)
    {
        if (IsLlama) return BuildLlamaArguments();
        if (IsSdCpp) return BuildSdCppArguments();

        // Windows : vLLM tourne dans WSL, les chemins des fichiers lui sont donnés en /mnt/c/…
        static string L(string? p) => LinuxHost.ToLinux(p ?? "");
        var a = new List<string> { "serve", L(ModelTarget), "--host", EffectiveHost, "--port", Port.ToString() };
        if (!string.IsNullOrWhiteSpace(ServedName)) { a.Add("--served-model-name"); a.Add(ServedName); }

        if (Omni)
        {
            // Chaque étape d'Omni a sa propre part de VRAM : la part choisie pour le serveur est répartie entre elles
            a.Add("--omni");
            // Délai par défaut de vLLM-Omni : 300 s, dépassé au premier démarrage sous WSL (compilation et capture
            // des graphes CUDA sur le système de fichiers 9P). Les options supplémentaires peuvent le remplacer.
            a.Add("--stage-init-timeout"); a.Add("900");
            if (OmniOverrides is { } ov) { a.Add("--stage-overrides"); a.Add(ov); }
            if (CpuOffload) a.Add("--enable-cpu-offload");
            // Décodage de l'image par tuiles : évite le manque de mémoire en fin de génération (≈ 1 Gio d'un coup en 1024×1024)
            if (VaeTiling || CpuOffload) a.Add("--vae-use-tiling");
        }
        else
        {
            a.Add("--gpu-memory-utilization");
            a.Add(GpuMemoryUtilization.ToString("0.00", CultureInfo.InvariantCulture));
            if (MaxModelLen is { } len) { a.Add("--max-model-len"); a.Add(len.ToString()); }
            if (KvCacheDtype != "auto") { a.Add("--kv-cache-dtype"); a.Add(KvCacheDtype); }
            if (EnforceEager) a.Add("--enforce-eager");
            if (CpuOffloadGb is > 0 and var gb) { a.Add("--cpu-offload-gb"); a.Add(gb.ToString("0.#", CultureInfo.InvariantCulture)); }
        }
        if (TensorParallel > 1) { a.Add("--tensor-parallel-size"); a.Add(TensorParallel.ToString()); }
        if (PipelineParallel > 1) { a.Add("--pipeline-parallel-size"); a.Add(PipelineParallel.ToString()); }
        if (MaxNumSeqs is { } seqs) { a.Add("--max-num-seqs"); a.Add(seqs.ToString()); }
        if (Dtype != "auto") { a.Add("--dtype"); a.Add(Dtype); }
        if (TrustRemoteCode) a.Add("--trust-remote-code");
        if (!PrefixCaching) a.Add("--no-enable-prefix-caching");
        if (!string.IsNullOrWhiteSpace(ApiKey)) { a.Add("--api-key"); a.Add(ApiKey); }
        if (!string.IsNullOrWhiteSpace(Tokenizer)) { a.Add("--tokenizer"); a.Add(L(Tokenizer)); }
        if (!string.IsNullOrWhiteSpace(HfConfigPath)) { a.Add("--hf-config-path"); a.Add(L(HfConfigPath)); }
        if (!string.IsNullOrWhiteSpace(HfOverrides)) { a.Add("--hf-overrides"); a.Add(HfOverrides); }
        if (!string.IsNullOrWhiteSpace(ReasoningParser)) { a.Add("--reasoning-parser"); a.Add(ReasoningParser); }
        if (!string.IsNullOrWhiteSpace(ToolCallParser)) { a.Add("--enable-auto-tool-choice"); a.Add("--tool-call-parser"); a.Add(ToolCallParser); }
        if (TextOnly)
        {
            // vLLM récent : une option ; avant : toutes les modalités limitées à 0 (même effet)
            if (options.Supports("--language-model-only") != false) a.Add("--language-model-only");
            else { a.Add("--limit-mm-per-prompt"); a.Add("{\"image\": 0, \"video\": 0, \"audio\": 0}"); }
        }
        if (SleepMode) a.Add("--enable-sleep-mode");
        if (Loras is { Count: > 0 })
        {
            a.Add("--enable-lora");
            a.Add("--lora-modules");
            a.AddRange(Loras.Select(l => $"{l.Name}={L(l.Path)}"));
            if (MaxLoraRank is { } rank) { a.Add("--max-lora-rank"); a.Add(rank.ToString()); }
        }
        if (MtpTokens > 0)
        {
            a.Add("--speculative-config");
            a.Add($"{{\"method\": \"{MtpMethod ?? "mtp"}\", \"num_speculative_tokens\": {MtpTokens}}}");
        }
        // --omni déjà ajouté plus haut : vLLM signale « Found duplicate keys --omni » pour un doublon
        a.AddRange(UiServices.SplitArgs(ExtraArgs).Where(x => !(Omni && x == "--omni")));
        return a;
    }

    /// <summary> Arguments de llama-server : le fichier GGUF est lu directement. </summary>
    private List<string> BuildLlamaArguments()
    {
        var a = new List<string>
        {
            "-m", ModelTarget, "--host", Host, "--port", Port.ToString(),
            "-c", (MaxModelLen ?? LlamaDefaultContext).ToString(),
            "-ngl", (LlamaGpuLayers ?? 999).ToString(),   // 999 = toutes les couches sur le GPU
            "--metrics",
        };
        if (!string.IsNullOrWhiteSpace(ServedName)) { a.Add("--alias"); a.Add(ServedName); }
        if (Embedding)
        {
            // Un passage tient dans un micro-batch (sinon « input is too large », AGENTS/plan RAG)
            a.AddRange(["--embedding", "-b", "8192", "-ub", "8192"]);
        }
        if (LlamaKvType != "f16")
        {
            // Cache KV quantifié : l'attention flash est nécessaire pour la partie V
            a.AddRange(["-fa", "on", "--cache-type-k", LlamaKvType, "--cache-type-v", LlamaKvType]);
        }
        if (!string.IsNullOrWhiteSpace(ApiKey)) { a.Add("--api-key"); a.Add(ApiKey); }
        a.AddRange(UiServices.SplitArgs(ExtraArgs));
        return a;
    }

    /// <summary>
    /// Arguments de sd-server : le fichier unique est lu directement. Les poids sont placés automatiquement
    /// entre GPU et RAM selon la mémoire libre (--auto-fit, actif par défaut).
    /// </summary>
    private List<string> BuildSdCppArguments()
    {
        // Transformer seul (FLUX.2, Z-Image) : --diffusion-model, avec le VAE et l'encodeur de texte à part
        var components = SdVae != null || SdLlm != null;
        var a = new List<string> { components ? "--diffusion-model" : "-m", SdModelFile ?? ModelTarget, "--listen-ip", Host, "--listen-port", Port.ToString() };
        if (SdVae != null) a.AddRange(["--vae", SdVae]);
        if (SdLlm != null) a.AddRange(["--llm", SdLlm]);
        if (SdOffload) a.Add("--offload-to-cpu");
        a.AddRange(UiServices.SplitArgs(ExtraArgs));
        return a;
    }

    /// <summary> Variables d'environnement du lancement (GPU visibles, bibliothèques, mode veille…). null = variable retirée. </summary>
    public Dictionary<string, string?> BuildEnvironment()
    {
        // Ordre PCI : les indices CUDA correspondent alors à ceux de nvidia-smi
        var env = new Dictionary<string, string?> { ["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID" };
        if (IsLlama && !OperatingSystem.IsWindows()) env["LD_LIBRARY_PATH"] = LlamaCppService.LibraryPath();
        if (IsSdCpp && !OperatingSystem.IsWindows()) env["LD_LIBRARY_PATH"] = StableDiffusionCppService.LibraryPath();
        if (SleepMode && !IsNative) env["VLLM_SERVER_DEV_MODE"] = "1";   // routes /sleep, /wake_up, /is_sleeping
        // WSL : pas de toolkit CUDA (nvcc) dans la distribution, donc le échantillonneur FlashInfer ne peut pas
        // compiler son noyau (« Could not find nvcc »). L'échantillonneur de PyTorch prend le relais.
        if (!IsNative && LinuxHost.IsWsl) env["VLLM_USE_FLASHINFER_SAMPLER"] = "0";
        if (GpuIds != null)
        {
            if (GpuVendor == "AMD") env["HIP_VISIBLE_DEVICES"] = GpuIds;
            else env["CUDA_VISIBLE_DEVICES"] = GpuIds;
        }
        return env;
    }

    /// <summary> Met un argument entre apostrophes si le shell l'exige. </summary>
    public static string Quote(string s) =>
        s.Length > 0 && s.All(c => char.IsLetterOrDigit(c) || "-_./:=,@+".Contains(c)) ? s : $"'{s.Replace("'", "'\\''")}'";

    /// <summary> Ligne de commande équivalente, à copier dans un terminal. </summary>
    /// <param name="options"> Options acceptées par le vLLM installé. </param>
    public string CommandLine(IVllmOptions options)
    {
        var env = GpuIds != null ? $"{(GpuVendor == "AMD" ? "HIP" : "CUDA")}_VISIBLE_DEVICES={GpuIds} " : "";
        if (SleepMode && !IsNative) env += "VLLM_SERVER_DEV_MODE=1 ";
        return env + (IsLlama ? "llama-server " : IsSdCpp ? "sd-server " : "vllm ") + string.Join(' ', BuildArguments(options).Select(Quote));
    }

    /// <summary> Résumé lisible de la configuration. </summary>
    public List<ConfigRow> Describe()
    {
        static string YesNo(bool b) => b ? Loc.T("Common_Yes") : Loc.T("Common_No");
        if (IsSdCpp)
            return
            [
                new(Loc.T("Cfg_Engine"), "stable-diffusion.cpp" + (StableDiffusionCppService.InstalledVersion is { } sv ? $" ({sv})" : "")),
                new(Loc.T("Launch_Model"), ModelRepo),
                new(Loc.T("Cfg_Target"), SdModelFile ?? ModelTarget),
                .. SdVae is { } vae ? new[] { new ConfigRow("--vae", vae) } : Array.Empty<ConfigRow>(),
                .. SdLlm is { } llm ? new[] { new ConfigRow("--llm", llm) } : Array.Empty<ConfigRow>(),
                new("--listen-ip / --listen-port", $"{Host} : {Port}"),
                new("--offload-to-cpu", YesNo(SdOffload)),
                .. string.IsNullOrWhiteSpace(ExtraArgs) ? Array.Empty<ConfigRow>() : [new ConfigRow(Loc.T("Launch_Extra"), ExtraArgs)],
            ];
        if (IsLlama)
            return
            [
                new(Loc.T("Cfg_Engine"), "llama.cpp" + (LlamaCppService.InstalledVersion is { } v ? $" ({v})" : "")),
                new(Loc.T("Launch_Model"), ModelRepo),
                new(Loc.T("Cfg_Target"), ModelTarget),
                new(Loc.T("Cfg_Gpus"), GpuIds ?? Loc.T("Cfg_AllGpus")),
                new("--host / --port", $"{Host} : {Port}"),
                new("--alias", ServedName),
                new("-c", (MaxModelLen ?? LlamaDefaultContext).ToString()),
                new("-ngl", LlamaGpuLayers?.ToString() ?? Loc.T("Launch_AllLayers")),
                new("--cache-type-k / -v", LlamaKvType),
                new("--api-key", string.IsNullOrEmpty(ApiKey) ? Loc.T("Cfg_None") : "••••••" + ApiKey[^Math.Min(4, ApiKey.Length)..]),
                .. string.IsNullOrWhiteSpace(ExtraArgs) ? Array.Empty<ConfigRow>() : [new ConfigRow(Loc.T("Launch_Extra"), ExtraArgs)],
            ];
        var rows = new List<ConfigRow>
        {
            new(Loc.T("Launch_Model"), ModelRepo),
            new(Loc.T("Cfg_Target"), ModelTarget),
            new(Loc.T("Cfg_Gpus"), GpuIds ?? Loc.T("Cfg_AllGpus")),
            new("--host / --port", $"{EffectiveHost} : {Port}"),
            new("--served-model-name", ServedName),
        };
        if (Omni)
        {
            rows.Add(new("--omni", YesNo(true)));
            rows.Add(new(Loc.T("Cfg_OmniVram"), $"{GpuMemoryUtilization * 100:0} %"));
            if (OmniOverrides is { } ov) rows.Add(new("--stage-overrides", ov));
        }
        else
        {
            rows.Add(new("--gpu-memory-utilization", $"{GpuMemoryUtilization:0.00}  ({GpuMemoryUtilization * 100:0} %)"));
            rows.Add(new("--max-model-len", MaxModelLen?.ToString() ?? "auto"));
            rows.Add(new("--kv-cache-dtype", KvCacheDtype));
            rows.Add(new("--enforce-eager", YesNo(EnforceEager)));
        }
        rows.Add(new("--tensor-parallel-size", TensorParallel.ToString()));
        if (PipelineParallel > 1) rows.Add(new("--pipeline-parallel-size", PipelineParallel.ToString()));
        rows.Add(new("--max-num-seqs", MaxNumSeqs?.ToString() ?? "auto"));
        rows.Add(new("--dtype", Dtype));
        rows.Add(new(Loc.T("Launch_PrefixCaching"), YesNo(PrefixCaching)));
        rows.Add(new("--trust-remote-code", YesNo(TrustRemoteCode)));
        rows.Add(new("--api-key", string.IsNullOrEmpty(ApiKey) ? Loc.T("Cfg_None") : "••••••" + ApiKey[^Math.Min(4, ApiKey.Length)..]));
        if (!string.IsNullOrWhiteSpace(Tokenizer)) rows.Add(new("--tokenizer", Tokenizer));
        if (!string.IsNullOrWhiteSpace(HfConfigPath)) rows.Add(new("--hf-config-path", HfConfigPath));
        if (!string.IsNullOrWhiteSpace(HfOverrides)) rows.Add(new("--hf-overrides", HfOverrides));
        rows.Add(new("--reasoning-parser", ReasoningParser ?? Loc.T("Cfg_None")));
        rows.Add(new("--tool-call-parser", ToolCallParser ?? Loc.T("Cfg_None")));
        if (TextOnly) rows.Add(new(Loc.T("Launch_TextOnly"), YesNo(true)));
        if (CpuOffloadGb is > 0 and var off) rows.Add(new("--cpu-offload-gb", off.ToString("0.#", CultureInfo.InvariantCulture)));
        if (CpuOffload) rows.Add(new("--enable-cpu-offload", YesNo(true)));
        rows.Add(new("--enable-sleep-mode", YesNo(SleepMode)));
        if (Loras is { Count: > 0 }) rows.Add(new("--lora-modules", string.Join("  ", Loras.Select(l => $"{l.Name}={l.Path}"))));
        if (MtpTokens > 0) rows.Add(new(Loc.T("Launch_Mtp"), Loc.F("Cfg_MtpValue", MtpMethod ?? "mtp", MtpTokens)));
        if (!string.IsNullOrWhiteSpace(ExtraArgs)) rows.Add(new(Loc.T("Launch_Extra"), ExtraArgs));
        return rows;
    }
}

/// <summary> Ligne du résumé de la configuration. </summary>
/// <param name="Label"> Libellé. </param>
/// <param name="Value"> Valeur. </param>
public sealed record ConfigRow(string Label, string Value);

/// <summary> Adaptateur LoRA : nom exposé par l'API (champ "model" des requêtes) et dossier. </summary>
public sealed class LoraModule
{
    /// <summary> Nom de l'adaptateur exposé par l'API. </summary>
    public string Name { get; set; } = "";
    /// <summary> Dossier de l'adaptateur. </summary>
    public string Path { get; set; } = "";
}

/// <summary> Configuration enregistrée en favori. </summary>
public sealed class FavoriteConfig
{
    /// <summary> Identifiant. </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary> Nom donné par l'utilisateur. </summary>
    public string Name { get; set; } = "";
    /// <summary> Date d'enregistrement. </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary> Configuration enregistrée. </summary>
    public LaunchConfig Config { get; set; } = new();
}
