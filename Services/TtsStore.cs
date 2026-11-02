/*
    Fichier      :  TtsStore.cs
    Projet       :  AlpenMux

    Description  :
        Audios générés par les modèles de synthèse vocale (TTS), conservés de façon permanente :
            ~/.local/share/alpenmux/tts/<id>.wav   l'audio
            ~/.local/share/alpenmux/tts/<id>.json  les informations de génération (texte, voix, modèle…)
            ~/.local/share/alpenmux/tts/previews/  aperçus des voix (cache, régénérables)

        Contient aussi la détection des modèles TTS et la lecture des informations utiles
        dans config.json (voix et langues prévues par le modèle), utilisées si le serveur
        ne les fournit pas.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlpenMux.Services;

/// <summary> Audio généré par un modèle de synthèse vocale, et ses informations. </summary>
public sealed class TtsClip
{
    /// <summary> Identifiant (nom des fichiers). </summary>
    public string Id { get; set; } = "";
    /// <summary> Texte lu. </summary>
    public string Text { get; set; } = "";
    /// <summary> Voix. </summary>
    public string Voice { get; set; } = "";
    /// <summary> Langue demandée. </summary>
    public string? Language { get; set; }
    /// <summary> Instructions de style données au modèle. </summary>
    public string? Instructions { get; set; }
    /// <summary> Modèle utilisé. </summary>
    public string Model { get; set; } = "";
    /// <summary> Nom du modèle exposé par l'API. </summary>
    public string ServedName { get; set; } = "";
    /// <summary> Date de génération. </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    /// <summary> Durée de l'audio (s). </summary>
    public double DurationSeconds { get; set; }
    /// <summary> Temps de génération (s). </summary>
    public double GenerationSeconds { get; set; }
    /// <summary> Fréquence d'échantillonnage (Hz). </summary>
    public int SampleRate { get; set; }
    /// <summary> Taille du fichier (octets). </summary>
    public long SizeBytes { get; set; }
}

/// <summary> Audios de synthèse vocale conservés sur le disque et lecture des voix / langues d'un modèle TTS. </summary>
public interface ITtsStore
{
    /// <summary> Dossier des audios. </summary>
    string Root { get; }

    /// <summary> Dossier des aperçus de voix. </summary>
    string PreviewDir { get; }

    /// <summary> Fichier WAV d'un audio. </summary>
    string AudioOf(string id);

    /// <summary> Audios enregistrés, du plus récent au plus ancien. </summary>
    List<TtsClip> LoadAll();

    /// <summary> Enregistre un audio et ses informations ; complète durée, fréquence et taille. </summary>
    TtsClip Save(TtsClip clip, byte[] wav);

    /// <summary> Supprime un audio et ses informations. </summary>
    void Delete(string id);

    /// <summary> Fichier d'aperçu d'une voix (même modèle + voix + langue = même fichier). </summary>
    string PreviewPath(string model, string voice, string? language);

    /// <summary> Nom de fichier proposé au téléchargement : début du texte + voix. </summary>
    string SuggestedFileName(TtsClip c);

    /// <summary> Le serveur fait-il de la synthèse vocale ? (vLLM-Omni + nom ou architecture du modèle) </summary>
    bool LooksLikeTts(LaunchConfig c);

    /// <summary> Voix prévues par le modèle (config.json), si le serveur ne répond pas. </summary>
    List<string> ConfigVoices(string modelTarget);

    /// <summary>
    /// Langues prévues par le modèle, au format attendu par l'API ("French", "English"…).
    /// Vide = le modèle ne prend pas de langue en paramètre.
    /// </summary>
    List<string> ConfigLanguages(string modelTarget);

    /// <summary> Phrase d'aperçu d'une voix, dans la langue choisie (sinon celle de l'interface). </summary>
    string PreviewSentence(string? language);
}

/// <summary> Implémentation de <see cref="ITtsStore"/>. </summary>
public sealed partial class TtsStore : ITtsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <inheritdoc />
    public string Root =>
        Path.Combine(AppPaths.DataDir, "tts");
    /// <inheritdoc />
    public string PreviewDir => Path.Combine(Root, "previews");

    /// <inheritdoc />
    public string AudioOf(string id) => Path.Combine(Root, id + ".wav");
    private string MetaOf(string id) => Path.Combine(Root, id + ".json");

    /// <inheritdoc />
    public List<TtsClip> LoadAll()
    {
        var res = new List<TtsClip>();
        if (!Directory.Exists(Root)) return res;
        foreach (var f in Directory.GetFiles(Root, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<TtsClip>(File.ReadAllText(f)) is { } c && File.Exists(AudioOf(c.Id))) res.Add(c);
            }
            catch { /* fichier abîmé : ignoré */ }
        }
        return res.OrderByDescending(c => c.CreatedAt).ToList();
    }

    /// <inheritdoc />
    public TtsClip Save(TtsClip clip, byte[] wav)
    {
        Directory.CreateDirectory(Root);
        clip.Id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var (duration, rate) = WavInfo(wav);
        clip.DurationSeconds = duration;
        clip.SampleRate = rate;
        clip.SizeBytes = wav.LongLength;
        File.WriteAllBytes(AudioOf(clip.Id), wav);
        File.WriteAllText(MetaOf(clip.Id), JsonSerializer.Serialize(clip, Json));
        return clip;
    }

    /// <inheritdoc />
    public void Delete(string id)
    {
        try { File.Delete(AudioOf(id)); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
        try { File.Delete(MetaOf(id)); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
    }

    /// <inheritdoc />
    public string PreviewPath(string model, string voice, string? language)
    {
        var key = SafeName($"{model}_{voice}_{language ?? "auto"}");
        return Path.Combine(PreviewDir, key + ".wav");
    }

    /// <inheritdoc />
    public string SuggestedFileName(TtsClip c)
    {
        var words = string.Join('_', c.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(6));
        var name = SafeName($"{words}_{c.Voice}");
        return (name.Length > 60 ? name[..60] : name) + ".wav";
    }

    [GeneratedRegex(@"[^\p{L}\p{N}_\-]+")]
    private static partial Regex UnsafeChars();

    private string SafeName(string s) => UnsafeChars().Replace(s, "_").Trim('_');

    /// <summary> Durée (s) et fréquence d'échantillonnage d'un fichier WAV (en-tête RIFF). </summary>
    /// <param name="wav"> Contenu complet du fichier WAV. </param>
    /// <returns> (0, 0) si l'en-tête est illisible. </returns>
    public static (double Duration, int SampleRate) WavInfo(byte[] wav)
    {
        try
        {
            if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return (0, 0);
            int pos = 12, rate = 0, byteRate = 0;
            while (pos + 8 <= wav.Length)
            {
                var id = Encoding.ASCII.GetString(wav, pos, 4);
                var size = BitConverter.ToUInt32(wav, pos + 4);
                if (id == "fmt ")
                {
                    rate = BitConverter.ToInt32(wav, pos + 12);
                    byteRate = BitConverter.ToInt32(wav, pos + 16);
                }
                else if (id == "data")
                {
                    // Flux sans taille connue (0 ou 0xFFFFFFFF) : jusqu'à la fin du fichier
                    long data = size == 0 || size == uint.MaxValue || pos + 8 + size > wav.Length ? wav.Length - pos - 8 : size;
                    return (byteRate > 0 ? data / (double)byteRate : 0, rate);
                }
                pos += 8 + (int)size + (int)(size & 1);
            }
            return (0, rate);
        }
        catch { return (0, 0); }
    }

    // ---------------------------------------------------------------- Détection / config.json

    [GeneratedRegex(@"tts|speech|voice|cosyvoice|voxcpm|higgs.?audio|fish.?(speech|audio|omni)|orpheus|kokoro|dia-|glm.?tts|spark", RegexOptions.IgnoreCase)]
    private static partial Regex TtsName();

    [GeneratedRegex(@"TTS|Speech|Code2Wav|CosyVoice|VoxCPM|Vocoder", RegexOptions.None)]
    private static partial Regex TtsArchitecture();

    /// <inheritdoc />
    public bool LooksLikeTts(LaunchConfig c)
    {
        if (!c.Omni) return false;
        if (TtsName().IsMatch(c.ModelRepo + " " + c.ModelTarget)) return true;
        return ReadConfig(c.ModelTarget) is { } cfg && cfg.RootElement.TryGetProperty("architectures", out var a)
               && a.ValueKind == JsonValueKind.Array && a.EnumerateArray().Any(x => TtsArchitecture().IsMatch(x.GetString() ?? ""));
    }

    private JsonDocument? ReadConfig(string modelTarget)
    {
        try
        {
            var dir = Directory.Exists(modelTarget) ? modelTarget : Path.GetDirectoryName(modelTarget);
            var f = dir == null ? null : Path.Combine(dir, "config.json");
            return f != null && File.Exists(f) ? JsonDocument.Parse(File.ReadAllText(f)) : null;
        }
        catch { return null; }
    }

    /// <summary> Cherche un objet (nom → id) ou une liste de noms sous l'une des clés, à n'importe quelle profondeur. </summary>
    private List<string> FindNames(JsonElement e, string[] keys, int depth = 0)
    {
        if (e.ValueKind != JsonValueKind.Object || depth > 4) return new();
        foreach (var p in e.EnumerateObject())
        {
            if (keys.Contains(p.Name))
            {
                if (p.Value.ValueKind == JsonValueKind.Object) return p.Value.EnumerateObject().Select(x => x.Name).ToList();
                if (p.Value.ValueKind == JsonValueKind.Array)
                    return p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList();
            }
        }
        foreach (var p in e.EnumerateObject())
            if (FindNames(p.Value, keys, depth + 1) is { Count: > 0 } r) return r;
        return new();
    }

    /// <inheritdoc />
    public List<string> ConfigVoices(string modelTarget)
    {
        using var cfg = ReadConfig(modelTarget);
        return cfg == null ? new() : FindNames(cfg.RootElement, ["spk_id", "speakers", "speaker_ids", "voices"]);
    }

    /// <inheritdoc />
    public List<string> ConfigLanguages(string modelTarget)
    {
        using var cfg = ReadConfig(modelTarget);
        if (cfg == null) return new();
        return FindNames(cfg.RootElement, ["codec_language_id", "languages", "supported_languages"])
            .Where(l => !l.Contains("dialect", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Length > 0 ? char.ToUpperInvariant(l[0]) + l[1..] : l)
            .Distinct().ToList();
    }

    /// <inheritdoc />
    public string PreviewSentence(string? language) => (language ?? "").ToLowerInvariant() switch
    {
        "french"     => "Bonjour ! Voici un aperçu de ma voix. J'espère qu'elle vous plaira.",
        "english"    => "Hello! This is a preview of my voice. I hope you like it.",
        "german"     => "Hallo! Das ist eine Hörprobe meiner Stimme. Ich hoffe, sie gefällt Ihnen.",
        "spanish"    => "¡Hola! Esta es una muestra de mi voz. Espero que te guste.",
        "italian"    => "Ciao! Questa è un'anteprima della mia voce. Spero che ti piaccia.",
        "portuguese" => "Olá! Esta é uma amostra da minha voz. Espero que goste.",
        "russian"    => "Привет! Это пример моего голоса. Надеюсь, он вам понравится.",
        "japanese"   => "こんにちは！これは私の声のサンプルです。気に入っていただけると嬉しいです。",
        "korean"     => "안녕하세요! 제 목소리 미리 듣기입니다. 마음에 드셨으면 좋겠어요.",
        "chinese"    => "你好！这是我的声音预览，希望你喜欢。",
        _ => Loc.T("Tts_PreviewSentence"),
    };
}
