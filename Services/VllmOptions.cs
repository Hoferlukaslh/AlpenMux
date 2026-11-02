/*
    Fichier      :  VllmOptions.cs
    Projet       :  AlpenMux

    Description  :
        Options acceptées par le vLLM installé, lues une fois dans `vllm serve --help=all` (≈ 15 s)
        et gardées en cache par version (~/.config/alpenmux/vllm-options.json).
        Sert à :
            - refuser avant le lancement une option inconnue de cette version (au lieu d'attendre
              l'échec « unrecognized arguments » après le chargement de Python) ;
            - choisir la forme d'une option selon la version (--language-model-only ou --limit-mm-per-prompt) ;
            - connaître les choix exacts de certaines options (--tool-call-parser {hermes,qwen3_coder,…}).
        Tant que la liste n'est pas connue, rien n'est refusé.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Options de vLLM mises en cache (vllm-options.json). </summary>
public sealed class VllmOptionsCache
{
    /// <summary> Version de vLLM (et de vLLM-Omni) lue. </summary>
    public string Version { get; set; } = "";
    /// <summary> Options acceptées. </summary>
    public List<string> Options { get; set; } = new();
    /// <summary> Valeurs acceptées par option. </summary>
    public Dictionary<string, List<string>> Choices { get; set; } = new();
}

/// <summary> Options et valeurs acceptées par le vLLM installé (lues dans vllm serve --help=all, mises en cache). </summary>
public interface IVllmOptions
{
    /// <summary> Levé (sur le thread de lecture) dès que la liste des options vient d'être relue. </summary>
    event Action? CacheUpdated;

    /// <summary> Levé quand la liste vient d'être lue (le formulaire peut revérifier la configuration). </summary>
    event Action? Loaded;

    /// <summary> Lit la liste en arrière-plan si elle manque (une seule lecture à la fois). </summary>
    Task EnsureAsync();

    /// <summary> true / false si la liste est connue, null sinon. </summary>
    bool? Supports(string option);

    /// <summary> Valeurs acceptées par cette option, ou null si inconnues. </summary>
    IReadOnlyList<string>? Choices(string option);

    /// <summary> Options de la ligne de commande que ce vLLM ne connaît pas (vide si la liste n'est pas connue). </summary>
    List<string> Unknown(IEnumerable<string> args);
}

/// <summary> Implémentation de <see cref="IVllmOptions"/>. </summary>
public sealed partial class VllmOptions : IVllmOptions
{
    /// <inheritdoc />
    public event Action? CacheUpdated;

    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;

    private readonly ILogger<VllmOptions> _logger;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv et versions installées. </param>
    /// <param name="logger"> Journal des erreurs. </param>
    public VllmOptions(ISettingsService settings, IPythonEnvironment python, ILogger<VllmOptions> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        _settings = settings;
        _python = python;
    }
    private VllmOptionsCache? _cache;
    private Task? _loading;

    private string CacheFile => Path.Combine(SettingsService.ConfigDirectory, "vllm-options.json");

    /// <summary> Clé de cache : vLLM et vLLM-Omni (qui ajoute ses options). </summary>
    private string? VersionKey => _python.VllmVersion is { } v ? v + "|" + _python.OmniVersion : null;

    /// <inheritdoc />
    public event Action? Loaded;

    [GeneratedRegex(@"(?<![\w-])(--[a-z0-9][a-z0-9\-\.]*)")]
    private static partial Regex OptionRegex();

    [GeneratedRegex(@"^\s{2}(--[a-z0-9][a-z0-9\-]*)[^\n{]*\{([^}]+)\}", RegexOptions.Multiline)]
    private static partial Regex ChoicesRegex();

    private VllmOptionsCache? Current()
    {
        var key = VersionKey;
        if (key == null) return null;
        if (_cache?.Version == key) return _cache;
        try
        {
            if (File.Exists(CacheFile) && JsonSerializer.Deserialize(File.ReadAllText(CacheFile), AppJsonContext.Default.VllmOptionsCache) is { } c && c.Version == key)
                return _cache = c;
        }
        catch { /* cache illisible : la liste sera relue */ }
        return null;
    }

    /// <inheritdoc />
    public Task EnsureAsync()
    {
        if (Current() != null || VersionKey is not { } key || !_python.VllmInstalled) return Task.CompletedTask;
        lock (CacheFile) return _loading ??= LoadAsync(key).ContinueWith(_ => { lock (CacheFile) _loading = null; });
    }

    private async Task LoadAsync(string key)
    {
        try
        {
            var env = new Dictionary<string, string?> { ["VIRTUAL_ENV"] = _settings.VenvPath, ["NO_COLOR"] = "1" };
            var (code, output) = await ProcessRunner.CaptureAsync(_python.VllmExe, ["serve", "--help=all"], 120_000, env, linux: true);
            if (code != 0 || !output.Contains("--max-model-len")) return;
            var cache = new VllmOptionsCache { Version = key };
            cache.Options = OptionRegex().Matches(output).Select(m => m.Groups[1].Value.TrimEnd('.')).Distinct().Order().ToList();
            foreach (Match m in ChoicesRegex().Matches(output))
                cache.Choices[m.Groups[1].Value] = m.Groups[2].Value.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            _cache = cache;
            Directory.CreateDirectory(SettingsService.ConfigDirectory);
            File.WriteAllText(CacheFile, JsonSerializer.Serialize(cache, AppJsonContext.Default.VllmOptionsCache));
            CacheUpdated?.Invoke();
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Loaded?.Invoke());
        }
        catch (Exception ex) { _logger.LogWarning("[vllm --help] {Message}", ex.Message); }
    }


    /// <inheritdoc />
    public bool? Supports(string option) => Current() is { } c ? c.Options.Contains(option) : null;

    /// <inheritdoc />
    public IReadOnlyList<string>? Choices(string option) => Current()?.Choices.GetValueOrDefault(option);

    /// <inheritdoc />
    public List<string> Unknown(IEnumerable<string> args)
    {
        if (Current() is not { } c) return [];
        var known = c.Options.ToHashSet();
        return args.Where(a => a.StartsWith("--") && a.Length > 2)
                   .Select(a => a.Split('=')[0])
                   .Where(a => !known.Contains(a))
                   .Distinct().ToList();
    }
}
