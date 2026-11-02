/*
    Fichier      :  EmbeddingClient.cs
    Projet       :  AlpenMux

    Description  :
        Client de l'API d'embedding compatible OpenAI (POST /v1/embeddings) d'un serveur lancé (llama.cpp avec
        --embedding, vLLM avec --runner pooling…). Applique le profil du modèle (préfixes requête / passage), envoie
        les textes par petits lots, normalise les vecteurs (cosinus = produit scalaire) et réessaie les erreurs
        passagères. Sans état : l'adresse et le modèle sont donnés à chaque appel.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Serveur d'embedding à interroger : adresse, nom du modèle servi et clé d'API éventuelle. </summary>
/// <param name="BaseUrl"> Adresse du serveur (ex. http://127.0.0.1:8001). </param>
/// <param name="Model"> Nom servi du modèle (champ « model » de la requête). </param>
/// <param name="ApiKey"> Clé d'API du serveur, ou null. </param>
public sealed record EmbeddingEndpoint(string BaseUrl, string Model, string? ApiKey);

/// <summary> Embeddings d'un serveur. </summary>
public interface IEmbeddingClient
{
    /// <summary>
    /// Vecteurs normalisés des textes, dans l'ordre. Une requête (question) reçoit le préfixe de requête du profil,
    /// un passage reçoit le préfixe de passage.
    /// </summary>
    /// <param name="endpoint"> Serveur et modèle. </param>
    /// <param name="texts"> Textes à embarquer. </param>
    /// <param name="isQuery"> Vrai pour une question, faux pour un passage de document. </param>
    /// <param name="ct"> Annulation. </param>
    Task<float[][]> EmbedAsync(EmbeddingEndpoint endpoint, IReadOnlyList<string> texts, bool isQuery, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IEmbeddingClient"/> en HTTP. </summary>
public sealed class EmbeddingClient : IEmbeddingClient
{
    /// <summary> Nombre de textes par requête (au-delà, le serveur peut refuser le lot). </summary>
    public const int BatchSize = 16;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly ILogger<EmbeddingClient> _log;

    /// <summary> Crée le client. </summary>
    /// <param name="log"> Journal (modèle sans profil connu). </param>
    public EmbeddingClient(ILogger<EmbeddingClient> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public async Task<float[][]> EmbedAsync(EmbeddingEndpoint endpoint, IReadOnlyList<string> texts, bool isQuery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var profile = EmbeddingProfiles.For(endpoint.Model);
        if (!profile.Known) _log.LogWarning("Modèle d'embedding « {Model} » inconnu : aucun préfixe appliqué, qualité de recherche possiblement réduite.", endpoint.Model);
        var prefix = isQuery ? profile.QueryPrefix : profile.PassagePrefix;

        var result = new float[texts.Count][];
        for (int start = 0; start < texts.Count; start += BatchSize)
        {
            int count = Math.Min(BatchSize, texts.Count - start);
            var batch = new string[count];
            for (int i = 0; i < count; i++) batch[i] = prefix + texts[start + i];
            var vectors = await PostAsync(endpoint, batch, ct);
            for (int i = 0; i < count; i++) result[start + i] = vectors[i];
        }
        return result;
    }

    /// <summary> Un lot : envoi, lecture, réessai (3 tentatives) sur erreur réseau ou 5xx. </summary>
    private static async Task<float[][]> PostAsync(EmbeddingEndpoint endpoint, string[] input, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl.TrimEnd('/')}/v1/embeddings")
                {
                    Content = new StringContent(BuildBody(endpoint.Model, input), Encoding.UTF8, "application/json"),
                };
                if (!string.IsNullOrWhiteSpace(endpoint.ApiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
                using var resp = await Http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if ((int)resp.StatusCode >= 500 && attempt < 3)
                {
                    await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
                    continue;
                }
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} : {Truncate(body)}");
                return Parse(body, input.Length);
            }
            catch (HttpRequestException) when (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("Le serveur d'embedding ne répond pas (délai dépassé).");
            }
        }
    }

    /// <summary> Corps de la requête (JSON). </summary>
    private static string BuildBody(string model, string[] input)
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("model", model);
            w.WriteStartArray("input");
            foreach (var s in input) w.WriteStringValue(s);
            w.WriteEndArray();
            w.WriteString("encoding_format", "float");
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary> Vecteurs de la réponse, remis dans l'ordre de l'entrée et normalisés. </summary>
    /// <param name="body"> Réponse JSON : data[] avec index et embedding. </param>
    /// <param name="expected"> Nombre de vecteurs attendus. </param>
    public static float[][] Parse(string body, int expected)
    {
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Réponse d'embedding inattendue : " + Truncate(body));
        var vectors = new float[expected][];
        int index = 0;
        foreach (var item in data.EnumerateArray())
        {
            int i = item.TryGetProperty("index", out var ix) && ix.TryGetInt32(out var parsed) ? parsed : index;
            index++;
            if (i < 0 || i >= expected) continue;
            var v = item.GetProperty("embedding").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray();
            vectors[i] = Normalize(v);
        }
        if (vectors.Any(v => v is null))
            throw new InvalidOperationException($"Réponse d'embedding incomplète ({data.GetArrayLength()} / {expected} vecteurs).");
        return vectors;
    }

    /// <summary> Normalise un vecteur (longueur 1) : le cosinus devient un produit scalaire. </summary>
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        foreach (var x in v) sum += (double)x * x;
        var norm = Math.Sqrt(sum);
        if (norm <= 0) return v;
        var f = (float)(1.0 / norm);
        for (int i = 0; i < v.Length; i++) v[i] *= f;
        return v;
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
