/*
    Fichier      :  ModelScanner.cs
    Projet       :  AlpenMux

    Description  :
        Détection des modèles dans le dossier des modèles (reprend start_vLLM.py).
        Formats reconnus :
            * dossier simple (hf download --local-dir) : <nom>/config.json ou <org>/<nom>/config.json
            * cache Hugging Face : models--<org>--<nom>/snapshots/<rev>/
            * dépôt GGUF : chaque fichier .gguf (ou groupe de parties) est un modèle à part entière
              (ex. LFM2.5-2.6B-Q4_K_M.gguf et LFM2.5-2.6B-Q8_0.gguf dans le même dossier).
              Les sous-dossiers contenant un autre modèle (config.json + poids) sont aussi listés.
        Pour chaque modèle : architecture, quantification, taille des poids,
        paramètres utiles à l'estimation du cache KV, détection audio/omni.
        Poids comptés : seulement ceux que vLLM charge (l'index safetensors fait foi ; .bin / .pt en double
        et consolidated.safetensors des dépôts Mistral ignorés quand des safetensors HF existent).
        Cache KV : les couches à fenêtre glissante (Gemma, gpt-oss…) ne gardent que la fenêtre.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Modèle détecté dans le dossier des modèles. </summary>
public sealed class ModelInfo
{
    /// <summary> Nom affiché. </summary>
    public required string Repo { get; init; }       // nom affiché
    /// <summary> Argument pour `vllm serve` (dossier, id HF ou fichier .gguf). </summary>
    public required string Target { get; init; }     // argument pour `vllm serve` (dossier, id HF ou fichier .gguf)
    /// <summary> Dossier des fichiers. </summary>
    public required string Root { get; init; }       // dossier des fichiers
    /// <summary> Disposition : "folder", "cache" ou "gguf". </summary>
    public required string Layout { get; init; }     // "folder", "cache" ou "gguf"

    /// <summary> Nom proposé pour --served-model-name. </summary>
    public string Name { get; init; } = "";

    /// <summary> Architecture (config.json). </summary>
    public string Architecture { get; init; } = "?";
    /// <summary> Type de modèle (model_type). </summary>
    public string? ModelType { get; init; }
    /// <summary> Quantification affichée. </summary>
    public string Quantization { get; init; } = "";
    /// <summary> Type des poids (torch_dtype). </summary>
    public string? TorchDtype { get; init; }
    /// <summary> Poids quantifiés. </summary>
    public bool IsQuantized { get; init; }
    /// <summary> Taille des poids (octets). </summary>
    public long WeightsBytes { get; init; }

    /// <summary> Taille des fichiers de poids sur le disque, quand elle diffère des poids chargés (pipeline diffusers en fp32). </summary>
    public long? FileBytes { get; init; }
    /// <summary> Taille sur le disque (octets). </summary>
    public long DiskBytes => FileBytes ?? WeightsBytes;

    /// <summary> Modèle d'image en fichier unique (ComfyUI…) : chemin du fichier et famille reconnue (FLUX.2, SDXL…). </summary>
    public string? SingleFile { get; init; }
    /// <summary> Type d'un modèle d'image « fichier unique » (FLUX.1, SDXL…), ou null. </summary>
    public string? SingleFileKind { get; init; }
    /// <summary> Le fichier unique peut être converti au format diffusers. </summary>
    public bool CanConvert => SingleFileConverter.CanConvert(SingleFileKind);
    /// <summary> Fichier unique complet (SD 1.x / 2.x, SDXL) : lançable tel quel avec stable-diffusion.cpp. </summary>
    public bool SdCppRunnable => SingleFileConverter.SdCppRunnable(SingleFileKind);
    /// <summary> Transformer seul (FLUX.2, Z-Image) : lancé avec stable-diffusion.cpp avec un VAE et un encodeur de texte. </summary>
    public bool SdCppWithComponents => SingleFileConverter.SdCppNeedsComponents(SingleFileKind);
    /// <summary> Lancement possible avec stable-diffusion.cpp, quel que soit le type de fichier unique. </summary>
    public bool SdCppLaunchable => SdCppRunnable || SdCppWithComponents;

    /// <summary>
    /// Modèle converti au format diffusers dont le fichier unique d'origine est encore là : il peut aussi être
    /// lancé avec stable-diffusion.cpp (choix du moteur au lancement).
    /// </summary>
    public string? OriginalFile { get; init; }
    /// <summary> Type du fichier d'origine d'un modèle converti. </summary>
    public string? OriginalKind { get; init; }
    /// <summary> Le fichier d'origine peut aussi être lancé avec stable-diffusion.cpp. </summary>
    public bool HasSdCppAlternative => OriginalFile != null && SingleFileConverter.SdCppRunnable(OriginalKind);

    /// <summary> Fichier lancé par stable-diffusion.cpp : le fichier unique, ou l'original d'un modèle converti. </summary>
    public string? SdCppFile => SdCppLaunchable ? SingleFile : HasSdCppAlternative ? OriginalFile : null;

    /// <summary> Pipeline diffusers : poids des encodeurs de texte (text_encoder*), gardables en RAM avec --enable-cpu-offload. </summary>
    public long TextEncoderBytes { get; init; }

    /// <summary> Coût du cache KV en fp16 (null = inconnu). </summary>
    public KvInfo? Kv { get; init; }

    /// <summary> Modèle multimodal (vision / audio) : le mode « texte seul » est proposé. </summary>
    public bool IsMultimodal { get; init; }

    /// <summary> Poids des modules vision / audio, non chargés en mode texte seul. </summary>
    public long MultimodalBytes { get; init; }

    /// <summary> Taille du vocabulaire : le surcoût de vLLM (logits) en dépend. </summary>
    public int? VocabSize { get; init; }

    /// <summary> Couches MTP (prédiction de plusieurs tokens) : décodage spéculatif possible sans modèle externe. </summary>
    public int? MtpLayers { get; init; }
    /// <summary> Poids des sous-modèles (dossiers avec leur propre config), chargés avec le modèle par vLLM-Omni. </summary>
    public long SubModelBytes { get; init; }
    /// <summary> Nombre de couches (GGUF) : effet de -ngl sur la VRAM avec llama.cpp. </summary>
    public int? NumLayers { get; init; }
    /// <summary> Contexte maximal du modèle. </summary>
    public int? MaxPositions { get; init; }
    /// <summary> Nombre de têtes d'attention. </summary>
    public int? AttentionHeads { get; init; }
    /// <summary> Modèle servi par vLLM-Omni. </summary>
    public bool IsOmni { get; init; }
    /// <summary> Modèle GGUF (llama.cpp). </summary>
    public bool IsGguf { get; init; }

    /// <summary> Modèle d'origine (dépôt HF) : tokenizer et config pour les GGUF. </summary>
    public string? BaseModel { get; init; }

    /// <summary> Dépôt Hugging Face d'où vient le modèle (si téléchargé par l'application). </summary>
    public string? SourceRepo { get; init; }

    /// <summary> Le dossier contient-il les fichiers du tokenizer (utile pour les GGUF) ? </summary>
    public bool HasTokenizer { get; init; }

    /// <summary> Fichiers abîmés trouvés. </summary>
    public List<string> BrokenFiles { get; init; } = new();

    /// <summary> Raison pour laquelle le modèle ne peut pas être lancé (null = utilisable). </summary>
    public string? Problem { get; set; }

    /// <summary> Point d'attention non bloquant (architecture non listée, tokenizer absent…). </summary>
    public string? Warning { get; init; }

    /// <summary> Date de modification. </summary>
    public DateTime Modified { get; init; }

    /// <summary> Le modèle ne peut pas être lancé. </summary>
    public bool IsBroken => Problem != null;

    /// <summary> VRAM de base estimée : poids + surcoût fixe (hors cache KV). </summary>
    public double BaseBytes => WeightsBytes * ArgsPlanner.WeightMargin + ArgsPlanner.OverheadBytes(WeightsBytes, VocabSize);
}

/// <summary>
/// Coût du cache KV par token : couches d'attention complète (grandit avec le contexte) et couches
/// à fenêtre glissante (plafonnées à la fenêtre : vLLM et llama.cpp ne gardent que les derniers tokens).
/// </summary>
public sealed record KvInfo(double FullPerToken, double SlidingPerToken = 0, int Window = 0)
{
    /// <summary> Le modèle utilise une fenêtre glissante sur une partie des couches. </summary>
    public bool HasSliding => SlidingPerToken > 0 && Window > 0;

    /// <summary> Octets du cache KV pour une séquence de cette longueur. </summary>
    public double Bytes(double tokens) => FullPerToken * tokens + SlidingPerToken * Math.Min(tokens, Window);

    /// <summary> Plus longue séquence qui tient dans ces octets. </summary>
    public int MaxTokens(double bytes)
    {
        if (bytes <= 0) return 0;
        var atWindow = Bytes(Window);
        double t;
        if (bytes <= atWindow) t = bytes / (FullPerToken + SlidingPerToken);
        else if (FullPerToken <= 0) return int.MaxValue;
        else t = Window + (bytes - atWindow) / FullPerToken;
        return (int)Math.Min(int.MaxValue, t);
    }

    /// <summary> Même cache avec un autre type (fp8 = 0,5) ou réparti sur plusieurs GPU (1 / tp). </summary>
    public KvInfo Scale(double f) => this with { FullPerToken = FullPerToken * f, SlidingPerToken = SlidingPerToken * f };
}

/// <summary> Adaptateur LoRA trouvé dans le dossier des modèles (adapter_config.json). </summary>
public sealed record LoraAdapterInfo(string Name, string Path, string? BaseModel, int Rank)
{
    /// <summary> L'adaptateur a-t-il été entraîné pour ce modèle (dépôt d'origine, nom ou chemin) ? </summary>
    public bool Fits(ModelInfo m)
    {
        if (string.IsNullOrWhiteSpace(BaseModel)) return false;
        var b = BaseModel.TrimEnd('/', '\\');
        var last = b.Split('/', '\\')[^1];
        return string.Equals(b, m.SourceRepo, StringComparison.OrdinalIgnoreCase)
               || string.Equals(b, m.Target.TrimEnd('/', '\\'), StringComparison.Ordinal)
               || string.Equals(b, LinuxHost.ToLinux(m.Target), StringComparison.Ordinal)
               || string.Equals(b, m.Repo, StringComparison.OrdinalIgnoreCase)
               || string.Equals(last, m.Name, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary> Fichier écrit par l'application à côté d'un modèle téléchargé. </summary>
public sealed class DownloadMetadata
{
    /// <summary> Dépôt d'origine. </summary>
    public string? Repo { get; set; }
    /// <summary> Modèle de base (tokenizer des GGUF). </summary>
    public string? BaseModel { get; set; }
    /// <summary> Date du téléchargement. </summary>
    public DateTime DownloadedAt { get; set; }
    /// <summary> Nom du fichier de métadonnées dans le dossier du modèle. </summary>
    public const string FileName = "." + AppPaths.Id + ".json";
}

/// <summary> Recherche des modèles et adaptateurs LoRA dans le dossier des modèles. </summary>
public interface IModelScanner
{
    /// <summary> Modèles du dossier (dossiers, cache Hugging Face, fichiers GGUF). </summary>
    List<ModelInfo> Scan(string modelsDir);

    /// <summary> Adaptateurs LoRA du dossier des modèles (un ou deux niveaux, et cache HF). </summary>
    List<LoraAdapterInfo> ScanAdapters(string modelsDir);
}

/// <summary> Implémentation de <see cref="IModelScanner"/>. </summary>
public sealed partial class ModelScanner : IModelScanner
{
    private readonly IDownloadStore _downloads;
    private readonly IModelCompat _compat;

    private readonly ILogger<ModelScanner> _logger;

    /// <summary> Crée le service. </summary>
    /// <param name="downloads"> Téléchargements en cours (dossiers à ignorer). </param>
    /// <param name="compat"> Compatibilité des modèles avec le vLLM installé. </param>
    /// <param name="logger"> Journal des erreurs. </param>
    public ModelScanner(IDownloadStore downloads, IModelCompat compat, ILogger<ModelScanner> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(downloads);
        ArgumentNullException.ThrowIfNull(compat);
        _downloads = downloads;
        _compat = compat;
    }

    /// <summary> Extensions des fichiers de poids. </summary>
    public static readonly HashSet<string> WeightExt = [".safetensors", ".bin", ".pt", ".pth", ".ckpt"];

    [GeneratedRegex("tts|omni|speech|diffusion|cosyvoice|voxcpm", RegexOptions.IgnoreCase)]
    private static partial Regex OmniPattern();

    /// <summary> Parties d'un GGUF découpé : nom-00001-of-00003.gguf </summary>
    [GeneratedRegex(@"^(.*)-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase)]
    private static partial Regex SplitGgufRegex();

    /// <summary> Quantification dans un nom de fichier GGUF (Q4_K_M, IQ3_XS, Q8_0, F16, BF16, MXFP4…). </summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9])(I?Q\d(?:_[A-Z0-9]+)*|BF16|F16|F32|MXFP4(?:_MOE)?|TQ\d_\d)(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex GgufQuantRegex();

    /// <summary> Quantification lisible d'après le nom d'un fichier GGUF (Q4_K_M…). </summary>
    public static string GgufQuantLabel(string fileName) =>
        GgufQuantRegex().Matches(Path.GetFileNameWithoutExtension(fileName)) is { Count: > 0 } m
            ? m[^1].Value.ToUpperInvariant() : Path.GetFileNameWithoutExtension(fileName);

    /// <summary>
    /// Nom court de chaque variante : la partie du nom de fichier qui les distingue
    /// (LFM2.5-2.6B-Q4_0.gguf / LFM2.5-2.6B-QAD-Q4_0.gguf -> "Q4_0" / "QAD-Q4_0").
    /// </summary>
    public static Dictionary<string, string> VariantLabels(IReadOnlyList<string> files)
    {
        var names = files.Select(f => Path.GetFileNameWithoutExtension(SplitGgufRegex().Match(Path.GetFileName(f)) is { Success: true } m
            ? m.Groups[1].Value + ".gguf" : f)).ToList();
        var prefix = names.Count > 1 ? names.Aggregate((a, b) => new string(a.Zip(b).TakeWhile(p => p.First == p.Second).Select(p => p.First).ToArray())) : "";
        // On coupe sur un séparateur pour ne pas tronquer au milieu d'un mot
        var cut = prefix.LastIndexOfAny(['-', '_', '.']) + 1;
        var res = new Dictionary<string, string>();
        for (int i = 0; i < files.Count; i++)
        {
            var label = names.Count > 1 ? names[i][cut..].Trim('-', '_', '.') : "";
            res[files[i]] = label.Length > 0 ? label : GgufQuantLabel(files[i]);
        }
        return res;
    }

    /// <summary> Projecteurs vision (mmproj) : ce ne sont pas des modèles à lancer. </summary>
    public static bool IsAuxiliaryGguf(string fileName) =>
        Path.GetFileName(fileName).Contains("mmproj", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public List<ModelInfo> Scan(string modelsDir)
    {
        var res = new List<ModelInfo>();
        if (!Directory.Exists(modelsDir)) return res;

        // Dossiers dont le téléchargement lancé par l'application n'est pas terminé
        var pending = _downloads.Load().Select(d => Path.GetFullPath(d.LocalDir)).ToList();
        foreach (var (root, repo, target, layout) in Candidates(modelsDir))
        {
            try
            {
                var found = Inspect(root, repo, target, layout).ToList();
                // Téléchargement interrompu : fichiers partiels de hf, poids encore absents
                if (HfService.PartialBytes(root) > 0 || pending.Any(p => Path.GetFullPath(root).StartsWith(p)))
                    foreach (var m in found) m.Problem ??= Loc.T("Model_Incomplete");
                res.AddRange(found);
            }
            catch (Exception ex) { _logger.LogWarning("[Scan] {Root} : {Message}", root, ex.Message); }
        }
        return res;
    }

    /// <inheritdoc />
    public List<LoraAdapterInfo> ScanAdapters(string modelsDir)
    {
        var res = new List<LoraAdapterInfo>();
        if (!Directory.Exists(modelsDir)) return res;
        IEnumerable<(string Dir, string Name)> Dirs()
        {
            foreach (var d in Directory.GetDirectories(modelsDir))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith('.')) continue;
                if (name.StartsWith("models--") && SnapshotDir(d) is { } snap) { yield return (snap, name["models--".Length..].Split("--")[^1]); continue; }
                yield return (d, name);
                foreach (var sub in Directory.GetDirectories(d).Where(x => !Path.GetFileName(x).StartsWith('.')))
                    yield return (sub, Path.GetFileName(sub));
            }
        }
        foreach (var (dir, name) in Dirs())
        {
            var cfgPath = Path.Combine(dir, "adapter_config.json");
            if (!File.Exists(cfgPath)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
                var root = doc.RootElement;
                var baseModel = root.TryGetProperty("base_model_name_or_path", out var b) ? b.GetString() : null;
                var rank = root.TryGetProperty("r", out var r) && r.TryGetInt32(out var ri) ? ri : 16;
                // Nom exposé par l'API : sans caractères qui gêneraient --lora-modules nom=chemin
                var clean = Regex.Replace(name, @"[^A-Za-z0-9._\-]", "-");
                res.Add(new LoraAdapterInfo(clean, dir, baseModel, rank));
            }
            catch { /* adaptateur illisible : ignoré */ }
        }
        return res;
    }

    private bool IsModelDir(string d) =>
        File.Exists(Path.Combine(d, "config.json")) || File.Exists(Path.Combine(d, "model_index.json")) || HasGguf(d);

    private IEnumerable<(string Root, string Repo, string Target, string Layout)> Candidates(string dir)
    {
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => Path.GetFileName(x), StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(d);
            if (name.StartsWith('.')) continue;

            if (name.StartsWith("models--"))
            {
                var snap = SnapshotDir(d);
                if (snap != null)
                {
                    var repo = name["models--".Length..].Replace("--", "/");
                    yield return (snap, repo, repo, "cache");
                }
            }
            else if (IsModelDir(d))
            {
                yield return (d, name, d, "folder");
                // Dépôt GGUF sans config.json : un sous-dossier peut contenir un autre modèle complet
                // (pas pour un pipeline diffusers : ses sous-dossiers sont les composants d'un seul modèle)
                if (!File.Exists(Path.Combine(d, "config.json")) && !File.Exists(Path.Combine(d, "model_index.json")))
                    foreach (var sub in SubModels(d))
                        yield return (sub, $"{name}/{Path.GetFileName(sub)}", sub, "folder");
            }
            // Poids sans configuration (fichier unique pour ComfyUI…) : listé avec une explication
            else if (Directory.EnumerateFiles(d).Any(f => WeightExt.Contains(Path.GetExtension(f).ToLowerInvariant())))
                yield return (d, name, d, "folder");
            else
            {
                foreach (var sub in Directory.GetDirectories(d).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var sn = Path.GetFileName(sub);
                    if (!sn.StartsWith('.') && IsModelDir(sub))
                        yield return (sub, $"{name}/{sn}", sub, "folder");
                }
            }
        }
    }

    private IEnumerable<string> SubModels(string d) =>
        Directory.GetDirectories(d)
            .Where(s => !Path.GetFileName(s).StartsWith('.') && File.Exists(Path.Combine(s, "config.json")) &&
                        Directory.EnumerateFiles(s, "*.safetensors").Any())
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

    private bool HasGguf(string d) => Directory.EnumerateFiles(d, "*.gguf").Any(f => !IsAuxiliaryGguf(f));

    private string? SnapshotDir(string repoDir)
    {
        var snaps = Path.Combine(repoDir, "snapshots");
        var refMain = Path.Combine(repoDir, "refs", "main");
        if (File.Exists(refMain))
        {
            var s = Path.Combine(snaps, File.ReadAllText(refMain).Trim());
            if (Directory.Exists(s)) return s;
        }
        if (!Directory.Exists(snaps)) return null;
        return Directory.GetDirectories(snaps).OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary> Taille d'un fichier en suivant les liens ; null si le lien est cassé. </summary>
    private long? FileSize(string f)
    {
        var fi = new FileInfo(f);
        if (fi.LinkTarget == null) return fi.Length;
        return fi.ResolveLinkTarget(true) is FileInfo { Exists: true } rf ? rf.Length : null;
    }

    private DownloadMetadata? ReadDownloadMetadata(string root)
    {
        try
        {
            var p = Path.Combine(root, DownloadMetadata.FileName);
            return File.Exists(p) ? JsonSerializer.Deserialize(File.ReadAllText(p), AppJsonContext.Default.DownloadMetadata) : null;
        }
        catch { return null; }
    }

    private bool HasTokenizerFiles(string root) =>
        File.Exists(Path.Combine(root, "tokenizer.json")) || File.Exists(Path.Combine(root, "tokenizer.model")) ||
        File.Exists(Path.Combine(root, "tokenizer_config.json"));

    private IEnumerable<ModelInfo> Inspect(string root, string repo, string target, string layout)
    {
        var meta = ReadDownloadMetadata(root);
        var ggufs = Directory.GetFiles(root, "*.gguf").Where(f => !IsAuxiliaryGguf(f)).OrderBy(f => f).ToList();
        var hasConfig = File.Exists(Path.Combine(root, "config.json"));

        if (ggufs.Count > 0)
            foreach (var m in InspectGguf(root, repo, ggufs, meta))
                yield return m;

        // Poids classiques (safetensors…) : un modèle "dossier" en plus des éventuelles variantes GGUF
        if (hasConfig && InspectStandard(root, repo, target, layout, meta) is { } std)
            yield return std;
        // Modèle de diffusion (format diffusers) : génération d'images / vidéo / audio avec vLLM-Omni
        else if (!hasConfig && File.Exists(Path.Combine(root, "model_index.json")) && InspectDiffusers(root, repo, target, layout, meta) is { } diff)
            yield return diff;
        // Poids sans configuration (fichier unique pour ComfyUI…) : affiché avec l'explication plutôt que masqué,
        // et proposé à la conversion au format diffusers quand c'est un modèle d'image connu
        else if (!hasConfig && ggufs.Count == 0 && !File.Exists(Path.Combine(root, "model_index.json")) &&
                 SelectWeightFiles(Directory.GetFiles(root), root) is { Count: > 0 } loose)
        {
            var single = loose.OrderByDescending(f => FileSize(f) ?? 0).First();
            var kind = single.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ? SingleFileConverter.Detect(single) : null;
            yield return new ModelInfo
            {
                Repo = repo, Target = target, Root = root, Layout = layout,
                Name = repo.Split('/')[^1],
                Architecture = "?",
                ModelType = "?",
                Quantization = "?",
                WeightsBytes = loose.Sum(f => FileSize(f) ?? 0),
                SourceRepo = meta?.Repo,
                BaseModel = meta?.BaseModel,
                // Complet (SD, SDXL) : lancé par stable-diffusion.cpp s'il est installé ; transformer seul (FLUX, Z-Image) : à convertir
                Problem = SingleFileConverter.SdCppRunnable(kind) || SingleFileConverter.SdCppNeedsComponents(kind)
                        ? (StableDiffusionCppService.Installed ? null : Loc.T(kind == "SD" ? "Compat_SingleFileSd" : "Compat_SingleFileSdCpp"))
                        : SingleFileConverter.CanConvert(kind) ? Loc.F("Compat_SingleFileConvert", kind!) : Loc.T("Compat_SingleFile"),
                SingleFile = single,
                SingleFileKind = kind,
                Modified = Directory.GetLastWriteTime(root),
            };
        }
    }

    /// <summary> Fichiers d'entraînement sans rapport avec l'inférence. </summary>
    private static bool IsTrainingFile(string name) =>
        name is "training_args.bin" or "optimizer.pt" or "scheduler.pt" or "optimizer.bin" || name.StartsWith("rng_state");

    /// <summary> Poids au format Mistral (consolidated*.safetensors), en double des poids HF dans les dépôts mistralai. </summary>
    [GeneratedRegex(@"^(.+)\.(fp16|bf16|fp32|non_ema|ema_only)\.safetensors$", RegexOptions.IgnoreCase)]
    private static partial Regex VariantRegex();

    /// <summary>
    /// Variante diffusers (nom.fp16.safetensors) d'un fichier présent aussi sans variante (nom.safetensors) :
    /// le chargeur prend la version sans variante, l'autre est un doublon.
    /// </summary>
    public static bool IsWeightVariant(string file, IEnumerable<string> siblings)
    {
        if (VariantRegex().Match(Path.GetFileName(file)) is not { Success: true } m) return false;
        var plain = m.Groups[1].Value + ".safetensors";
        return siblings.Any(f => string.Equals(Path.GetFileName(f), plain, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsConsolidated(string f) => Path.GetFileName(f).StartsWith("consolidated", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fichiers de poids que vLLM charge, à la racine du modèle : les safetensors (ceux de l'index s'il existe,
    /// sinon tous sauf consolidated* quand d'autres existent) ; .bin / .pt seulement s'il n'y a aucun safetensors.
    /// </summary>
    public static List<string> SelectWeightFiles(IReadOnlyList<string> topLevelFiles, string root)
    {
        var weights = topLevelFiles.Where(f => WeightExt.Contains(Path.GetExtension(f).ToLowerInvariant()) &&
                                               !Path.GetFileName(f).StartsWith('.') && !IsTrainingFile(Path.GetFileName(f))).ToList();
        var st = weights.Where(f => f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).ToList();
        st = st.Where(f => !IsWeightVariant(f, st)).ToList();
        var pool = st.Count > 0 ? st : weights;
        var index = SafetensorsReader.IndexedFiles(Path.Combine(root, st.Count > 0 ? "model.safetensors.index.json" : "pytorch_model.bin.index.json"));
        if (index is { Count: > 0 } && pool.Where(f => index.Contains(Path.GetFileName(f))).ToList() is { Count: > 0 } listed)
            return listed;
        if (pool.Count > 1 && pool.Any(f => !IsConsolidated(f)))
            return pool.Where(f => !IsConsolidated(f)).ToList();
        return pool;
    }

    private ModelInfo? InspectStandard(string root, string repo, string target, string layout, DownloadMetadata? meta)
    {
        var broken = new List<string>();
        long weights = 0, subWeights = 0;
        int count = 0;

        // Poids principaux : à la racine, sans les doublons
        var top = Directory.GetFiles(root);
        var chosen = SelectWeightFiles(top, root);
        foreach (var f in chosen)
        {
            if (FileSize(f) is { } size) { weights += size; count++; }
            else broken.Add(Path.GetFileName(f));
        }

        foreach (var f in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
        {
            var rel = Path.GetRelativePath(root, f);
            var sep = Path.DirectorySeparatorChar;   // \ sous Windows
            if (!rel.Contains(sep) || rel.Split(sep).Any(p => p.StartsWith('.'))) continue;
            if (!WeightExt.Contains(Path.GetExtension(f).ToLowerInvariant())) continue;
            // Sous-modèle avec sa propre config (ex. décodeur audio speech_tokenizer/) : compté à part.
            // vLLM-Omni le charge avec le modèle principal, il compte donc dans sa VRAM.
            if (File.Exists(Path.Combine(root, rel.Split(sep)[0], "config.json")))
            {
                if (FileSize(f) is { } sub) subWeights += sub;
            }
            // Poids seulement dans des sous-dossiers (ancienne disposition) : comptés comme avant
            else if (chosen.Count == 0)
            {
                if (FileSize(f) is { } size) { weights += size; count++; }
                else broken.Add(Path.GetFileName(f));
            }
        }
        if (count == 0 && broken.Count == 0) return null;

        JsonElement cfg = default;
        try { cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config.json"))).RootElement; } catch { /* JSON illisible : valeur ignorée */ }

        string? modelType = cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("model_type", out var mt) ? mt.ToString() : null;
        var archs = ArchitecturesOf(cfg);

        var dtype = FindKey(cfg, "torch_dtype")?.ToString() ?? FindKey(cfg, "dtype")?.ToString() ?? "?";
        var quant = DetectQuant(cfg);
        var quantMethod = QuantMethodOf(cfg);
        // MLX (format Apple) : quantification sans quant_method
        if (ModelCompat.IsMlx(root, cfg))
        {
            var bits = cfg.TryGetProperty("quantization", out var mq) && mq.ValueKind == JsonValueKind.Object && mq.TryGetProperty("bits", out var mb) ? $" {mb} bits" : "";
            quant = "MLX" + bits;
            quantMethod = null;
        }
        var hasTokenizer = HasTokenizerFiles(root);
        var compat = broken.Count > 0 ? new CompatResult(null, null) : _compat.Check(root, cfg, archs, quantMethod, hasTokenizer);

        var text = TextConfig(cfg);
        int? heads = FindInt(text, "num_attention_heads");
        var kv = KvFromConfig(cfg);

        var hay = string.Join(' ', new[] { repo, modelType ?? "" }.Concat(archs));
        var isOmni = OmniPattern().IsMatch(hay);
        // Multimodal : encodeur vision / audio dans la config (les modèles de synthèse vocale passent par vLLM-Omni)
        var isMm = !isOmni && cfg.ValueKind == JsonValueKind.Object &&
                   (cfg.TryGetProperty("vision_config", out _) || cfg.TryGetProperty("audio_config", out _) ||
                    cfg.TryGetProperty("visual", out _) || cfg.TryGetProperty("vision_tower", out _));
        var mmBytes = isMm ? SafetensorsReader.MultimodalBytes(chosen) : 0;
        var mtp = FindInt(cfg, "num_nextn_predict_layers") ?? FindInt(cfg, "mtp_num_hidden_layers") ?? FindInt(cfg, "num_mtp_layers");

        return new ModelInfo
        {
            Repo = repo, Target = target, Root = root, Layout = layout,
            Name = repo.Split('/')[^1],
            Architecture = (archs.FirstOrDefault() ?? "?") + (modelType != null ? $" ({modelType})" : ""),
            ModelType = modelType,
            Quantization = quant ?? Loc.F("Model_NoQuant", dtype),
            TorchDtype = dtype,
            IsQuantized = quant != null,
            WeightsBytes = weights,
            Kv = kv,
            IsMultimodal = isMm,
            MultimodalBytes = mmBytes,
            MtpLayers = mtp is > 0 ? mtp : null,
            VocabSize = FindInt(text, "vocab_size"),
            SubModelBytes = subWeights,
            MaxPositions = FindInt(text, "max_position_embeddings"),
            AttentionHeads = heads,
            IsOmni = isOmni,
            SourceRepo = meta?.Repo,
            BaseModel = meta?.BaseModel,
            HasTokenizer = hasTokenizer,
            BrokenFiles = broken,
            Problem = broken.Count > 0 ? Loc.F("Model_BrokenFiles", string.Join(", ", broken)) : compat.Problem,
            Warning = compat.Warning,
            Modified = Directory.GetLastWriteTime(root),
        };
    }

    /// <summary>
    /// Pipeline diffusers : model_index.json à la racine, un sous-dossier par composant (transformer, vae, text_encoder…).
    /// Tous les composants sont chargés en VRAM par vLLM-Omni.
    /// </summary>
    private ModelInfo? InspectDiffusers(string root, string repo, string target, string layout, DownloadMetadata? meta)
    {
        string? cls = null;
        // Composants du pipeline : clés de model_index.json de la forme "unet": ["diffusers", "UNet2DConditionModel"]
        // (les autres sous-dossiers, comme vae_1_0/ dans SDXL, ne sont pas chargés)
        var components = new HashSet<string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "model_index.json")));
            cls = doc.RootElement.TryGetProperty("_class_name", out var c) ? c.GetString() : null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Array) components.Add(p.Name);
        }
        catch { /* model_index.json illisible : composants inconnus */ }

        long weights = 0, onDisk = 0, textEncoders = 0;
        var broken = new List<string>();
        foreach (var sub in Directory.GetDirectories(root).Where(d => !Path.GetFileName(d).StartsWith('.')))
        {
            if (components.Count > 0 && !components.Contains(Path.GetFileName(sub))) continue;
            foreach (var f in SelectWeightFiles(Directory.GetFiles(sub), sub))
            {
                if (FileSize(f) is { } size)
                {
                    onDisk += size;
                    var bytes = f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) && SafetensorsReader.Bytes16(f) is { } loaded ? loaded : size;
                    weights += bytes;
                    if (Path.GetFileName(sub).StartsWith("text_encoder")) textEncoders += bytes;
                }
                else broken.Add(Path.GetFileName(f));
            }
        }
        if (weights == 0 && broken.Count == 0) return null;

        // Fichier unique d'origine resté à la racine (modèle converti par « Convertir pour vLLM »)
        var original = Directory.GetFiles(root).Where(f => f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
                                .OrderByDescending(f => FileSize(f) ?? 0).FirstOrDefault();
        var originalKind = original != null ? SingleFileConverter.Detect(original) : null;
        return new ModelInfo
        {
            Repo = repo, Target = target, Root = root, Layout = layout,
            Name = repo.Split('/')[^1],
            Architecture = (cls ?? "Diffusers") + " (diffusers)",
            ModelType = "diffusers",
            Quantization = Loc.F("Model_NoQuant", "?"),
            WeightsBytes = weights,
            FileBytes = onDisk,
            TextEncoderBytes = textEncoders,
            OriginalFile = originalKind != null ? original : null,
            OriginalKind = originalKind,
            IsOmni = true,
            SourceRepo = meta?.Repo,
            BaseModel = meta?.BaseModel,
            HasTokenizer = true,
            BrokenFiles = broken,
            Problem = broken.Count > 0 ? Loc.F("Model_BrokenFiles", string.Join(", ", broken)) : null,
            Modified = Directory.GetLastWriteTime(root),
        };
    }

    private IEnumerable<ModelInfo> InspectGguf(string root, string repo, List<string> files, DownloadMetadata? meta)
    {
        // Regroupe les parties d'un même GGUF découpé
        var groups = files
            .GroupBy(f => SplitGgufRegex().Match(Path.GetFileName(f)) is { Success: true } m ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(f))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
        var hasTokenizer = HasTokenizerFiles(root);
        var single = files.Count == 1;
        var labels = VariantLabels(groups.Select(g => g.OrderBy(f => f).First()).ToList());

        foreach (var g in groups)
        {
            var parts = g.OrderBy(f => f).ToList();
            var first = parts[0];
            long size = 0;
            var broken = new List<string>();
            foreach (var p in parts)
            {
                if (FileSize(p) is { } s) size += s;
                else broken.Add(Path.GetFileName(p));
            }

            var md = broken.Count == 0 ? GgufReader.ReadMetadata(first) : null;
            var arch = md?.GetValueOrDefault("general.architecture") as string;
            long? Int(string key) => arch != null && md!.GetValueOrDefault($"{arch}.{key}") is long v ? v : null;

            var layers = Int("block_count");
            var heads = Int("attention.head_count");
            var kv = arch != null ? KvFromGguf(md!, arch) : null;

            var quant = labels[first];
            string? problem = broken.Count > 0 ? Loc.F("Model_BrokenFiles", string.Join(", ", broken))
                : null;
            var label = single ? repo : $"{repo} · {quant}";

            yield return new ModelInfo
            {
                Repo = label, Target = first, Root = root, Layout = "gguf",
                Name = Path.GetFileNameWithoutExtension(first),
                Architecture = "GGUF" + (arch != null ? $" ({arch})" : ""),
                ModelType = arch,
                Quantization = $"GGUF {GgufQuantLabel(first)}",
                IsQuantized = true,
                IsGguf = true,
                WeightsBytes = size,
                Kv = kv,
                NumLayers = (int?)layers,
                MaxPositions = (int?)Int("context_length"),
                AttentionHeads = (int?)heads,
                IsOmni = OmniPattern().IsMatch(repo + " " + arch),
                SourceRepo = meta?.Repo,
                BaseModel = meta?.BaseModel,
                HasTokenizer = hasTokenizer,
                BrokenFiles = broken,
                Problem = problem,
                Warning = parts.Count > 1 ? Loc.T("Model_SplitGguf") : null,   // llama.cpp charge les parties suivantes lui-même
                Modified = File.GetLastWriteTime(first),
            };
        }
    }

    /// <summary>
    /// Cache KV d'après config.json. Par couche : attention complète, fenêtre glissante, ou pas de cache qui grandit
    /// (couches linéaires / Mamba des modèles hybrides). Les couches qui partagent le cache d'une autre
    /// (num_kv_shared_layers) n'en ont pas. Gemma 4 : têtes et dimension propres aux couches complètes.
    /// La liste layer_types n'est utilisée que si elle décrit bien ce modèle (même nombre de couches) : une config
    /// imbriquée (ex. prédicteur de 5 couches dans Qwen3-TTS) ne doit pas remplacer les 28 couches du modèle.
    /// </summary>
    public static KvInfo? KvFromConfig(JsonElement cfg)
    {
        cfg = TextConfig(cfg);
        int? layers  = FindInt(cfg, "num_hidden_layers");
        int? heads   = FindInt(cfg, "num_attention_heads");
        int? kvHeads = FindInt(cfg, "num_key_value_heads") ?? heads;
        int? headDim = FindInt(cfg, "head_dim");
        if (headDim == null && FindInt(cfg, "hidden_size") is { } hid && heads is > 0)
            headDim = hid / heads.Value;
        if (layers is not { } n || kvHeads is not { } k || headDim is not { } h) return null;

        var globalK = FindInt(cfg, "num_global_key_value_heads") ?? k;
        var globalH = FindInt(cfg, "global_head_dim") ?? h;
        var window = FindInt(cfg, "sliding_window") ?? 0;
        var shared = Math.Clamp(FindInt(cfg, "num_kv_shared_layers") ?? 0, 0, n);
        var owned = n - shared;

        // Type de chaque couche : liste explicite, sinon motif (Gemma 3 : sliding_window_pattern = 6 -> 5 glissantes pour 1 complète)
        string[]? types = null;
        if (FindKey(cfg, "layer_types") is { ValueKind: JsonValueKind.Array } lt && lt.GetArrayLength() == n)
            types = lt.EnumerateArray().Select(x => x.ToString()).ToArray();
        else if (window > 0 && FindInt(cfg, "sliding_window_pattern") is { } pattern && pattern > 1)
            types = Enumerable.Range(0, n).Select(i => (i + 1) % pattern == 0 ? "full_attention" : "sliding_attention").ToArray();

        if (types == null) return new KvInfo(2.0 * owned * k * h * 2);

        // vLLM range toutes les couches dans des pages de même taille (gestionnaire de cache hybride) :
        // chaque couche coûte autant que la plus grosse. Gemma 4 : couches complètes 2 Kio/token, glissantes 8 Kio
        // -> 8 Kio pour toutes (mesuré : 2,34 Gio pour 32 768 tokens, comme annoncé par vLLM).
        var fullSize = 2.0 * globalK * globalH * 2;
        var slidingSize = 2.0 * k * h * 2;
        int nFull = 0, nSliding = 0;
        for (int i = 0; i < owned; i++)
        {
            switch (types[i])
            {
                case "full_attention" or "attention" or "global_attention": nFull++; break;
                case "sliding_attention" or "local_attention" or "chunked_attention":
                    if (window > 0) nSliding++; else nFull++;
                    break;
                // linear_attention, mamba, conv… : état de taille fixe, pas de cache par token
            }
        }
        if (nFull == 0 && nSliding == 0) return new KvInfo(2.0 * owned * k * h * 2);   // types inconnus : prudence
        if (nSliding > 0 && nFull > 0) fullSize = slidingSize = Math.Max(fullSize, slidingSize);
        // Couches glissantes : la fenêtre plus quelques blocs en cours de remplissage
        return new KvInfo(nFull * fullSize, nSliding * slidingSize, nSliding > 0 ? window + SlidingSlack : 0);
    }

    /// <summary> Tokens gardés en plus de la fenêtre glissante (blocs partiels ; mesuré ≈ 90 sur Gemma 4). </summary>
    private const int SlidingSlack = 128;

    /// <summary> Cache KV d'après les métadonnées GGUF (têtes par couche, couches glissantes de llama.cpp). </summary>
    private KvInfo? KvFromGguf(Dictionary<string, object> md, string arch)
    {
        long? Int(string key) => md.GetValueOrDefault($"{arch}.{key}") is long v ? v : null;
        var layers = Int("block_count");
        var heads = Int("attention.head_count");
        var emb = Int("embedding_length");
        var headDim = Int("attention.key_length") ?? (emb is { } e && heads is > 0 ? e / heads : null);
        var headDimSwa = Int("attention.key_length_swa") ?? headDim;
        if (headDim is not { } hd || layers is not { } n) return null;

        var kvRaw = md.GetValueOrDefault($"{arch}.attention.head_count_kv");
        long KvHeads(int i) => kvRaw is long[] per && i < per.Length ? per[i] : (kvRaw as long?) ?? heads ?? 0;
        var window = Int("attention.sliding_window") ?? 0;
        var pattern = md.GetValueOrDefault($"{arch}.attention.sliding_window_pattern") as long[];
        var shared = Int("attention.shared_kv_layers") ?? 0;

        double full = 0, sliding = 0;
        for (int i = 0; i < n - shared; i++)
        {
            var isSwa = window > 0 && pattern != null && i < pattern.Length && pattern[i] != 0;
            if (isSwa) sliding += 2.0 * KvHeads(i) * (headDimSwa ?? hd) * 2;
            else full += 2.0 * KvHeads(i) * hd * 2;
        }
        if (full == 0 && sliding == 0) return null;
        return new KvInfo(full, sliding, sliding > 0 ? (int)window : 0);
    }

    /// <summary>
    /// Partie « modèle de langage » d'une config multimodale (text_config…) : sinon la recherche en profondeur
    /// pourrait trouver le nombre de couches de l'encodeur audio ou vision, rangé avant dans le fichier.
    /// </summary>
    /// <summary> Architectures déclarées dans config.json (vide si absentes). </summary>
    public static List<string> ArchitecturesOf(JsonElement cfg) =>
        cfg.ValueKind == JsonValueKind.Object && cfg.TryGetProperty("architectures", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Select(x => x.ToString()).ToList() : new List<string>();

    /// <summary> quant_method de quantization_config (null si absent). </summary>
    public static string? QuantMethodOf(JsonElement cfg) =>
        FindKey(cfg, "quantization_config") is { ValueKind: JsonValueKind.Object } qc &&
        qc.TryGetProperty("quant_method", out var qm) ? qm.ToString() : null;

    internal static JsonElement TextConfig(JsonElement cfg)
    {
        if (cfg.ValueKind != JsonValueKind.Object) return cfg;
        foreach (var key in new[] { "text_config", "llm_config", "language_config", "thinker_config" })
            if (cfg.TryGetProperty(key, out var t) && t.ValueKind == JsonValueKind.Object && FindInt(t, "num_hidden_layers") != null)
                return t;
        return cfg;
    }

    private string? DetectQuant(JsonElement cfg)
    {
        if (FindKey(cfg, "quantization_config") is not { ValueKind: JsonValueKind.Object } q) return null;
        var method = q.TryGetProperty("quant_method", out var m) ? m.ToString() : "?";
        if (method == "compressed-tensors" && q.TryGetProperty("config_groups", out var groups) && groups.ValueKind == JsonValueKind.Object)
        {
            foreach (var g in groups.EnumerateObject())
            {
                if (!g.Value.TryGetProperty("weights", out var w) || w.ValueKind != JsonValueKind.Object) continue;
                if (!w.TryGetProperty("num_bits", out var wb)) continue;
                var ab = g.Value.TryGetProperty("input_activations", out var ia) && ia.ValueKind == JsonValueKind.Object &&
                         ia.TryGetProperty("num_bits", out var abv) ? abv.ToString() : "16";
                var kind = w.TryGetProperty("type", out var t) && t.ToString() == "float" ? "float" : "int";
                return $"compressed-tensors W{wb}A{ab} ({kind}{wb})";
            }
        }
        var bits = q.TryGetProperty("bits", out var b) ? b.ToString() : q.TryGetProperty("w_bit", out var wb2) ? wb2.ToString() : null;
        return bits != null ? $"{method} {bits} bits" : method;
    }

    private static JsonElement? FindKey(JsonElement obj, string key, int depth = 3)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        if (obj.TryGetProperty(key, out var v) && v.ValueKind != JsonValueKind.Null) return v;
        if (depth == 0) return null;
        foreach (var p in obj.EnumerateObject())
            if (FindKey(p.Value, key, depth - 1) is { } r) return r;
        return null;
    }

    internal static int? FindInt(JsonElement obj, string key) =>
        FindKey(obj, key) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt32(out var i) ? i : null;

    /// <summary> Taille totale d'un dossier (liens suivis), 0 si absent. </summary>
    public static long DirectorySize(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                try { total += new FileInfo(f).Length; } catch { /* fichier disparu entre-temps */ }
            }
        }
        catch { /* dossier illisible : taille partielle */ }
        return total;
    }
}
