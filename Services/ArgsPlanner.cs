/*
    Fichier      :  ArgsPlanner.cs
    Projet       :  AlpenMux

    Description  :
        Calcul automatique des arguments vLLM à partir du GPU et de la config du modèle
        (reprend les heuristiques de start_vLLM.py) :
            - --gpu-memory-utilization : VRAM libre (moins une marge) / VRAM utilisable
            - --max-model-len          : ce que le cache KV permet dans le budget restant (fenêtre glissante comprise),
                                         plafonné par le réglage « Contexte automatique maximal » (32 768 par défaut)
            - --kv-cache-dtype fp8 + --enforce-eager si le modèle remplit > 80 % de la VRAM
        Le paramètre "share" (0..1) permet de ne prendre qu'une partie de la VRAM libre,
        pour lancer plusieurs modèles sur le même GPU.
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace AlpenMux.Services;

/// <summary> VRAM disponible pour un lancement. </summary>
/// <param name="Name"> Nom du ou des GPU. </param>
/// <param name="Total"> VRAM totale par GPU (octets). </param>
/// <param name="Free"> VRAM libre par GPU (octets). </param>
/// <param name="Count"> Nombre de GPU. </param>
public sealed record GpuBudget(string Name, double Total, double Free, int Count)
{
    /// <summary> PyTorch voit ~3 % de VRAM de moins que nvidia-smi. </summary>
    public double Usable => Total * ArgsPlanner.UsableFrac;
}

/// <summary> Arguments calculés automatiquement pour un modèle. </summary>
/// <param name="GpuMemoryUtilization"> Valeur de --gpu-memory-utilization. </param>
/// <param name="MaxModelLen"> Contexte retenu (--max-model-len). </param>
/// <param name="Fp8KvCache"> Cache KV en FP8 conseillé. </param>
/// <param name="EnforceEager"> Graphes CUDA désactivés pour gagner de la VRAM. </param>
/// <param name="EstimatedBytes"> Mémoire estimée par GPU (octets). </param>
/// <param name="Ratio"> Part de la VRAM utilisable occupée. </param>
/// <param name="Fits"> Le modèle tient. </param>
/// <param name="Tight"> Il tient de justesse. </param>
/// <param name="Explanation"> Explication affichée. </param>
public sealed record PlannedArgs(
    double GpuMemoryUtilization, int MaxModelLen, bool Fp8KvCache, bool EnforceEager,
    double EstimatedBytes, double Ratio, bool Fits, bool Tight, string Explanation);

/// <summary> Calcul automatique des arguments de lancement (VRAM, contexte, GPU) selon le modèle et le matériel. </summary>
public static class ArgsPlanner
{
    /// <summary> Un gibioctet, en octets. </summary>
    public const double Gib = 1024d * 1024 * 1024;
    /// <summary> Plafond du surcoût fixe en Gio (contexte CUDA, activations, graphes CUDA). </summary>
    public const double OverheadGib   = 1.5;   // plafond du surcoût (contexte CUDA + activations + graphes CUDA)

    /// <summary>
    /// Surcoût en plus des poids : contexte CUDA, activations, graphes CUDA. Il grandit avec le modèle :
    /// ≈ 0,4 Gio + 8 % des poids, entre 0,5 et 1,5 Gio (mesuré : Qwen3-TTS 1,7B = 4,1 Gio de poids
    /// → ≈ 0,35 Gio de surcoût réel ; un forfait de 1,5 Gio faisait refuser des configurations qui marchent).
    /// </summary>
    public static double OverheadBytes(double weightsBytes, int? vocabSize = null) =>
        Math.Clamp(0.4 + 0.08 * weightsBytes / Gib, 0.5, OverheadGib) * Gib + VocabBytes(vocabSize);

    /// <summary>
    /// Logits et échantillonnage pendant le profilage de vLLM : proportionnels au vocabulaire (≈ 4 Kio par entrée
    /// au-delà de 32 k). Mesuré : Gemma 4 12B (262 144 entrées) ≈ 2,1 Gio de surcoût au lieu de ≈ 1 Gio.
    /// </summary>
    public static double VocabBytes(int? vocabSize) => vocabSize is { } v && v > 32768 ? (v - 32768) * 4096.0 : 0;
    /// <summary> Part maximale de la VRAM libre utilisée. </summary>
    public const double UsableFrac    = 0.97;
    /// <summary> Marge laissée libre sur le GPU (Gio). </summary>
    public const double SafetyGib     = 0.3;   // marge laissée libre sur le GPU

    /// <summary>
    /// Contexte CUDA du processus vLLM qui démarre : nvidia-smi le compte encore comme libre, vLLM non
    /// (mesuré sur RTX 5070 : 11,29 Gio libres pour nvidia-smi, 10,75 pour vLLM). Sans cette marge, vLLM refuse
    /// de démarrer (« Free memory on device … is less than desired GPU memory utilization »).
    /// </summary>
    public const double CudaContextGib = 0.55;

    /// <summary> VRAM qu'un nouveau serveur vLLM peut réserver sur ce GPU. </summary>
    public static double LaunchableBytes(double freeBytes) => Math.Max(0, freeBytes - (SafetyGib + CudaContextGib) * Gib);
    /// <summary> Coefficient appliqué à la taille des poids. </summary>
    public const double WeightMargin  = 1.0;
    /// <summary> Au-delà de cette part de la VRAM, le lancement est jugé « juste ». </summary>
    public const double TightRatio    = 0.80;
    /// <summary> Valeur maximale de --gpu-memory-utilization. </summary>
    public const double MaxUtil       = 0.95;
    /// <summary> Plafond de contexte proposé par défaut (jetons). </summary>
    public const int    DefaultContextCap = 32768;

    /// <summary> Plafond du contexte calculé automatiquement (Paramètres ; 0 = aucun, le maximum du modèle). </summary>
    /// <param name="settings"> Réglages de l'utilisateur. </param>
    /// <returns> Le plafond en jetons, ou <see cref="int.MaxValue"/> sans plafond. </returns>
    public static int ContextCapOf(AppSettings settings) => settings.AutoContextCap is > 0 and var c ? c : int.MaxValue;

    /// <summary> Budget VRAM des GPU sélectionnés (tous si gpuIds est vide). </summary>
    public static GpuBudget? Budget(IReadOnlyList<GpuSample> gpus, string? gpuIds)
    {
        var sel = gpus;
        if (!string.IsNullOrWhiteSpace(gpuIds))
        {
            var ids = gpuIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var i) ? i : -1).ToHashSet();
            sel = gpus.Where(g => ids.Contains(g.Index)).ToList();
        }
        if (sel.Count == 0) return null;
        // vLLM répartit le modèle sur les GPU (tensor parallel) : on raisonne sur le plus petit
        var min = sel.MinBy(g => g.MemFree)!;
        return new GpuBudget(min.Name, sel.Min(g => g.MemTotal), min.MemFree, sel.Count);
    }

    /// <summary>
    /// Choix automatique des GPU : le plus petit nombre de GPU (les plus libres d'abord) sur lequel
    /// le modèle tient. Le tensor parallel doit diviser le nombre de têtes d'attention.
    /// </summary>
    public static (List<int> Ids, int TensorParallel) AutoSelectGpus(ModelInfo m, IReadOnlyList<GpuSample> gpus, int contextCap, double share = 1.0)
    {
        if (gpus.Count <= 1) return (gpus.Select(g => g.Index).ToList(), 1);
        var byFree = gpus.OrderByDescending(g => g.MemFree).ToList();
        bool Valid(int n) => m.AttentionHeads is not { } h || h % n == 0;

        for (int n = 1; n <= byFree.Count; n++)
        {
            if (!Valid(n)) continue;
            var sel = byFree.Take(n).ToList();
            var ids = string.Join(',', sel.Select(g => g.Index));
            if (Plan(m, Budget(gpus, ids), contextCap, share, n).Fits)
                return (sel.Select(g => g.Index).OrderBy(i => i).ToList(), n);
        }
        // Ne tient nulle part : le plus grand nombre de GPU utilisable
        var max = Enumerable.Range(1, byFree.Count).Where(Valid).DefaultIfEmpty(1).Max();
        return (byFree.Take(max).Select(g => g.Index).OrderBy(i => i).ToList(), max);
    }

    /// <summary> Calcule les arguments pour ce modèle et ce budget de VRAM. </summary>
    /// <param name="m"> Modèle. </param>
    /// <param name="gpu"> VRAM disponible (null = aucun GPU). </param>
    /// <param name="contextCap"> Plafond du contexte automatique (voir ContextCapOf). </param>
    /// <param name="share"> Part de la VRAM libre à utiliser. </param>
    /// <param name="tensorParallel"> Nombre de GPU en parallélisme de tenseurs. </param>
    /// <param name="textOnly"> Modèle multimodal lancé en texte seul. </param>
    /// <param name="cpuOffload"> Encodeurs de texte en RAM. </param>
    /// <param name="gpuFraction"> Part du modèle sur le GPU (le reste en RAM). </param>
    public static PlannedArgs Plan(ModelInfo m, GpuBudget? gpu, int contextCap, double share = 1.0, int tensorParallel = 1, bool textOnly = false,
                                   bool cpuOffload = false, double gpuFraction = 1.0)
    {
        tensorParallel = Math.Max(1, tensorParallel);
        // Poids répartis sur les GPU, surcoût fixe sur chacun ; en mode texte seul, l'encodeur vision / audio n'est pas chargé ;
        // gpuFraction < 1 : le reste des poids est en RAM (--cpu-offload-gb)
        var weights = LoadedWeights(m, textOnly, cpuOffload) * Math.Clamp(gpuFraction, 0.05, 1.0);
        var baseBytes = weights * WeightMargin / tensorParallel + OverheadBytes(weights / tensorParallel, m.VocabSize);

        if (gpu == null)
            return new PlannedArgs(0.90, 4096, false, false, baseBytes, 0, true, false, Loc.T("Plan_NoGpu"));

        var free = LaunchableBytes(gpu.Free) * Math.Clamp(share, 0.05, 1.0);
        var ratio = baseBytes / gpu.Usable;
        var fits  = baseBytes <= free;
        var tight = ratio > TightRatio;

        // Avec plusieurs instances, l'utilisation demandée ne doit pas dépasser la part de VRAM libre
        var util = Math.Clamp(free / gpu.Usable, share < 1.0 ? 0.05 : 0.30, MaxUtil);
        util = Math.Floor(util * 100) / 100;

        int kvBytes = tight ? 1 : 2;
        int ctx = 4096;
        int? uncapped = null;
        if (m.Kv is { } kvInfo)
        {
            var budget = util * gpu.Usable - baseBytes;
            var kv     = kvInfo.Scale(kvBytes / 2.0 / tensorParallel);
            var fit    = budget > 0 ? kv.MaxTokens(budget) / 256 * 256 : 512;
            var modelMax = m.MaxPositions ?? DefaultContextCap;
            var cap = contextCap;
            ctx = Math.Max(512, Math.Min(fit, Math.Min(modelMax, cap)));
            if (Math.Min(fit, modelMax) > cap) uncapped = Math.Min(fit, modelMax);
        }
        else if (tight) ctx = 2048;

        string expl;
        if (!fits) expl = Loc.F("Plan_DoesNotFit", Format.Bytes(baseBytes), Format.Bytes(free));
        else if (tight) expl = Loc.T("Plan_Tight");
        else expl = Loc.T("Plan_Comfortable");

        if (uncapped is { } u && fits) expl += " " + Loc.F("Plan_ContextCapped", ctx.ToString("N0"), u.ToString("N0"));

        return new PlannedArgs(util, ctx, tight, tight, baseBytes, ratio, fits, tight, expl);
    }

    /// <summary>
    /// Poids chargés en VRAM : sans l'encodeur vision / audio en mode texte seul, sans les encodeurs de texte
    /// d'un pipeline d'image gardés en RAM (--enable-cpu-offload).
    /// </summary>
    public static double LoadedWeights(ModelInfo m, bool textOnly, bool cpuOffload = false) =>
        cpuOffload && m.TextEncoderBytes > 0 ? Math.Max(0, m.WeightsBytes - m.TextEncoderBytes)
        : textOnly && m.IsMultimodal ? Math.Max(0, m.WeightsBytes - m.MultimodalBytes) : m.WeightsBytes;

    /// <summary> Pipeline d'image qui ne tient sur le GPU qu'avec ses encodeurs de texte en RAM. </summary>
    public static bool NeedsCpuOffload(ModelInfo m, GpuBudget? gpu, int contextCap) =>
        m.TextEncoderBytes > 0 && gpu != null && !Plan(m, gpu, contextCap).Fits && Plan(m, gpu, contextCap, cpuOffload: true).Fits;
}
