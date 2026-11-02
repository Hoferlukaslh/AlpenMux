/*
    Fichier      :  InstanceStore.cs
    Projet       :  AlpenMux

    Description  :
        Historique des serveurs lancés, conservé jusqu'à ce que l'utilisateur les supprime :
            ~/.local/share/alpenmux/instances/<id>/instance.json   configuration, état, dates
            ~/.local/share/alpenmux/instances/<id>/console.log     sortie complète de vLLM
        vLLM écrit directement dans console.log : le journal est complet même si l'application
        est fermée pendant que le serveur tourne, et le serveur peut être retrouvé au lancement suivant.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Serveur enregistré dans l'historique (instance.json). </summary>
public sealed class InstanceRecord
{
    /// <summary> Identifiant (nom du dossier). </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary> Configuration de lancement. </summary>
    public LaunchConfig Config { get; set; } = new();
    /// <summary> Arguments utilisés au lancement. </summary>
    public List<string> Arguments { get; set; } = new();
    /// <summary> Variables d'environnement du lancement (null = retirée). </summary>
    public Dictionary<string, string?> Environment { get; set; } = new();
    /// <summary> Date de création. </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary> Dernier démarrage. </summary>
    public DateTime? StartedAt { get; set; }
    /// <summary> Dernier arrêt. </summary>
    public DateTime? EndedAt { get; set; }
    /// <summary> État (nom de InstanceState). </summary>
    public string State { get; set; } = "Stopped";
    /// <summary> Code de sortie du dernier lancement. </summary>
    public int? ExitCode { get; set; }
    /// <summary> Dernière erreur détectée. </summary>
    public string? LastError { get; set; }
    /// <summary> PID du groupe de processus, pour retrouver le serveur au prochain lancement. </summary>
    public int? Pid { get; set; }
    /// <summary> Nombre de lancements. </summary>
    public int Runs { get; set; }
    /// <summary> Jetons générés au total. </summary>
    public double TotalGenerated { get; set; }
}

/// <summary> Historique des serveurs lancés (configuration, état et journal), sur le disque. </summary>
public interface IInstanceStore
{
    /// <summary> Dossier de l'historique. </summary>
    string Root { get; }

    /// <summary> Dossier d'un serveur. </summary>
    string DirOf(string id);

    /// <summary> Journal (console.log) d'un serveur. </summary>
    string LogOf(string id);

    /// <summary> Enregistre un serveur (écriture atomique). </summary>
    void Save(InstanceRecord r);

    /// <summary> Serveurs de l'historique, du plus ancien au plus récent. </summary>
    List<InstanceRecord> LoadAll();

    /// <summary> Supprime un serveur et son journal. </summary>
    void Delete(string id);

    /// <summary> Ajoute une ligne au journal d'un serveur. </summary>
    void AppendLine(string id, string line);

    /// <summary> Dernières lignes du journal (lecture de la fin du fichier seulement). </summary>
    List<string> ReadTail(string id, int maxBytes = 2 * 1024 * 1024, bool keepAnsi = false);

    /// <summary> Taille du journal (octets). </summary>
    long LogLength(string id);
}

/// <summary> Implémentation de <see cref="IInstanceStore"/>. </summary>
public sealed class InstanceStore : IInstanceStore
{
    private readonly ILogger<InstanceStore> _logger;

    /// <summary> Crée le service. </summary>
    /// <param name="logger"> Journal des erreurs. </param>
    public InstanceStore(ILogger<InstanceStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc />
    public string Root =>
        Path.Combine(AppPaths.DataDir, "instances");

    /// <inheritdoc />
    public string DirOf(string id) => Path.Combine(Root, id);
    /// <inheritdoc />
    public string LogOf(string id) => Path.Combine(DirOf(id), "console.log");

    /// <inheritdoc />
    public void Save(InstanceRecord r)
    {
        try
        {
            Directory.CreateDirectory(DirOf(r.Id));
            var tmp = Path.Combine(DirOf(r.Id), "instance.json.tmp");
            File.WriteAllText(tmp, JsonSerializer.Serialize(r, AppJsonContext.Default.InstanceRecord));
            File.Move(tmp, Path.Combine(DirOf(r.Id), "instance.json"), overwrite: true);
        }
        catch (Exception ex) { _logger.LogWarning("[Instances] Sauvegarde impossible : {Message}", ex.Message); }
    }

    /// <inheritdoc />
    public List<InstanceRecord> LoadAll()
    {
        var res = new List<InstanceRecord>();
        if (!Directory.Exists(Root)) return res;
        foreach (var d in Directory.GetDirectories(Root))
        {
            try
            {
                var f = Path.Combine(d, "instance.json");
                if (File.Exists(f) && JsonSerializer.Deserialize(File.ReadAllText(f), AppJsonContext.Default.InstanceRecord) is { } r)
                    res.Add(r);
            }
            catch (Exception ex) { _logger.LogWarning("[Instances] {Dir} : {Message}", d, ex.Message); }
        }
        return res.OrderBy(r => r.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        try { if (Directory.Exists(DirOf(id))) Directory.Delete(DirOf(id), true); }
        catch (Exception ex) { _logger.LogWarning("[Instances] Suppression impossible : {Message}", ex.Message); }
    }

    /// <inheritdoc />
    public void AppendLine(string id, string line)
    {
        try
        {
            Directory.CreateDirectory(DirOf(id));
            File.AppendAllText(LogOf(id), line + "\n");
        }
        catch { /* journal impossible à écrire : ligne perdue, le serveur continue */ }
    }

    /// <inheritdoc />
    public List<string> ReadTail(string id, int maxBytes = 2 * 1024 * 1024, bool keepAnsi = false)
    {
        var path = LogOf(id);
        if (!File.Exists(path)) return new();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - maxBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[fs.Length - start];
        fs.ReadExactly(buf);
        var text = Encoding.UTF8.GetString(buf);
        var lines = text.Split('\n').Select(l => l.Split('\r', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
            .Select(l => keepAnsi ? l : ProcessRunner.StripAnsi(l))
            .Where(l => l.Length > 0 && !InstanceFilters.IsProbe(ProcessRunner.StripAnsi(l))).ToList();
        if (start > 0 && lines.Count > 0) lines.RemoveAt(0);   // première ligne probablement coupée
        return lines;
    }

    /// <inheritdoc />
    public long LogLength(string id) => File.Exists(LogOf(id)) ? new FileInfo(LogOf(id)).Length : 0;
}

/// <summary> Filtres appliqués au journal des serveurs. </summary>
public static class InstanceFilters
{
    /// <summary> Sondes /metrics, /health et /is_sleeping de l'application : pas affichées dans la console. </summary>
    public static bool IsProbe(string line) =>
        line.Contains("\"GET /metrics HTTP") || line.Contains("\"GET /health HTTP") || line.Contains("\"GET /is_sleeping HTTP");
}
