/*
    Fichier      :  StableDiffusionCppService.cs
    Projet       :  AlpenMux

    Description  :
        stable-diffusion.cpp (sd-server) : génère des images en lisant directement les fichiers uniques
        (format ComfyUI / Automatic1111 : SD 1.x / 2.x, SDXL…), sans conversion au format diffusers,
        en gardant leur quantification. API compatible OpenAI (/v1/images/generations) et Automatic1111
        (/sdapi/v1/txt2img).

        Installation sans droits administrateur : binaire officiel précompilé des publications GitHub de
        leejet/stable-diffusion.cpp, extrait dans
            ~/.local/share/alpenmux/stable-diffusion.cpp/current      (+ fichier VERSION)
        Variante : Linux → Vulkan (pas de binaire CUDA publié pour Linux ; Vulkan fonctionne sur les cartes NVIDIA
        avec leur pilote), ROCm pour AMD ; Windows → CUDA 12 (+ archive cudart) pour NVIDIA, sinon Vulkan.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Installation et mise à jour de stable-diffusion.cpp (binaire officiel adapté au GPU). </summary>
public interface IStableDiffusionCppService
{
    /// <summary> Dernière version publiée et archive(s) à télécharger pour cette machine. </summary>
    Task<BinaryRelease?> LatestAsync(CancellationToken ct = default);

    /// <summary> Télécharge et installe (ou met à jour) stable-diffusion.cpp. Retourne vrai si sd-server est en place. </summary>
    Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IStableDiffusionCppService"/>. </summary>
public sealed class StableDiffusionCppService : IStableDiffusionCppService
{
    private readonly ISystemMonitorService _monitor;

    /// <summary> Crée le service. </summary>
    /// <param name="monitor"> Présence d'un GPU NVIDIA (choix de la variante). </param>
    public StableDiffusionCppService(ISystemMonitorService monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
    }
    // Une publication par compilation (master-1028-37bc88b) : on prend la plus récente qui contient l'archive voulue
    private const string ReleasesApi = "https://api.github.com/repos/leejet/stable-diffusion.cpp/releases?per_page=10";
    private static readonly HttpClient Http = PrebuiltBinaries.CreateGitHubClient();

    /// <summary> Dossier de stable-diffusion.cpp dans les données de l'application. </summary>
    public static string Root => Path.Combine(AppPaths.DataDir, "stable-diffusion.cpp");
    /// <summary> Version installée en service. </summary>
    public static string CurrentDir => PrebuiltBinaries.CurrentDirOf(Root);
    private static string ServerName => OperatingSystem.IsWindows() ? "sd-server.exe" : "sd-server";

    /// <summary> Chemin de sd-server (null = non installé). </summary>
    public static string? ServerExe => PrebuiltBinaries.FindServer(CurrentDir, ServerName);

    /// <summary> sd-server est installé. </summary>
    public static bool Installed => ServerExe != null;

    /// <summary> Version installée (nom de la publication), ou null. </summary>
    public static string? InstalledVersion => PrebuiltBinaries.InstalledVersion(CurrentDir);

    /// <summary> Dossier des bibliothèques de l'archive (LD_LIBRARY_PATH sous Linux). </summary>
    public static string LibraryPath()
    {
        var dirs = new List<string>();
        if (ServerExe is { } exe) dirs.Add(Path.GetDirectoryName(exe)!);
        var current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(current)) dirs.Add(current);
        return string.Join(':', dirs);
    }

    /// <summary> L'archive voulue pour cette machine. </summary>
    private bool Matches(string name, out bool cuda)
    {
        cuda = false;
        if (!name.StartsWith("sd-") || !name.EndsWith(".zip")) return false;
        if (OperatingSystem.IsWindows())
        {
            if (_monitor.HasNvidia) { cuda = true; return name.Contains("-bin-win-cuda12-x64"); }
            return name.Contains("-bin-win-vulkan-x64");
        }
        if (!name.Contains("-bin-Linux-") || !name.Contains("x86_64")) return false;
        if (!_monitor.HasNvidia && Directory.Exists("/sys/module/amdgpu")) return name.Contains("-rocm-");
        return name.EndsWith("-vulkan.zip");
    }

    /// <inheritdoc />
    public async Task<BinaryRelease?> LatestAsync(CancellationToken ct = default)
    {
        if (await PrebuiltBinaries.ReleasesAsync(Http, ReleasesApi, ct) is not { } releases) return null;
        foreach (var release in releases)   // du plus récent au plus ancien
        {
            var cuda = false;
            var main = release.Assets.FirstOrDefault(a => Matches(a.Name, out cuda));
            if (main == null) continue;
            // Windows + CUDA : les DLL CUDA ne sont pas installées en général
            var rt = cuda ? release.Assets.FirstOrDefault(a => a.Name.StartsWith("cudart-sd-bin-win-cu12")) : null;
            return new BinaryRelease(release.Tag, main.Url, main.Size, rt?.Url, rt?.Size ?? 0);
        }
        return null;
    }

    /// <inheritdoc />
    public Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct) =>
        PrebuiltBinaries.InstallAsync(Http, new BinaryInstallSpec("stable-diffusion.cpp", Root, ServerName,
            "SdCpp_Installing", "SdCpp_NoRelease", "SdCpp_NoServer", MakeExecutable), LatestAsync, progress, log, ct);

    /// <summary> Linux : les fichiers sd-* de l'archive zip n'ont pas le droit d'exécution. </summary>
    private static void MakeExecutable(string exe)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!).Where(f => Path.GetFileName(f).StartsWith("sd-")))
            File.SetUnixFileMode(f, File.GetUnixFileMode(f) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
