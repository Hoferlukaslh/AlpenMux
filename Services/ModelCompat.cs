/*
    Fichier      :  ModelCompat.cs
    Projet       :  AlpenMux

    Description  :
        Vérifie avant le lancement qu'un modèle peut être chargé par la version de vLLM installée :
            - formats d'autres moteurs (MLX pour Apple Silicon, ExLlama EXL2, ONNX seul…) : bloquant
            - méthode de quantification absente de vLLM : bloquant
            - bitsandbytes sans le paquet bitsandbytes : bloquant
            - architecture absente du registre de vLLM : avertissement (backend Transformers possible)
            - tokenizer absent : avertissement
        Corrige aussi automatiquement des incompatibilités connues entre vLLM et certains modèles
        (KnownHfOverrides : valeurs ajoutées à la config du modèle via --hf-overrides).
        La liste des architectures et des quantifications est lue dans le vLLM installé
        (model_executor/models/registry.py, layers/quantization/__init__.py) : elle suit toujours la version réelle.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlpenMux.Services;

/// <summary> Résultat de l'analyse de compatibilité. </summary>
/// <param name="Problem"> Problème bloquant, ou null. </param>
/// <param name="Warning"> Avertissement, ou null. </param>
public sealed record CompatResult(string? Problem, string? Warning);

/// <summary> Compatibilité d'un modèle avec le vLLM installé et correctifs connus à appliquer. </summary>
public interface IModelCompat
{
    /// <summary> Oublie les informations lues dans le vLLM installé. </summary>
    void Invalidate();

    /// <summary>
    /// Valeurs à ajouter à la config du modèle (JSON de --hf-overrides), ou null.
    ///   - Gemma 4 « unified » : le code de vLLM lit vision_config.num_soft_tokens, que la config de Google
    ///     ne contient pas → AttributeError au démarrage.
    ///     Appliqué seulement si le vLLM installé lit ce champ et que la config ne le fournit pas.
    /// </summary>
    string? KnownHfOverrides(ModelInfo m);

    /// <summary> Analyse d'un modèle "dossier" (config.json + poids). </summary>
    CompatResult Check(string root, JsonElement cfg, IReadOnlyList<string> archs, string? quantMethod, bool hasTokenizer);

    /// <summary> Même analyse, avant téléchargement : d'après les chemins des fichiers du dépôt (sans dossier local). </summary>
    /// <param name="paths"> Chemins relatifs des fichiers du dépôt. </param>
    /// <param name="cfg"> config.json distant. </param>
    /// <param name="archs"> Architectures déclarées. </param>
    /// <param name="quantMethod"> quant_method de la config (null si absent). </param>
    /// <param name="hasTokenizer"> Vrai si un tokenizer est présent. </param>
    CompatResult CheckRemote(IReadOnlyList<string> paths, JsonElement cfg, IReadOnlyList<string> archs, string? quantMethod, bool hasTokenizer);
}

/// <summary> Implémentation de <see cref="IModelCompat"/>. </summary>
public sealed partial class ModelCompat : IModelCompat
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv et versions installées. </param>
    public ModelCompat(ISettingsService settings, IPythonEnvironment python)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        _settings = settings;
        _python = python;
    }
    private sealed record Support(string Version, HashSet<string> Architectures, HashSet<string> Quantizations);
    private Support? _cache;

    [GeneratedRegex("\"([A-Za-z0-9_]+)\"\\s*:\\s*\\(")]
    private static partial Regex RegistryEntry();

    [GeneratedRegex("QuantizationMethods\\s*=\\s*Literal\\[(.*?)\\]", RegexOptions.Singleline)]
    private static partial Regex QuantLiteral();

    [GeneratedRegex("\"([A-Za-z0-9_\\-]+)\"")]
    private static partial Regex Quoted();

    /// <summary> Capacités du vLLM installé (null si vLLM absent ou illisible). </summary>
    private Support? LoadSupport()
    {
        // vLLM-Omni ajoute ses propres architectures (TTS, omni…) : elles comptent aussi
        var version = _python.VllmVersion is { } vv ? vv + "|" + _python.OmniVersion : null;
        if (version == null) return null;
        if (_cache?.Version == version) return _cache;
        try
        {
            var lib = LinuxHost.Local(LinuxHost.Combine(_settings.VenvPath, "lib"));   // \\wsl.localhost\… sous Windows
            var vllm = Directory.GetDirectories(lib, "python*").Select(p => Path.Combine(p, "site-packages", "vllm")).FirstOrDefault(Directory.Exists);
            if (vllm == null) return null;

            var archs = new HashSet<string>();
            var omni = Path.Combine(Path.GetDirectoryName(vllm)!, "vllm_omni");
            foreach (var reg in new[] { Path.Combine(vllm, "model_executor", "models", "registry.py"),
                                        Path.Combine(omni, "model_executor", "models", "registry.py") })
                if (File.Exists(reg))
                    foreach (Match m in RegistryEntry().Matches(File.ReadAllText(reg))) archs.Add(m.Groups[1].Value);

            var quants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var q = Path.Combine(vllm, "model_executor", "layers", "quantization", "__init__.py");
            if (File.Exists(q) && QuantLiteral().Match(File.ReadAllText(q)) is { Success: true } lit)
                foreach (Match m in Quoted().Matches(lit.Groups[1].Value)) quants.Add(m.Groups[1].Value);

            return _cache = new Support(version, archs, quants);
        }
        catch { return null; }
    }

    /// <inheritdoc />
    public void Invalidate() => _cache = null;

    // ---------------------------------------------------------------- Correctifs connus

    /// <summary> Images de Gemma 4 : nombre de "soft tokens" par image par défaut (processor_config.json de Google). </summary>
    private const int Gemma4SoftTokens = 280;

    private string? VllmFile(params string[] parts)
    {
        try
        {
            var lib = LinuxHost.Local(LinuxHost.Combine(_settings.VenvPath, "lib"));   // \\wsl.localhost\… sous Windows
            var vllm = Directory.GetDirectories(lib, "python*").Select(p => Path.Combine(p, "site-packages", "vllm")).FirstOrDefault(Directory.Exists);
            var f = vllm == null ? null : Path.Combine([vllm, .. parts]);
            return f != null && File.Exists(f) ? f : null;
        }
        catch { return null; }
    }

    /// <inheritdoc />
    public string? KnownHfOverrides(ModelInfo m)
    {
        var isGemma4 = (m.ModelType ?? "").StartsWith("gemma4", StringComparison.OrdinalIgnoreCase)
                       || m.Architecture.Contains("Gemma4", StringComparison.OrdinalIgnoreCase);
        if (!isGemma4 || m.IsGguf) return null;   // GGUF : servi par llama.cpp
        if (VllmFile("model_executor", "models", "gemma4_unified.py") is not { } code ||
            !File.ReadAllText(code).Contains("vision_config.num_soft_tokens")) return null;

        JsonElement? cfg = null;
        try { cfg = JsonDocument.Parse(File.ReadAllText(Path.Combine(m.Root, "config.json"))).RootElement.Clone(); } catch { /* JSON illisible : valeur ignorée */ }

        if (cfg is { } c && c.ValueKind == JsonValueKind.Object)
        {
            if (!c.TryGetProperty("vision_config", out var v) || v.ValueKind != JsonValueKind.Object) return null;
            if (v.TryGetProperty("num_soft_tokens", out _)) return null;
        }
        return Gemma4Override();
    }

    private static string Gemma4Override() => $"{{\"vision_config\": {{\"num_soft_tokens\": {Gemma4SoftTokens}}}}}";

    /// <inheritdoc />
    public CompatResult Check(string root, JsonElement cfg, IReadOnlyList<string> archs, string? quantMethod, bool hasTokenizer)
    {
        var onnxOnly = !Directory.EnumerateFiles(root, "*.safetensors").Any() && !Directory.EnumerateFiles(root, "*.bin").Any() &&
                       !Directory.EnumerateFiles(root, "*.pt").Any() && Directory.EnumerateFiles(root, "*.onnx", SearchOption.AllDirectories).Any();
        return Evaluate(IsMlx(root, cfg), IsExl(quantMethod, File.Exists(Path.Combine(root, "measurement.json"))),
            onnxOnly, quantMethod, archs, hasTokenizer);
    }

    /// <inheritdoc />
    public CompatResult CheckRemote(IReadOnlyList<string> paths, JsonElement cfg, IReadOnlyList<string> archs, string? quantMethod, bool hasTokenizer)
    {
        // Mêmes règles que Check, mais d'après la liste des fichiers du dépôt (rien n'est encore sur le disque)
        bool Top(string p) => !p.Contains('/');
        var topWeights = paths.Any(p => Top(p) && (p.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase) ||
                                                   p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
                                                   p.EndsWith(".pt", StringComparison.OrdinalIgnoreCase)));
        var mlx = paths.Any(p => Top(p) && p.EndsWith(".npz", StringComparison.OrdinalIgnoreCase)) || IsMlxConfig(cfg);
        var exl = IsExl(quantMethod, paths.Contains("measurement.json"));
        var onnxOnly = !topWeights && paths.Any(p => p.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase));
        return Evaluate(mlx, exl, onnxOnly, quantMethod, archs, hasTokenizer);
    }

    private static bool IsExl(string? quantMethod, bool hasMeasurement) =>
        quantMethod is "exl2" or "exl3" || hasMeasurement;

    /// <summary> Règles communes au modèle local et au dépôt distant : formats d'autres moteurs, quantification, architecture. </summary>
    private CompatResult Evaluate(bool mlx, bool exl, bool onnxOnly, string? quantMethod, IReadOnlyList<string> archs, bool hasTokenizer)
    {
        string? problem = null;
        var warnings = new List<string>();

        // Formats d'autres moteurs
        if (mlx)
            problem = Loc.T("Compat_Mlx");
        else if (exl)
            problem = Loc.F("Compat_OtherEngine", "ExLlama (EXL2/EXL3)");
        else if (onnxOnly)
            problem = Loc.F("Compat_OtherEngine", "ONNX");

        var support = LoadSupport();
        if (problem == null && support != null)
        {
            if (quantMethod != null && support.Quantizations.Count > 0 && !support.Quantizations.Contains(quantMethod) &&
                quantMethod != "bitsandbytes" && !IsKnownAlias(quantMethod, support))
                problem = Loc.F("Compat_Quant", quantMethod, _python.VllmVersion ?? "?");
            else if (quantMethod == "bitsandbytes" && _python.PackageVersion("bitsandbytes") == null)
                problem = Loc.T("Compat_Bnb");

            var arch = archs.FirstOrDefault();
            if (arch != null && support.Architectures.Count > 0 && !support.Architectures.Contains(arch))
                warnings.Add(Loc.F("Compat_Arch", arch, _python.VllmVersion ?? "?"));
        }
        if (archs.Count == 0) warnings.Add(Loc.T("Compat_NoArch"));
        if (!hasTokenizer) warnings.Add(Loc.T("Compat_NoTokenizer"));
        return new CompatResult(problem, warnings.Count > 0 ? string.Join(" ", warnings) : null);
    }

    // Noms de config HF différents de ceux du registre vLLM
    private static bool IsKnownAlias(string method, Support s) => method.ToLowerInvariant() switch
    {
        "awq" or "gptq" => true,
        "modelopt" or "nvfp4" => s.Quantizations.Any(x => x.StartsWith("modelopt")),
        _ => false,
    };

    /// <summary>
    /// MLX (Apple) : objet "quantization" / "quantization_config" SANS quant_method mais avec
    /// group_size / bits / mode (les exports récents écrivent les deux clés), ou poids .npz.
    /// </summary>
    public static bool IsMlx(string root, JsonElement cfg) =>
        Directory.EnumerateFiles(root, "*.npz").Any() || IsMlxConfig(cfg);

    /// <summary> Partie « config.json » de <see cref="IsMlx(string, JsonElement)"/>, utilisable sans dossier local. </summary>
    public static bool IsMlxConfig(JsonElement cfg)
    {
        if (cfg.ValueKind != JsonValueKind.Object) return false;
        foreach (var key in new[] { "quantization", "quantization_config" })
            if (cfg.TryGetProperty(key, out var q) && q.ValueKind == JsonValueKind.Object && !q.TryGetProperty("quant_method", out _) &&
                (q.TryGetProperty("group_size", out _) || q.TryGetProperty("bits", out _) || q.TryGetProperty("mode", out _)))
                return true;
        return false;
    }
}
