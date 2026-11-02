/*
    Fichier      :  HfService.cs
    Projet       :  AlpenMux

    Description  :
        Accès à Hugging Face :
            - recherche de modèles et liste des fichiers d'un dépôt (API HTTP)
            - vérification du token (whoami)
            - téléchargement via `hf download <dépôt> --local-dir <dossier>`

        Progression du téléchargement : la taille totale vient de l'API, la quantité
        téléchargée est mesurée sur le disque (fichiers .incomplete compris), complétée
        par le débit réseau quand les fichiers ne sont pas encore écrits (Xet).
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Résultat d'une recherche sur Hugging Face. </summary>
/// <param name="Id"> Dépôt (org/nom). </param>
/// <param name="Downloads"> Nombre de téléchargements. </param>
/// <param name="Likes"> Nombre de « j'aime ». </param>
/// <param name="PipelineTag"> Tâche du modèle (text-generation…). </param>
/// <param name="Gated"> Accès soumis à une demande. </param>
/// <param name="LastModified"> Dernière modification. </param>
public sealed record HfSearchResult(string Id, long Downloads, long Likes, string? PipelineTag, bool Gated, DateTime? LastModified);

/// <summary> Fichier d'un dépôt. </summary>
/// <param name="Path"> Chemin dans le dépôt. </param>
/// <param name="Size"> Taille (octets). </param>
public sealed record HfFile(string Path, long Size);

/// <summary> Accès à Hugging Face : recherche, liste des fichiers, téléchargements avec hf, jeton de l'utilisateur. </summary>
public interface IHfService
{
    /// <summary> Jeton Hugging Face des réglages, ou null. </summary>
    string? Token { get; }

    /// <summary> Exécutable hf : celui du venv vLLM, sinon celui du système. </summary>
    string? HfExecutable();

    /// <summary> Recherche des modèles sur Hugging Face. </summary>
    Task<List<HfSearchResult>> SearchAsync(string query, CancellationToken ct = default);

    /// <summary> Liste les fichiers d'un dépôt (à une révision donnée). </summary>
    Task<List<HfFile>> ListFilesAsync(string repo, string? revision, CancellationToken ct = default);

    /// <summary> Modèle d'origine déclaré dans la fiche du dépôt (base_model), utile pour le tokenizer des GGUF. </summary>
    Task<string?> GetBaseModelAsync(string repo, CancellationToken ct = default);

    /// <summary> config.json du dépôt (à une révision donnée), lu avant téléchargement ; null si absent ou illisible. </summary>
    Task<JsonElement?> GetConfigAsync(string repo, string? revision, CancellationToken ct = default);

    /// <summary> Retourne le nom d'utilisateur associé au token, ou lève une exception. </summary>
    Task<string> WhoAmIAsync(string token, CancellationToken ct = default);

    /// <summary> Arrête les `hf download` qui écrivent encore dans ce dossier. Retourne le nombre de processus arrêtés. </summary>
    Task<int> KillOrphansAsync(string localDir);

    /// <summary> Télécharge un dépôt (ou une partie) avec `hf download`, avec progression. Retourne vrai en cas de succès. </summary>
    Task<bool> DownloadAsync(string repo, string localDir, string? include, string? exclude, string? revision,
        ProgressTracker progress, Action<string> log, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IHfService"/>. </summary>
public sealed class HfService : IHfService
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (jeton, venv). </param>
    /// <param name="python"> Recherche de la commande hf. </param>
    public HfService(ISettingsService settings, IPythonEnvironment python)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        _settings = settings;
        _python = python;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string Api = "https://huggingface.co/api";

    /// <inheritdoc />
    public string? Token => string.IsNullOrWhiteSpace(_settings.Current.HfToken)
        ? null : _settings.Current.HfToken!.Trim();

    /// <inheritdoc />
    public string? HfExecutable()
    {
        var venvHf = LinuxHost.Combine(_settings.VenvPath, "bin", "hf");   // chemin Linux (WSL sous Windows)
        if (LinuxHost.FileExists(venvHf)) return venvHf;
        return _python.FindTool("hf");
    }

    private HttpRequestMessage Request(string url, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        token ??= Token;
        if (token != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd(AppPaths.UserAgent);
        return req;
    }

    /// <inheritdoc />
    public async Task<List<HfSearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var url = $"{Api}/models?search={Uri.EscapeDataString(query)}&sort=downloads&direction=-1&limit=30";
        using var resp = await Http.SendAsync(Request(url), ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));

        var res = new List<HfSearchResult>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            res.Add(new HfSearchResult(
                e.GetProperty("id").GetString() ?? "",
                e.TryGetProperty("downloads", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0,
                e.TryGetProperty("likes", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt64() : 0,
                e.TryGetProperty("pipeline_tag", out var p) ? p.GetString() : null,
                e.TryGetProperty("gated", out var g) && g.ValueKind is not (JsonValueKind.False or JsonValueKind.Null),
                e.TryGetProperty("lastModified", out var lm) && lm.TryGetDateTime(out var dt) ? dt : null));
        }
        return res;
    }

    /// <inheritdoc />
    public async Task<List<HfFile>> ListFilesAsync(string repo, string? revision, CancellationToken ct = default)
    {
        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim();
        var url = $"{Api}/models/{repo}/tree/{Uri.EscapeDataString(rev)}?recursive=true";
        var res = new List<HfFile>();

        while (url != null)
        {
            using var resp = await Http.SendAsync(Request(url), ct);
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException(Loc.T("Hf_Gated"));
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new FileNotFoundException(Loc.F("Hf_RepoNotFound", repo));
            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (e.GetProperty("type").GetString() != "file") continue;
                long size = e.TryGetProperty("lfs", out var lfs) && lfs.ValueKind == JsonValueKind.Object
                    ? lfs.GetProperty("size").GetInt64()
                    : e.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                res.Add(new HfFile(e.GetProperty("path").GetString() ?? "", size));
            }

            // Pagination (dépôts avec beaucoup de fichiers) : en-tête Link rel="next"
            url = null;
            if (resp.Headers.TryGetValues("Link", out var links))
            {
                var m = Regex.Match(string.Join(",", links), "<([^>]+)>;\\s*rel=\"next\"");
                if (m.Success) url = m.Groups[1].Value;
            }
        }
        return res;
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetConfigAsync(string repo, string? revision, CancellationToken ct = default)
    {
        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim();
        try
        {
            using var resp = await Http.SendAsync(Request($"https://huggingface.co/{repo}/resolve/{Uri.EscapeDataString(rev)}/config.json"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            // Clone : l'élément reste utilisable après la libération du document
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            // Pas de config distante (réseau coupé, fichier absent) : l'estimation retombe sur la taille des fichiers
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetBaseModelAsync(string repo, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.SendAsync(Request($"{Api}/models/{repo}"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("cardData", out var card) || card.ValueKind != JsonValueKind.Object) return null;
            if (!card.TryGetProperty("base_model", out var bm)) return null;
            return bm.ValueKind switch
            {
                JsonValueKind.String => bm.GetString(),
                JsonValueKind.Array when bm.GetArrayLength() > 0 => bm[0].GetString(),
                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Part du trafic réseau attribuée à chaque téléchargement en cours.
    /// Xet écrit les fichiers par gros blocs : entre deux blocs, la taille sur le disque ne bouge pas.
    /// Chaque seconde, les octets reçus sont répartis entre les téléchargements actifs,
    /// ce qui donne une progression continue même avec plusieurs téléchargements en parallèle.
    /// </summary>
    private sealed class NetMeter
    {
        public double Bytes;
        public double Needed;
        public double Remaining => Math.Max(0, Needed * 0.99 - Bytes);
    }

    private static readonly List<NetMeter> Meters = new();
    private static long _lastRx;
    private static Timer? _netTimer;

    private static NetMeter StartMeter(double needed)
    {
        var m = new NetMeter { Needed = needed };
        lock (Meters)
        {
            if (Meters.Count == 0)
            {
                _lastRx = SystemMonitorService.ReadNetRxTotal();
                _netTimer = new Timer(_ => ShareNetwork(), null, 1000, 1000);
            }
            Meters.Add(m);
        }
        return m;
    }

    private static void StopMeter(NetMeter m)
    {
        lock (Meters)
        {
            Meters.Remove(m);
            if (Meters.Count == 0) { _netTimer?.Dispose(); _netTimer = null; }
        }
    }

    private static void ShareNetwork()
    {
        lock (Meters)
        {
            var rx = SystemMonitorService.ReadNetRxTotal();
            var inc = Math.Max(0, rx - _lastRx);
            _lastRx = rx;
            // Répartition selon ce qu'il reste à télécharger : un petit téléchargement presque fini reçoit peu
            var total = Meters.Sum(m => m.Remaining);
            if (total <= 0) return;
            foreach (var m in Meters) m.Bytes += inc * (m.Remaining / total);
        }
    }

    /// <inheritdoc />
    public async Task<string> WhoAmIAsync(string token, CancellationToken ct = default)
    {
        using var resp = await Http.SendAsync(Request($"{Api}/whoami-v2", token), ct);
        if (!resp.IsSuccessStatusCode) throw new UnauthorizedAccessException(Loc.T("Hf_TokenInvalid"));
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "?" : "?";
    }

    /// <summary> Correspondance façon fnmatch (* et ?), plusieurs motifs séparés par des espaces/virgules. </summary>
    public static Func<string, bool> GlobFilter(string? patterns)
    {
        var list = (patterns ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (list.Length == 0) return _ => true;
        var regs = list.Select(p => new Regex("^" + Regex.Escape(p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase)).ToList();
        return path => regs.Any(r => r.IsMatch(path));
    }

    /// <summary> Découpe une liste de motifs séparés par des virgules ou des espaces. </summary>
    public static IEnumerable<string> SplitPatterns(string? patterns) =>
        (patterns ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// Télécharge un dépôt avec hf CLI. Retourne true si le téléchargement a abouti.
    /// </summary>
    /// <summary> Fichiers que vLLM n'utilise pas (doublons ONNX / TensorFlow / Flax, poids "original", journaux). </summary>
    public const string UselessFilesPatterns = "onnx/* *.onnx *.onnx_data runs/* *.h5 *.msgpack *.tflite *.ot coreml/* openvino/* *openvino_model* original/*";

    /// <summary>
    /// Poids en double dans un dépôt, dossier par dossier : .bin / .pt quand des safetensors existent à côté,
    /// consolidated*.safetensors (format Mistral) quand des safetensors HF existent aussi.
    /// vLLM ne charge qu'une copie : la télécharger deux fois double l'espace disque (et faussait l'estimation de VRAM).
    /// Retourne les chemins exacts à exclure.
    /// </summary>
    public static List<string> DuplicateWeights(IEnumerable<HfFile> files)
    {
        var res = new List<string>();
        var all = files.ToList();
        // Pipeline diffusers : seuls les composants (sous-dossiers) sont chargés. Les poids à la racine
        // sont des copies « un seul fichier » pour d'autres outils (sd_xl_base_1.0.safetensors…).
        var diffusers = all.Any(f => f.Path == "model_index.json");
        foreach (var dir in all.GroupBy(f => Path.GetDirectoryName(f.Path) ?? ""))
        {
            var names = dir.Select(f => f.Path).ToList();
            var st = names.Where(p => p.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)).ToList();
            if (st.Count == 0) continue;
            if (diffusers && dir.Key == "") { res.AddRange(names.Where(p => ModelScanner.WeightExt.Contains(Path.GetExtension(p).ToLowerInvariant()))); continue; }
            // Variantes diffusers (unet/diffusion_pytorch_model.fp16.safetensors) à côté des poids normaux, qui sont ceux chargés
            res.AddRange(st.Where(p => ModelScanner.IsWeightVariant(p, st)));
            res.AddRange(names.Where(p => Path.GetExtension(p).ToLowerInvariant() is ".bin" or ".pt" or ".pth" or ".ckpt"));
            var consolidated = st.Where(p => Path.GetFileName(p).StartsWith("consolidated", StringComparison.OrdinalIgnoreCase)).ToList();
            if (consolidated.Count > 0 && consolidated.Count < st.Count) res.AddRange(consolidated);
        }
        return res;
    }

    /// <summary> Octets des fichiers partiels laissés par un téléchargement interrompu (.cache/huggingface/download/**/*.incomplete). </summary>
    public static long PartialBytes(string localDir)
    {
        var dir = Path.Combine(localDir, ".cache", "huggingface", "download");
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.incomplete", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Supprime les fichiers partiels (.incomplete) restants. À n'appeler qu'après un téléchargement réussi :
    /// après une pause, hf (Xet) recommence le fichier en cours sous un autre nom et laisse l'ancien partiel,
    /// qui ferait croire pour toujours à un téléchargement incomplet.
    /// </summary>
    public static void CleanPartials(string localDir)
    {
        var dir = Path.Combine(localDir, ".cache", "huggingface", "download");
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, "*.incomplete", SearchOption.AllDirectories))
                try { File.Delete(f); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
        }
        catch { /* dossier illisible : fichiers partiels laissés */ }
    }

    /// <inheritdoc />
    public async Task<int> KillOrphansAsync(string localDir)
    {
        // Motif pkill (expression régulière étendue) : « download … --local-dir <dossier> » en fin de chemin
        var dir = Regex.Replace(LinuxHost.ToLinux(localDir).TrimEnd('/'), @"[.^$*+?()\[\]{}|\\]", @"\$0");
        var pattern = $"download .* --local-dir {dir}( |$)";
        try
        {
            var (_, pids) = await ProcessRunner.CaptureAsync("pgrep", ["-f", pattern], 5000, linux: true);
            var n = pids.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => int.TryParse(l.Trim(), out _));
            if (n == 0) return 0;
            await ProcessRunner.CaptureAsync("pkill", ["-TERM", "-f", pattern], 5000, linux: true);
            // Attend la fin (libération des verrous), puis force
            for (int i = 0; i < 20; i++)
            {
                await Task.Delay(250);
                if ((await ProcessRunner.CaptureAsync("pgrep", ["-f", pattern], 5000, linux: true)).Code != 0) return n;
            }
            await ProcessRunner.CaptureAsync("pkill", ["-KILL", "-f", pattern], 5000, linux: true);
            return n;
        }
        catch { return 0; }
    }

    /// <inheritdoc />
    public async Task<bool> DownloadAsync(string repo, string localDir, string? include, string? exclude, string? revision,
        ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        var hf = HfExecutable() ?? throw new InvalidOperationException(Loc.T("Hf_NotInstalled"));

        progress.Start(Loc.F("Hf_Downloading", repo));
        progress.SetDetail(Loc.T("Hf_ListingFiles"));

        // Taille totale à télécharger (fichiers déjà présents avec la bonne taille exclus)
        // Un `hf download` du même dossier encore actif (application fermée ou plantée pendant le téléchargement)
        // garderait le verrou des fichiers : le nouveau attendrait sans rien faire.
        if (await KillOrphansAsync(localDir) > 0) log(Loc.T("Hf_OrphanStopped"));

        // Taille totale ; déjà sur le disque : fichiers complets seulement. Un fichier partiel (.incomplete) d'une
        // tentative précédente n'est pas compté : hf (Xet) recommence le fichier en cours sous un autre nom.
        double total = 0, already = 0;
        try
        {
            var filter = GlobFilter(include);
            var excluded = string.IsNullOrWhiteSpace(exclude) ? (_ => false) : GlobFilter(exclude);
            foreach (var f in (await ListFilesAsync(repo, revision, ct)).Where(f => filter(f.Path) && !excluded(f.Path)))
            {
                total += f.Size;
                var local = Path.Combine(localDir, f.Path);
                if (File.Exists(local) && new FileInfo(local).Length == f.Size) already += f.Size;
            }
            already = Math.Min(total, already);
            log(Loc.F("Hf_TotalSize", Format.Bytes(total - already)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log("⚠ " + ex.Message);
            if (ex is UnauthorizedAccessException) { progress.Finish(ex.Message); return false; }
        }

        Directory.CreateDirectory(localDir);
        var startDisk = ModelScanner.DirectorySize(localDir);

        var args = new List<string> { "download", repo, "--local-dir", LinuxHost.ToLinux(localDir) };
        foreach (var p in SplitPatterns(include)) { args.Add("--include"); args.Add(p); }
        foreach (var p in SplitPatterns(exclude)) { args.Add("--exclude"); args.Add(p); }
        if (!string.IsNullOrWhiteSpace(revision)) { args.Add("--revision"); args.Add(revision.Trim()); }

        var env = new Dictionary<string, string?> { ["HF_HUB_DISABLE_PROGRESS_BARS"] = null };
        if (Token != null) env["HF_TOKEN"] = Token;

        log("$ " + hf + " " + string.Join(' ', args));
        progress.SetDetail(Loc.T("Hf_Starting"));

        var needed = total - already;
        var meter = StartMeter(needed);
        int code;
        await using (BackgroundTicker.Start(TimeSpan.FromSeconds(1), () =>
        {
            if (total <= 0) return;
            var disk = ModelScanner.DirectorySize(localDir) - startDisk;
            double net;
            lock (Meters)
            {
                // Le disque fait foi quand il est en avance sur l'estimation réseau
                if (disk > meter.Bytes) meter.Bytes = disk;
                net = meter.Bytes;
            }
            var done = already + Math.Max(0, Math.Max(disk, Math.Min(net, needed * 0.99)));
            done = Math.Min(done, total);
            progress.Report(done, total, $"{Format.Bytes(done)} / {Format.Bytes(total)}");
        }, ct))
        {
            try
            {
                code = await ProcessRunner.RunAsync(hf, args, (line, isProgress) =>
                {
                    if (!isProgress) log(line);
                }, env, ct, linux: true);
            }
            finally { StopMeter(meter); }
        }

        if (code == 0)
        {
            CleanPartials(localDir);
            // Mémorise l'origine du modèle (dépôt + modèle de base pour le tokenizer des GGUF)
            try
            {
                var meta = new DownloadMetadata { Repo = repo, BaseModel = await GetBaseModelAsync(repo, CancellationToken.None), DownloadedAt = DateTime.Now };
                File.WriteAllText(Path.Combine(localDir, DownloadMetadata.FileName),
                    System.Text.Json.JsonSerializer.Serialize(meta, AppJsonContext.Default.DownloadMetadata));
            }
            catch { /* métadonnées facultatives */ }
            progress.Finish(Loc.T("Hf_Done"));
        }
        else progress.Finish(Loc.F("Common_ExitCode", code));
        return code == 0;
    }
}
