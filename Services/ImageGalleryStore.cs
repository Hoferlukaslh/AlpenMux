/*
    Fichier      :  ImageGalleryStore.cs
    Projet       :  AlpenMux

    Description  :
        Galerie de la page Images, conservée sur le disque jusqu'à suppression par l'utilisateur.
        Chaque image est un PNG dans ~/.local/share/alpenmux/images, accompagné d'un fichier .json du même nom
        qui garde les informations de la génération (prompt, réglages, serveur, durée, date), et d'une miniature
        dans images/thumbs. La galerie ne charge que les miniatures ; la pleine résolution est lue à l'ouverture.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Informations d'une génération, enregistrées à côté de son image. </summary>
/// <param name="Prompt"> Prompt positif. </param>
/// <param name="NegativePrompt"> Prompt négatif (vide si aucun). </param>
/// <param name="Size"> Taille, par exemple « 1024x1024 ». </param>
/// <param name="Steps"> Étapes de diffusion, ou « par défaut ». </param>
/// <param name="Guidance"> Guidance (CFG) envoyée, ou « par défaut ». </param>
/// <param name="Model"> Nom du modèle servi. </param>
/// <param name="Server"> Nom du serveur qui a généré l'image. </param>
/// <param name="DurationSeconds"> Durée de la génération, en secondes. </param>
/// <param name="Created"> Heure de la génération. </param>
public sealed record GenerationRecord(string Prompt, string NegativePrompt, string Size, string Steps, string Guidance,
    string Model, string Server, double DurationSeconds, DateTime Created);

/// <summary> Image de la galerie : fichier, informations (absentes pour une image sans .json), miniature. </summary>
/// <param name="FilePath"> Chemin du PNG (pleine résolution). </param>
/// <param name="Info"> Informations de la génération, null si le .json manque ou est illisible. </param>
/// <param name="Thumbnail"> Miniature, décodée à petite taille. </param>
/// <param name="Created"> Date de la génération (ou de création du fichier sans .json). </param>
public sealed record StoredImage(string FilePath, GenerationRecord? Info, Bitmap Thumbnail, DateTime Created);

/// <summary> Stockage de la galerie des images générées. </summary>
public interface IImageGalleryStore
{
    /// <summary> Dossier des images. </summary>
    string Directory { get; }

    /// <summary> Relit les miniatures de toutes les images (la pleine résolution n'est pas lue). </summary>
    Task<List<StoredImage>> LoadAllAsync();

    /// <summary> Enregistre une image, ses informations et sa miniature. </summary>
    StoredImage Save(byte[] png, GenerationRecord info);

    /// <summary> Lit l'image en pleine résolution (à l'ouverture du carrousel). </summary>
    Bitmap LoadFull(string filePath);

    /// <summary> Supprime une image et ses informations. </summary>
    void Delete(string filePath);
}

/// <summary> Implémentation de <see cref="IImageGalleryStore"/> : fichiers PNG et .json dans le dossier des images. </summary>
public sealed class ImageGalleryStore : IImageGalleryStore
{
    /// <summary> Largeur des miniatures, en pixels (affichées au plus à 180 px). </summary>
    private const int ThumbWidth = 360;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly ILogger<ImageGalleryStore> _log;

    /// <summary> Crée le service. </summary>
    /// <param name="log"> Journal. </param>
    public ImageGalleryStore(ILogger<ImageGalleryStore> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <inheritdoc />
    public string Directory => Path.Combine(AppPaths.DataDir, "images");

    private string ThumbDirectory => Path.Combine(Directory, "thumbs");

    private string ThumbPath(string png) => Path.Combine(ThumbDirectory, Path.GetFileName(png));

    /// <inheritdoc />
    public Task<List<StoredImage>> LoadAllAsync() => Task.Run(() =>
    {
        var result = new List<StoredImage>();
        if (!System.IO.Directory.Exists(Directory)) return result;
        foreach (var file in System.IO.Directory.GetFiles(Directory, "*.png"))
        {
            try
            {
                var info = ReadInfo(file);
                var created = info?.Created ?? File.GetLastWriteTime(file);
                result.Add(new StoredImage(file, info, LoadThumbnail(file), created));
            }
            catch (Exception ex)
            {
                // Image illisible : ignorée, le fichier reste sur le disque
                _log.LogWarning("[galerie] {File} ignorée : {Message}", file, ex.Message);
            }
        }
        result.Sort((a, b) => a.Created.CompareTo(b.Created));
        return result;
    });

    /// <inheritdoc />
    public StoredImage Save(byte[] png, GenerationRecord info)
    {
        System.IO.Directory.CreateDirectory(Directory);
        // Nom unique : deux images générées dans la même seconde ne doivent pas s'écraser
        var stem = info.Created.ToString("yyyyMMdd-HHmmss");
        var index = 0;
        string path;
        do path = Path.Combine(Directory, $"{stem}-{index++}.png");
        while (File.Exists(path));
        File.WriteAllBytes(path, png);
        File.WriteAllText(Path.ChangeExtension(path, ".json"), JsonSerializer.Serialize(info, Options));
        using var source = new MemoryStream(png);
        return new StoredImage(path, info, MakeThumbnail(path, source), info.Created);
    }

    /// <inheritdoc />
    public Bitmap LoadFull(string filePath) => new(filePath);

    /// <inheritdoc />
    public void Delete(string filePath)
    {
        File.Delete(filePath);
        var json = Path.ChangeExtension(filePath, ".json");
        if (File.Exists(json)) File.Delete(json);
        var thumb = ThumbPath(filePath);
        if (File.Exists(thumb)) File.Delete(thumb);
    }

    /// <summary> Miniature d'une image : celle du dossier thumbs, ou créée une fois pour les images antérieures. </summary>
    private Bitmap LoadThumbnail(string png)
    {
        var thumb = ThumbPath(png);
        if (File.Exists(thumb)) return new Bitmap(thumb);
        using var source = File.OpenRead(png);
        return MakeThumbnail(png, source);
    }

    /// <summary> Décode une miniature à partir de l'image, l'enregistre dans thumbs et la retourne. </summary>
    private Bitmap MakeThumbnail(string png, Stream source)
    {
        var thumbnail = Bitmap.DecodeToWidth(source, ThumbWidth);
        System.IO.Directory.CreateDirectory(ThumbDirectory);
        using var output = File.Create(ThumbPath(png));
        thumbnail.Save(output);
        return thumbnail;
    }

    private GenerationRecord? ReadInfo(string png)
    {
        var json = Path.ChangeExtension(png, ".json");
        if (!File.Exists(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<GenerationRecord>(File.ReadAllText(json));
        }
        catch (JsonException ex)
        {
            // Informations illisibles : l'image reste affichée sans détails
            _log.LogWarning("[galerie] {File} illisible : {Message}", json, ex.Message);
            return null;
        }
    }
}
