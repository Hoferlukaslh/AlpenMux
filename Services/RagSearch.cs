/*
    Fichier      :  RagSearch.cs
    Projet       :  AlpenMux

    Description  :
        Recherche dans une base de connaissances : la question est embarquée avec le préfixe de requête du modèle,
        puis recherche hybride (vecteurs + BM25, fusion RRF). Fournit aussi le bloc <context> envoyé au modèle de
        chat : les passages sont des données (balises), jamais des consignes ; le budget de tokens limite leur taille.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Recherche dans les bases de connaissances. </summary>
public interface IRagSearch
{
    /// <summary> Passages les plus pertinents d'une base pour une question. </summary>
    /// <param name="baseId"> Identifiant de la base. </param>
    /// <param name="endpoint"> Serveur d'embedding (même modèle que l'index). </param>
    /// <param name="query"> Question. </param>
    /// <param name="topK"> Nombre maximal de passages. </param>
    /// <param name="ct"> Annulation. </param>
    Task<List<RagHit>> SearchAsync(string baseId, EmbeddingEndpoint endpoint, string query, int topK, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IRagSearch"/>. </summary>
public sealed class RagSearch : IRagSearch
{
    private readonly IRagStore _store;
    private readonly IEmbeddingClient _embeddings;

    /// <summary> Crée le service. </summary>
    /// <param name="store"> Bases de connaissances. </param>
    /// <param name="embeddings"> Client d'embedding. </param>
    public RagSearch(IRagStore store, IEmbeddingClient embeddings)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(embeddings);
        _store = store;
        _embeddings = embeddings;
    }

    /// <inheritdoc />
    public async Task<List<RagHit>> SearchAsync(string baseId, EmbeddingEndpoint endpoint, string query, int topK, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(query)) return [];
        if (_store.Get(baseId) is null) throw new InvalidOperationException(Loc.T("Rag_NoBase"));
        using var db = _store.Open(baseId);

        var indexedModel = db.GetMeta("model");
        if (indexedModel is null) return [];   // base jamais indexée : rien à chercher
        if (indexedModel != endpoint.Model)
            throw new InvalidOperationException(Loc.F("Rag_ModelMismatch", indexedModel, endpoint.Model));

        var vector = (await _embeddings.EmbedAsync(endpoint, [query], isQuery: true, ct))[0];
        if (db.Dimension != vector.Length)
            throw new InvalidOperationException(Loc.F("Rag_DimMismatch", db.Dimension, vector.Length));
        return db.Search(vector, query, topK);
    }
}

/// <summary> Bloc de contexte du message envoyé au modèle, et tri des passages selon le budget de tokens. </summary>
public static class RagContext
{
    /// <summary>
    /// Bloc de contexte : chaque passage entre une balise source numérotée, puis la consigne de citation.
    /// Sans passage, la consigne dit au modèle qu'aucune source pertinente n'a été trouvée.
    /// </summary>
    /// <param name="hits"> Passages retenus, dans l'ordre de pertinence. </param>
    /// <param name="citeInstruction"> Consigne quand il y a des sources (citer [n]). </param>
    /// <param name="noSourceInstruction"> Consigne quand il n'y en a pas. </param>
    public static string Build(IReadOnlyList<RagHit> hits, string citeInstruction, string noSourceInstruction)
    {
        var sb = new StringBuilder("<context>\n");
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            var section = Attr(h.Heading);
            var page = h.Page is { } p ? $" page=\"{p}\"" : "";
            sb.Append($"<source id=\"{i + 1}\" file=\"{Attr(System.IO.Path.GetFileName(h.Path))}\" section=\"{section}\"{page}>\n")
              .Append(h.Text.Trim()).Append("\n</source>\n");
        }
        sb.Append("</context>\n");
        sb.Append(hits.Count > 0 ? citeInstruction : noSourceInstruction);
        return sb.ToString();
    }

    /// <summary> Garde les premiers passages qui tiennent dans le budget (estimation ≈ 4 caractères par token). </summary>
    /// <param name="hits"> Passages, du plus pertinent au moins pertinent. </param>
    /// <param name="tokenBudget"> Budget en tokens. </param>
    public static List<RagHit> WithinBudget(IReadOnlyList<RagHit> hits, int tokenBudget)
    {
        var kept = new List<RagHit>();
        int used = 0;
        foreach (var h in hits)
        {
            int cost = RagChunker.EstimateTokens(h.Text) + 16;
            if (used + cost > tokenBudget) break;
            kept.Add(h);
            used += cost;
        }
        return kept;
    }

    /// <summary> Attribut XML sûr : guillemets, chevrons et esperluettes remplacés. </summary>
    private static string Attr(string s) => s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");
}
