/*
    Fichier      :  UvPipInstaller.cs
    Projet       :  AlpenMux

    Description  :
        `uv pip install` dans le venv de vLLM, avec suivi de la progression.
        uv annonce les gros paquets ("Downloading torch (846.0MiB)") puis " Downloaded torch". Le total annoncé
        sert de référence, le débit réseau sert à suivre l'avancement entre deux annonces.
        Utilisé par DependencyService (installations) et EnvironmentChecker (réparation de PyTorch).
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Installation de paquets Python dans le venv avec uv, progression comprise. </summary>
public interface IUvPipInstaller
{
    /// <summary> uv pip install dans le venv, avec progression (octets) et temps restant. </summary>
    /// <param name="packages"> Paquets ou spécifications pip ("vllm", "torch==2.9.0"…). </param>
    /// <param name="title"> Titre affiché par la barre de progression. </param>
    /// <param name="progress"> Suivi de la progression. </param>
    /// <param name="log"> Reçoit chaque ligne de sortie de uv. </param>
    /// <param name="ct"> Annulation de l'installation. </param>
    /// <param name="upgrade"> Ajoute --upgrade. </param>
    /// <param name="backend"> Build PyTorch (--torch-backend) ; null = "auto". </param>
    /// <param name="constraints"> Fichier de contraintes (chemin Linux) ou null. </param>
    /// <param name="extraArgs"> Arguments supplémentaires pour uv. </param>
    /// <returns> Vrai si uv s'est terminé sans erreur. </returns>
    Task<bool> InstallAsync(IEnumerable<string> packages, string title,
        ProgressTracker progress, Action<string> log, CancellationToken ct, bool upgrade = false,
        string? backend = null, string? constraints = null, IEnumerable<string>? extraArgs = null);
}

/// <summary> Implémentation de <see cref="IUvPipInstaller"/>. </summary>
public sealed partial class UvPipInstaller : IUvPipInstaller
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;

    /// <summary> Crée l'installateur. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Chemins de uv et du Python du venv. </param>
    public UvPipInstaller(ISettingsService settings, IPythonEnvironment python)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        _settings = settings;
        _python = python;
    }

    [GeneratedRegex(@"^Downloading (\S+) \(([\d.]+)\s*([KMGT]i?B)\)")]
    private static partial Regex UvDownloadingRegex();

    [GeneratedRegex(@"^\s*Downloaded (\S+)")]
    private static partial Regex UvDownloadedRegex();

    [GeneratedRegex(@"^(Resolved|Prepared|Installed|Uninstalled|Audited) (\d+) package")]
    private static partial Regex UvSummaryRegex();

    private static double ParseSize(string v, string unit)
    {
        var x = double.Parse(v, CultureInfo.InvariantCulture);
        return x * unit[0] switch { 'K' => 1024, 'M' => 1024 * 1024, 'G' => 1024d * 1024 * 1024, 'T' => 1024d * 1024 * 1024 * 1024, _ => 1 };
    }

    /// <inheritdoc />
    public async Task<bool> InstallAsync(IEnumerable<string> packages, string title,
        ProgressTracker progress, Action<string> log, CancellationToken ct, bool upgrade = false,
        string? backend = null, string? constraints = null, IEnumerable<string>? extraArgs = null)
    {
        progress.UnitsAreBytes = true;
        progress.Start(title);
        progress.SetDetail(Loc.T("Dep_Resolving"));

        var args = new List<string> { "pip", "install", "-p", _python.VenvPython };
        if (upgrade) args.Add("--upgrade");
        args.AddRange(packages);
        args.Add($"--torch-backend={backend ?? "auto"}");
        if (constraints != null) { args.Add("--constraint"); args.Add(constraints); }
        if (extraArgs != null) args.AddRange(extraArgs);
        log("$ uv " + string.Join(' ', args));

        var sizes = new Dictionary<string, double>();
        var completed = new HashSet<string>();
        double announced = 0;
        bool downloadPhase = true;
        long netStart = SystemMonitorService.ReadNetRxTotal();
        var gate = new object();

        int code;
        await using (BackgroundTicker.Start(TimeSpan.FromSeconds(1), () =>
        {
            lock (gate)
            {
                if (!downloadPhase || announced <= 0) return;
                var doneByEvents = completed.Sum(n => sizes.GetValueOrDefault(n));
                var net  = SystemMonitorService.ReadNetRxTotal() - netStart;
                var done = Math.Max(doneByEvents, Math.Min(net, announced * 0.99));
                progress.Report(done, announced,
                    Loc.F("Dep_DownloadedOf", Format.Bytes(done), Format.Bytes(announced), completed.Count, sizes.Count));
            }
        }, ct))
        {
            code = await ProcessRunner.RunAsync(_python.Uv, args, (line, isProgress) =>
            {
                if (isProgress) return;
                log(line);
                lock (gate)
                {
                    if (UvDownloadingRegex().Match(line) is { Success: true } d)
                    {
                        var size = ParseSize(d.Groups[2].Value, d.Groups[3].Value);
                        if (sizes.TryAdd(d.Groups[1].Value, size)) announced += size;
                        if (sizes.Count == 1) netStart = SystemMonitorService.ReadNetRxTotal();
                    }
                    else if (UvDownloadedRegex().Match(line) is { Success: true } dd)
                        completed.Add(dd.Groups[1].Value);
                    else if (UvSummaryRegex().Match(line) is { Success: true } s)
                    {
                        switch (s.Groups[1].Value)
                        {
                            case "Resolved":
                                progress.SetDetail(Loc.F("Dep_ResolvedN", s.Groups[2].Value));
                                break;
                            case "Prepared":
                                downloadPhase = false;
                                progress.SetIndeterminate(Loc.T("Dep_InstallingFiles"));
                                break;
                        }
                    }
                }
            }, new Dictionary<string, string?> { ["VIRTUAL_ENV"] = _settings.VenvPath }, ct, linux: true);
        }

        progress.Finish(code == 0 ? Loc.T("Common_Done") : Loc.F("Common_ExitCode", code));
        return code == 0;
    }
}
