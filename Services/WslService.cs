/*
    Fichier      :  WslService.cs
    Projet       :  AlpenMux

    Description  :
        Windows : vLLM ne tourne que sous Linux, l'application le fait donc tourner dans WSL2, et s'occupe de tout :
            1. WSL2 lui-même : `wsl --install --no-distribution` avec les droits administrateur (fenêtre UAC) ;
               l'installateur NSIS le fait déjà, un redémarrage de Windows peut être nécessaire.
            2. Une distribution dédiée « AlpenMux » (Ubuntu 24.04), importée depuis l'image officielle d'Ubuntu
               pour WSL (`wsl --import`) : elle ne touche pas aux distributions de l'utilisateur, et se supprime
               d'un coup (`wsl --unregister AlpenMux`, proposé par la désinstallation).
               /etc/wsl.conf : root par défaut, PATH de Windows non ajouté (uv et Python de Windows ignorés).
            3. Ensuite, comme sous Linux : paquets apt (en root, sans mot de passe), uv, venv, vLLM… (DependencyService).
        Le GPU NVIDIA est partagé par le pilote Windows (pas de pilote à installer dans la distribution).
        Les messages de wsl.exe sont lus en UTF-8 (WSL_UTF8=1).
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Windows : installation de WSL2 et de la distribution AlpenMux, état de WSL. </summary>
public static class WslService
{
    /// <summary> Image officielle d'Ubuntu 24.04 pour WSL. </summary>
    private static string RootfsUrl =>
        $"https://cloud-images.ubuntu.com/wsl/releases/24.04/current/ubuntu-noble-wsl-{(RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64")}-wsl.rootfs.tar.gz";

    /// <summary> Dossier du disque virtuel de la distribution (ext4.vhdx). </summary>
    public static string DistroDir => Path.Combine(AppPaths.DataDir, "wsl", LinuxHost.Distro);

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    private static bool? _wslReady;
    private static bool? _distro;

    /// <summary> WSL2 est installé et fonctionne (`wsl --status`). </summary>
    public static bool WslReady => _wslReady ??= LinuxHost.IsWsl && RunWsl(["--status"], 15000).Code == 0;

    /// <summary> La distribution AlpenMux existe. </summary>
    public static bool DistroInstalled => _distro ??= LinuxHost.IsWsl && ListDistros().Contains(LinuxHost.Distro, StringComparer.OrdinalIgnoreCase);

    /// <summary> Relit l'état de WSL au prochain accès. </summary>
    public static void Invalidate() { _wslReady = null; _distro = null; }

    /// <summary> Version de WSL (`wsl --version`, première ligne), null si inconnue. </summary>
    public static string? Version
    {
        get
        {
            var (code, output) = RunWsl(["--version"], 10000);
            return code == 0 ? output.Split('\n').FirstOrDefault(l => l.Trim().Length > 0)?.Split(':').Last().Trim() : null;
        }
    }

    private static List<string> ListDistros()
    {
        var (code, output) = RunWsl(["--list", "--quiet"], 15000);
        return code == 0 ? output.Split('\n').Select(l => l.Trim('\r', ' ', '\0')).Where(l => l.Length > 0).ToList() : [];
    }

    /// <summary> wsl.exe avec ces arguments, synchrone (sortie en UTF-8). </summary>
    private static (int Code, string Output) RunWsl(IEnumerable<string> args, int timeoutMs)
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            psi.Environment["WSL_UTF8"] = "1";
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { /* processus déjà terminé */ } return (-1, "timeout"); }
            return (p.ExitCode, (o.Result + e.Result).Replace("\0", ""));
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static Task<int> RunWslAsync(IEnumerable<string> args, Action<string> log, CancellationToken ct) =>
        ProcessRunner.RunAsync("wsl.exe", args, (l, p) => { if (!p) log(l.Replace("\0", "")); },
            new Dictionary<string, string?> { ["WSL_UTF8"] = "1" }, ct);

    // ---------------------------------------------------------------- 1. WSL2

    /// <summary>
    /// Installe WSL2 (fonctionnalités Windows + noyau) avec les droits administrateur : Windows affiche la fenêtre UAC.
    /// Retourne false si l'installation a échoué ou si un redémarrage est nécessaire (RebootRequired).
    /// </summary>
    public static async Task<bool> InstallWslAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        progress.UnitsAreBytes = false;
        progress.Start(Loc.T("Wsl_Installing"));
        progress.SetIndeterminate(Loc.T("Wsl_Uac"));
        log("$ wsl.exe --install --no-distribution  (" + Loc.T("Wsl_Admin") + ")");
        try
        {
            // Fenêtre de console visible : l'installation de WSL affiche sa propre progression
            using var p = Process.Start(new ProcessStartInfo("wsl.exe", "--install --no-distribution")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Normal,
            })!;
            await p.WaitForExitAsync(ct);
            log(Loc.F("Common_ExitCode", p.ExitCode));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Fenêtre UAC refusée
            log("✘ " + Loc.T("Wsl_UacDenied"));
            progress.Finish(Loc.T("Wsl_UacDenied"));
            return false;
        }

        Invalidate();
        if (WslReady) { progress.Finish(Loc.T("Common_Done")); return true; }
        RebootRequired = true;
        log("⚠ " + Loc.T("Wsl_Reboot"));
        progress.Finish(Loc.T("Wsl_Reboot"));
        return false;
    }

    /// <summary> WSL vient d'être activé : Windows doit redémarrer avant de pouvoir l'utiliser. </summary>
    public static bool RebootRequired { get; private set; }

    /// <summary> Met WSL à jour (`wsl --update`) : nécessaire aux anciennes versions installées avec Windows. </summary>
    public static async Task<bool> UpdateWslAsync(Action<string> log, CancellationToken ct)
    {
        log("$ wsl.exe --update");
        var code = await RunWslAsync(["--update"], log, ct);
        Invalidate();
        return code == 0;
    }

    // ---------------------------------------------------------------- 2. Distribution AlpenMux

    /// <summary> Télécharge l'image d'Ubuntu et l'importe sous le nom AlpenMux (WSL2), puis la configure. </summary>
    public static async Task<bool> InstallDistroAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        if (!WslReady) { log("✘ " + Loc.T("Wsl_Missing")); return false; }
        progress.UnitsAreBytes = true;
        progress.Start(Loc.T("Wsl_Distro_Installing"));

        var cache = Path.Combine(AppPaths.DataDir, "wsl");
        Directory.CreateDirectory(cache);
        var tar = Path.Combine(cache, "ubuntu-rootfs.tar.gz");
        try
        {
            log("↓ " + RootfsUrl);
            await PrebuiltBinaries.DownloadAsync(Http, RootfsUrl, tar, "Ubuntu 24.04", progress, ct);

            progress.SetIndeterminate(Loc.T("Wsl_Distro_Importing"));
            Directory.CreateDirectory(DistroDir);
            log($"$ wsl.exe --import {LinuxHost.Distro} \"{DistroDir}\" \"{tar}\" --version 2");
            var code = await RunWslAsync(["--import", LinuxHost.Distro, DistroDir, tar, "--version", "2"], log, ct);
            if (code != 0)
            {
                // WSL trop ancien (import WSL2 refusé) : mise à jour puis nouvel essai
                log(Loc.T("Wsl_Updating"));
                await UpdateWslAsync(log, ct);
                code = await RunWslAsync(["--import", LinuxHost.Distro, DistroDir, tar, "--version", "2"], log, ct);
            }
            if (code != 0) { progress.Finish(Loc.F("Common_ExitCode", code)); return false; }
        }
        finally
        {
            try { File.Delete(tar); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
        }

        Invalidate();
        if (!DistroInstalled) { progress.Finish(Loc.T("Wsl_Distro_Missing")); return false; }

        // root par défaut, PATH de Windows non mélangé à celui de Linux ; redémarrage de la distribution pour l'appliquer
        const string conf = "[user]\ndefault=root\n\n[interop]\nappendWindowsPath=false\n\n[boot]\nsystemd=false\n";
        await ProcessRunner.RunAsync("sh", ["-c", $"printf '{conf.Replace("\n", "\\n")}' > /etc/wsl.conf"], (l, _) => log(l), null, ct, linux: true);
        RunWsl(["--terminate", LinuxHost.Distro], 30000);
        log("✔ " + Loc.F("Wsl_Distro_Ready", LinuxHost.Distro));
        progress.Finish(Loc.T("Common_Done"));
        return true;
    }

    /// <summary> GPU NVIDIA visible dans la distribution (nvidia-smi de WSL, fourni par le pilote Windows). </summary>
    public static string? GpuInDistro()
    {
        if (!DistroInstalled) return null;
        var (code, output) = LinuxHost.Exec("/usr/lib/wsl/lib/nvidia-smi", ["--query-gpu=name", "--format=csv,noheader"], 30000);
        return code == 0 ? output.Split('\n')[0].Trim() : null;
    }
}
