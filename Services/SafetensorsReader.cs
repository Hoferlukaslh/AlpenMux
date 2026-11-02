/*
    Fichier      :  SafetensorsReader.cs
    Projet       :  AlpenMux

    Description  :
        Lecture de l'en-tête des fichiers .safetensors (8 octets de longueur + JSON), sans lire les poids :
        taille de chaque tenseur. Sert à mesurer la part des modules vision / audio d'un modèle
        multimodal (VRAM économisée par le mode « texte seul »).
        Liste aussi les fichiers d'un index (model.safetensors.index.json) pour ne compter
        que les poids que vLLM charge vraiment.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlpenMux.Services;

/// <summary> Lecture des en-têtes .safetensors (noms, types et formes des tenseurs) sans charger les poids. </summary>
public static partial class SafetensorsReader
{
    /// <summary>
    /// En-tête JSON d'un fichier .safetensors (8 octets de longueur, puis le JSON). Lève une exception si le fichier
    /// est illisible ; null si la longueur annoncée est absurde (fichier qui n'est pas un .safetensors).
    /// </summary>
    /// <param name="path"> Fichier .safetensors. </param>
    /// <returns> Document à libérer par l'appelant, ou null. </returns>
    public static JsonDocument? ReadHeader(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Span<byte> len = stackalloc byte[8];
        fs.ReadExactly(len);
        var n = BitConverter.ToInt64(len);
        if (n <= 0 || n > 256L * 1024 * 1024) return null;
        var buf = new byte[n];
        fs.ReadExactly(buf);
        return JsonDocument.Parse(buf);
    }

    /// <summary> Taille de chaque tenseur (nom -> octets), null si l'en-tête est illisible. </summary>
    public static Dictionary<string, long>? ReadTensorSizes(string path)
    {
        try
        {
            using var doc = ReadHeader(path);
            if (doc == null) return null;
            var res = new Dictionary<string, long>();
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "__metadata__" || !p.Value.TryGetProperty("data_offsets", out var off) || off.GetArrayLength() != 2) continue;
                res[p.Name] = off[1].GetInt64() - off[0].GetInt64();
            }
            return res;
        }
        catch { return null; }
    }

    /// <summary>
    /// Octets une fois chargés en 16 bits : les tenseurs F32 comptent pour moitié (vLLM-Omni convertit les pipelines
    /// diffusers dans le dtype du serveur, bf16 par défaut). null si l'en-tête est illisible.
    /// </summary>
    public static long? Bytes16(string path)
    {
        try
        {
            using var doc = ReadHeader(path);
            if (doc == null) return null;
            long total = 0;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name == "__metadata__" || !p.Value.TryGetProperty("data_offsets", out var off) || off.GetArrayLength() != 2) continue;
                var size = off[1].GetInt64() - off[0].GetInt64();
                total += p.Value.TryGetProperty("dtype", out var dt) && dt.GetString() is "F32" or "F64" ? size / (dt.GetString() == "F64" ? 4 : 2) : size;
            }
            return total;
        }
        catch { return null; }
    }

    /// <summary> Composant d'un nom de tenseur propre aux encodeurs vision / audio et à leurs projecteurs. </summary>
    [GeneratedRegex(@"^(vision|visual|image|audio|speech_encoder|multi_modal|mm_proj|mm_projector|embed_vision|embed_audio|patch_embed|vit)", RegexOptions.IgnoreCase)]
    private static partial Regex TowerPart();

    /// <summary> Le tenseur appartient-il à la partie vision / audio d'un modèle multimodal ? </summary>
    public static bool IsMultimodalTensor(string name)
    {
        var parts = name.Split('.');
        if (parts.Contains("language_model")) return false;
        return parts.Any(p => TowerPart().IsMatch(p));
    }

    /// <summary> Octets des modules vision / audio dans ces fichiers (0 si rien de reconnu). </summary>
    public static long MultimodalBytes(IEnumerable<string> files)
    {
        long total = 0;
        foreach (var f in files.Where(f => f.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase)))
            if (ReadTensorSizes(f) is { } sizes)
                total += sizes.Where(kv => IsMultimodalTensor(kv.Key)).Sum(kv => kv.Value);
        return total;
    }

    /// <summary> Fichiers cités par un index (model.safetensors.index.json, pytorch_model.bin.index.json), null si absent. </summary>
    public static HashSet<string>? IndexedFiles(string indexPath)
    {
        try
        {
            if (!File.Exists(indexPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
            if (!doc.RootElement.TryGetProperty("weight_map", out var map) || map.ValueKind != JsonValueKind.Object) return null;
            return map.EnumerateObject().Select(p => p.Value.GetString() ?? "").Where(s => s.Length > 0).ToHashSet();
        }
        catch { return null; }
    }
}
