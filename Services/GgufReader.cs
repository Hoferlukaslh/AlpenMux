/*
    Fichier      :  GgufReader.cs
    Projet       :  AlpenMux

    Description  :
        Lecture des métadonnées d'un fichier GGUF (en-tête clé/valeur, sans charger les poids).
        Sert à estimer le cache KV et le contexte maximal comme pour un config.json :
            general.architecture, <arch>.block_count, <arch>.context_length,
            <arch>.embedding_length, <arch>.attention.head_count(_kv), <arch>.attention.key_length
        head_count_kv peut être un tableau (une valeur par couche, 0 pour les couches sans
        attention des modèles hybrides comme LFM2) : il est alors conservé tel quel.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AlpenMux.Services;

/// <summary> Lecture des métadonnées d'un fichier GGUF (architecture, contexte, taille du vocabulaire…). </summary>
public static class GgufReader
{
    private const uint Magic = 0x46554747; // "GGUF"

    /// <summary> Métadonnées scalaires (+ tableaux d'entiers) ou null si le fichier n'est pas un GGUF lisible. </summary>
    public static Dictionary<string, object>? ReadMetadata(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var r = new BinaryReader(fs, Encoding.UTF8);
            if (r.ReadUInt32() != Magic) return null;
            var version = r.ReadUInt32();
            if (version < 2) return null;
            r.ReadUInt64();                 // nombre de tenseurs
            var kvCount = r.ReadUInt64();

            var res = new Dictionary<string, object>();
            for (ulong i = 0; i < kvCount; i++)
            {
                var key = ReadString(r);
                var type = r.ReadUInt32();
                var value = ReadValue(r, type, keep: true);
                if (value != null) res[key] = value;
            }
            return res;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GGUF] {path} : {ex.Message}");
            return null;
        }
    }

    private static string ReadString(BinaryReader r)
    {
        var len = r.ReadUInt64();
        if (len > 1 << 24) throw new InvalidDataException("chaîne trop longue");
        return Encoding.UTF8.GetString(r.ReadBytes((int)len));
    }

    private static object? ReadValue(BinaryReader r, uint type, bool keep)
    {
        switch (type)
        {
            case 0: return (long)r.ReadByte();
            case 1: return (long)r.ReadSByte();
            case 2: return (long)r.ReadUInt16();
            case 3: return (long)r.ReadInt16();
            case 4: return (long)r.ReadUInt32();
            case 5: return (long)r.ReadInt32();
            case 6: return (double)r.ReadSingle();
            case 7: return r.ReadByte() != 0;
            case 8:
                if (keep) return ReadString(r);
                r.BaseStream.Seek((long)r.ReadUInt64(), SeekOrigin.Current);
                return null;
            case 9:
            {
                var itemType = r.ReadUInt32();
                var count = r.ReadUInt64();
                // Tableaux d'entiers ou de booléens courts conservés (head_count_kv, sliding_window_pattern par couche),
                // le reste est sauté
                bool isInt = itemType is 0 or 1 or 2 or 3 or 4 or 5 or 7 or 10 or 11;
                var list = isInt && count <= 4096 ? new List<long>() : null;
                for (ulong i = 0; i < count; i++)
                {
                    var v = ReadValue(r, itemType, keep: list != null);
                    if (list != null) list.Add(v switch { long l => l, true => 1, _ => 0 });
                }
                return list?.ToArray();
            }
            case 10: return (long)r.ReadUInt64();
            case 11: return r.ReadInt64();
            case 12: return r.ReadDouble();
            default: throw new InvalidDataException($"type GGUF inconnu {type}");
        }
    }
}
