/*
    Fichier      :  PythonEnvironment.cs
    Projet       :  AlpenMux

    Description  :
        Lecture de l'environnement Python côté Linux, sans rien installer :
            - outils (uv, hf, curl…) cherchés dans le PATH et les dossiers « utilisateur » ;
            - chemins du venv de vLLM (python, vllm) ;
            - versions des paquets installés, lues dans les dossiers dist-info (sans lancer Python) ;
            - version du pilote NVIDIA.
        Séparé de DependencyService (installation) pour que les autres services puissent lire le venv
        sans dépendre de l'installateur.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Chemins, outils et versions de l'environnement Python côté Linux (lecture seule). </summary>
public interface IPythonEnvironment
{
    /// <summary>
    /// Cherche un programme dans le PATH, puis dans les dossiers où les installateurs « utilisateur » le placent
    /// (~/.local/bin n'est pas toujours dans le PATH d'une application lancée depuis le menu).
    /// </summary>
    /// <param name="exe"> Nom du programme (sans chemin). </param>
    /// <returns> Chemin complet (chemin Linux sous WSL), ou null si introuvable. </returns>
    string? FindTool(string exe);

    /// <summary> Chemin de uv à utiliser pour lancer les commandes ("uv" si introuvable, pour un message d'erreur clair). </summary>
    string Uv { get; }

    /// <summary> Python du venv de vLLM (chemin Linux). </summary>
    string VenvPython { get; }

    /// <summary> Exécutable vllm du venv (chemin Linux). </summary>
    string VllmExe { get; }

    /// <summary> Le venv existe (son Python est présent). </summary>
    bool VenvExists { get; }

    /// <summary> vLLM est installé dans le venv. </summary>
    bool VllmInstalled { get; }

    /// <summary> Version d'un paquet du venv, lue dans son dossier dist-info (sans lancer Python). </summary>
    /// <param name="distName"> Nom de distribution tel qu'il apparaît dans le dossier dist-info (ex. "vllm_omni"). </param>
    /// <returns> La version, ou null si le paquet est absent. </returns>
    string? PackageVersion(string distName);

    /// <summary> Version de vLLM installée, ou null. </summary>
    string? VllmVersion { get; }

    /// <summary> Version de vLLM-Omni installée, ou null. </summary>
    string? OmniVersion { get; }

    /// <summary> Version du pilote NVIDIA et nom du premier GPU (nvidia-smi), ou null sans pilote. </summary>
    Task<string?> NvidiaDriverAsync();
}

/// <summary> Implémentation de <see cref="IPythonEnvironment"/> à partir des réglages (chemin du venv). </summary>
public sealed class PythonEnvironment : IPythonEnvironment
{
    private readonly ISettingsService _settings;

    // Sous Windows, chaque lecture traverse \\wsl.localhost : versions gardées quelques secondes
    private readonly Dictionary<string, (string? Version, DateTime At)> _versionCache = new();

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    public PythonEnvironment(ISettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    /// <inheritdoc />
    public string? FindTool(string exe)
    {
        if (LinuxHost.IsWsl)
        {
            // Dans la distribution WSL (chemin Linux retourné)
            foreach (var dir in new[] { "/root/.local/bin", "/usr/local/bin", "/usr/bin" })
                if (LinuxHost.FileExists(LinuxHost.Combine(dir, exe))) return LinuxHost.Combine(dir, exe);
            return null;
        }
        if (ProcessRunner.Which(exe) is { } p) return p;
        foreach (var dir in new[] { Path.Combine(SettingsService.Home, ".local", "bin"), Path.Combine(SettingsService.Home, ".cargo", "bin") })
            if (File.Exists(Path.Combine(dir, exe))) return Path.Combine(dir, exe);
        return null;
    }

    /// <inheritdoc />
    public string Uv => FindTool("uv") ?? "uv";

    /// <inheritdoc />
    public string VenvPython => LinuxHost.Combine(_settings.VenvPath, "bin", "python");

    /// <inheritdoc />
    public string VllmExe => LinuxHost.Combine(_settings.VenvPath, "bin", "vllm");

    /// <inheritdoc />
    public bool VenvExists => LinuxHost.FileExists(VenvPython);

    /// <inheritdoc />
    public bool VllmInstalled => LinuxHost.FileExists(VllmExe);

    /// <inheritdoc />
    public string? PackageVersion(string distName)
    {
        if (!LinuxHost.IsWsl) return ReadPackageVersion(distName);
        lock (_versionCache)
        {
            if (_versionCache.TryGetValue(distName, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(3)) return c.Version;
            var v = ReadPackageVersion(distName);
            _versionCache[distName] = (v, DateTime.UtcNow);
            return v;
        }
    }

    private string? ReadPackageVersion(string distName)
    {
        try
        {
            if (!LinuxHost.DirectoryExists(LinuxHost.Combine(_settings.VenvPath, "lib"))) return null;
            var lib = LinuxHost.Local(LinuxHost.Combine(_settings.VenvPath, "lib"));
            foreach (var py in Directory.GetDirectories(lib, "python*"))
            {
                var sp = Path.Combine(py, "site-packages");
                if (!Directory.Exists(sp)) continue;
                var d = Directory.GetDirectories(sp, distName + "-*.dist-info").FirstOrDefault();
                if (d != null)
                {
                    var n = Path.GetFileName(d)[(distName.Length + 1)..];
                    return n[..^".dist-info".Length];
                }
            }
        }
        catch (Exception) { /* venv illisible ou en cours de modification : version inconnue */ }
        return null;
    }

    /// <inheritdoc />
    public string? VllmVersion => PackageVersion("vllm");

    /// <inheritdoc />
    public string? OmniVersion => PackageVersion("vllm_omni");

    /// <inheritdoc />
    public async Task<string?> NvidiaDriverAsync()
    {
        if (ProcessRunner.Which("nvidia-smi") == null) return null;
        var (code, output) = await ProcessRunner.CaptureAsync("nvidia-smi",
            ["--query-gpu=driver_version,name", "--format=csv,noheader"], 8000);
        return code == 0 ? output.Split('\n')[0].Trim() : null;
    }
}
