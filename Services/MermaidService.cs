/*
    Fichier      :  MermaidService.cs
    Projet       :  AlpenMux

    Description  :
        Diagrammes Mermaid des réponses du chat (```mermaid) dessinés en PNG, en local, avec mermaidx :
        la vraie bibliothèque Mermaid exécutée dans un moteur JavaScript embarqué (QuickJS), sans navigateur
        ni Node.js. Rien n'est envoyé à un service en ligne (les discussions peuvent contenir des données
        personnelles).
            - mermaidx installé à la demande dans le venv (comme markitdown) ;
            - un lancement de Python par réponse (Assets/Scripts/render_mermaid.py), tous ses diagrammes d'un coup ;
            - images en cache (empreinte du code + thème), fond transparent, thème sombre ou clair ;
            - erreur de syntaxe renvoyée telle que Mermaid la donne (affichée, ou envoyée au modèle pour
              correction si l'option est active).
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Rendu des diagrammes Mermaid en PNG (paquet Python mermaidx dans le venv). </summary>
public interface IMermaidService
{
    /// <summary> Version de mermaidx installée, ou null. </summary>
    string? Version { get; }

    /// <summary> mermaidx est installé dans le venv. </summary>
    bool Installed { get; }

    /// <summary> Installe mermaidx dans le venv (installation sûre). Retourne vrai s'il est installé. </summary>
    Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);

    /// <summary> Dessine les diagrammes (ceux déjà en cache ne sont pas redessinés). </summary>
    Task<List<MermaidService.Result>> RenderAsync(IReadOnlyList<string> codes, bool dark, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IMermaidService"/>. </summary>
public sealed class MermaidService : IMermaidService
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;
    private readonly IDependencyService _dependencies;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv et versions installées. </param>
    /// <param name="dependencies"> Installation sûre du paquet dans le venv. </param>
    public MermaidService(ISettingsService settings, IPythonEnvironment python, IDependencyService dependencies)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(dependencies);
        _settings = settings;
        _python = python;
        _dependencies = dependencies;
    }
    /// <summary> Paquet Python du rendu. </summary>
    public const string Package = "mermaidx";

    private static string CacheDir => Path.Combine(AppPaths.DataDir, "diagrams");
    /// <inheritdoc />
    public string? Version => _python.PackageVersion("mermaidx");
    /// <inheritdoc />
    public bool Installed => Version != null;

    private static readonly SemaphoreSlim InstallLock = new(1, 1);

    /// <inheritdoc />
    public async Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        await InstallLock.WaitAsync(ct);
        try
        {
            if (Installed) return true;
            if (!_python.VenvExists) { log(Loc.T("Chat_DocNoVenv")); return false; }
            return await _dependencies.SafeInstallAsync([Package], Loc.T("Dep_InstallingMermaid"), false, progress, log, ct) && Installed;
        }
        finally { InstallLock.Release(); }
    }

    /// <summary> Erreurs déjà obtenues (par empreinte) : un diagramme invalide n'est pas réessayé à chaque affichage. </summary>
    /// <summary> Version du rendu : à changer quand l'aspect des diagrammes change, pour ne pas reprendre le cache. </summary>
    private const string RenderVersion = "r3:";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Failures = new();

    /// <summary> Résultat d'un diagramme : image PNG, ou erreur de Mermaid. </summary>
    public sealed record Result(string? Png, string? Error);

    /// <inheritdoc />
    public async Task<List<Result>> RenderAsync(IReadOnlyList<string> codes, bool dark, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        // Le script choisit la palette : le thème sombre garde les nœuds clairs (texte foncé), donc lisibles
        // même avec les couleurs pastel que donne le modèle
        var theme = dark ? "dark" : "light";
        var results = new Result?[codes.Count];
        var todo = new List<(int Index, string Code, string Png)>();
        for (int i = 0; i < codes.Count; i++)
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RenderVersion + theme + ":" + codes[i])))[..20].ToLowerInvariant();
            var png = Path.Combine(CacheDir, hash + ".png");
            if (File.Exists(png)) results[i] = new Result(png, null);
            else if (Failures.TryGetValue(hash, out var known)) results[i] = new Result(null, known);
            else todo.Add((i, codes[i], png));
        }

        if (todo.Count > 0)
        {
            var request = Path.Combine(CacheDir, $"request-{Guid.NewGuid():N}.json");
            await using (var fs = File.Create(request))
            await using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteBoolean("dark", dark);
                w.WriteStartArray("items");
                foreach (var (_, code, png) in todo)
                {
                    w.WriteStartObject();
                    w.WriteString("code", code);
                    w.WriteString("out", LinuxHost.ToLinux(png));   // Windows : le Python du venv tourne dans WSL
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            try
            {
                var lines = new List<string>();
                var env = new Dictionary<string, string?> { ["VIRTUAL_ENV"] = _settings.VenvPath, ["PYTHONIOENCODING"] = "utf-8" };
                var exit = await ProcessRunner.RunAsync(_python.VenvPython,
                    ["-I", LinuxHost.ToLinux(AppPaths.ExtractScript("render_mermaid.py")), LinuxHost.ToLinux(request)],
                    (l, p) => { if (!p && l.TrimStart().StartsWith('{')) lines.Add(l.Trim()); }, env, ct, linux: true);
                for (int k = 0; k < todo.Count; k++)
                {
                    var (index, _, png) = todo[k];
                    string? error = k < lines.Count ? null : Loc.F("Common_ExitCode", exit);
                    if (k < lines.Count)
                    {
                        using var doc = JsonDocument.Parse(lines[k]);
                        if (!doc.RootElement.GetProperty("ok").GetBoolean())
                            error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "?";
                    }
                    results[index] = error == null && File.Exists(png) ? new Result(png, null) : new Result(null, error ?? "?");
                    // Erreur de syntaxe (pas un échec du lancement) : mémorisée pour la session
                    if (results[index]!.Png == null && k < lines.Count) Failures[Path.GetFileNameWithoutExtension(png)] = results[index]!.Error!;
                }
            }
            finally { try { File.Delete(request); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ } }
        }
        return results.Select(r => r!).ToList();
    }

    /// <summary> Première ligne utile d'une erreur Mermaid (pour l'affichage sous la réponse). </summary>
    public static string ShortError(string error) =>
        error.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? error;
}
