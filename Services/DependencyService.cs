/*
    Fichier      :  DependencyService.cs
    Projet       :  AlpenMux

    Description  :
        Vérification et installation des dépendances, sur Arch (pacman) et Debian / Ubuntu (apt) :
            - paquets système : git, gcc (compilation Triton), uv, hf
                Arch   : paquets uv et python-huggingface-hub
                Debian : uv par l'installateur officiel d'Astral (dans ~/.local/bin, sans droits
                         administrateur), hf installé dans le venv ; curl pour l'installateur
            - environnement virtuel Python créé avec uv (Python géré par uv)
            - llama.cpp (optionnel, binaire officiel, voir LlamaCppService) : sert les modèles GGUF
            - vLLM et vLLM-Omni (optionnel), installés avec SafeInstallAsync :
              build PyTorch cohérent choisi avant l'installation, famille torch figée ensuite,
              vérification après coup, réparation puis retour en arrière si nécessaire

        Les commandes pacman / apt passent par pkexec (fenêtre de mot de passe graphique).
        Windows : tout ceci se passe dans la distribution WSL « AlpenMux » (Ubuntu, en root : apt sans mot de passe),
        voir LinuxHost et WslService ; le venv est un chemin Linux (/root/vllm-env/.venv).

        Lecture du venv (chemins, versions) : PythonEnvironment ; `uv pip install` avec progression : UvPipInstaller.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary>
/// Dépendance système. Package = nom du paquet ; null = absent des dépôts de cette distribution.
/// ViaInstaller = installé par un installateur officiel (uv sur Debian) et non par le gestionnaire de paquets.
/// </summary>
public sealed record SystemPackage(string Package, string Executable, string Purpose, bool ViaInstaller = false);

/// <summary> Famille de distribution Linux. </summary>
public enum Distro
{
    /// <summary> Arch Linux et dérivées (pacman). </summary>
    Arch,
    /// <summary> Debian, Ubuntu et dérivées (apt), dont la distribution WSL. </summary>
    Debian,
    /// <summary> Distribution non reconnue. </summary>
    Unknown
}

/// <summary> Installation des dépendances : paquets système, venv, vLLM, vLLM-Omni et paquets Python optionnels. </summary>
public interface IDependencyService
{
    /// <summary> Paquets système requis qui manquent encore. </summary>
    List<SystemPackage> MissingPackages();

    /// <summary> Installe des paquets système (pkexec, ou apt dans WSL) puis uv si besoin. Retourne vrai en cas de succès. </summary>
    Task<bool> InstallSystemPackagesAsync(IReadOnlyList<SystemPackage> pkgs,
        ProgressTracker progress, Action<string> log, CancellationToken ct);

    /// <summary> Crée le venv avec uv et la version de Python choisie. Retourne vrai en cas de succès. </summary>
    Task<bool> CreateVenvAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);

    /// <summary>
    /// Installation sûre dans le venv :
    ///   - vLLM lui-même : le build PyTorch (cu130, cu128…) est choisi par simulation pour que
    ///     torch / torchvision / torchaudio viennent du même build ;
    ///   - autres paquets : la famille torch installée est figée (contraintes) et le build mémorisé réutilisé ;
    ///   - ensuite : vérification (statique + test réel). En cas d'échec : réparation,
    ///     puis retour à l'état précédent si le problème persiste.
    /// </summary>
    Task<bool> SafeInstallAsync(IReadOnlyList<string> packages, string title, bool installsVllm,
        ProgressTracker progress, Action<string> log, CancellationToken ct, bool upgrade = false);

    /// <summary> Installe (ou met à jour) vLLM avec un build PyTorch cohérent. </summary>
    Task<bool> InstallVllmAsync(ProgressTracker progress, Action<string> log, CancellationToken ct, bool upgrade = false);

    /// <summary> Installe vLLM-Omni dans la version qui correspond au vLLM installé. </summary>
    Task<bool> InstallOmniAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IDependencyService"/>. </summary>
public sealed partial class DependencyService : IDependencyService
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;
    private readonly IUvPipInstaller _uv;
    private readonly IEnvironmentChecker _env;
    private readonly IHfService _hf;

    /// <summary> Crée le service d'installation. </summary>
    /// <param name="settings"> Réglages (venv, version de Python, spécification de vLLM). </param>
    /// <param name="python"> Lecture du venv et recherche des outils. </param>
    /// <param name="uv"> Installation de paquets avec uv. </param>
    /// <param name="env"> Garde-fou de l'environnement PyTorch. </param>
    /// <param name="hf"> Recherche de la commande hf. </param>
    public DependencyService(ISettingsService settings, IPythonEnvironment python, IUvPipInstaller uv,
        IEnvironmentChecker env, IHfService hf)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(uv);
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(hf);
        _settings = settings;
        _python = python;
        _uv = uv;
        _env = env;
        _hf = hf;
    }

    /// <summary> Famille de la distribution, d'après /etc/os-release (ID et ID_LIKE), sinon d'après le gestionnaire présent. </summary>
    public static Distro CurrentDistro { get; } = DetectDistro();

    private static Distro DetectDistro()
    {
        if (LinuxHost.IsWsl) return Distro.Debian;   // distribution WSL : Ubuntu
        try
        {
            var ids = File.ReadAllLines("/etc/os-release")
                .Where(l => l.StartsWith("ID=") || l.StartsWith("ID_LIKE="))
                .SelectMany(l => l[(l.IndexOf('=') + 1)..].Trim('"').Split(' '))
                .ToHashSet();
            if (ids.Contains("arch")) return Distro.Arch;
            if (ids.Contains("debian") || ids.Contains("ubuntu")) return Distro.Debian;
        }
        catch { /* pas de /etc/os-release */ }
        return ProcessRunner.Which("pacman") != null ? Distro.Arch
             : ProcessRunner.Which("apt-get") != null ? Distro.Debian
             : Distro.Unknown;
    }

    /// <summary> Les paquets système peuvent être installés (Arch ou Debian reconnus). </summary>
    public static bool CanInstallPackages => CurrentDistro != Distro.Unknown;
    /// <summary> Gestionnaire de paquets de la distribution (pacman, apt). </summary>
    public static string PackageManager => CurrentDistro switch { Distro.Arch => "pacman", Distro.Debian => "apt", _ => "?" };

    /// <summary> Installateur officiel de uv (dans ~/.local/bin, sans modifier les fichiers du shell). </summary>
    public const string UvInstallerUrl = "https://astral.sh/uv/install.sh";

    /// <summary> Paquets système requis pour cette distribution. </summary>
    public static readonly SystemPackage[] SystemPackages = CurrentDistro == Distro.Debian
        ?
        [
            new("git",  "git",  "Dep_Pkg_Git"),
            // Ubuntu minimal de WSL : gcc sans les en-têtes de la libc avec --no-install-recommends
            new(LinuxHost.IsWsl ? "build-essential" : "gcc", "gcc", "Dep_Pkg_Gcc"),
            // En-têtes de Python (Python.h) : Triton les demande pour compiler ses noyaux (sinon « fatal error: Python.h »).
            // Distribution WSL = Ubuntu 24.04 fixe (Python 3.12) : son chemin d'en-têtes est connu.
            .. LinuxHost.IsWsl ? [new("python3-dev", "/usr/include/python3.12/Python.h", "Dep_Pkg_PyDev")] : Array.Empty<SystemPackage>(),
            new("curl", "curl", "Dep_Pkg_Curl"),
            new("uv",   "uv",   "Dep_Pkg_Uv", ViaInstaller: true),
        ]
        :
        [
            new("uv",                     "uv",  "Dep_Pkg_Uv"),
            new("git",                    "git", "Dep_Pkg_Git"),
            new("gcc",                    "gcc", "Dep_Pkg_Gcc"),
            new("python-huggingface-hub", "hf",  "Dep_Pkg_Hf"),
        ];

    /// <inheritdoc />
    public List<SystemPackage> MissingPackages() => SystemPackages.Where(p => !IsInstalled(p)).ToList();

    /// <summary> Paquet présent : exécutable trouvé, ou fichier absolu (en-têtes) présent côté Linux. </summary>
    private bool IsInstalled(SystemPackage p) => p.Executable switch
    {
        "hf" => _hf.HfExecutable() != null,
        ['/', ..] => LinuxHost.FileExists(p.Executable),
        _ => _python.FindTool(p.Executable) != null,
    };

    /// <summary> Commande à copier dans un terminal si la fenêtre de mot de passe ne fonctionne pas. </summary>
    public static string ManualInstallCommand(IEnumerable<SystemPackage> pkgs)
    {
        var list = pkgs.ToList();
        var native = string.Join(' ', list.Where(p => !p.ViaInstaller).Select(p => p.Package));
        var parts = new List<string>();
        if (native.Length > 0)
            parts.Add(LinuxHost.IsWsl ? "apt-get install -y " + native
                      : CurrentDistro == Distro.Debian ? "sudo apt-get install -y " + native : "sudo pacman -S --needed --noconfirm " + native);
        if (list.Any(p => p.ViaInstaller && p.Executable == "uv"))
            parts.Add($"curl -LsSf {UvInstallerUrl} | UV_NO_MODIFY_PATH=1 sh");
        var cmd = string.Join("  &&  ", parts);
        // Windows : commande à taper dans PowerShell (root dans la distribution AlpenMux)
        return LinuxHost.IsWsl ? $"wsl -d {LinuxHost.Distro} -u root -- sh -c \"{cmd.Replace("  &&  ", " && ")}\"" : cmd;
    }

    // ---------------------------------------------------------------- Installation

    [GeneratedRegex(@"^\((\d+)/(\d+)\)")]
    private static partial Regex PacmanStepRegex();

    /// <inheritdoc />
    public async Task<bool> InstallSystemPackagesAsync(IReadOnlyList<SystemPackage> pkgs,
        ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        if (pkgs.Count == 0) return true;
        progress.UnitsAreBytes = false;
        progress.Start(Loc.T("Dep_InstallingPackages"));

        var native = pkgs.Where(p => !p.ViaInstaller).Select(p => p.Package).ToList();
        if (native.Count > 0 && !await InstallNativeAsync(native, progress, log, ct)) return false;

        // Installateurs officiels, sans droits administrateur (uv sur Debian)
        if (pkgs.Any(p => p.ViaInstaller && p.Executable == "uv") && _python.FindTool("uv") == null && !await InstallUvAsync(progress, log, ct))
            return false;

        progress.Finish(Loc.T("Common_Done"));
        return true;
    }

    /// <summary> pacman ou apt-get via pkexec (fenêtre de mot de passe graphique). </summary>
    private static async Task<bool> InstallNativeAsync(List<string> packages, ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        if (LinuxHost.IsWsl) return await InstallAptInWslAsync(packages, progress, log, ct);
        if (ProcessRunner.Which("pkexec") == null)
        {
            log(Loc.T("Dep_NoPkexec"));
            progress.Finish(Loc.T("Dep_NoPkexec"));
            return false;
        }

        List<string> args;
        Action<string> onLine;
        if (CurrentDistro == Distro.Debian)
        {
            // Une seule authentification pour la mise à jour des listes et l'installation ; sortie en anglais pour le suivi
            var cmd = "apt-get update && apt-get install -y --no-install-recommends " + string.Join(' ', packages);
            args = ["env", "DEBIAN_FRONTEND=noninteractive", "LC_ALL=C", "sh", "-c", cmd];
            int done = 0;
            onLine = line =>
            {
                log(line);
                if (line.StartsWith("Setting up ")) done++;
                progress.Report(Math.Min(done, packages.Count), packages.Count, line.Trim());
            };
        }
        else
        {
            args = ["pacman", "-S", "--needed", "--noconfirm", .. packages];
            // pacman affiche "(1/3) installation de uv…" : on suit ces étapes (téléchargement puis installation)
            int phase = 0;
            onLine = line =>
            {
                log(line);
                var m = PacmanStepRegex().Match(line.Trim());
                if (!m.Success) return;
                var i = int.Parse(m.Groups[1].Value);
                var n = int.Parse(m.Groups[2].Value);
                if (i == 1 && phase < 2) phase++;
                progress.Report(i + (phase - 1) * n, 2 * n, line.Trim());
            };
        }

        log("$ pkexec " + string.Join(' ', args));
        var code = await ProcessRunner.RunAsync("pkexec", args, (line, _) => onLine(line), null, ct);
        if (code == 126 || code == 127) log(Loc.T("Dep_PkexecDenied"));
        if (code != 0) progress.Finish(Loc.F("Common_ExitCode", code));
        return code == 0;
    }

    /// <summary> Windows : apt-get dans la distribution WSL, en root (pas de mot de passe). </summary>
    private static async Task<bool> InstallAptInWslAsync(List<string> packages, ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        var cmd = "apt-get update && apt-get install -y --no-install-recommends ca-certificates " + string.Join(' ', packages);
        log($"$ ({LinuxHost.Distro}) " + cmd);
        int done = 0;
        var code = await ProcessRunner.RunAsync("sh", ["-c", cmd], (line, _) =>
        {
            log(line);
            if (line.StartsWith("Setting up ")) done++;
            progress.Report(Math.Min(done, packages.Count), packages.Count, line.Trim());
        }, new Dictionary<string, string?> { ["DEBIAN_FRONTEND"] = "noninteractive", ["LC_ALL"] = "C" }, ct, linux: true);
        if (code != 0) progress.Finish(Loc.F("Common_ExitCode", code));
        return code == 0;
    }

    /// <summary> uv par l'installateur officiel d'Astral, dans ~/.local/bin (rien à modifier dans le système). </summary>
    private async Task<bool> InstallUvAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        if (_python.FindTool("curl") == null) { log("✘ curl"); return false; }
        progress.SetDetail(Loc.T("Dep_InstallingUv"));
        var script = $"curl -LsSf {UvInstallerUrl} | sh";
        log("$ " + script);
        var code = await ProcessRunner.RunAsync("sh", ["-c", script], (l, p) => { if (!p) log(l); },
            new Dictionary<string, string?> { ["UV_NO_MODIFY_PATH"] = "1" }, ct, linux: true);
        return code == 0 && _python.FindTool("uv") != null;
    }

    /// <inheritdoc />
    public async Task<bool> CreateVenvAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        var venv = _settings.VenvPath;
        var py   = _settings.Current.PythonVersion;
        progress.UnitsAreBytes = true;
        progress.Start(Loc.F("Dep_CreatingVenv", venv));

        if (!LinuxHost.IsWsl) Directory.CreateDirectory(Path.GetDirectoryName(venv)!);   // uv crée les dossiers manquants dans WSL
        string[] args = ["venv", "--python", py, "--seed", venv];
        log("$ uv " + string.Join(' ', args));
        var code = await ProcessRunner.RunAsync(_python.Uv, args, (l, p) => { if (!p) log(l); }, null, ct, linux: true);
        progress.Finish(code == 0 ? Loc.T("Common_Done") : Loc.F("Common_ExitCode", code));
        return code == 0;
    }

    /// <inheritdoc />
    public async Task<bool> SafeInstallAsync(IReadOnlyList<string> packages, string title, bool installsVllm,
        ProgressTracker progress, Action<string> log, CancellationToken ct, bool upgrade = false)
    {
        var before = _env.InstalledPackages();
        string? backend = null, constraints = null;

        progress.UnitsAreBytes = true;
        progress.Start(title);
        progress.SetDetail(Loc.T("Env_ChoosingBackend"));
        if (installsVllm)
        {
            var extra = upgrade ? new[] { "--upgrade" } : Array.Empty<string>();
            var choice = await _env.ChooseBackendAsync(packages, extra, log, ct);
            if (choice is not { } c)
            {
                log("✘ " + Loc.T("Env_NoConsistentBackend"));
                progress.Finish(Loc.T("Env_NoConsistentBackend"));
                return false;
            }
            backend = c.Backend;
        }
        else
            constraints = _env.WriteConstraints();

        var ok = await _uv.InstallAsync(packages, title, progress, log, ct, upgrade, backend ?? _env.SavedBackend, constraints);
        if (!ok)
        {
            if (constraints != null) log(Loc.T("Env_ConstraintHint"));
            return false;
        }
        if (backend != null) _env.Remember(backend);
        _env.WriteConstraints();

        // Vérification, réparation, retour en arrière
        progress.Start(Loc.T("Env_Testing"));
        var problem = await _env.VerifyAsync(log, ct);
        if (problem == null) { progress.Finish(Loc.T("Env_Ok")); return true; }

        log("⚠ " + problem);
        log(Loc.T("Env_TryRepair"));
        if (await _env.RepairAsync(progress, log, ct))
        {
            progress.Start(Loc.T("Env_Testing"));
            problem = await _env.VerifyAsync(log, ct);
            if (problem == null) { progress.Finish(Loc.T("Env_Repaired")); return true; }
            log("⚠ " + problem);
        }

        if (!installsVllm)
        {
            log(Loc.T("Env_RollingBack"));
            await RollbackAsync(before, progress, log, ct);
            await _env.VerifyAsync(log, ct);
        }
        progress.Finish(Loc.T("Env_StillBroken"));
        return false;
    }

    /// <summary> Retire les paquets ajoutés et remet les versions modifiées depuis l'état "before". </summary>
    private async Task RollbackAsync(Dictionary<string, string> before, ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        var after = _env.InstalledPackages();
        var added = after.Keys.Where(k => !before.ContainsKey(k)).ToList();
        var changed = before.Where(kv => after.TryGetValue(kv.Key, out var v) && v != kv.Value).Select(kv => $"{kv.Key}=={kv.Value}").ToList();

        if (added.Count > 0)
        {
            var args = new List<string> { "pip", "uninstall", "-p", _python.VenvPython };
            args.AddRange(added);
            log("$ uv " + string.Join(' ', args));
            await ProcessRunner.RunAsync(_python.Uv, args, (l, p) => { if (!p) log(l); }, null, ct, linux: true);
        }
        if (changed.Count > 0)
            await _uv.InstallAsync(changed, Loc.T("Env_RollingBack"), progress, log, ct, backend: _env.SavedBackend, extraArgs: ["--no-deps"]);
    }

    /// <inheritdoc />
    public Task<bool> InstallVllmAsync(ProgressTracker progress, Action<string> log, CancellationToken ct, bool upgrade = false) =>
        SafeInstallAsync([_settings.Current.VllmPackageSpec], Loc.T("Dep_InstallingVllm"), true, progress, log, ct, upgrade);

    /// <inheritdoc />
    public Task<bool> InstallOmniAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        // vLLM-Omni doit correspondre à la version de vLLM installée
        var v = _python.VllmVersion;
        var spec = v != null ? $"vllm-omni=={Regex.Match(v, @"^\d+\.\d+\.\d+").Value}" : "vllm-omni";
        return SafeInstallAsync([spec], Loc.T("Dep_InstallingOmni"), false, progress, log, ct);
    }

}
