/*
    Fichier      :  ModelFit.cs
    Projet       :  AlpenMux

    Description  :
        Verdict « tient en VRAM ? » d'un modèle (carte de la page Modèles, aperçu avant téléchargement).
        Même calcul que le lancement (ArgsPlanner) : choix des GPU, déchargement en RAM, plan de contexte.
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace AlpenMux.Services;

/// <summary> Verdict affiché pour un modèle, avec le plan de lancement qui le fonde. </summary>
/// <param name="Text"> Libellé du verdict. </param>
/// <param name="Class"> Classe de style : ok, warn ou error. </param>
/// <param name="Icon"> Icône du verdict. </param>
/// <param name="Budget"> VRAM retenue (null sans GPU). </param>
/// <param name="TensorParallel"> Nombre de GPU retenus. </param>
/// <param name="Offload"> Encodeurs de texte en RAM nécessaires. </param>
/// <param name="Plan"> Plan calculé avec ces réglages. </param>
public sealed record ModelFitResult(string Text, string Class, string Icon, GpuBudget? Budget, int TensorParallel, bool Offload, PlannedArgs Plan);

/// <summary> Fonctions pures du verdict d'un modèle. </summary>
public static class ModelFit
{
    /// <summary> Verdict pour ce modèle et les GPU actuels. </summary>
    /// <param name="model"> Modèle (local ou aperçu d'un dépôt distant). </param>
    /// <param name="gpus"> GPU actuels (VRAM libre). </param>
    /// <param name="contextCap"> Plafond du contexte automatique (réglages). </param>
    public static ModelFitResult Describe(ModelInfo model, IReadOnlyList<GpuSample> gpus, int contextCap)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(gpus);
        // Multi-GPU : estimation sur le plus petit nombre de GPU où le modèle tient
        var (ids, tp) = ArgsPlanner.AutoSelectGpus(model, gpus, contextCap);
        var budget = ArgsPlanner.Budget(gpus, string.Join(',', ids));
        // Pipeline d'image trop gros : estimation avec ses encodeurs de texte en RAM, réglage proposé au lancement
        var offload = ArgsPlanner.NeedsCpuOffload(model, budget, contextCap);
        var plan = ArgsPlanner.Plan(model, budget, contextCap, 1.0, tp, cpuOffload: offload);

        var (text, cls, icon) = model.IsBroken
            ? (model.BrokenFiles.Count > 0 ? Loc.T("Model_Broken") : model.Problem!, "error", Icons.XCircle)
            : budget == null ? (Loc.T("Model_NoGpu"), "muted", Icons.Info)
            : !plan.Fits ? (Loc.F("Model_DoesNotFit", Format.Bytes(gpus.Max(g => g.MemFree))), "error", Icons.XCircle)
            : offload ? (Loc.T("Model_FitsWithOffload"), "warn", Icons.Warning)
            : tp > 1 ? (Loc.F("Model_MultiGpu", tp), plan.Tight ? "warn" : "ok", Icons.CheckCircle)
            : plan.Tight ? (Loc.T("Model_Tight"), "warn", Icons.Warning)
            : (Loc.T("Model_Comfortable"), "ok", Icons.CheckCircle);
        return new ModelFitResult(text, cls, icon, budget, tp, offload, plan);
    }
}
