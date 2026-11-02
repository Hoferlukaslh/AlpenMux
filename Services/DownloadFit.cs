/*
    Fichier      :  DownloadFit.cs
    Projet       :  AlpenMux

    Description  :
        Verdict « tiendra en VRAM ? » calculé avant le téléchargement, à partir de la taille des fichiers
        choisis (le modèle n'est pas encore sur le disque, donc pas de config.json à lire).
        Estimation grossière : poids × 1,08 + 1,5 Gio (cache KV, contexte CUDA, activations).
        Multi-GPU : les VRAM des GPU sont additionnées (tensor parallel répartit les poids).
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace AlpenMux.Services;

/// <summary> Verdict avant téléchargement. </summary>
/// <param name="Level"> ok (devrait tenir), warn (tiendra si la VRAM libre augmente) ou error (ne tiendra pas). </param>
/// <param name="NeededBytes"> VRAM estimée nécessaire (octets). </param>
/// <param name="FreeBytes"> VRAM libre (octets). </param>
/// <param name="TotalBytes"> VRAM totale (octets). </param>
public sealed record DownloadFitVerdict(string Level, double NeededBytes, double FreeBytes, double TotalBytes);

/// <summary> Fonctions pures du verdict de téléchargement (voir la description du fichier). </summary>
public static class DownloadFit
{
    /// <summary> Marge sur les poids (cache, petites tables). </summary>
    public const double WeightFactor = 1.08;

    /// <summary> Surcoût fixe (contexte CUDA, activations, cache KV minimal). </summary>
    public const double OverheadBytes = 1.5 * (1L << 30);

    /// <summary> Verdict pour des poids de taille donnée ; null si aucun GPU n'est détecté. </summary>
    /// <param name="weightBytes"> Taille des fichiers à charger (octets). </param>
    /// <param name="gpus"> GPU actuels. </param>
    public static DownloadFitVerdict? Evaluate(long weightBytes, IReadOnlyList<GpuSample> gpus)
    {
        ArgumentNullException.ThrowIfNull(gpus);
        if (gpus.Count == 0) return null;
        var needed = weightBytes * WeightFactor + OverheadBytes;
        var free = gpus.Sum(g => g.MemFree);
        var total = gpus.Sum(g => g.MemTotal);
        var level = needed <= free * ArgsPlanner.UsableFrac ? "ok"
            : needed <= total * ArgsPlanner.UsableFrac ? "warn"
            : "error";
        return new DownloadFitVerdict(level, needed, free, total);
    }
}
