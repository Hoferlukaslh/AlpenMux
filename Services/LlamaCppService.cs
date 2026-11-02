/*
    Fichier      :  LlamaCppService.cs
    Projet       :  AlpenMux

    Description  :
        llama.cpp (llama-server) : sert les modèles GGUF en lisant le fichier directement
        (pas d'import ni de copie, contrairement à Ollama).

        Installation sans droits administrateur : le binaire officiel précompilé est téléchargé
        depuis les publications GitHub de ggml-org/llama.cpp et extrait dans
            ~/.local/share/alpenmux/llama.cpp/current      (+ fichier VERSION)
        Variante choisie selon le GPU : CUDA 13 (pilote NVIDIA >= 580) ou CUDA 12, ROCm (AMD),
        sinon Vulkan. Si les bibliothèques CUDA (libcudart, libcublas) manquent sur le système,
        l'archive "cudart" de la même version est ajoutée.
        Windows : binaire Windows natif (archives -bin-win-cuda-13.x / 12.x, -bin-win-vulkan, .zip) dans
        %LOCALAPPDATA%\alpenmux\llama.cpp\current, avec toujours l'archive "cudart" (pas de CUDA installé en général).
        llama.cpp lit le fichier GGUF directement depuis le disque Windows (pas de passage par WSL).
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Installation et mise à jour de llama.cpp (binaire officiel adapté au GPU). </summary>
public interface ILlamaCppService
{
    /// <summary> Dernière version publiée et archive(s) à télécharger pour cette machine. </summary>
    Task<BinaryRelease?> LatestAsync(CancellationToken ct = default);

    /// <summary> Télécharge et installe (ou met à jour) llama.cpp. Retourne vrai si llama-server est en place. </summary>
    Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="ILlamaCppService"/>. </summary>
public sealed partial class LlamaCppService : ILlamaCppService
{
    private readonly ISystemMonitorService _monitor;
    private readonly IPythonEnvironment _python;

    /// <summary> Crée le service. </summary>
    /// <param name="monitor"> Présence d'un GPU NVIDIA (choix de la variante). </param>
    /// <param name="python"> Version du pilote NVIDIA. </param>
    public LlamaCppService(ISystemMonitorService monitor, IPythonEnvironment python)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(python);
        _monitor = monitor;
        _python = python;
    }
    // Les compilations quotidiennes (b12345) sont publiées comme « pré-versions » : "latest" ne les renvoie pas,
    // on parcourt donc les publications récentes et on prend la première qui contient l'archive voulue.
    private const string ReleasesApi = "https://api.github.com/repos/ggml-org/llama.cpp/releases?per_page=15";
    private static readonly HttpClient Http = PrebuiltBinaries.CreateGitHubClient();

    /// <summary> Dossier de llama.cpp dans les données de l'application. </summary>
    public static string Root => Path.Combine(AppPaths.DataDir, "llama.cpp");
    /// <summary> Version installée en service. </summary>
    public static string CurrentDir => PrebuiltBinaries.CurrentDirOf(Root);

    /// <summary> Chemin de llama-server (null = non installé). </summary>
    public static string? ServerExe => PrebuiltBinaries.FindServer(CurrentDir, ServerName);

    private static string ServerName => OperatingSystem.IsWindows() ? "llama-server.exe" : "llama-server";

    /// <summary> Fin du nom des archives pour ce système : -x64.tar.gz (Linux), -x64.zip / -arm64.zip (Windows). </summary>
    private static string ArchiveSuffix => OperatingSystem.IsWindows()
        ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "-arm64.zip" : "-x64.zip")
        : "-x64.tar.gz";

    /// <summary> llama-server est installé. </summary>
    public static bool Installed => ServerExe != null;

    /// <summary> Version installée (ex. "b11998"). </summary>
    public static string? InstalledVersion => PrebuiltBinaries.InstalledVersion(CurrentDir);

    /// <summary> Dossiers des bibliothèques à ajouter à LD_LIBRARY_PATH (celles de l'archive, puis CUDA du système). </summary>
    public static string LibraryPath()
    {
        var dirs = new List<string>();
        if (Directory.Exists(CurrentDir))
            dirs.AddRange(Directory.EnumerateFiles(CurrentDir, "*.so*", SearchOption.AllDirectories)
                .Select(f => Path.GetDirectoryName(f)!).Distinct());
        // CUDA du système : /opt/cuda (Arch), /usr/local/cuda (installateur NVIDIA, Debian / Ubuntu)
        foreach (var d in new[] { "/opt/cuda/lib64", "/usr/local/cuda/lib64" })
            if (Directory.Exists(d)) dirs.Add(d);
        var current = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (!string.IsNullOrEmpty(current)) dirs.Add(current);
        return string.Join(':', dirs);
    }

    // ---------------------------------------------------------------- Choix de la variante

    [GeneratedRegex(@"^(\d+)")]
    private static partial Regex LeadingNumber();

    /// <summary> Motif du nom d'archive adapté à la machine, ex. "-bin-ubuntu-cuda-13". </summary>
    private async Task<(string Pattern, int? CudaMajor)> VariantAsync()
    {
        if (_monitor.HasNvidia)
        {
            // CUDA 13 demande un pilote 580 ou plus récent
            var driver = await _python.NvidiaDriverAsync();
            var major = driver != null && LeadingNumber().Match(driver) is { Success: true } m ? int.Parse(m.Value) : 0;
            var cuda = major >= 580 ? 13 : 12;
            return ($"-bin-{Os}-cuda-{cuda}.", cuda);
        }
        if (OperatingSystem.IsWindows()) return ("-bin-win-vulkan-", null);
        if (Directory.Exists("/sys/module/amdgpu")) return ("-bin-ubuntu-rocm-", null);
        return ("-bin-ubuntu-vulkan-x64", null);
    }

    private static string Os => OperatingSystem.IsWindows() ? "win" : "ubuntu";

    /// <summary> Les bibliothèques CUDA de cette version majeure sont-elles déjà sur le système ? </summary>
    private static bool SystemHasCudaRuntime(int major)
    {
        if (OperatingSystem.IsWindows()) return false;   // archive cudart toujours ajoutée (DLL à côté de llama-server.exe)
        string[] dirs = ["/opt/cuda/lib64", "/usr/local/cuda/lib64", "/usr/lib", "/usr/lib64", "/usr/lib/x86_64-linux-gnu"];
        return dirs.Any(d => File.Exists(Path.Combine(d, $"libcudart.so.{major}")) && File.Exists(Path.Combine(d, $"libcublas.so.{major}")));
    }

    /// <inheritdoc />
    public async Task<BinaryRelease?> LatestAsync(CancellationToken ct = default)
    {
        if (await PrebuiltBinaries.ReleasesAsync(Http, ReleasesApi, ct) is not { } releases) return null;
        var (pattern, cuda) = await VariantAsync();
        bool Linux64(string n) => n.EndsWith(ArchiveSuffix, StringComparison.Ordinal);

        foreach (var release in releases)   // du plus récent au plus ancien
        {
            var main = release.Assets.FirstOrDefault(a => a.Name.StartsWith("llama-") && a.Name.Contains(pattern) && Linux64(a.Name));
            if (main == null) continue;

            string? cudartUrl = null;
            long cudartSize = 0;
            if (cuda is { } c && !SystemHasCudaRuntime(c))
            {
                var rt = release.Assets.FirstOrDefault(a => a.Name.StartsWith("cudart-") && a.Name.Contains(pattern) && Linux64(a.Name));
                if (rt != null) { cudartUrl = rt.Url; cudartSize = rt.Size; }
            }
            return new BinaryRelease(release.Tag, main.Url, main.Size, cudartUrl, cudartSize);
        }
        return null;
    }

    // ---------------------------------------------------------------- Installation / mise à jour

    /// <inheritdoc />
    public Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct) =>
        PrebuiltBinaries.InstallAsync(Http, new BinaryInstallSpec("llama.cpp", Root, ServerName,
            "Llama_Installing", "Llama_NoRelease", "Llama_NoServer"), LatestAsync, progress, log, ct);
}
