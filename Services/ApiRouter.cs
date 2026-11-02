/*
    Fichier      :  ApiRouter.cs
    Projet       :  AlpenMux

    Description  :
        Adresse API unique (http://127.0.0.1:9000/v1 par défaut) pour tous les serveurs lancés :
        Newelle, Open WebUI… n'ont qu'une URL à connaître, au lieu d'un port par serveur.
            - GET  /v1/models : modèles de tous les serveurs prêts (adaptateurs LoRA compris)
            - autres routes   : la requête est transmise au serveur dont le modèle correspond au champ "model"
                                du corps JSON (ou du formulaire multipart), réponse en flux (streaming SSE)
            - un serveur en veille est réveillé avant la requête ; si l'option est active, les autres serveurs
              en mode veille sur les mêmes GPU sont d'abord mis en veille (un seul modèle en VRAM à la fois)
            - la clé d'API de chaque serveur est ajoutée par le routeur ; le routeur peut avoir sa propre clé
        Sans "model" et avec un seul serveur prêt, la requête lui est envoyée.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using AlpenMux.ViewModels;

namespace AlpenMux.Services;

/// <summary> Routeur intégré : une seule adresse compatible OpenAI devant tous les serveurs lancés. </summary>
public interface IApiRouter
{
    /// <summary> Le routeur écoute. </summary>
    bool IsRunning { get; }

    /// <summary> Dernière erreur de démarrage, ou null. </summary>
    string? Error { get; }

    /// <summary> Nombre de requêtes routées depuis le démarrage. </summary>
    long Requests { get; }

    /// <summary> Levé quand l'état change (démarré, arrêté, erreur, requête). </summary>
    event Action? Changed;

    /// <summary> Adresse de base à donner aux clients (…/v1). </summary>
    string BaseUrl { get; }

    /// <summary> Démarre (ou redémarre avec les réglages actuels) ; arrête si le routeur est désactivé. </summary>
    void Apply();

    /// <summary> Arrête l'écoute. </summary>
    void Stop();
}

/// <summary> Implémentation de <see cref="IApiRouter"/>. </summary>
public sealed partial class ApiRouter : IApiRouter
{
    private readonly ISettingsService _settings;
    private readonly IAppState _appState;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages du routeur (adresse, port, clé). </param>
    /// <param name="appState"> Serveurs lancés vers lesquels router les requêtes. </param>
    public ApiRouter(ISettingsService settings, IAppState appState)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(appState);
        _settings = settings;
        _appState = appState;
    }


    private static readonly HttpClient Http = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,   // générations longues en streaming
    };
    private static readonly HttpClient ModelsHttp = new() { Timeout = TimeSpan.FromSeconds(4) };

    /// <summary> En-têtes que HttpListener gère lui-même : non recopiés. </summary>
    private static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Content-Length", "Host", "Authorization", "Upgrade",
        "Proxy-Connection", "TE", "Trailer",
    };

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _switchLock = new(1, 1);

    /// <inheritdoc />
    public bool IsRunning => _listener?.IsListening == true;
    /// <inheritdoc />
    public string? Error { get; private set; }
    /// <inheritdoc />
    public long Requests { get; private set; }

    /// <inheritdoc />
    public event Action? Changed;

    private AppSettings S => _settings.Current;

    /// <inheritdoc />
    public string BaseUrl => $"http://{(_localhostOnly ? "localhost" : S.RouterHost is "0.0.0.0" or "" ? "127.0.0.1" : S.RouterHost)}:{S.RouterPort}/v1";

    /// <summary> Windows sans réservation d'URL (port changé) : écoute sur « localhost » seulement. </summary>
    private bool _localhostOnly;

    /// <summary> Windows : n'accepter que les connexions locales quand l'adresse choisie est 127.0.0.1. </summary>
    private bool LoopbackOnly => OperatingSystem.IsWindows() && S.RouterHost is "127.0.0.1" or "localhost";

    // ---------------------------------------------------------------- Démarrage / arrêt

    /// <inheritdoc />
    public void Apply()
    {
        Stop();
        Error = null;
        if (!S.RouterEnabled) { Notify(); return; }
        try
        {
            _listener = StartListener();
            _cts = new CancellationTokenSource();
            _ = AcceptLoopAsync(_listener, _cts.Token);
        }
        catch (Exception ex)
        {
            Error = _appState.IsPortTaken(S.RouterPort) ? Loc.F("Router_PortTaken", S.RouterPort) : ex.Message;
            _listener = null;
        }
        Notify();
    }

    /// <summary>
    /// Linux : écoute directe. Windows (http.sys) : sans droits administrateur, seul « localhost » est permis ;
    /// l'installateur réserve http://+:9000/ pour tous les utilisateurs (127.0.0.1 compris). Si la réservation manque
    /// (port changé), repli sur « localhost ».
    /// </summary>
    private HttpListener StartListener()
    {
        _localhostOnly = false;
        var host = S.RouterHost is "0.0.0.0" or "" or "*" ? "*" : S.RouterHost;
        if (OperatingSystem.IsWindows()) host = "+";
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://{host}:{S.RouterPort}/");
        try { listener.Start(); return listener; }
        catch (HttpListenerException) when (OperatingSystem.IsWindows())
        {
            listener.Close();
            listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{S.RouterPort}/");
            listener.Start();
            _localhostOnly = true;
            return listener;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch { /* déjà fermé */ }
        _listener = null;
    }

    private void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => HandleAsync(ctx, ct), ct);
        }
    }

    // ---------------------------------------------------------------- Requêtes

    private List<InstanceViewModel> ReadyServers() =>
        Dispatcher.UIThread.Invoke(() => _appState.Instances.Where(i => i.IsReady).ToList());

    private async Task HandleAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        try
        {
            if (LoopbackOnly && !IPAddress.IsLoopback(req.RemoteEndPoint.Address))
            {
                resp.StatusCode = 403;
                return;
            }
            // CORS : clients web locaux (Open WebUI dans le navigateur…)
            resp.AddHeader("Access-Control-Allow-Origin", "*");
            if (req.HttpMethod == "OPTIONS")
            {
                resp.AddHeader("Access-Control-Allow-Headers", "*");
                resp.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                resp.StatusCode = 204;
                return;
            }
            var path = req.Url?.AbsolutePath ?? "/";
            if (path is "/health" or "/v1/health") { await WriteJsonAsync(resp, 200, "{\"status\":\"ok\"}"); return; }

            if (!string.IsNullOrEmpty(S.RouterApiKey) && req.Headers["Authorization"] != $"Bearer {S.RouterApiKey}")
            {
                await WriteErrorAsync(resp, 401, Loc.T("Router_BadKey"), "invalid_api_key");
                return;
            }

            Requests++;
            Notify();
            if (req.HttpMethod == "GET" && path is "/v1/models" or "/models")
            {
                await WriteJsonAsync(resp, 200, await ModelsJsonAsync(ct));
                return;
            }

            // Corps lu en entier pour trouver le modèle (petit : texte, ou formulaire audio)
            using var body = new MemoryStream();
            if (req.HasEntityBody) await req.InputStream.CopyToAsync(body, ct);
            var bytes = body.ToArray();
            var model = ModelOf(bytes, req.ContentType);

            var target = await FindServerAsync(model, ct);
            if (target == null)
            {
                var names = string.Join(", ", (await ModelIdsAsync(ct)).Select(x => x.Id).Distinct());
                await WriteErrorAsync(resp, 404, model == null ? Loc.F("Router_NoModelField", names) : Loc.F("Router_UnknownModel", model, names), "model_not_found");
                return;
            }
            if (!await EnsureAwakeAsync(target))
            {
                await WriteErrorAsync(resp, 503, Loc.F("Router_WakeFailed", target.ServedName), "server_unavailable");
                return;
            }
            await ForwardAsync(target, req, resp, bytes, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { await WriteErrorAsync(resp, 502, ex.Message, "bad_gateway"); } catch { /* client déjà déconnecté */ }
        }
        finally
        {
            try { resp.Close(); } catch { /* déjà fermé */ }
        }
    }

    [GeneratedRegex("name=\"model\"\\r?\\n\\r?\\n([^\\r\\n]+)")]
    private static partial Regex MultipartModel();

    /// <summary> Champ "model" de la requête : JSON, ou formulaire multipart (transcription audio). </summary>
    private static string? ModelOf(byte[] body, string? contentType)
    {
        if (body.Length == 0) return null;
        try
        {
            if (contentType?.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) == true)
            {
                var head = Encoding.UTF8.GetString(body, 0, Math.Min(body.Length, 64 * 1024));
                return MultipartModel().Match(head) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            }
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("model", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary> Modèles annoncés par chaque serveur prêt (nom servi + adaptateurs LoRA). </summary>
    private async Task<List<(string Id, InstanceViewModel Server, JsonElement Entry)>> ModelIdsAsync(CancellationToken ct)
    {
        var res = new List<(string, InstanceViewModel, JsonElement)>();
        foreach (var s in ReadyServers())
        {
            // sd-server annonce toujours « sd-cpp-local » : on expose le nom donné dans l'application
            if (s.Config.IsSdCpp)
            {
                using var named = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = s.ServedName, ["object"] = "model", ["owned_by"] = AppPaths.Id }, AppJsonContext.Default.DictionaryStringString));
                res.Add((s.ServedName, s, named.RootElement.Clone()));
                continue;
            }
            try
            {
                using var r = new HttpRequestMessage(HttpMethod.Get, s.LocalUrl + "/v1/models");
                if (s.ApiKey != null) r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
                using var resp = await ModelsHttp.SendAsync(r, ct);
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                foreach (var e in doc.RootElement.GetProperty("data").EnumerateArray())
                    if (e.TryGetProperty("id", out var id) && id.GetString() is { } sid) res.Add((sid, s, e.Clone()));
            }
            catch
            {
                // Serveur occupé (réveil, chargement) : son nom servi suffit
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, string> { ["id"] = s.ServedName, ["object"] = "model", ["owned_by"] = AppPaths.Id }, AppJsonContext.Default.DictionaryStringString));
                res.Add((s.ServedName, s, doc.RootElement.Clone()));
            }
        }
        return res;
    }

    private async Task<string> ModelsJsonAsync(CancellationToken ct)
    {
        var models = await ModelIdsAsync(ct);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("object", "list");
            w.WriteStartArray("data");
            foreach (var (id, _, entry) in models.DistinctBy(x => x.Id)) entry.WriteTo(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private async Task<InstanceViewModel?> FindServerAsync(string? model, CancellationToken ct)
    {
        var ready = ReadyServers();
        if (model == null) return ready.Count == 1 ? ready[0] : null;
        // Nom servi d'abord (sans appel réseau), sinon la liste des modèles (adaptateurs LoRA, chemin du modèle…)
        if (ready.FirstOrDefault(s => s.ServedName == model) is { } byName) return byName;
        if (ready.FirstOrDefault(s => s.Config.Loras?.Any(l => l.Name == model) == true) is { } byLora) return byLora;
        return (await ModelIdsAsync(ct)).FirstOrDefault(x => x.Id == model).Server;
    }

    /// <summary>
    /// Réveille le serveur s'il dort. Avec « un seul modèle éveillé », les autres serveurs en mode veille
    /// qui partagent un GPU avec lui sont d'abord mis en veille (leur VRAM est libérée).
    /// </summary>
    private async Task<bool> EnsureAwakeAsync(InstanceViewModel target)
    {
        if (!target.IsSleeping) return true;
        await _switchLock.WaitAsync();
        try
        {
            if (!target.IsSleeping) return true;
            if (S.RouterAutoSleep)
            {
                var others = Dispatcher.UIThread.Invoke(() => _appState.Instances
                    .Where(i => i != target && i.CanSleep && SharesGpu(i, target)).ToList());
                foreach (var o in others) await Dispatcher.UIThread.InvokeAsync(o.SleepAsync);
            }
            return await Dispatcher.UIThread.InvokeAsync(target.WakeUpAsync);
        }
        finally { _switchLock.Release(); }
    }

    private static bool SharesGpu(InstanceViewModel a, InstanceViewModel b)
    {
        if (a.Config.GpuIds == null || b.Config.GpuIds == null) return true;
        var ga = a.Config.GpuIds.Split(',');
        return b.Config.GpuIds.Split(',').Any(ga.Contains);
    }

    private static async Task ForwardAsync(InstanceViewModel target, HttpListenerRequest req, HttpListenerResponse resp, byte[] body, CancellationToken ct)
    {
        var url = target.LocalUrl + (req.Url?.PathAndQuery ?? "/");
        using var msg = new HttpRequestMessage(new HttpMethod(req.HttpMethod), url);
        if (body.Length > 0)
        {
            msg.Content = new ByteArrayContent(body);
            if (req.ContentType != null) msg.Content.Headers.TryAddWithoutValidation("Content-Type", req.ContentType);
        }
        foreach (string? key in req.Headers.AllKeys)
        {
            if (key == null || HopHeaders.Contains(key) || key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)) continue;
            msg.Headers.TryAddWithoutValidation(key, req.Headers.GetValues(key) ?? []);
        }
        if (target.ApiKey != null) msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.ApiKey);

        using var answer = await Http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.StatusCode = (int)answer.StatusCode;
        foreach (var h in answer.Headers.Concat(answer.Content.Headers))
        {
            if (HopHeaders.Contains(h.Key)) continue;
            if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) resp.ContentType = string.Join(", ", h.Value);
            else resp.AddHeader(h.Key, string.Join(", ", h.Value));
        }
        if (answer.Content.Headers.ContentLength is { } len) resp.ContentLength64 = len;
        else resp.SendChunked = true;

        // Lecture / écriture par petits morceaux : les tokens arrivent au client dès qu'ils sont générés
        await using var src = await answer.Content.ReadAsStreamAsync(ct);
        var buf = new byte[8192];
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await resp.OutputStream.WriteAsync(buf.AsMemory(0, n), ct);
            await resp.OutputStream.FlushAsync(ct);
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse resp, int code, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        resp.StatusCode = code;
        resp.ContentType = "application/json";
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes);
    }

    private static Task WriteErrorAsync(HttpListenerResponse resp, int code, string message, string type)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteStartObject("error");
            w.WriteString("message", message);
            w.WriteString("type", type);
            w.WriteNumber("code", code);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return WriteJsonAsync(resp, code, Encoding.UTF8.GetString(ms.ToArray()));
    }
}
