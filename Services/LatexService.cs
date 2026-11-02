/*
    Fichier      :  LatexService.cs
    Projet       :  AlpenMux

    Description  :
        Formules LaTeX des réponses du chat ($$…$$, \[…\], $…$, \(…\)) dessinées en PNG, en local, avec ziamath :
        Python pur (la police STIX Two Math est embarquée), sans TeX, sans navigateur ni réseau.
            - ziamath installé à la demande dans le venv (comme mermaidx) ;
            - un lancement de Python par réponse (Assets/Scripts/render_latex.py) : SVG dans le cache ;
            - SVG converti en PNG avec Svg.Skia (Markdown.Avalonia n'affiche pas le SVG) ;
            - images en cache (empreinte de la formule + thème), fond transparent, couleur du thème ;
            - une formule invalide reste affichée telle quelle (code LaTeX).
        Les blocs de code et le code en ligne ne sont jamais convertis.
*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using Svg.Skia;

namespace AlpenMux.Services;

/// <summary> Rendu des formules LaTeX en PNG (paquet Python ziamath dans le venv). </summary>
public interface ILatexService
{
    /// <summary> Version de ziamath installée, ou null. </summary>
    string? Version { get; }

    /// <summary> ziamath est installé dans le venv. </summary>
    bool Installed { get; }

    /// <summary> Installe ziamath dans le venv (installation sûre). Retourne vrai s'il est installé. </summary>
    Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);

    /// <summary> Dessine les formules (celles déjà en cache ne sont pas redessinées). </summary>
    Task<List<LatexService.Result>> RenderAsync(IReadOnlyList<LatexService.Formula> formulas, bool dark, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="ILatexService"/>. </summary>
public sealed partial class LatexService : ILatexService
{
    private readonly IPythonEnvironment _python;
    private readonly IDependencyService _dependencies;
    private readonly ISettingsService _settings;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv et versions installées. </param>
    /// <param name="dependencies"> Installation sûre du paquet dans le venv. </param>
    public LatexService(ISettingsService settings, IPythonEnvironment python, IDependencyService dependencies)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(dependencies);
        _settings = settings;
        _python = python;
        _dependencies = dependencies;
    }

    /// <summary> Paquet Python du rendu. </summary>
    public const string Package = "ziamath";

    /// <summary> Version du rendu : à changer quand l'aspect des formules change, pour ne pas reprendre le cache. </summary>
    private const string RenderVersion = "r1:";
    /// <summary>
    /// Taille de police (unités du SVG). Markdown.Avalonia affiche le PNG à sa taille en pixels : avec le facteur
    /// <see cref="Scale"/>, cette taille donne une formule à la taille du texte des bulles.
    /// </summary>
    private const float FontSize = 9;
    /// <summary> Facteur de rastérisation : PNG deux fois plus de pixels, net sur les écrans haute densité. </summary>
    private const float Scale = 2;

    private static string CacheDir => Path.Combine(AppPaths.DataDir, "formulas");
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static readonly SemaphoreSlim RenderLock = new(1, 1);
    /// <summary> Erreurs déjà obtenues (par empreinte) : une formule invalide n'est pas réessayée à chaque affichage. </summary>
    private static readonly ConcurrentDictionary<string, string> Failures = new();

    /// <summary> Formule trouvée dans un texte : LaTeX sans délimiteurs, et mode bloc (faux) ou en ligne (vrai). </summary>
    public sealed record Formula(string Tex, bool Inline);

    /// <summary> Résultat d'une formule : image PNG, ou erreur. </summary>
    public sealed record Result(string? Png, string? Error);

    /// <summary> Position d'une formule dans le texte (pour la remplacer par son image). </summary>
    public sealed record FormulaSpan(int Index, int Length, Formula Formula);

    /// <inheritdoc />
    public string? Version => _python.PackageVersion(Package);
    /// <inheritdoc />
    public bool Installed => Version != null;

    /// <inheritdoc />
    public async Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        await InstallLock.WaitAsync(ct);
        try
        {
            if (Installed) return true;
            if (!_python.VenvExists) { log(Loc.T("Chat_DocNoVenv")); return false; }
            return await _dependencies.SafeInstallAsync([Package], Loc.T("Dep_InstallingLatex"), false, progress, log, ct) && Installed;
        }
        finally { InstallLock.Release(); }
    }

    // Code (``` ou `) ignoré ; $$…$$ et \[…\] en bloc ; \(…\) et $…$ en ligne. Le $ ouvrant ne suit pas un mot
    // (prix « 5$ ») et n'est pas suivi d'un espace ; le $ fermant ne précède pas un mot.
    [GeneratedRegex(@"(?<code>```.*?```|`[^`\n]+`)|\$\$(?<display>.+?)\$\$|\\\[(?<display>.+?)\\\]|\\\((?<inline>.+?)\\\)|(?<![\w\\$])\$(?=\S)(?<inline>[^$\n]+?)(?<=\S)\$(?![\w$])", RegexOptions.Singleline)]
    private static partial Regex FormulaRegex();

    /// <summary> Formules d'un texte, hors blocs et code en ligne. </summary>
    public static List<FormulaSpan> Find(string text)
    {
        var spans = new List<FormulaSpan>();
        foreach (Match m in FormulaRegex().Matches(text))
        {
            if (m.Groups["code"].Success) continue;
            var display = m.Groups["display"].Success;
            var tex = (display ? m.Groups["display"] : m.Groups["inline"]).Value.Trim();
            if (tex.Length == 0) continue;
            spans.Add(new FormulaSpan(m.Index, m.Length, new Formula(tex, !display)));
        }
        return spans;
    }

    /// <summary>
    /// Remplace chaque formule dont l'image existe (<paramref name="imageFor"/> renvoie son PNG) par une image Markdown.
    /// Les formules en bloc sont isolées sur leur propre paragraphe ; les autres restent dans la phrase.
    /// </summary>
    public static string Substitute(string text, Func<Formula, string?> imageFor)
    {
        var spans = Find(text);
        if (spans.Count == 0) return text;
        var sb = new StringBuilder();
        int pos = 0;
        foreach (var span in spans)
        {
            if (imageFor(span.Formula) is not { } png) continue;
            var image = $"![{Loc.T("Latex_Alt")}]({new Uri(png).AbsoluteUri})";
            sb.Append(text, pos, span.Index - pos);
            sb.Append(span.Formula.Inline ? image : $"\n\n{image}\n\n");
            pos = span.Index + span.Length;
        }
        sb.Append(text, pos, text.Length - pos);
        return sb.ToString();
    }

    /// <inheritdoc />
    public async Task<List<Result>> RenderAsync(IReadOnlyList<Formula> formulas, bool dark, CancellationToken ct)
    {
        Directory.CreateDirectory(CacheDir);
        var results = new Result?[formulas.Count];
        var todo = new List<(int Index, string Png)>();
        for (int i = 0; i < formulas.Count; i++)
        {
            var hash = Hash(formulas[i], dark);
            var png = Path.Combine(CacheDir, hash + ".png");
            if (File.Exists(png)) results[i] = new Result(png, null);
            else if (Failures.TryGetValue(hash, out var known)) results[i] = new Result(null, known);
            else todo.Add((i, png));
        }

        if (todo.Count > 0)
        {
            // Un lancement de Python à la fois : plusieurs messages chargés ensemble ne se disputent pas le venv
            await RenderLock.WaitAsync(ct);
            var request = Path.Combine(CacheDir, $"request-{Guid.NewGuid():N}.json");
            await using (var fs = File.Create(request))
            await using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject();
                w.WriteString("color", dark ? "#E8EAF0" : "#1C1F26");   // couleur du texte du thème de l'application
                w.WriteStartArray("items");
                foreach (var (index, png) in todo)
                {
                    w.WriteStartObject();
                    w.WriteString("latex", formulas[index].Tex);
                    w.WriteBoolean("inline", formulas[index].Inline);
                    w.WriteNumber("size", FontSize);
                    w.WriteString("out", LinuxHost.ToLinux(Path.ChangeExtension(png, ".svg")));   // Windows : le Python du venv tourne dans WSL
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            try
            {
                var lines = new List<string>();
                var env = new Dictionary<string, string?> { ["VIRTUAL_ENV"] = _settings.VenvPath, ["PYTHONIOENCODING"] = "utf-8" };
                var exit = await ProcessRunner.RunAsync(_python.VenvPython,
                    ["-I", LinuxHost.ToLinux(AppPaths.ExtractScript("render_latex.py")), LinuxHost.ToLinux(request)],
                    (l, p) => { if (!p && l.TrimStart().StartsWith('{')) lines.Add(l.Trim()); }, env, ct, linux: true);
                for (int k = 0; k < todo.Count; k++)
                {
                    var (index, png) = todo[k];
                    var svg = Path.ChangeExtension(png, ".svg");
                    string? error = k < lines.Count ? null : Loc.F("Common_ExitCode", exit);
                    if (k < lines.Count)
                    {
                        using var doc = JsonDocument.Parse(lines[k]);
                        if (!doc.RootElement.GetProperty("ok").GetBoolean())
                            error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : "?";
                    }
                    if (error == null && File.Exists(svg))
                    {
                        try { Rasterize(svg, png); }
                        catch (Exception ex) { error = ex.Message; }
                    }
                    results[index] = error == null && File.Exists(png) ? new Result(png, null) : new Result(null, error ?? "?");
                    // Erreur de syntaxe (pas un échec du lancement) : mémorisée pour la session
                    if (results[index]!.Png == null && k < lines.Count) Failures[Path.GetFileNameWithoutExtension(png)] = results[index]!.Error!;
                    try { File.Delete(svg); } catch { /* SVG temporaire : laissé en place si verrouillé, sans gravité */ }
                }
            }
            finally
            {
                try { File.Delete(request); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ }
                RenderLock.Release();
            }
        }
        return results.Select(r => r!).ToList();
    }

    /// <summary> Convertit le SVG de ziamath en PNG transparent (Svg.Skia), écrit de façon atomique. </summary>
    private static void Rasterize(string svgPath, string pngPath)
    {
        using var svg = new SKSvg();
        if (svg.Load(svgPath) is not { } picture) throw new InvalidOperationException("SVG illisible");
        var width = Math.Max(1, (int)Math.Ceiling(picture.CullRect.Width * Scale));
        var height = Math.Max(1, (int)Math.Ceiling(picture.CullRect.Height * Scale));
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(Scale);
            canvas.DrawPicture(picture);
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var tmp = pngPath + ".tmp";
        using (var fs = File.Create(tmp)) data.SaveTo(fs);
        File.Move(tmp, pngPath, overwrite: true);
    }

    private static string Hash(Formula formula, bool dark) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{RenderVersion}{(dark ? "dark" : "light")}:{(formula.Inline ? "i" : "d")}:{formula.Tex}")))[..20].ToLowerInvariant();
}
