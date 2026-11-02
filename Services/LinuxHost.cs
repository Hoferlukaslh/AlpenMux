/*
    Fichier      :  LinuxHost.cs
    Projet       :  AlpenMux

    Description  :
        Le « côté Linux » de l'application : là où tournent uv, Python, vLLM, vLLM-Omni et hf.
            - sous Linux : la machine elle-même (rien ne change) ;
            - sous Windows : la distribution WSL2 dédiée « AlpenMux » (voir WslService), en root.
        Deux sortes de chemins :
            - chemin Linux (venv, arguments de vLLM) : /root/vllm-env/.venv, /mnt/c/Users/… ;
            - chemin local (lecture de fichiers par l'application) : sous Windows, la distribution est vue
              en \\wsl.localhost\AlpenMux\…, et les dossiers Windows sont vus par WSL en /mnt/<lettre>/….
        Les commandes Linux passent sous Windows par `wsl.exe -d AlpenMux -u root --exec env … commande` :
        les arguments arrivent tels quels (pas de shell intermédiaire), l'environnement est donné à `env`.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace AlpenMux.Services;

/// <summary> Le « côté Linux » de l'application : la machine elle-même, ou la distribution WSL sous Windows. </summary>
public static class LinuxHost
{
    /// <summary> Vrai sous Windows : le côté Linux est une distribution WSL2. </summary>
    public static bool IsWsl => OperatingSystem.IsWindows();

    /// <summary> Nom de la distribution WSL dédiée à l'application. </summary>
    public const string Distro = AppPaths.DisplayName;

    /// <summary> Dossier personnel côté Linux (root dans la distribution WSL). </summary>
    public static string Home => IsWsl ? "/root" : SettingsService.Home;

    /// <summary> PATH de base côté Linux sous WSL (uv dans ~/.local/bin, pilote NVIDIA de WSL dans /usr/lib/wsl/lib). </summary>
    public const string WslPath = "/root/.local/bin:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/lib/wsl/lib";

    /// <summary> Assemble un chemin Linux (toujours avec /, même sous Windows). </summary>
    public static string Combine(params string[] parts)
    {
        var res = parts.Length > 0 ? parts[0].TrimEnd('/') : "";
        foreach (var p in parts.Skip(1).Select(p => p.Trim('/')).Where(p => p.Length > 0)) res += "/" + p;
        return res.Length == 0 ? "/" : res;
    }

    /// <summary> Développe ~ dans un chemin Linux. </summary>
    public static string Expand(string path)
    {
        path = path.Trim();
        if (!IsWsl) return SettingsService.ExpandPath(path);
        if (path == "~") return Home;
        if (path.StartsWith("~/")) return Combine(Home, path[2..]);
        return path.Replace('\\', '/');
    }

    /// <summary> Chemin Linux -> chemin lisible par l'application (\\wsl.localhost\AlpenMux\… sous Windows). </summary>
    public static string Local(string linuxPath)
    {
        if (!IsWsl) return linuxPath;
        // Dossier Windows vu par WSL : on revient au chemin Windows
        if (linuxPath.StartsWith("/mnt/") && linuxPath.Length >= 6 && char.IsLetter(linuxPath[5]) && (linuxPath.Length == 6 || linuxPath[6] == '/'))
            return $"{char.ToUpperInvariant(linuxPath[5])}:\\" + linuxPath[Math.Min(7, linuxPath.Length)..].Replace('/', '\\');
        return $@"\\wsl.localhost\{Distro}" + linuxPath.Replace('/', '\\');
    }

    /// <summary> Chemin local (dossier Windows ou \\wsl.localhost\…) -> chemin pour les programmes Linux. </summary>
    public static string ToLinux(string path)
    {
        if (!IsWsl || string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith('/')) return path;   // déjà un chemin Linux
        // Pas un chemin absolu Windows : identifiant de dépôt Hugging Face (org/modèle), laissé tel quel
        if (!(path.Length >= 3 && path[1] == ':' && path[2] is '\\' or '/') && !path.StartsWith(@"\\")) return path;
        var full = Path.GetFullPath(path);
        foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
        {
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = full[prefix.Length..];
            var slash = rest.IndexOf('\\');
            return slash < 0 ? "/" : rest[slash..].Replace('\\', '/');
        }
        if (full.Length >= 2 && full[1] == ':')
            return "/mnt/" + char.ToLowerInvariant(full[0]) + full[2..].Replace('\\', '/');
        return full.Replace('\\', '/');
    }

    /// <summary>
    /// Démarrage d'un programme côté Linux. Sous Linux : comme ProcessRunner.CreateStartInfo.
    /// Sous Windows : wsl.exe, l'environnement passé par `env` (null = variable retirée), setsid si demandé.
    /// </summary>
    public static ProcessStartInfo StartInfo(string file, IEnumerable<string> args, IDictionary<string, string?>? env = null,
        bool newSession = false, bool redirectStdin = false, string? workDir = null)
    {
        if (!IsWsl)
        {
            var psi = ProcessRunner.CreateStartInfo(file, args, env, newSession, redirectStdin);
            if (workDir != null) psi.WorkingDirectory = workDir;
            return psi;
        }

        var w = new ProcessStartInfo("wsl.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = redirectStdin,
            UseShellExecute = false,
            CreateNoWindow  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        w.Environment["WSL_UTF8"] = "1";   // messages de wsl.exe en UTF-8 (sinon UTF-16)
        foreach (var a in new[] { "-d", Distro, "-u", "root", "--cd", workDir ?? "/root", "--exec", "/usr/bin/env" }) w.ArgumentList.Add(a);

        var vars = new Dictionary<string, string?>
        {
            ["HOME"] = "/root", ["PATH"] = WslPath, ["PYTHONUNBUFFERED"] = "1", ["NO_COLOR"] = "1", ["LC_ALL"] = "C.UTF-8",
        };
        if (env != null) foreach (var (k, v) in env) vars[k] = v;
        foreach (var (k, v) in vars.Where(kv => kv.Value == null)) { w.ArgumentList.Add("-u"); w.ArgumentList.Add(k); }
        foreach (var (k, v) in vars.Where(kv => kv.Value != null)) w.ArgumentList.Add($"{k}={v}");

        // setsid -w : attend le processus et rend son code de sortie. Sans -w, setsid se détache (fork) quand il est
        // meneur de groupe, ce qui est le cas sous wsl.exe : le parent sort aussitôt avec 0 et la sortie est perdue.
        if (newSession) { w.ArgumentList.Add("setsid"); w.ArgumentList.Add("-w"); }
        w.ArgumentList.Add(file);
        foreach (var a in args) w.ArgumentList.Add(a);
        return w;
    }

    /// <summary> PATH côté Linux avec un dossier en tête (bin du venv). </summary>
    public static string PathWith(string dir) =>
        dir + ":" + (IsWsl ? WslPath : Environment.GetEnvironmentVariable("PATH"));

    /// <summary> Commande courte côté Linux, synchrone (signaux, état d'un processus). </summary>
    public static (int Code, string Output) Exec(string file, IEnumerable<string> args, int timeoutMs = 10000)
    {
        try
        {
            using var p = new Process { StartInfo = StartInfo(file, args) };
            p.Start();
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { /* processus déjà terminé */ } return (-1, "timeout"); }
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    /// <summary> Le fichier existe-t-il côté Linux ? (sous Windows : sans démarrer la distribution si elle n'existe pas) </summary>
    public static bool FileExists(string linuxPath) => (!IsWsl || WslService.DistroInstalled) && File.Exists(Local(linuxPath));

    /// <summary> Le dossier existe-t-il côté Linux ? </summary>
    public static bool DirectoryExists(string linuxPath) => (!IsWsl || WslService.DistroInstalled) && Directory.Exists(Local(linuxPath));
}
