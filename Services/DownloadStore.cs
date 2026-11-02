/*
    Fichier      :  DownloadStore.cs
    Projet       :  AlpenMux

    Description  :
        Téléchargements en cours, mémorisés dans ~/.local/share/alpenmux/downloads.json.
        Au redémarrage de l'application, ils sont relancés : `hf download` reprend les fichiers
        déjà téléchargés (et les fichiers partiels .incomplete) au lieu de tout recommencer.
        Ceux que l'utilisateur a mis en pause restent en pause (Paused).
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Téléchargement en cours mémorisé pour être repris au démarrage. </summary>
public sealed record PendingDownload
{
    /// <summary> Dépôt Hugging Face (org/nom). </summary>
    public string Repo { get; set; } = "";
    /// <summary> Dossier de destination. </summary>
    public string LocalDir { get; set; } = "";
    /// <summary> Motifs de fichiers à inclure. </summary>
    public string? Include { get; set; }
    /// <summary> Motifs de fichiers à exclure. </summary>
    public string? Exclude { get; set; }
    /// <summary> Révision (branche, tag ou commit). </summary>
    public string? Revision { get; set; }
    /// <summary> Variante choisie (quantification GGUF…), affichée dans le titre. </summary>
    public string? Variant { get; set; }
    /// <summary> Mis en pause par l'utilisateur : ne repart pas tout seul au lancement. </summary>
    public bool Paused { get; set; }
}

/// <summary> Téléchargements en cours mémorisés sur le disque, relancés au démarrage. </summary>
public interface IDownloadStore
{
    /// <summary> Téléchargements mémorisés. </summary>
    List<PendingDownload> Load();

    /// <summary> Mémorise un téléchargement (remplace celui du même dossier et des mêmes fichiers). </summary>
    void Add(PendingDownload d);

    /// <summary> Oublie un téléchargement (terminé ou annulé). </summary>
    void Remove(PendingDownload d);
}

/// <summary> Implémentation de <see cref="IDownloadStore"/>. </summary>
public sealed class DownloadStore : IDownloadStore
{
    private readonly ILogger<DownloadStore> _logger;

    /// <summary> Crée le service. </summary>
    /// <param name="logger"> Journal des erreurs. </param>
    public DownloadStore(ILogger<DownloadStore> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    private static readonly object Lock = new();
    private string FilePath => Path.Combine(AppPaths.DataDir, "downloads.json");

    /// <inheritdoc />
    public List<PendingDownload> Load()
    {
        lock (Lock)
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppJsonContext.Default.ListPendingDownload) ?? new()
                    : new();
            }
            catch { return new(); }
        }
    }

    /// <inheritdoc />
    public void Add(PendingDownload d)
    {
        lock (Lock)
        {
            var list = Load().Where(x => !Same(x, d)).ToList();
            list.Add(d);
            Save(list);
        }
    }

    /// <inheritdoc />
    public void Remove(PendingDownload d)
    {
        lock (Lock) Save(Load().Where(x => !Same(x, d)).ToList());
    }

    private bool Same(PendingDownload a, PendingDownload b) => a.LocalDir == b.LocalDir && a.Include == b.Include;

    private void Save(List<PendingDownload> list)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            if (list.Count == 0) { File.Delete(FilePath); return; }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, AppJsonContext.Default.ListPendingDownload));
        }
        catch (Exception ex) { _logger.LogWarning("[Downloads] {Message}", ex.Message); }
    }
}
