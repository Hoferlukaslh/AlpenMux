/*
    Fichier      :  VramUsage.cs
    Projet       :  AlpenMux

    Description  :
        Répartition de la VRAM déjà occupée sur un GPU, pour les barres de VRAM (Accueil, Serveurs) :
        autres applications, puis un segment par serveur lancé par l'application (ses processus GPU
        sont retrouvés par leur session), puis la VRAM libre.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AlpenMux.Controls;

namespace AlpenMux.Services;

/// <summary> Répartition de la VRAM affichée par les barres (autres applications, serveurs, libre). </summary>
public static class VramUsage
{
    /// <summary> Couleur du thème actuel (VramOther, VramServer…). </summary>
    public static IBrush Brush(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    /// <summary> Parts déjà occupées sur ce GPU : autres applications, puis chaque serveur de l'application. </summary>
    /// <param name="appState"> Serveurs de l'application (pour attribuer chaque processus GPU à son serveur). </param>
    /// <param name="sample"> Dernière mesure du système. </param>
    /// <param name="gpu"> GPU concerné. </param>
    /// <param name="serversBytes"> Reçoit la mémoire occupée par les serveurs de l'application. </param>
    public static List<VramSegment> Occupied(IAppState appState, SystemSample sample, GpuSample gpu, out double serversBytes)
    {
        var running = new List<VramSegment>();
        serversBytes = 0;
        foreach (var grp in sample.GpuProcesses.Where(p => p.GpuIndex == gpu.Index)
                     .GroupBy(p => appState.InstanceOfProcess(p.Pid)).Where(g => g.Key != null))
        {
            var bytes = grp.Sum(p => p.Memory);
            serversBytes += bytes;
            running.Add(new VramSegment(grp.Key!.Title, grp.Key.ServedName, bytes, Brush("VramServer"), false, Loc.T("Vram_RunningServer")));
        }
        var segs = new List<VramSegment>
        {
            new(Loc.T("Vram_Others"), Loc.T("Vram_OthersShort"), Math.Max(0, gpu.MemUsed - serversBytes), Brush("VramOther"), false),
        };
        segs.AddRange(running);
        return segs;
    }

    /// <summary> Segment de la VRAM libre (pour la légende). </summary>
    public static VramSegment Free(double bytes, string labelKey = "Vram_FreeNow") =>
        new(Loc.T(labelKey), Loc.T("Vram_FreeShort"), bytes, Brush("VramFree"), false);
}
