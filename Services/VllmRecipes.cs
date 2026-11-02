/*
    Fichier      :  VllmRecipes.cs
    Projet       :  AlpenMux

    Description  :
        Recettes officielles de vLLM (vllm-project/recipes, publiées sur https://recipes.vllm.ai) :
        réglages recommandés par modèle.
            - index : https://recipes.vllm.ai/models.json (gardé un jour dans ~/.config/alpenmux/recipes/)
            - fiche : https://recipes.vllm.ai/<org>/<modèle>.json (base_args, features, opt_in_features)
        Le modèle est retrouvé par son dépôt d'origine, son modèle de base ou son nom
        (gemma-4-12B-it-AWQ-4bit -> google/gemma-4-12B-it).
        Options retenues : base_args et fonctions actives par défaut, sauf celles que le formulaire règle
        lui-même (parallélisme, mémoire, contexte, parsers, texte seul, MTP) et les fichiers du dépôt vLLM
        (--chat-template examples/…) absents d'une installation pip. Les parsers recommandés sont gardés à part.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Recette officielle pour un modèle. </summary>
/// <param name="Title"> Titre de la recette. </param>
/// <param name="Url"> Page de la recette. </param>
/// <param name="ExtraArgs"> Arguments recommandés. </param>
/// <param name="ReasoningParser"> Parser de raisonnement recommandé. </param>
/// <param name="ToolParser"> Parser d'appels d'outils recommandé. </param>
/// <param name="TrustRemoteCode"> --trust-remote-code exigé. </param>
public sealed record VllmRecipe(string Title, string Url, List<string> ExtraArgs, string? ReasoningParser, string? ToolParser, bool TrustRemoteCode);

/// <summary> Recettes officielles de vLLM : arguments recommandés pour un modèle connu. </summary>
public interface IVllmRecipes
{
    /// <summary> Fiche de la recette correspondant au modèle (null si aucune ou hors ligne). </summary>
    Task<VllmRecipe?> FindAsync(ModelInfo m);
}

/// <summary> Implémentation de <see cref="IVllmRecipes"/>. </summary>
public sealed class VllmRecipes : IVllmRecipes
{
    private const string Site = "https://recipes.vllm.ai";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private List<(string HfId, string Title, string Url, string Json)>? _index;
    private static readonly Dictionary<string, VllmRecipe?> Cache = new();

    private string CacheDir => Path.Combine(SettingsService.ConfigDirectory, "recipes");

    /// <summary> Options déjà réglées par le formulaire : jamais reprises de la recette. </summary>
    private static readonly HashSet<string> Managed =
    [
        "--tensor-parallel-size", "-tp", "--pipeline-parallel-size", "--data-parallel-size", "--max-model-len",
        "--gpu-memory-utilization", "--enable-auto-tool-choice", "--tool-call-parser", "--reasoning-parser",
        "--language-model-only", "--speculative-config", "-sc", "--host", "--port", "--served-model-name",
        "--kv-cache-dtype", "--chat-template", "--trust-remote-code",
    ];

    private async Task<string?> GetCachedAsync(string url, string file, TimeSpan maxAge)
    {
        var path = Path.Combine(CacheDir, file);
        try
        {
            if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < maxAge) return await File.ReadAllTextAsync(path);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(AppPaths.UserAgent);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
            var text = await resp.Content.ReadAsStringAsync();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, text);
            return text;
        }
        catch
        {
            // Hors ligne : la copie précédente, même ancienne
            try { return File.Exists(path) ? await File.ReadAllTextAsync(path) : null; } catch { return null; }
        }
    }

    private async Task<List<(string HfId, string Title, string Url, string Json)>> IndexAsync()
    {
        if (_index != null) return _index;
        var res = new List<(string, string, string, string)>();
        if (await GetCachedAsync($"{Site}/models.json", "models.json", TimeSpan.FromDays(1)) is { } text)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    string S(string k) => e.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                    if (S("hf_id").Length > 0 && S("json").Length > 0) res.Add((S("hf_id"), S("title"), S("url"), S("json")));
                }
            }
            catch { /* index des recettes illisible : liste vide */ }
        }
        return _index = res;
    }

    /// <inheritdoc />
    public async Task<VllmRecipe?> FindAsync(ModelInfo m)
    {
        if (Cache.TryGetValue(m.Target, out var cached)) return cached;
        VllmRecipe? recipe = null;
        try
        {
            var index = await IndexAsync();
            var entry = Match(index, m);
            if (entry is { } e && await GetCachedAsync(Site + e.Json, e.Json.Trim('/').Replace('/', '_'), TimeSpan.FromDays(7)) is { } json)
                recipe = Parse(json, e.Title, Site + e.Url);
        }
        catch { /* pas de réseau ou recette illisible : aucune recette */ }
        Cache[m.Target] = recipe;
        return recipe;
    }

    private (string HfId, string Title, string Url, string Json)? Match(List<(string HfId, string Title, string Url, string Json)> index, ModelInfo m)
    {
        foreach (var id in new[] { m.SourceRepo, m.BaseModel })
            if (!string.IsNullOrWhiteSpace(id) && index.FirstOrDefault(x => x.HfId.Equals(id, StringComparison.OrdinalIgnoreCase)) is { HfId.Length: > 0 } exact)
                return exact;
        var name = m.Name.ToLowerInvariant();
        // Même nom, sinon le nom de recette le plus long contenu dans celui du modèle (variantes quantifiées)
        var hit = index.Where(x => x.HfId.Split('/')[^1].Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (hit.Count == 0)
            hit = index.Where(x => x.HfId.Split('/')[^1].Length >= 6 && name.StartsWith(x.HfId.Split('/')[^1].ToLowerInvariant()))
                       .OrderByDescending(x => x.HfId.Split('/')[^1].Length).ToList();
        return hit.Count > 0 ? hit[0] : null;
    }

    private static VllmRecipe? Parse(string json, string title, string url)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var args = new List<string>();
        string? reasoning = null, tool = null;
        var trust = false;

        static IEnumerable<string> Strings(JsonElement a) =>
            a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.GetRawText()) : [];

        void Add(IEnumerable<string> list)
        {
            var l = list.ToList();
            for (int i = 0; i < l.Count; i++)
            {
                if (!l[i].StartsWith('-')) continue;
                var values = l.Skip(i + 1).TakeWhile(x => !x.StartsWith("--")).ToList();
                if (l[i] == "--reasoning-parser" && values.Count > 0) reasoning = values[0];
                else if (l[i] == "--tool-call-parser" && values.Count > 0) tool = values[0];
                else if (l[i] == "--trust-remote-code") trust = true;
                else if (!Managed.Contains(l[i]) && !args.Contains(l[i])) { args.Add(l[i]); args.AddRange(values); }
            }
        }

        if (root.TryGetProperty("model", out var model) && model.TryGetProperty("base_args", out var baseArgs)) Add(Strings(baseArgs));
        var optIn = root.TryGetProperty("opt_in_features", out var oi) ? Strings(oi).ToHashSet() : [];
        if (root.TryGetProperty("features", out var features) && features.ValueKind == JsonValueKind.Object)
            foreach (var f in features.EnumerateObject().Where(f => !optIn.Contains(f.Name)))
                if (f.Value.TryGetProperty("args", out var fa)) Add(Strings(fa));

        return new VllmRecipe(title, url, args, reasoning, tool, trust);
    }
}
