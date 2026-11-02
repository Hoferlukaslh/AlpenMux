/*
    Fichier      :  ChatParsers.cs
    Projet       :  AlpenMux

    Description  :
        Choix automatique de --reasoning-parser et --tool-call-parser.
        Sans parser de raisonnement, la réflexion du modèle arrive dans la réponse ; sans parser d'outils,
        les clients (Newelle, Open WebUI…) ne peuvent pas utiliser les outils.
        On lit le modèle de conversation (chat_template) et les tokens spéciaux du tokenizer plutôt que de deviner
        d'après le nom : Qwen3 et Qwen3-Coder, par exemple, n'utilisent pas le même format d'appel d'outils.
        Un parser n'est proposé que s'il existe dans le vLLM installé (liste lue dans ses sources) ;
        un parser de raisonnement à <think> seulement si le tokenizer connaît <think> et </think>
        (sinon vLLM s'arrête : « could not locate think start/end tokens »).
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlpenMux.Services;

/// <summary> Parsers choisis pour un modèle. </summary>
/// <param name="Reasoning"> Parser de raisonnement (null = aucun). </param>
/// <param name="Tool"> Parser d'appels d'outils (null = aucun). </param>
public sealed record ParserChoice(string? Reasoning, string? Tool);

/// <summary> Parsers de raisonnement et d'appels d'outils : liste du vLLM installé et détection pour un modèle. </summary>
public interface IChatParsers
{
    /// <summary> Parsers de raisonnement du vLLM installé. </summary>
    IReadOnlyList<string> ReasoningParsers { get; }

    /// <summary> Parsers d'appels d'outils du vLLM installé. </summary>
    IReadOnlyList<string> ToolParsers { get; }

    /// <summary> Parsers recommandés pour ce modèle (null = aucun). GGUF : rien (llama.cpp a ses propres réglages). </summary>
    ParserChoice Detect(ModelInfo m);

    /// <summary> Oublie la liste lue (vLLM mis à jour). </summary>
    void Invalidate();
}

/// <summary> Implémentation de <see cref="IChatParsers"/>. </summary>
public sealed partial class ChatParsers : IChatParsers
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;
    private readonly IVllmOptions _options;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv et versions installées. </param>
    /// <param name="options"> Valeurs acceptées par --tool-call-parser ; sa relecture vide le catalogue. </param>
    public ChatParsers(ISettingsService settings, IPythonEnvironment python, IVllmOptions options)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(options);
        _settings = settings;
        _python = python;
        _options = options;
        options.CacheUpdated += Invalidate;
    }
    private sealed record Catalog(string Version, HashSet<string> Reasoning, HashSet<string> Tool);
    private Catalog? _catalog;

    // Parsers enregistrés : entrées de dictionnaire ("nom": (module, classe)) ou décorateurs register_module("nom")
    [GeneratedRegex("^\\s*\"([A-Za-z0-9_\\-]+)\"\\s*:\\s*\\(", RegexOptions.Multiline)]
    private static partial Regex DictEntry();

    [GeneratedRegex("register_module\\(\\s*(?:name\\s*=\\s*)?(\\[[^\\]]*\\]|\"[^\"]+\"|'[^']+')")]
    private static partial Regex Decorator();

    [GeneratedRegex("[\"']([A-Za-z0-9_\\-]+)[\"']")]
    private static partial Regex Quoted();

    private string? VllmDir()
    {
        try
        {
            var lib = LinuxHost.Local(LinuxHost.Combine(_settings.VenvPath, "lib"));   // \\wsl.localhost\… sous Windows
            return Directory.GetDirectories(lib, "python*").Select(p => Path.Combine(p, "site-packages", "vllm")).FirstOrDefault(Directory.Exists);
        }
        catch { return null; }
    }

    private static HashSet<string> ReadNames(IEnumerable<string> dirs)
    {
        var res = new HashSet<string>();
        foreach (var d in dirs.Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(d, "*.py"))
            {
                var text = File.ReadAllText(f);
                if (Path.GetFileName(f) == "__init__.py")
                    foreach (Match m in DictEntry().Matches(text)) res.Add(m.Groups[1].Value);
                foreach (Match m in Decorator().Matches(text))
                    foreach (Match q in Quoted().Matches(m.Groups[1].Value)) res.Add(q.Groups[1].Value);
            }
        return res;
    }

    /// <summary> Parsers du vLLM installé (null si vLLM est absent). </summary>
    private Catalog? Load()
    {
        var version = _python.VllmVersion;
        if (version == null || VllmDir() is not { } vllm) return null;
        if (_catalog?.Version == version) return _catalog;
        try
        {
            var reasoning = ReadNames([Path.Combine(vllm, "reasoning")]);
            var tool = ReadNames([Path.Combine(vllm, "tool_parsers"), Path.Combine(vllm, "entrypoints", "openai", "tool_parsers")]);
            // Liste exacte des choix de --tool-call-parser quand l'aide de vLLM est en cache
            if (_options.Choices("--tool-call-parser") is { Count: > 0 } choices) tool = choices.ToHashSet();
            return _catalog = new Catalog(version, reasoning, tool);
        }
        catch { return null; }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReasoningParsers => Load()?.Reasoning.Order().ToList() ?? [];
    /// <inheritdoc />
    public IReadOnlyList<string> ToolParsers => Load()?.Tool.Order().ToList() ?? [];

    // ---------------------------------------------------------------- Lecture du modèle

    /// <summary> Modèle de conversation : chat_template.jinja, chat_template.json ou tokenizer_config.json. </summary>
    public static string ReadChatTemplate(string root)
    {
        try
        {
            var jinja = Path.Combine(root, "chat_template.jinja");
            if (File.Exists(jinja)) return File.ReadAllText(jinja);
            foreach (var name in new[] { "chat_template.json", "tokenizer_config.json" })
            {
                var f = Path.Combine(root, name);
                if (!File.Exists(f)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                if (!doc.RootElement.TryGetProperty("chat_template", out var t)) continue;
                if (t.ValueKind == JsonValueKind.String) return t.GetString() ?? "";
                // Plusieurs modèles nommés (default, tool_use…) : on les lit tous
                if (t.ValueKind == JsonValueKind.Array)
                    return string.Join("\n", t.EnumerateArray().Select(x => x.TryGetProperty("template", out var v) ? v.GetString() : ""));
            }
        }
        catch { /* tokenizer_config.json illisible : pas de modèle de chat */ }
        return "";
    }

    /// <summary> Tokens ajoutés du tokenizer (tokenizer.json et tokenizer_config.json). </summary>
    private static HashSet<string> SpecialTokens(string root)
    {
        var res = new HashSet<string>();
        try
        {
            var tj = Path.Combine(root, "tokenizer.json");
            if (File.Exists(tj))
            {
                using var fs = File.OpenRead(tj);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.TryGetProperty("added_tokens", out var added) && added.ValueKind == JsonValueKind.Array)
                    foreach (var t in added.EnumerateArray())
                        if (t.TryGetProperty("content", out var c)) res.Add(c.GetString() ?? "");
            }
            var tc = Path.Combine(root, "tokenizer_config.json");
            if (File.Exists(tc))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(tc));
                if (doc.RootElement.TryGetProperty("added_tokens_decoder", out var dec) && dec.ValueKind == JsonValueKind.Object)
                    foreach (var p in dec.EnumerateObject())
                        if (p.Value.TryGetProperty("content", out var c)) res.Add(c.GetString() ?? "");
            }
        }
        catch { /* tokenizer.json illisible : pas de jetons spéciaux */ }
        return res;
    }

    /// <summary> Le tokenizer décrit-il lui-même sa réponse (response_template, transformers ≥ 5) ? -> parser « hf ». </summary>
    private (bool Thinking, bool Tools) ResponseTemplate(string root)
    {
        try
        {
            var tc = Path.Combine(root, "tokenizer_config.json");
            if (!File.Exists(tc)) return (false, false);
            using var doc = JsonDocument.Parse(File.ReadAllText(tc));
            if (!doc.RootElement.TryGetProperty("response_template", out var rt)) return (false, false);
            var text = rt.GetRawText();
            return (text.Contains("\"thinking\""), text.Contains("\"tool_calls\""));
        }
        catch { return (false, false); }
    }

    // ---------------------------------------------------------------- Choix

    /// <inheritdoc />
    public ParserChoice Detect(ModelInfo m)
    {
        if (m.IsGguf || m.IsOmni || Load() is not { } cat) return new ParserChoice(null, null);
        var template = ReadChatTemplate(m.Root);
        var tokens = SpecialTokens(m.Root);
        var type = (m.ModelType ?? "").ToLowerInvariant();
        var name = (m.Repo + " " + m.SourceRepo).ToLowerInvariant();
        var (rtThinking, rtTools) = ResponseTemplate(m.Root);

        string? Pick(HashSet<string> avail, params string[] names) => names.FirstOrDefault(avail.Contains);

        // Parser propre au modèle d'abord (testé par vLLM) ; « hf » (lu dans le tokenizer) en dernier recours
        var reasoning = DetectReasoning() ?? (rtThinking && cat.Reasoning.Contains("hf") ? "hf" : null);
        var tool = DetectTool() ?? (rtTools && cat.Tool.Contains("hf") ? "hf" : null);
        return new ParserChoice(reasoning, tool);

        string? DetectTool()
        {
            if (template.Length == 0) return null;
            bool Has(string s) => template.Contains(s, StringComparison.Ordinal);
            var t = cat.Tool;
            if (type == "gpt_oss" || Has("<|channel|>")) return Pick(t, "openai");
            if (Has("<minimax:tool_call>")) return Pick(t, type.Contains("m3") ? "minimax_m3" : "minimax_m2", "minimax_m2");
            if (Has("<seed:tool_call>")) return Pick(t, "seed_oss");
            if (Has("<|tool_calls_section_begin|>")) return Pick(t, type.Contains("k3") ? "kimi_k3" : "kimi_k2", "kimi_k2");
            if (Has("<｜tool▁calls▁begin｜>") || Has("<｜tool▁call▁begin｜>"))
                return type.Contains("v32") ? Pick(t, "deepseek_v32", "deepseek_v31")
                     : Has("```json") ? Pick(t, "deepseek_v3") : Pick(t, "deepseek_v31", "deepseek_v3");
            if (Has("<arg_key>")) return Regex.IsMatch(name, @"glm-?(4\.[7-9]|5)") ? Pick(t, "glm47", "glm45") : Pick(t, "glm45");
            if (type.StartsWith("gemma4") && Has("<|tool_call>")) return Pick(t, "gemma4");
            if (Has("<start_function_call>")) return Pick(t, "functiongemma");
            if (Has("<|tool_call_start|>")) return Pick(t, "lfm2", "pythonic");
            if (Has("<function=") && Has("<parameter=")) return Pick(t, "qwen3_coder", "qwen3_xml");
            if (Has("<tool_call>")) return type.StartsWith("ernie") ? Pick(t, "ernie45", "hermes")
                                       : type.Contains("hunyuan") ? Pick(t, "hunyuan_a13b", "hermes")
                                       : Pick(t, "hermes");
            if (Has("[TOOL_CALLS]")) return Pick(t, "mistral");
            if (type == "llama4") return Pick(t, "llama4_pythonic", "llama4_json");
            if (Has("<|python_tag|>") || (type == "llama" && Has("ipython"))) return Pick(t, "llama3_json");
            if (Has("<|tool_call|>")) return type.Contains("hybrid") ? Pick(t, "granite4", "granite") : Pick(t, "granite");
            if (Has("<|action_start|><|plugin|>")) return Pick(t, "internlm");
            if (Has("functools[")) return Pick(t, "phi4_mini_json");
            if (type == "jamba" && Has("<tool_calls>")) return Pick(t, "jamba");
            if (type.StartsWith("olmo3") && Has("<function_calls>")) return Pick(t, "olmo3");
            if (name.Contains("xlam")) return Pick(t, "xlam");
            return null;
        }

        string? DetectReasoning()
        {
            var r = cat.Reasoning;
            bool Has(string s) => template.Contains(s, StringComparison.Ordinal);
            if (type == "gpt_oss" || Has("<|channel|>")) return Pick(r, "openai_gptoss");
            if (type.StartsWith("gemma4") && Has("<|channel>")) return Pick(r, "gemma4");
            if (Has("<seed:think>")) return Pick(r, "seed_oss");
            if (Has("[THINK]") && tokens.Contains("[THINK]")) return Pick(r, "mistral");
            if (Has("Here is my thought process")) return Pick(r, "granite");

            // Famille <think> : le tokenizer doit connaître les deux balises, et le modèle doit vraiment réfléchir
            // (sinon un parser qui attend la réflexion range toute la réponse dans reasoning_content)
            if (!tokens.Contains("<think>") || !tokens.Contains("</think>") || !Has("<think>")) return null;
            var thinks = Has("enable_thinking") || Has("thinking") && Has("add_generation_prompt") && Regex.IsMatch(template, @"add_generation_prompt[\s\S]{0,400}<think>")
                         || Regex.IsMatch(name, @"thinking|[-_]r1\b|r1[-_]|reason|qwq");
            if (!thinks) return null;
            if (type.StartsWith("qwen3") || type.StartsWith("qwen3_5")) return Pick(r, "qwen3", "deepseek_r1");
            if (type.StartsWith("glm4_moe") || type.StartsWith("glm")) return Regex.IsMatch(name, @"glm-?(4\.[7-9]|5)") ? Pick(r, "glm47", "glm45") : Pick(r, "glm45", "deepseek_r1");
            if (type.StartsWith("minimax")) return Pick(r, type.Contains("m3") ? "minimax_m3" : "minimax_m2_append_think", "minimax_m2");
            if (type.Contains("hunyuan")) return Pick(r, "hunyuan_a13b");
            if (type.StartsWith("ernie")) return Pick(r, "ernie45");
            if (type.StartsWith("kimi")) return Pick(r, "kimi_k2");
            if (type.StartsWith("olmo3")) return Pick(r, "olmo3");
            if (type.StartsWith("step3")) return Pick(r, type.Contains("p5") ? "step3p5" : "step3");
            if (type.StartsWith("mimo")) return Pick(r, "mimo");
            if (type.StartsWith("deepseek_v3") && !Regex.IsMatch(name, @"r1")) return Pick(r, "deepseek_v3", "deepseek_r1");
            return Pick(r, "deepseek_r1", "qwen3");
        }
    }

    /// <inheritdoc />
    public void Invalidate() => _catalog = null;
}
