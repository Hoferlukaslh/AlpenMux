/*
    Fichier      :  OmniDeploy.cs
    Projet       :  AlpenMux

    Description  :
        vLLM-Omni découpe certains modèles en plusieurs "étapes" (ex. Qwen3-TTS : étape 0 = talker,
        étape 1 = décodeur audio), chacune avec sa propre part de VRAM, fixée dans
        vllm_omni/deploy/<model_type>.yaml. Ces valeurs sont prévues pour de gros GPU
        (0,3 + 0,3 de la VRAM) : sur une carte de 12 Gio, l'étape 0 n'a pas la place de charger
        ses poids ET son cache KV ("No available memory for the cache blocks").

        Ce fichier lit les étapes et leurs parts (en suivant base_config) ; la page Serveurs
        répartit ensuite la VRAM choisie pour le serveur entre les étapes via --stage-overrides :
        dans les mêmes proportions, sauf si l'étape principale n'a alors pas assez pour ses
        poids et un minimum de cache KV (elle est servie en priorité).
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AlpenMux.Services;

/// <summary> Étape d'un modèle vLLM-Omni. </summary>
/// <param name="Id"> Numéro de l'étape. </param>
/// <param name="GpuMemoryUtilization"> Part de la VRAM par défaut. </param>
/// <param name="Devices"> GPU utilisés. </param>
public sealed record OmniStage(int Id, double GpuMemoryUtilization, string Devices);

/// <summary> Étapes d'un modèle vLLM-Omni (fichiers de déploiement du paquet installé). </summary>
public interface IOmniDeploy
{
    /// <summary> Étapes du modèle (vide si vLLM-Omni absent ou configuration introuvable). </summary>
    List<OmniStage> Stages(string? modelType);
}

/// <summary> Implémentation de <see cref="IOmniDeploy"/>. </summary>
public sealed partial class OmniDeploy : IOmniDeploy
{
    private readonly ISettingsService _settings;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    public OmniDeploy(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }
    [GeneratedRegex(@"^(\s*)-\s*stage_id:\s*(\d+)")]
    private static partial Regex StageStart();

    [GeneratedRegex(@"^\s*gpu_memory_utilization:\s*([\d.]+)")]
    private static partial Regex UtilLine();

    [GeneratedRegex(@"^\s*devices:\s*[""']?([^""'#\s]+)")]
    private static partial Regex DevicesLine();

    [GeneratedRegex(@"^base_config:\s*([^\s#]+)")]
    private static partial Regex BaseLine();

    private string? DeployDir()
    {
        var lib = LinuxHost.Local(LinuxHost.Combine(_settings.VenvPath, "lib"));   // \\wsl.localhost\… sous Windows
        if (!Directory.Exists(lib)) return null;
        return Directory.GetDirectories(lib, "python*")
            .Select(p => Path.Combine(p, "site-packages", "vllm_omni", "deploy")).FirstOrDefault(Directory.Exists);
    }

    /// <inheritdoc />
    public List<OmniStage> Stages(string? modelType)
    {
        if (string.IsNullOrWhiteSpace(modelType) || DeployDir() is not { } dir) return new();
        var file = Path.Combine(dir, modelType + ".yaml");
        return File.Exists(file) ? Parse(file, dir, 0) : new();
    }

    /// <summary> Lecture minimale du YAML : seules les étapes de premier niveau nous intéressent. </summary>
    private static List<OmniStage> Parse(string file, string dir, int depth)
    {
        var lines = File.ReadAllLines(file);
        var stages = new Dictionary<int, (double? Util, string? Devices)>();

        // Configuration de base d'abord (les valeurs du fichier courant la complètent)
        foreach (var l in lines)
            if (BaseLine().Match(l) is { Success: true } b && depth < 5 && File.Exists(Path.Combine(dir, b.Groups[1].Value)))
                foreach (var s in Parse(Path.Combine(dir, b.Groups[1].Value), dir, depth + 1))
                    stages[s.Id] = (s.GpuMemoryUtilization, s.Devices);

        bool inStages = false;
        int? current = null;
        int itemIndent = -1;
        foreach (var raw in lines)
        {
            var line = raw.Split('#')[0];
            if (line.Trim().Length == 0) continue;
            var indent = line.Length - line.TrimStart().Length;
            if (indent == 0) { inStages = line.StartsWith("stages:"); current = null; continue; }
            if (!inStages) continue;

            if (StageStart().Match(line) is { Success: true } st)
            {
                var ind = st.Groups[1].Value.Length;
                if (itemIndent < 0) itemIndent = ind;
                if (ind != itemIndent) { current = null; continue; }   // étapes imbriquées (plateformes)
                current = int.Parse(st.Groups[2].Value);
                if (!stages.ContainsKey(current.Value)) stages[current.Value] = (null, null);
                continue;
            }
            if (current is not { } id || indent > itemIndent + 2) continue;
            if (UtilLine().Match(line) is { Success: true } u)
                stages[id] = (double.Parse(u.Groups[1].Value, CultureInfo.InvariantCulture), stages[id].Devices);
            else if (DevicesLine().Match(line) is { Success: true } d)
                stages[id] = (stages[id].Util, d.Groups[1].Value);
        }

        return stages.OrderBy(kv => kv.Key)
            .Select(kv => new OmniStage(kv.Key, kv.Value.Util ?? 0.9, kv.Value.Devices ?? "0"))
            .ToList();
    }

    /// <summary> Part minimale laissée à une étape secondaire (décodeur audio, etc.) quand l'étape principale manque de place. </summary>
    public const double SecondaryFloor = 0.08;   // ≈ 1 Gio sur 12 Gio : le décodeur audio de Qwen3-TTS en utilise ≈ 0,3

    /// <summary> Contexte de l'étape principale prévu par vLLM-Omni (max_model_len des fichiers deploy). </summary>
    public const int MainStageContext = 4096;

    /// <summary>
    /// Besoin de l'étape principale : poids + surcoût + cache KV d'une requête de MainStageContext tokens
    /// (vLLM refuse de démarrer en dessous). Taille par token inconnue : 0,5 Gio.
    /// </summary>
    public static double MainNeedBytes(ModelInfo m)
    {
        // L'étape principale charge aussi les sous-modèles (ex. Qwen3-TTS : 3,6 + 0,6 Gio = 4,1 Gio mesurés)
        var weights = m.WeightsBytes + m.SubModelBytes;
        return weights + ArgsPlanner.OverheadBytes(weights, m.VocabSize)
               + (m.Kv is { } kv ? kv.Bytes(MainStageContext) : 0.5 * ArgsPlanner.Gib);
    }

    /// <summary>
    /// Répartit la part de VRAM "total" entre les étapes qui partagent un même GPU.
    /// Sans "mainMin" : proportionnellement aux valeurs prévues par vLLM-Omni.
    /// Avec "mainMin" (besoin connu du modèle principal) : chaque autre étape reçoit sa part
    /// proportionnelle, plafonnée à SecondaryFloor, et l'étape principale reçoit tout le reste.
    /// Les valeurs d'origine (0,3 + 0,3) donnent autant au petit décodeur qu'au modèle principal :
    /// la VRAM ajoutée partait au décodeur (0,3 Gio utilisés) au lieu du cache KV du modèle.
    /// vLLM-Omni plafonne de toute façon une étape secondaire à la VRAM libre si elle en manque.
    /// </summary>
    public static Dictionary<string, double> Split(IReadOnlyDictionary<string, double>? weights, IReadOnlyDictionary<string, string>? devices,
                                                   double total, double? mainMin)
    {
        var res = new Dictionary<string, double>();
        if (weights == null || weights.Count == 0) return res;
        static double Floor2(double v) => Math.Floor(v * 100 + 1e-9) / 100;
        foreach (var group in weights.GroupBy(w => devices?.GetValueOrDefault(w.Key) ?? "0"))
        {
            var sum = group.Sum(w => w.Value);
            if (sum <= 0) continue;
            var share = group.ToDictionary(w => w.Key, w => total * w.Value / sum);

            var main = group.OrderByDescending(w => w.Value).ThenBy(w => int.Parse(w.Key)).First().Key;
            if (mainMin != null && share.Count > 1)
            {
                // Priorité au modèle principal : les autres étapes gardent un minimum, il prend le reste
                foreach (var k in share.Keys.Where(k => k != main).ToList())
                    share[k] = Math.Min(share[k], SecondaryFloor);
                share[main] = total - share.Where(kv => kv.Key != main).Sum(kv => kv.Value);
            }
            foreach (var (id, v) in share) res[id] = Floor2(v);
        }
        return res;
    }

    /// <summary> JSON de --stage-overrides pour une répartition calculée par Split. </summary>
    public static string? BuildOverrides(IReadOnlyDictionary<string, double> split)
    {
        if (split.Count == 0) return null;
        var parts = split.OrderBy(kv => int.Parse(kv.Key))
            .Select(kv => $"\"{kv.Key}\": {{\"gpu_memory_utilization\": {kv.Value.ToString("0.00", CultureInfo.InvariantCulture)}}}");
        return "{" + string.Join(", ", parts) + "}";
    }
}
