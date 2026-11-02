/*
    Fichier      :  SdComponentService.cs
    Projet       :  AlpenMux

    Description  :
        Composants manquants d'un modèle d'image « transformer seul » (FLUX.2 klein 4B, format ComfyUI) lancé avec
        stable-diffusion.cpp : le VAE et l'encodeur de texte Qwen3. Téléchargement automatique depuis le dépôt
        Comfy-Org (FLUX.2 klein 4B uniquement : seul modèle dont les fichiers sont connus), rangé à part dans
        ~/.local/share/alpenmux/image-components/ pour ne pas apparaître dans la liste des modèles.
        Pour les autres modèles, le VAE et l'encodeur se choisissent à la main sur la page Serveurs.
*/

using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Fichiers VAE et encodeur de texte prêts à être passés à stable-diffusion.cpp. </summary>
/// <param name="Vae"> Chemin du VAE. </param>
/// <param name="Llm"> Chemin de l'encodeur de texte (--llm). </param>
public sealed record SdComponentFiles(string Vae, string Llm);

/// <summary> Téléchargement des composants manquants d'un modèle d'image lancé avec stable-diffusion.cpp. </summary>
public interface ISdComponentService
{
    /// <summary> Le modèle (nom de fichier) a des composants téléchargeables automatiquement. </summary>
    /// <param name="modelFile"> Chemin ou nom du fichier unique du transformer. </param>
    bool CanDownload(string? modelFile);

    /// <summary> Télécharge les composants s'ils ne sont pas déjà là. Retourne leurs chemins, ou null en cas d'échec. </summary>
    /// <param name="progress"> Suivi affiché sur la page Serveurs. </param>
    /// <param name="log"> Journal du téléchargement. </param>
    /// <param name="ct"> Annulation. </param>
    Task<SdComponentFiles?> DownloadAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="ISdComponentService"/>. </summary>
public sealed class SdComponentService : ISdComponentService
{
    // Fichiers repris du dépôt Comfy-Org (VAE Flux 2 et encodeur Qwen3 4B), tels que ComfyUI les utilise
    private const string Repo = "Comfy-Org/vae-text-encorder-for-flux-klein-4b";
    private const string VaeRel = "split_files/vae/flux2-vae.safetensors";
    private const string LlmRel = "split_files/text_encoders/qwen_3_4b.safetensors";
    private static readonly Regex KleinFourB = new(@"klein[-_. ]?4b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly IHfService _hf;
    private readonly string _dir;

    /// <summary> Crée le service. </summary>
    /// <param name="hf"> Téléchargement Hugging Face (jeton inclus). </param>
    public SdComponentService(IHfService hf)
    {
        ArgumentNullException.ThrowIfNull(hf);
        _hf = hf;
        _dir = Path.Combine(AppPaths.DataDir, "image-components", "flux2-klein-4b");
    }

    /// <inheritdoc />
    public bool CanDownload(string? modelFile) =>
        modelFile != null && KleinFourB.IsMatch(Path.GetFileName(modelFile));

    /// <inheritdoc />
    public async Task<SdComponentFiles?> DownloadAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        var vae = Path.Combine(_dir, VaeRel);
        var llm = Path.Combine(_dir, LlmRel);
        if (File.Exists(vae) && File.Exists(llm)) return new SdComponentFiles(vae, llm);

        // Seuls les deux fichiers voulus sont téléchargés (le reste du dépôt est ignoré)
        var ok = await _hf.DownloadAsync(Repo, _dir, $"{VaeRel} {LlmRel}", null, null, progress, log, ct);
        return ok && File.Exists(vae) && File.Exists(llm) ? new SdComponentFiles(vae, llm) : null;
    }
}
