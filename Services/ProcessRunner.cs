/*
    Fichier      :  ProcessRunner.cs
    Projet       :  AlpenMux

    Description  :
        Lancement de processus externes (uv, pacman, hf, vllm) avec lecture en continu
        de stdout/stderr. Les lignes sont découpées sur \n ET \r (barres tqdm),
        les codes couleur ANSI sont retirés.

        Les processus longs sont lancés via `setsid` : ils ont leur propre groupe
        de processus, ce qui permet de les arrêter avec tous leurs enfants
        (SIGINT, puis SIGTERM, puis SIGKILL) comme le faisait start_vLLM.py.

        Windows :
            - commandes « côté Linux » (linux: true) : dans la distribution WSL (LinuxHost), le groupe de
              processus Linux est annoncé sur la première ligne pour pouvoir l'arrêter (kill dans WSL) ;
            - programmes Windows (llama-server.exe, nvidia-smi.exe) : arrêt de l'arborescence (taskkill).
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Codes couleur ANSI des lignes écrites par l'application dans les journaux. </summary>
public static class Ansi
{
    /// <summary> Fin de couleur ANSI. </summary>
    public const string Reset        = "\x1B[0m";
    /// <summary> Couleur des titres du journal. </summary>
    public const string HeaderColor  = "\x1B[1;36m";
    /// <summary> Couleur des commandes du journal. </summary>
    public const string CommandColor = "\x1B[90m";
    /// <summary> Couleur des succès du journal. </summary>
    public const string OkColor      = "\x1B[1;32m";
    /// <summary> Couleur des erreurs du journal. </summary>
    public const string ErrorColor   = "\x1B[1;31m";
}

/// <summary> Lancement de processus externes : lecture en continu des sorties, sessions (setsid) et arrêt du groupe. </summary>
public static partial class ProcessRunner
{
    /// <summary> Numéros des signaux POSIX utilisés. </summary>
    public const int SIGINT = 2, SIGKILL = 9, SIGTERM = 15;

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int SysKill(int pid, int sig);

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*\x07|[@-Z\\-_])")]
    private static partial Regex AnsiRegex();

    /// <summary> Retire les séquences ANSI d'une ligne. </summary>
    public static string StripAnsi(string s) => AnsiRegex().Replace(s, "");

    /// <summary> Ligne écrite en tête par les commandes WSL : identifiant du groupe de processus Linux. </summary>
    private const string PgidMarker = "__ALPENMUX_PGID__";

    /// <summary>
    /// Envoie un signal à tout un groupe de processus. wsl = groupe Linux dans la distribution WSL (Windows).
    /// Windows, processus Windows : SIGINT / SIGTERM = fermeture demandée, SIGKILL = arrêt forcé (arborescence).
    /// </summary>
    public static bool KillGroup(int pgid, int sig, bool wsl = false)
    {
        if (wsl) return LinuxHost.Exec("kill", [$"-{sig}", "--", $"-{pgid}"]).Code == 0;
        if (OperatingSystem.IsWindows())
        {
            var args = sig == SIGKILL ? $"/PID {pgid} /T /F" : $"/PID {pgid} /T";
            try { using var p = Process.Start(new ProcessStartInfo("taskkill.exe", args) { CreateNoWindow = true, UseShellExecute = false }); p?.WaitForExit(10000); }
            catch { return false; }
            return true;
        }
        return SysKill(-pgid, sig) == 0;
    }

    /// <summary> Le groupe de processus existe-t-il encore ? </summary>
    public static bool GroupAlive(int pgid, bool wsl = false)
    {
        if (wsl) return LinuxHost.Exec("kill", ["-0", "--", $"-{pgid}"]).Code == 0;
        if (OperatingSystem.IsWindows())
        {
            try { using var p = Process.GetProcessById(pgid); return !p.HasExited; }
            catch { return false; }
        }
        return SysKill(-pgid, 0) == 0;
    }

    /// <summary> Cherche un exécutable dans le PATH (extensions .exe… sous Windows). </summary>
    public static string? Which(string exe)
    {
        if (exe.Contains('/') || exe.Contains('\\')) return File.Exists(exe) ? exe : null;
        string[] exts = OperatingSystem.IsWindows() && !Path.HasExtension(exe)
            ? ["", .. (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')]
            : [""];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            foreach (var ext in exts)
            {
                var p = Path.Combine(dir, exe + ext);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    /// <summary> Démarrage d'un programme avec sorties redirigées en UTF-8, environnement modifié et nouvelle session si demandé. </summary>
    public static ProcessStartInfo CreateStartInfo(string file, IEnumerable<string> args,
        IDictionary<string, string?>? env = null, bool newSession = false, bool redirectStdin = false)
    {
        // setsid n'existe que sous Linux (sous Windows, l'arborescence est arrêtée par taskkill)
        newSession &= !OperatingSystem.IsWindows();
        var psi = new ProcessStartInfo
        {
            FileName = newSession ? "setsid" : file,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            RedirectStandardInput  = redirectStdin,
            UseShellExecute = false,
            CreateNoWindow  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };
        // -w : voir LinuxHost.StartInfo (setsid se détache sinon quand il est meneur de groupe)
        if (newSession) { psi.ArgumentList.Add("-w"); psi.ArgumentList.Add(file); }
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        if (env != null)
            foreach (var (k, v) in env)
            {
                if (v == null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }
        return psi;
    }

    /// <summary>
    /// Démarre la lecture des deux flux. Chaque "ligne" (séparée par \n ou \r) est passée à
    /// <paramref name="onLine"/> ; isProgress = true si la ligne se terminait par \r (mise à jour en place).
    /// </summary>
    public static Task PumpAsync(Process p, Action<string, bool> onLine) =>
        Task.WhenAll(
            Task.Run(() => Pump(p.StandardOutput, onLine)),
            Task.Run(() => Pump(p.StandardError,  onLine)));

    private static void Pump(StreamReader reader, Action<string, bool> onLine)
    {
        var sb  = new StringBuilder();
        var buf = new char[4096];
        try
        {
            int n;
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
            {
                SplitLines(buf, n, sb, (line, isProgress) => onLine(StripAnsi(line), isProgress));
            }
            if (sb.Length > 0) onLine(StripAnsi(sb.ToString()), false);
        }
        catch (Exception) { /* flux fermé */ }
    }

    /// <summary>
    /// Découpe des caractères lus en lignes. \r (barre de progression réécrite sur place) et \n terminent une ligne ;
    /// une ligne terminée par \r est signalée comme progression. Le début de ligne inachevé reste dans <paramref name="sb"/>.
    /// </summary>
    /// <param name="buf"> Caractères lus. </param>
    /// <param name="n"> Nombre de caractères valides dans <paramref name="buf"/>. </param>
    /// <param name="sb"> Ligne en cours, conservée d'une lecture à l'autre. </param>
    /// <param name="emit"> Reçoit chaque ligne non vide et vrai si c'est une ligne de progression. </param>
    private static void SplitLines(char[] buf, int n, StringBuilder sb, Action<string, bool> emit)
    {
        for (int i = 0; i < n; i++)
        {
            char c = buf[i];
            if (c is '\n' or '\r')
            {
                if (sb.Length > 0) { emit(sb.ToString(), c == '\r'); sb.Clear(); }
            }
            else sb.Append(c);
        }
    }

    /// <summary>
    /// Suit un fichier qui grandit (comme tail -f) à partir de <paramref name="offset"/>.
    /// S'arrête quand <paramref name="finished"/> devient vrai et que tout a été lu.
    /// <paramref name="keepAnsi"/> : garde les codes couleur (console colorée).
    /// </summary>
    public static async Task TailAsync(string path, long offset, Action<string, bool> onLine, Func<bool> finished, CancellationToken ct,
        bool keepAnsi = false)
    {
        string Clean(string s) => keepAnsi ? s : StripAnsi(s);
        while (!File.Exists(path) && !ct.IsCancellationRequested && !finished())
            await Task.Delay(200, CancellationToken.None);
        if (!File.Exists(path)) return;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        fs.Seek(Math.Min(offset, fs.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var sb  = new StringBuilder();
        var buf = new char[8192];
        while (!ct.IsCancellationRequested)
        {
            int n = await reader.ReadAsync(buf, 0, buf.Length);
            if (n == 0)
            {
                if (finished())
                {
                    // Dernière lecture : ce qui a pu être écrit juste avant la fin
                    await Task.Delay(300, CancellationToken.None);
                    if ((n = await reader.ReadAsync(buf, 0, buf.Length)) == 0) break;
                }
                else
                {
                    try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
                    continue;
                }
            }
            SplitLines(buf, n, sb, (line, isProgress) => onLine(Clean(line), isProgress));
        }
        if (sb.Length > 0) onLine(Clean(sb.ToString()), false);
    }

    /// <summary>
    /// Le processus existe-t-il et sa ligne de commande contient-elle ce texte ?
    /// wsl = processus Linux de la distribution WSL ; Windows sinon : le processus existe (le texte n'est pas vérifié).
    /// </summary>
    public static bool ProcessMatches(int pid, string needle, bool wsl = false)
    {
        if (wsl) return LinuxHost.Exec("cat", [$"/proc/{pid}/cmdline"]) is { Code: 0 } r && r.Output.Replace('\0', ' ').Contains(needle);
        if (OperatingSystem.IsWindows()) return GroupAlive(pid);
        try
        {
            var cmd = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            return cmd.Contains(needle);
        }
        catch { return false; }
    }

    /// <summary>
    /// Exécute une commande jusqu'au bout. Retourne le code de sortie.
    /// linux = programme du côté Linux (uv, python, vllm, hf) : dans la distribution WSL sous Windows.
    /// </summary>
    public static async Task<int> RunAsync(string file, IEnumerable<string> args, Action<string, bool>? onLine = null,
        IDictionary<string, string?>? env = null, CancellationToken ct = default, bool linux = false)
    {
        var inWsl = linux && LinuxHost.IsWsl;
        int? wslGroup = null;
        var output = onLine ?? ((_, _) => { });
        ProcessStartInfo psi;
        if (inWsl)
        {
            // Le groupe Linux est annoncé en premier : l'arrêter arrête aussi les enfants (uv, pip, compilations)
            psi = LinuxHost.StartInfo("sh", ["-c", $"echo {PgidMarker} $$ >&2; exec \"$@\"", "sh", file, .. args], env, newSession: true);
            output = (l, prog) =>
            {
                if (wslGroup == null && l.StartsWith(PgidMarker) && int.TryParse(l[PgidMarker.Length..].Trim(), out var g)) { wslGroup = g; return; }
                (onLine ?? ((_, _) => { }))(l, prog);
            };
        }
        else psi = linux ? LinuxHost.StartInfo(file, args, env, newSession: true) : CreateStartInfo(file, args, env, newSession: true);

        using var p = new Process { StartInfo = psi };
        p.Start();
        var pump = PumpAsync(p, output);

        await using (ct.Register(() => _ = inWsl ? StopWslAsync(p, () => wslGroup) : StopGroupAsync(p, 5)))
        {
            await p.WaitForExitAsync(CancellationToken.None);
            await pump;
        }
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }

    private static async Task StopWslAsync(Process p, Func<int?> group)
    {
        if (group() is { } g) await StopGroupAsync(g, 5, wsl: true);
        try { if (!p.HasExited) p.Kill(true); } catch { /* processus déjà terminé */ }
    }

    /// <summary> Exécute une commande courte et retourne (code, stdout+stderr). linux : voir RunAsync. </summary>
    public static async Task<(int Code, string Output)> CaptureAsync(string file, IEnumerable<string> args,
        int timeoutMs = 15000, IDictionary<string, string?>? env = null, bool linux = false)
    {
        try
        {
            var psi = linux ? LinuxHost.StartInfo(file, args, env) : CreateStartInfo(file, args, env);
            using var p = new Process { StartInfo = psi };
            p.Start();
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutMs);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { /* processus déjà terminé */ } return (-1, "timeout"); }
            return (p.ExitCode, (await outTask) + (await errTask));
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    /// <summary>
    /// Arrête un processus lancé avec setsid et tous ses enfants :
    /// SIGINT (patience), puis SIGTERM (10 s), puis SIGKILL.
    /// </summary>
    public static async Task StopGroupAsync(Process p, int sigintPatienceSeconds = 30)
    {
        if (OperatingSystem.IsWindows())
        {
            // Programme Windows : arrêt de toute l'arborescence
            try { if (!p.HasExited) p.Kill(true); } catch { /* processus déjà terminé */ }
            return;
        }
        int pgid;
        try { pgid = p.Id; } catch { return; }
        await StopGroupAsync(pgid, sigintPatienceSeconds);
    }

    /// <summary>
    /// Même chose à partir du seul identifiant de groupe (serveur retrouvé après un redémarrage de l'application).
    /// wsl = groupe Linux dans la distribution WSL (les vérifications passent par wsl.exe : un peu moins souvent).
    /// </summary>
    public static async Task StopGroupAsync(int pgid, int sigintPatienceSeconds = 30, bool wsl = false)
    {
        var poll = wsl ? 1000 : 300;
        foreach (var (sig, patience) in new[] { (SIGINT, sigintPatienceSeconds), (SIGTERM, 10), (SIGKILL, 5) })
        {
            if (!GroupAlive(pgid, wsl)) return;
            KillGroup(pgid, sig, wsl);
            var deadline = DateTime.UtcNow.AddSeconds(patience);
            while (DateTime.UtcNow < deadline)
            {
                if (!GroupAlive(pgid, wsl)) return;
                await Task.Delay(poll);
            }
        }
    }
}
