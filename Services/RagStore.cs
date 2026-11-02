/*
    Fichier      :  RagStore.cs
    Projet       :  AlpenMux

    Description  :
        Bases de connaissances du RAG. Une base = un dossier ~/.local/share/alpenmux/rag/<id>/ avec :
            - base.json : nom, sources (dossiers et fichiers), modèle d'embedding utilisé pour l'index ;
            - index.db  : SQLite (WAL) avec les documents, les passages, leurs vecteurs (float32 normalisés, BLOB)
              et une table FTS5 (BM25) synchronisée par des déclencheurs.
        La recherche est hybride : similarité cosinus exacte (produit scalaire SIMD, sans index ANN : suffisant
        jusqu'à ~200 000 passages) et BM25, fusionnées par Reciprocal Rank Fusion (RRF, k = 60).
        Une seule connexion par base, protégée par un verrou : l'indexation et la recherche peuvent se croiser.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics.Tensors;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace AlpenMux.Services;

/// <summary> Réglages d'une base de connaissances (fichier base.json). </summary>
public sealed class RagBaseConfig
{
    /// <summary> Identifiant (nom du dossier de la base). </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary> Nom affiché. </summary>
    public string Name { get; set; } = "";
    /// <summary> Dossiers et fichiers indexés. </summary>
    public List<string> Sources { get; set; } = new();
    /// <summary> Nom du modèle d'embedding qui a servi à l'index (vide tant que rien n'est indexé). </summary>
    public string EmbeddingModel { get; set; } = "";
    /// <summary> Création. </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary> Dernière indexation réussie. </summary>
    public DateTime? IndexedAt { get; set; }
}

/// <summary> Ligne de la table documents. </summary>
/// <param name="Id"> Identifiant. </param>
/// <param name="Path"> Chemin du fichier source. </param>
/// <param name="Size"> Taille en octets au moment de l'indexation. </param>
/// <param name="Mtime"> Date de modification (secondes Unix) au moment de l'indexation. </param>
/// <param name="Sha256"> Empreinte du contenu (détecte un fichier touché sans changement). </param>
/// <param name="Error"> Raison de l'échec de l'indexation, ou null. </param>
public sealed record RagDocument(long Id, string Path, long Size, long Mtime, string Sha256, string? Error);

/// <summary> Passage à écrire dans l'index, avec son vecteur normalisé. </summary>
public sealed record RagChunkRow(int Ord, string Heading, int? Page, string Text, int Tokens, float[] Vector);

/// <summary> Passage trouvé par la recherche. </summary>
/// <param name="ChunkId"> Identifiant du passage. </param>
/// <param name="Path"> Fichier source. </param>
/// <param name="Heading"> Chemin de titres. </param>
/// <param name="Page"> Page, ou null. </param>
/// <param name="Text"> Texte du passage. </param>
/// <param name="Cosine"> Similarité avec la requête (vecteurs). </param>
/// <param name="Bm25"> Score BM25 (négatif : plus petit = plus pertinent), null si le mot-clé n'a rien trouvé. </param>
/// <param name="Score"> Score de fusion RRF (plus grand = meilleur). </param>
public sealed record RagHit(long ChunkId, string Path, string Heading, int? Page, string Text, double Cosine, double? Bm25, double Score);

/// <summary> Compte d'une base (pour la liste). </summary>
/// <param name="Documents"> Fichiers indexés (sans erreur). </param>
/// <param name="Chunks"> Passages. </param>
/// <param name="Errors"> Fichiers en erreur. </param>
/// <param name="Bytes"> Taille des fichiers indexés. </param>
/// <param name="Dimension"> Dimension des vecteurs (0 = rien d'indexé). </param>
public sealed record RagStats(int Documents, int Chunks, int Errors, long Bytes, int Dimension);

/// <summary> Gestion des bases de connaissances (liste, création, suppression, ouverture de l'index). </summary>
public interface IRagStore
{
    /// <summary> Dossier qui contient les bases. </summary>
    string Root { get; }

    /// <summary> Toutes les bases, de la plus récente à la plus ancienne. </summary>
    IReadOnlyList<RagBaseConfig> List();

    /// <summary> Réglages d'une base, ou null si elle n'existe pas. </summary>
    /// <param name="id"> Identifiant de la base. </param>
    RagBaseConfig? Get(string id);

    /// <summary> Enregistre les réglages d'une base. </summary>
    void SaveConfig(RagBaseConfig config);

    /// <summary> Crée une base (vide, pas encore indexée). </summary>
    /// <param name="name"> Nom affiché. </param>
    /// <param name="sources"> Dossiers et fichiers à indexer. </param>
    RagBaseConfig Create(string name, IEnumerable<string> sources);

    /// <summary> Supprime une base et son index. </summary>
    void Delete(string id);

    /// <summary> Ouvre l'index d'une base (à fermer avec Dispose). </summary>
    RagDatabase Open(string id);

    /// <summary> Une base a été créée, supprimée ou modifiée (appelé depuis le thread qui fait le changement). </summary>
    event Action? Changed;
}

/// <summary> Implémentation de <see cref="IRagStore"/> : un dossier par base sous AppPaths.DataDir/rag. </summary>
public sealed class RagStore : IRagStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary> Crée le magasin dans le dossier des données de l'application. </summary>
    public RagStore() : this(Path.Combine(AppPaths.DataDir, "rag")) { }

    /// <summary> Crée le magasin dans un dossier donné (tests). </summary>
    /// <param name="root"> Dossier des bases. </param>
    internal RagStore(string root) => Root = root;

    /// <inheritdoc />
    public string Root { get; }

    /// <inheritdoc />
    public event Action? Changed;

    private string Dir(string id) => Path.Combine(Root, id);

    /// <inheritdoc />
    public IReadOnlyList<RagBaseConfig> List()
    {
        var list = new List<RagBaseConfig>();
        if (!Directory.Exists(Root)) return list;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var file = Path.Combine(dir, "base.json");
            if (!File.Exists(file)) continue;
            try
            {
                var cfg = JsonSerializer.Deserialize<RagBaseConfig>(File.ReadAllText(file));
                if (cfg != null) list.Add(cfg);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // base.json illisible : la base est ignorée (ses fichiers restent sur le disque)
            }
        }
        return list.OrderByDescending(c => c.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public RagBaseConfig? Get(string id) => id.Length == 0 || id.Any(c => c is '/' or '\\' or '.') ? null
        : List().FirstOrDefault(c => c.Id == id);

    /// <inheritdoc />
    public void SaveConfig(RagBaseConfig config)
    {
        Directory.CreateDirectory(Dir(config.Id));
        File.WriteAllText(Path.Combine(Dir(config.Id), "base.json"), JsonSerializer.Serialize(config, Json));
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public RagBaseConfig Create(string name, IEnumerable<string> sources)
    {
        var cfg = new RagBaseConfig { Name = name.Trim(), Sources = sources.Distinct().ToList() };
        SaveConfig(cfg);
        return cfg;
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        // Identifiant hors dossier racine (ex. « .. ») : refusé
        if (id.Length == 0 || id.Any(c => c is '/' or '\\' or '.')) throw new ArgumentException("Identifiant de base invalide", nameof(id));
        var dir = Dir(id);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Changed?.Invoke();
    }

    /// <inheritdoc />
    public RagDatabase Open(string id)
    {
        Directory.CreateDirectory(Dir(id));
        return new RagDatabase(Path.Combine(Dir(id), "index.db"));
    }
}

/// <summary>
/// Index d'une base : SQLite, écriture par lot, recherche hybride. Une instance par base ouverte ; le verrou
/// protège la connexion et le cache des vecteurs (rechargé après chaque écriture).
/// </summary>
public sealed class RagDatabase : IDisposable
{
    /// <summary> Version du découpage et du schéma : changer ce nombre force une réindexation. </summary>
    public const string SchemaVersion = "1";

    /// <summary> Constante RRF : score = Σ 1 / (RrfK + rang). </summary>
    public const int RrfK = 60;

    private static readonly Regex WordRegex = new(@"[\p{L}\p{N}]{3,}", RegexOptions.Compiled);
    /// <summary> Mots vides français et anglais : trop fréquents pour aider BM25. </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "les", "des", "une", "est", "que", "qui", "dans", "pour", "par", "sur", "avec", "sans", "ses", "aux", "dans",
        "elle", "ils", "elles", "nous", "vous", "tous", "toute", "tout", "très", "plus", "moins", "mais", "donc",
        "car", "comme", "sont", "ont", "fait", "faire", "cette", "ces", "leur", "leurs", "entre", "vers",
        "quel", "quelle", "quels", "quelles", "comment", "pourquoi", "combien", "quoi", "donne", "moi", "peux",
        "the", "and", "for", "are", "was", "with", "this", "that", "from", "have", "has", "not", "you", "your",
    };

    private readonly object _lock = new();
    private readonly SqliteConnection _db;

    // Cache des vecteurs : une matrice contiguë (n × dimension), rechargée après chaque écriture
    private float[]? _matrix;
    private long[]? _matrixIds;
    private int _matrixDim;

    /// <summary> Ouvre (ou crée) l'index à ce chemin. </summary>
    /// <param name="path"> Fichier index.db. </param>
    public RagDatabase(string path)
    {
        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            CREATE TABLE IF NOT EXISTS documents (
                id INTEGER PRIMARY KEY, path TEXT UNIQUE NOT NULL, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                sha256 TEXT NOT NULL DEFAULT '', title TEXT NOT NULL DEFAULT '', indexed_at INTEGER NOT NULL DEFAULT 0,
                error TEXT);
            CREATE TABLE IF NOT EXISTS chunks (
                id INTEGER PRIMARY KEY, doc_id INTEGER NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
                ord INTEGER NOT NULL, heading TEXT NOT NULL DEFAULT '', page INTEGER, text TEXT NOT NULL,
                tokens INTEGER NOT NULL, vector BLOB NOT NULL);
            CREATE INDEX IF NOT EXISTS chunks_doc ON chunks(doc_id);
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(text, heading, content='chunks', content_rowid='id',
                tokenize='unicode61 remove_diacritics 2');
            CREATE TRIGGER IF NOT EXISTS chunks_ai AFTER INSERT ON chunks BEGIN
                INSERT INTO chunks_fts(rowid, text, heading) VALUES (new.id, new.text, new.heading);
            END;
            CREATE TRIGGER IF NOT EXISTS chunks_ad AFTER DELETE ON chunks BEGIN
                INSERT INTO chunks_fts(chunks_fts, rowid, text, heading) VALUES ('delete', old.id, old.text, old.heading);
            END;
            """);
        if (GetMeta("schema") is null) SetMeta("schema", SchemaVersion);
    }

    /// <summary> Dimension des vecteurs (0 = aucun passage encore). </summary>
    public int Dimension
    {
        get
        {
            lock (_lock)
            {
                var v = Scalar("SELECT value FROM meta WHERE key = 'dimension'");
                return v is string s && int.TryParse(s, out var d) ? d : 0;
            }
        }
    }

    /// <summary> Lit une valeur de la table meta. </summary>
    public string? GetMeta(string key)
    {
        lock (_lock) return Scalar("SELECT value FROM meta WHERE key = @k", ("@k", key)) as string;
    }

    /// <summary> Écrit une valeur de la table meta (null = supprimée). </summary>
    public void SetMeta(string key, string? value)
    {
        lock (_lock)
        {
            if (value is null) Run("DELETE FROM meta WHERE key = @k", ("@k", key));
            else Run("INSERT INTO meta(key, value) VALUES (@k, @v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                ("@k", key), ("@v", value));
        }
    }

    /// <summary> Fichiers connus de l'index. </summary>
    public List<RagDocument> Documents()
    {
        lock (_lock)
        {
            var list = new List<RagDocument>();
            using var cmd = Command("SELECT id, path, size, mtime, sha256, error FROM documents ORDER BY path");
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new RagDocument(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        }
    }

    /// <summary>
    /// Enregistre un fichier et ses passages dans une seule transaction : une annulation ne laisse jamais un fichier
    /// marqué « à jour » avec d'anciens passages (la relance le retraitera).
    /// </summary>
    /// <param name="path"> Chemin du fichier source. </param>
    /// <param name="size"> Taille. </param>
    /// <param name="mtime"> Date de modification (secondes Unix). </param>
    /// <param name="sha256"> Empreinte du contenu. </param>
    /// <param name="error"> Erreur de conversion ou d'indexation (le fichier n'a alors aucun passage), ou null. </param>
    /// <param name="chunks"> Passages (vide en cas d'erreur). </param>
    public void SaveDocument(string path, long size, long mtime, string sha256, string? error, IReadOnlyList<RagChunkRow> chunks)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            Run("""
                INSERT INTO documents(path, size, mtime, sha256, indexed_at, error) VALUES (@p, @s, @m, @h, @i, @e)
                ON CONFLICT(path) DO UPDATE SET size = excluded.size, mtime = excluded.mtime, sha256 = excluded.sha256,
                    indexed_at = excluded.indexed_at, error = excluded.error
                """,
                ("@p", path), ("@s", size), ("@m", mtime), ("@h", sha256),
                ("@i", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ("@e", error));
            long docId = (long)Scalar("SELECT id FROM documents WHERE path = @p", ("@p", path))!;
            Run("DELETE FROM chunks WHERE doc_id = @d", ("@d", docId));
            if (chunks.Count > 0)
            {
                using var cmd = Command("INSERT INTO chunks(doc_id, ord, heading, page, text, tokens, vector) VALUES (@d, @o, @h, @p, @t, @n, @v)");
                var d = cmd.Parameters.Add("@d", SqliteType.Integer);
                var o = cmd.Parameters.Add("@o", SqliteType.Integer);
                var h = cmd.Parameters.Add("@h", SqliteType.Text);
                var p = cmd.Parameters.Add("@p", SqliteType.Integer);
                var t = cmd.Parameters.Add("@t", SqliteType.Text);
                var n = cmd.Parameters.Add("@n", SqliteType.Integer);
                var v = cmd.Parameters.Add("@v", SqliteType.Blob);
                foreach (var c in chunks)
                {
                    d.Value = docId; o.Value = c.Ord; h.Value = c.Heading;
                    p.Value = c.Page is { } page ? (object)page : DBNull.Value;
                    t.Value = c.Text; n.Value = c.Tokens;
                    v.Value = VectorBytes(c.Vector);
                    cmd.ExecuteNonQuery();
                }
            }
            tx.Commit();
            InvalidateCache();
        }
    }

    /// <summary> Supprime un fichier et ses passages (fichier disparu du dossier source). </summary>
    public void DeleteDocument(long docId)
    {
        lock (_lock)
        {
            Run("DELETE FROM documents WHERE id = @d", ("@d", docId));
            InvalidateCache();
        }
    }

    /// <summary> Vide l'index (fichiers, passages, métadonnées) : réindexation complète, par exemple après un changement de modèle. </summary>
    public void Clear()
    {
        lock (_lock)
        {
            Run("DELETE FROM documents");
            Run("DELETE FROM meta");
            InvalidateCache();
        }
    }

    /// <summary> Nombre de passages et de fichiers, pour la liste des bases. </summary>
    public RagStats Stats()
    {
        lock (_lock)
        {
            using var cmd = Command("""
                SELECT (SELECT COUNT(*) FROM documents WHERE error IS NULL),
                       (SELECT COUNT(*) FROM chunks),
                       (SELECT COUNT(*) FROM documents WHERE error IS NOT NULL),
                       (SELECT COALESCE(SUM(size), 0) FROM documents WHERE error IS NULL)
                """);
            using var r = cmd.ExecuteReader();
            r.Read();
            var dim = Dimension;
            return new RagStats(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.GetInt64(3), dim);
        }
    }

    /// <summary>
    /// Recherche hybride : les <paramref name="candidates"/> meilleurs passages par similarité (vecteur normalisé)
    /// et par BM25 sont fusionnés (RRF). Un passage vectoriel sous <paramref name="minCosine"/> n'est gardé que s'il
    /// contient aussi un mot de la question : mieux vaut zéro source qu'un passage hors sujet.
    /// </summary>
    /// <param name="query"> Vecteur normalisé de la requête (même dimension que l'index). </param>
    /// <param name="text"> Texte de la requête (mots-clés pour BM25). </param>
    /// <param name="topK"> Nombre de passages renvoyés. </param>
    /// <param name="candidates"> Candidats de chaque liste avant fusion. </param>
    /// <param name="minCosine"> Similarité minimale d'un passage sans mot commun. </param>
    public List<RagHit> Search(float[] query, string text, int topK = 8, int candidates = 30, double minCosine = 0.3)
    {
        lock (_lock)
        {
            EnsureMatrix();
            var vectorRank = new Dictionary<long, (int Rank, double Cosine)>();
            if (_matrix is { Length: > 0 } m && _matrixIds is { } ids && query.Length == _matrixDim)
            {
                int n = ids.Length, dim = _matrixDim;
                var scores = new double[n];
                var q = query.AsSpan();
                for (int i = 0; i < n; i++) scores[i] = TensorPrimitives.Dot(q, m.AsSpan(i * dim, dim));
                var order = Enumerable.Range(0, n).OrderByDescending(i => scores[i]).Take(candidates).ToList();
                for (int r = 0; r < order.Count; r++) vectorRank[ids[order[r]]] = (r + 1, scores[order[r]]);
            }

            var keywordRank = new Dictionary<long, (int Rank, double Bm25)>();
            var match = FtsQuery(text);
            if (match is not null)
            {
                using var cmd = Command("SELECT rowid, bm25(chunks_fts) FROM chunks_fts WHERE chunks_fts MATCH @q ORDER BY bm25(chunks_fts) LIMIT @n",
                    ("@q", match), ("@n", candidates));
                using var r = cmd.ExecuteReader();
                int rank = 0;
                while (r.Read()) keywordRank[r.GetInt64(0)] = (++rank, r.GetDouble(1));
            }

            // Fusion RRF ; filtre : similarité trop faible ET aucun mot commun → écarté
            var fused = new Dictionary<long, double>();
            foreach (var (id, v) in vectorRank) fused[id] = (fused.GetValueOrDefault(id)) + 1.0 / (RrfK + v.Rank);
            foreach (var (id, k) in keywordRank) fused[id] = fused.GetValueOrDefault(id) + 1.0 / (RrfK + k.Rank);
            var kept = fused
                .Where(kv => keywordRank.ContainsKey(kv.Key) || vectorRank.TryGetValue(kv.Key, out var v) && v.Cosine >= minCosine)
                .OrderByDescending(kv => kv.Value)
                .Take(topK)
                .ToList();

            var hits = new List<RagHit>();
            foreach (var (id, score) in kept)
            {
                using var cmd = Command("""
                    SELECT d.path, c.heading, c.page, c.text FROM chunks c JOIN documents d ON d.id = c.doc_id WHERE c.id = @i
                    """, ("@i", id));
                using var r = cmd.ExecuteReader();
                if (!r.Read()) continue;
                hits.Add(new RagHit(
                    id, r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetString(3),
                    vectorRank.TryGetValue(id, out var vr) ? vr.Cosine : 0,
                    keywordRank.TryGetValue(id, out var kr) ? kr.Bm25 : null,
                    score));
            }
            return hits;
        }
    }

    /// <summary> Requête FTS5 : mots de la question, sans mots vides, reliés par OR (le classement BM25 fait le tri). </summary>
    public static string? FtsQuery(string text)
    {
        var words = WordRegex.Matches(text).Select(m => m.Value).Where(w => !StopWords.Contains(w)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
        if (words.Count == 0) return null;
        return string.Join(" OR ", words.Select(w => "\"" + w.Replace("\"", "\"\"") + "\""));
    }

    /// <summary> Octets d'un vecteur float32 (little-endian, format du BLOB). </summary>
    public static byte[] VectorBytes(float[] v) => MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    /// <summary> Charge tous les vecteurs en une matrice contiguë si le cache est vide. </summary>
    private void EnsureMatrix()
    {
        if (_matrix != null) return;
        using var count = Command("SELECT COUNT(*) FROM chunks");
        int n = Convert.ToInt32(count.ExecuteScalar());
        if (n == 0) { _matrix = []; _matrixIds = []; _matrixDim = 0; return; }
        int dim = Dimension;
        if (dim <= 0) { _matrix = []; _matrixIds = []; _matrixDim = 0; return; }

        var matrix = new float[(long)n * dim];
        var ids = new long[n];
        using var cmd = Command("SELECT id, vector FROM chunks ORDER BY id");
        using var r = cmd.ExecuteReader();
        int row = 0;
        while (r.Read() && row < n)
        {
            var blob = (byte[])r[1];
            if (blob.Length != dim * sizeof(float)) continue;   // passage d'un autre modèle : ignoré
            ids[row] = r.GetInt64(0);
            MemoryMarshal.Cast<byte, float>(blob).CopyTo(matrix.AsSpan(row * dim, dim));
            row++;
        }
        if (row < n) { Array.Resize(ref ids, row); matrix = matrix[..(row * dim)]; }
        _matrix = matrix; _matrixIds = ids; _matrixDim = dim;
    }

    private void InvalidateCache()
    {
        _matrix = null; _matrixIds = null; _matrixDim = 0;
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private SqliteCommand Command(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private void Run(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(sql, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock) _db.Dispose();
    }
}
