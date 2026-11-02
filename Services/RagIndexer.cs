/*
    Fichier      :  RagIndexer.cs
    Projet       :  AlpenMux

    Description  :
        Indexation d'une base de connaissances : recherche des fichiers dans les sources (dossiers, fichiers),
        détection des modifications (taille, date, puis empreinte SHA-256), conversion en Markdown (texte lu
        directement, PDF par pdfminer, autres formats par markitdown, en un seul processus pour tout le lot),
        découpage en passages, embedding par le serveur choisi, écriture par fichier (une transaction).
        Un fichier en erreur est noté et n'arrête pas l'indexation ; une erreur du serveur d'embedding l'arrête
        (les fichiers déjà traités restent indexés). Annulable : la relance reprend là où elle s'est arrêtée.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Étape de l'indexation (pour le libellé de progression). </summary>
public enum RagIndexStage
{
    /// <summary> Recherche des fichiers et détection des modifications. </summary>
    Scanning,
    /// <summary> Conversion des documents en Markdown. </summary>
    Converting,
    /// <summary> Découpage et embedding des passages. </summary>
    Embedding,
}

/// <summary> Avancement de l'indexation. </summary>
/// <param name="Stage"> Étape en cours. </param>
/// <param name="FilesTotal"> Fichiers à traiter. </param>
/// <param name="FilesDone"> Fichiers traités. </param>
/// <param name="ChunksDone"> Passages embarqués. </param>
/// <param name="Current"> Fichier en cours, ou null. </param>
public sealed record RagIndexProgress(RagIndexStage Stage, int FilesTotal, int FilesDone, int ChunksDone, string? Current);

/// <summary> Bilan d'une indexation. </summary>
/// <param name="Indexed"> Fichiers (re)indexés avec succès. </param>
/// <param name="Unchanged"> Fichiers inchangés, non retraités. </param>
/// <param name="Removed"> Fichiers disparus du dossier, retirés de l'index. </param>
/// <param name="Failed"> Fichiers en erreur (raison dans l'index). </param>
/// <param name="Chunks"> Passages écrits pendant cette indexation. </param>
public sealed record RagIndexReport(int Indexed, int Unchanged, int Removed, int Failed, int Chunks);

/// <summary> Indexation des bases de connaissances. </summary>
public interface IRagIndexer
{
    /// <summary>
    /// Indexe (ou met à jour) une base avec le serveur d'embedding donné. Si l'index a été fait avec un autre modèle,
    /// il faut <paramref name="rebuild"/> : on ne mélange jamais des vecteurs de deux modèles.
    /// </summary>
    /// <param name="baseId"> Identifiant de la base. </param>
    /// <param name="endpoint"> Serveur d'embedding (lancé et prêt). </param>
    /// <param name="rebuild"> Vider l'index avant (réindexation complète). </param>
    /// <param name="progress"> Avancement (appelé depuis la tâche de fond). </param>
    /// <param name="ct"> Annulation. </param>
    Task<RagIndexReport> IndexAsync(string baseId, EmbeddingEndpoint endpoint, bool rebuild, IProgress<RagIndexProgress>? progress, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IRagIndexer"/>. </summary>
public sealed class RagIndexer : IRagIndexer
{
    /// <summary> Extensions lues directement, sans conversion. </summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".py", ".js", ".ts", ".java", ".c", ".h", ".cpp", ".hpp", ".go", ".rs", ".sh", ".yml", ".yaml",
        ".toml", ".ini", ".cfg", ".conf", ".tex", ".rst", ".sql",
    };

    /// <summary> Toutes les extensions prises en compte dans un dossier. </summary>
    public static readonly HashSet<string> SupportedExtensions = BuildSupported();

    private readonly IRagStore _store;
    private readonly IEmbeddingClient _embeddings;
    private readonly IMarkItDownService _markItDown;
    private readonly IPythonEnvironment _python;
    private readonly ILogger<RagIndexer> _log;

    /// <summary> Crée l'indexeur. </summary>
    /// <param name="store"> Bases de connaissances. </param>
    /// <param name="embeddings"> Client d'embedding. </param>
    /// <param name="markItDown"> Conversion des formats bureautiques (installation à la demande). </param>
    /// <param name="python"> Python du venv. </param>
    /// <param name="log"> Journal. </param>
    public RagIndexer(IRagStore store, IEmbeddingClient embeddings, IMarkItDownService markItDown, IPythonEnvironment python, ILogger<RagIndexer> log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embeddings);
        ArgumentNullException.ThrowIfNull(markItDown);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(log);
        _store = store;
        _embeddings = embeddings;
        _markItDown = markItDown;
        _python = python;
        _log = log;
    }

    private static HashSet<string> BuildSupported()
    {
        var set = new HashSet<string>(CodeExtensions, StringComparer.OrdinalIgnoreCase);
        foreach (var e in MarkItDownService.TextExtensions) set.Add(e);
        foreach (var pattern in MarkItDownService.Patterns) set.Add(Path.GetExtension(pattern.TrimStart('*')));
        return set;
    }

    /// <summary> Fichiers à indexer : les fichiers des sources et, pour les dossiers, tout ce qui est pris en charge (caché et « ~$ » exclus). </summary>
    /// <param name="sources"> Dossiers et fichiers. </param>
    public static List<string> Discover(IEnumerable<string> sources)
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        bool Visible(string file) => !Path.GetFileName(file).StartsWith('.') && !Path.GetFileName(file).StartsWith("~$", StringComparison.Ordinal);
        foreach (var source in sources)
        {
            if (File.Exists(source))
            {
                if (SupportedExtensions.Contains(Path.GetExtension(source))) found.Add(Path.GetFullPath(source));
            }
            else if (Directory.Exists(source))
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
                foreach (var file in Directory.EnumerateFiles(source, "*", options))
                {
                    if (!SupportedExtensions.Contains(Path.GetExtension(file)) || !Visible(file)) continue;
                    // Dossier caché (« .git », « .cache ») : ignoré
                    if (Path.GetRelativePath(source, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.StartsWith('.'))) continue;
                    found.Add(Path.GetFullPath(file));
                }
            }
        }
        return found.ToList();
    }

    /// <summary> Fichier à (re)traiter, avec ses empreintes. </summary>
    private sealed record Pending(string Path, long Size, long Mtime, string Sha256);

    /// <inheritdoc />
    public async Task<RagIndexReport> IndexAsync(string baseId, EmbeddingEndpoint endpoint, bool rebuild, IProgress<RagIndexProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var cfg = _store.Get(baseId) ?? throw new InvalidOperationException(Loc.T("Rag_NoBase"));
        using var db = _store.Open(baseId);

        var indexedModel = db.GetMeta("model");
        if (rebuild) db.Clear();
        else if (indexedModel is not null && indexedModel != endpoint.Model)
            throw new InvalidOperationException(Loc.F("Rag_ModelMismatch", indexedModel, endpoint.Model));

        // 1. Fichiers : retirés (disparus), inchangés, ou à traiter
        progress?.Report(new RagIndexProgress(RagIndexStage.Scanning, 0, 0, 0, null));
        var files = Discover(cfg.Sources);
        var present = new HashSet<string>(files, StringComparer.Ordinal);
        var known = db.Documents().ToDictionary(d => d.Path, StringComparer.Ordinal);
        int removed = 0;
        foreach (var d in known.Values)
        {
            if (present.Contains(d.Path)) continue;
            db.DeleteDocument(d.Id);
            removed++;
        }

        int unchanged = 0;
        var pending = new List<Pending>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            long size = info.Length, mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds();
            if (known.TryGetValue(file, out var old) && old.Size == size && old.Mtime == mtime && old.Error is null)
            {
                unchanged++;
                continue;
            }
            using var stream = File.OpenRead(file);
            var sha = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            pending.Add(new Pending(file, size, mtime, sha));
        }
        progress?.Report(new RagIndexProgress(RagIndexStage.Converting, pending.Count, 0, 0, null));

        // 2. Conversion : texte lu ici, le reste par un seul processus Python
        var markdown = new Dictionary<string, string>(StringComparer.Ordinal);
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var toConvert = new List<Pending>();
        foreach (var p in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (MarkItDownService.IsPlainText(p.Path) || CodeExtensions.Contains(Path.GetExtension(p.Path)))
            {
                try { markdown[p.Path] = await File.ReadAllTextAsync(p.Path, ct); }
                catch (IOException ex) { errors[p.Path] = ex.Message; }
            }
            else toConvert.Add(p);
        }
        if (toConvert.Count > 0)
            await ConvertBatchAsync(toConvert, markdown, errors, ct);

        // 3. Découpage et embedding, fichier par fichier
        var profile = EmbeddingProfiles.For(endpoint.Model);
        int maxTokens = Math.Min(RagChunker.DefaultMaxTokens, profile.MaxTokens - 100);
        int targetTokens = Math.Min(RagChunker.DefaultTargetTokens, maxTokens * 5 / 8);
        db.SetMeta("model", endpoint.Model);
        db.SetMeta("profile", profile.Family);
        db.SetMeta("schema", RagDatabase.SchemaVersion);

        int indexed = 0, failed = 0, chunksWritten = 0, done = 0;
        foreach (var p in pending)
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(p.Path);
            progress?.Report(new RagIndexProgress(RagIndexStage.Embedding, pending.Count, done, chunksWritten, name));

            if (errors.TryGetValue(p.Path, out var error) || !markdown.TryGetValue(p.Path, out var md))
            {
                db.SaveDocument(p.Path, p.Size, p.Mtime, p.Sha256, error ?? Loc.T("Rag_NoText"), []);
                failed++;
                done++;
                continue;
            }

            var passages = RagChunker.Split(md, targetTokens, maxTokens);
            if (passages.Count == 0)
            {
                db.SaveDocument(p.Path, p.Size, p.Mtime, p.Sha256, Loc.T("Rag_NoText"), []);
                failed++;
                done++;
                continue;
            }

            // Le nom du fichier et le chemin de titres sont embarqués avec le passage : la question les retrouve
            var texts = passages.Select(x => $"{Header(name, x.Heading)}\n\n{x.Text}").ToList();
            var vectors = await _embeddings.EmbedAsync(endpoint, texts, isQuery: false, ct);
            int dim = vectors[0].Length;
            var storedDim = db.Dimension;
            if (storedDim == 0) db.SetMeta("dimension", dim.ToString());
            else if (storedDim != dim)
                throw new InvalidOperationException(Loc.F("Rag_DimMismatch", storedDim, dim));

            var rows = passages.Select((x, i) => new RagChunkRow(i, x.Heading, x.Page, x.Text, x.Tokens, vectors[i])).ToList();
            db.SaveDocument(p.Path, p.Size, p.Mtime, p.Sha256, null, rows);
            indexed++;
            done++;
            chunksWritten += rows.Count;
        }

        cfg.EmbeddingModel = endpoint.Model;
        cfg.IndexedAt = DateTime.UtcNow;
        _store.SaveConfig(cfg);
        _log.LogInformation("Base « {Name} » indexée : {Indexed} fichier(s), {Chunks} passage(s), {Failed} en erreur", cfg.Name, indexed, chunksWritten, failed);
        return new RagIndexReport(indexed, unchanged, removed, failed, chunksWritten);
    }

    /// <summary> En-tête de passage embarqué : nom du fichier et chemin de titres. </summary>
    public static string Header(string fileName, string heading) =>
        heading.Length > 0 ? $"Fichier : {fileName} > {heading}" : $"Fichier : {fileName}";

    /// <summary> Convertit les fichiers par un seul processus Python ; les erreurs vont dans <paramref name="errors"/>. </summary>
    private async Task ConvertBatchAsync(List<Pending> files, Dictionary<string, string> markdown, Dictionary<string, string> errors, CancellationToken ct)
    {
        // markitdown fournit aussi pdfminer (PDF) : installé à la demande, comme pour le chat
        if (!_markItDown.Installed)
        {
            if (!await _markItDown.InstallAsync(new ProgressTracker(), _ => { }, ct) || !_markItDown.Installed)
            {
                foreach (var f in files) errors[f.Path] = Loc.T("Chat_DocInstallFailed");
                return;
            }
        }

        var work = Path.Combine(AppPaths.DataDir, "tmp", "rag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var request = Path.Combine(work, "request.json");
            var outDir = Path.Combine(work, "out");
            var linuxByLocal = files.ToDictionary(f => f.Path, f => LinuxHost.ToLinux(f.Path), StringComparer.Ordinal);
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                outdir = LinuxHost.ToLinux(outDir),
                files = files.Select(f => linuxByLocal[f.Path]).ToList(),
            });
            await File.WriteAllTextAsync(request, json, ct);

            var script = LinuxHost.ToLinux(AppPaths.ExtractScript("rag_convert.py"));
            var localByLinux = linuxByLocal.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal);
            var env = new Dictionary<string, string?> { ["VIRTUAL_ENV"] = _python.VenvPython, ["PYTHONIOENCODING"] = "utf-8" };
            int code = await ProcessRunner.RunAsync(_python.VenvPython,
                ["-I", script, LinuxHost.ToLinux(request)],
                (line, isErr) => ReadConversionLine(line, isErr, localByLinux, markdown, errors),
                env, ct, linux: true);
            if (code != 0) _log.LogWarning("rag_convert.py a terminé avec le code {Code}", code);

            // Fichiers sans sortie (le processus s'est arrêté) : erreur explicite
            foreach (var f in files.Where(f => !markdown.ContainsKey(f.Path) && !errors.ContainsKey(f.Path)))
                errors[f.Path] = Loc.F("Rag_ConvertCrashed", code);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (IOException) { /* dossier temporaire verrouillé : nettoyé au prochain lancement */ }
        }
    }

    /// <summary> Lit une ligne de sortie de rag_convert.py : chemin du Markdown produit, ou erreur. </summary>
    private static void ReadConversionLine(string line, bool isErr, Dictionary<string, string> localByLinux,
        Dictionary<string, string> markdown, Dictionary<string, string> errors)
    {
        if (isErr || !line.TrimStart().StartsWith('{')) return;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("path", out var pathEl) || !localByLinux.TryGetValue(pathEl.GetString() ?? "", out var local)) return;
            if (root.TryGetProperty("error", out var err))
            {
                errors[local] = err.GetString() ?? "";
                return;
            }
            var outLinux = root.GetProperty("out").GetString() ?? "";
            markdown[local] = File.ReadAllText(LinuxHost.Local(outLinux));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
        {
            // Ligne illisible : le fichier correspondant sera signalé en erreur à la fin
        }
    }
}
