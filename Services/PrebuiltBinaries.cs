/*
    Fichier      :  PrebuiltBinaries.cs
    Projet       :  AlpenMux

    Description  :
        Outils communs aux moteurs installés à partir de binaires précompilés (llama.cpp, stable-diffusion.cpp) :
            - lecture des publications GitHub (brouillons ignorés) ;
            - téléchargement d'un fichier avec progression (aussi utilisé pour l'image Ubuntu de WslService) ;
            - installation : archives téléchargées dans un dossier temporaire, extraites (.zip ou .tar.gz),
              serveur vérifié, fichier VERSION écrit, puis remplacement de la version en service
              (<dossier>/current ; les serveurs en cours gardent leurs fichiers ouverts).
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Version publiée d'un moteur et archives à télécharger pour cette machine. </summary>
/// <param name="Tag"> Nom de la version (b12345, master-1028-37bc88b…). </param>
/// <param name="Url"> Archive principale. </param>
/// <param name="Size"> Taille de l'archive principale (octets). </param>
/// <param name="CudartUrl"> Archive des bibliothèques CUDA, si nécessaire. </param>
/// <param name="CudartSize"> Taille de l'archive CUDA (octets). </param>
public sealed record BinaryRelease(string Tag, string Url, long Size, string? CudartUrl, long CudartSize);

/// <summary> Fichier joint à une publication GitHub. </summary>
/// <param name="Name"> Nom du fichier. </param>
/// <param name="Url"> Adresse de téléchargement. </param>
/// <param name="Size"> Taille (octets). </param>
public sealed record ReleaseAsset(string Name, string Url, long Size);

/// <summary> Publication GitHub (hors brouillons). </summary>
/// <param name="Tag"> Nom de la version. </param>
/// <param name="Assets"> Fichiers joints. </param>
public sealed record GitHubRelease(string Tag, IReadOnlyList<ReleaseAsset> Assets);

/// <summary> Ce qui distingue l'installation d'un moteur précompilé. </summary>
/// <param name="Product"> Nom affiché dans le journal (llama.cpp…). </param>
/// <param name="Root"> Dossier du moteur dans les données de l'application. </param>
/// <param name="ServerName"> Nom de l'exécutable du serveur. </param>
/// <param name="InstallingKey"> Clé du titre de la progression. </param>
/// <param name="NoReleaseKey"> Clé du message « aucune version trouvée ». </param>
/// <param name="NoServerKey"> Clé du message « serveur absent de l'archive ». </param>
/// <param name="AfterExtract"> Traitement facultatif une fois le serveur extrait (reçoit son chemin). </param>
public sealed record BinaryInstallSpec(string Product, string Root, string ServerName, string InstallingKey, string NoReleaseKey,
    string NoServerKey, Action<string>? AfterExtract = null)
{
    /// <summary> Version en service. </summary>
    public string CurrentDir => PrebuiltBinaries.CurrentDirOf(Root);
}

/// <summary> Publications GitHub, téléchargement et installation des moteurs précompilés. </summary>
public static class PrebuiltBinaries
{
    /// <summary> Client HTTP sans délai maximal (gros fichiers) avec le User-Agent exigé par l'API GitHub. </summary>
    public static HttpClient CreateGitHubClient()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(AppPaths.UserAgent);   // exigé par l'API GitHub
        return c;
    }

    /// <summary> Dossier de la version en service d'un moteur. </summary>
    /// <param name="root"> Dossier du moteur. </param>
    public static string CurrentDirOf(string root) => Path.Combine(root, "current");

    /// <summary> Chemin du serveur installé, ou null. </summary>
    /// <param name="currentDir"> Version en service. </param>
    /// <param name="serverName"> Nom de l'exécutable. </param>
    public static string? FindServer(string currentDir, string serverName) =>
        Directory.Exists(currentDir)
            ? Directory.EnumerateFiles(currentDir, serverName, SearchOption.AllDirectories).FirstOrDefault()
            : null;

    /// <summary> Version installée (fichier VERSION), ou null. </summary>
    /// <param name="currentDir"> Version en service. </param>
    public static string? InstalledVersion(string currentDir)
    {
        try { return File.ReadAllText(Path.Combine(currentDir, "VERSION")).Trim(); }
        catch { return null; }
    }

    /// <summary> Publications récentes, de la plus récente à la plus ancienne (brouillons exclus). </summary>
    /// <param name="http"> Client HTTP. </param>
    /// <param name="api"> Adresse de l'API (…/releases?per_page=n). </param>
    /// <param name="ct"> Annulation. </param>
    /// <returns> null si l'API répond par une erreur. </returns>
    public static async Task<List<GitHubRelease>?> ReleasesAsync(HttpClient http, string api, CancellationToken ct)
    {
        using var resp = await http.GetAsync(api, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var res = new List<GitHubRelease>();
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var assets = release.GetProperty("assets").EnumerateArray()
                .Select(a => new ReleaseAsset(a.GetProperty("name").GetString() ?? "", a.GetProperty("browser_download_url").GetString() ?? "",
                                              a.GetProperty("size").GetInt64()))
                .ToList();
            res.Add(new GitHubRelease(release.GetProperty("tag_name").GetString() ?? "?", assets));
        }
        return res;
    }

    /// <summary> Télécharge un fichier en signalant la progression. </summary>
    /// <param name="http"> Client HTTP. </param>
    /// <param name="url"> Adresse du fichier. </param>
    /// <param name="file"> Fichier créé. </param>
    /// <param name="label"> Détail affiché pendant le téléchargement. </param>
    /// <param name="progress"> Suivi de la progression. </param>
    /// <param name="ct"> Annulation. </param>
    /// <param name="offset"> Octets déjà téléchargés par les fichiers précédents du même lot. </param>
    /// <param name="total"> Taille totale du lot ; 0 = taille annoncée par le serveur (progression masquée si inconnue). </param>
    /// <returns> Nombre d'octets téléchargés. </returns>
    public static async Task<long> DownloadAsync(HttpClient http, string url, string file, string label, ProgressTracker progress,
        CancellationToken ct, long offset = 0, long total = 0)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (total <= 0) total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(file);
        var buf = new byte[1 << 16];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress.Report(offset + read, total, label);
        }
        return read;
    }

    /// <summary> Installe (ou met à jour) un moteur à partir de sa dernière publication. </summary>
    /// <param name="http"> Client HTTP. </param>
    /// <param name="spec"> Ce qui distingue ce moteur. </param>
    /// <param name="latest"> Recherche de la publication adaptée à la machine. </param>
    /// <param name="progress"> Suivi de la progression. </param>
    /// <param name="log"> Journal de l'installation. </param>
    /// <param name="ct"> Annulation. </param>
    /// <returns> Vrai si le serveur est en place. </returns>
    public static async Task<bool> InstallAsync(HttpClient http, BinaryInstallSpec spec, Func<CancellationToken, Task<BinaryRelease?>> latest,
        ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        progress.UnitsAreBytes = true;
        progress.Start(Loc.T(spec.InstallingKey));
        progress.SetDetail(Loc.T("Llama_Checking"));

        BinaryRelease? rel;
        try { rel = await latest(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { log("✘ " + ex.Message); rel = null; }
        if (rel == null)
        {
            log("✘ " + Loc.T(spec.NoReleaseKey));
            progress.Finish(Loc.T(spec.NoReleaseKey));
            return false;
        }

        Directory.CreateDirectory(spec.Root);
        var staging = Path.Combine(spec.Root, ".staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            var total = rel.Size + rel.CudartSize;
            long done = 0;
            foreach (var url in new[] { rel.Url, rel.CudartUrl }.OfType<string>())
            {
                var file = Path.Combine(staging, Path.GetFileName(new Uri(url).LocalPath));
                log("↓ " + url);
                done += await DownloadAsync(http, url, file, Path.GetFileName(file), progress, ct, done, total);

                progress.SetDetail(Loc.T("Llama_Extracting"));
                if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    await Task.Run(() => ZipFile.ExtractToDirectory(file, staging, overwriteFiles: true), ct);
                else
                {
                    var (code, output) = await ProcessRunner.CaptureAsync("tar", ["-xzf", file, "-C", staging], 300000);
                    if (code != 0) { log("✘ tar : " + output); progress.Finish(Loc.F("Common_ExitCode", code)); return false; }
                }
                File.Delete(file);
            }

            var exe = Directory.EnumerateFiles(staging, spec.ServerName, SearchOption.AllDirectories).FirstOrDefault();
            if (exe == null)
            {
                log("✘ " + Loc.T(spec.NoServerKey));
                progress.Finish(Loc.T(spec.NoServerKey));
                return false;
            }
            spec.AfterExtract?.Invoke(exe);
            await File.WriteAllTextAsync(Path.Combine(staging, "VERSION"), rel.Tag, ct);

            // Remplacement de l'ancienne version (les serveurs en cours gardent leurs fichiers ouverts)
            if (Directory.Exists(spec.CurrentDir)) Directory.Delete(spec.CurrentDir, true);
            Directory.Move(staging, spec.CurrentDir);
            log($"✔ {spec.Product} {rel.Tag} → {spec.CurrentDir}");
            progress.Finish(Loc.F("Dep_Version", rel.Tag));
            return true;
        }
        finally
        {
            if (Directory.Exists(staging)) try { Directory.Delete(staging, true); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
        }
    }
}
