/*
    Fichier      :  MarkItDownService.cs
    Projet       :  AlpenMux

    Description  :
        Documents joints aux discussions : convertis en Markdown par markitdown (Microsoft), installé dans le venv
        (paquet markitdown avec les formats bureautiques : PDF, Word, PowerPoint, Excel, Outlook…).
        Le modèle reçoit le texte Markdown, pas le fichier. Les fichiers texte (.txt, .md…) sont lus tels quels.
        markitdown est installé à la demande (première pièce jointe) ou depuis la page Installation.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Conversion de documents (PDF, Word, Excel…) en Markdown avec MarkItDown, pour les joindre au chat. </summary>
public interface IMarkItDownService
{
    /// <summary> Exécutable markitdown du venv (chemin Linux). </summary>
    string Exe { get; }

    /// <summary> MarkItDown est installé dans le venv. </summary>
    bool Installed { get; }

    /// <summary> Version installée, ou null. </summary>
    string? Version { get; }

    /// <summary> Installe MarkItDown dans le venv (installation sûre). </summary>
    Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct);

    /// <summary> Contenu Markdown du fichier ; installe markitdown s'il manque. Lève une exception en cas d'échec. </summary>
    Task<string> ConvertAsync(string file, Action<string>? status, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="IMarkItDownService"/>. </summary>
public sealed class MarkItDownService : IMarkItDownService
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;
    private readonly IDependencyService _dependencies;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv et versions installées. </param>
    /// <param name="dependencies"> Installation sûre du paquet dans le venv. </param>
    public MarkItDownService(ISettingsService settings, IPythonEnvironment python, IDependencyService dependencies)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(dependencies);
        _settings = settings;
        _python = python;
        _dependencies = dependencies;
    }
    /// <summary> Paquet installé : markitdown avec les convertisseurs des formats de documents courants. </summary>
    public const string Package = "markitdown[pdf,docx,pptx,xlsx,xls,outlook]";

    /// <summary> Lus directement, sans conversion. </summary>
    public static readonly string[] TextExtensions = [".txt", ".md", ".markdown", ".log", ".text"];

    /// <summary> Formats proposés dans le sélecteur de fichiers (texte + ceux de markitdown). </summary>
    public static readonly string[] Patterns =
    [
        "*.txt", "*.md", "*.markdown", "*.log", "*.pdf", "*.docx", "*.pptx", "*.xlsx", "*.xls", "*.csv", "*.json", "*.xml",
        "*.html", "*.htm", "*.epub", "*.msg", "*.zip",
    ];

    /// <inheritdoc />
    public string Exe => LinuxHost.Combine(_settings.VenvPath, "bin", "markitdown");
    /// <inheritdoc />
    public bool Installed => LinuxHost.FileExists(Exe);
    /// <inheritdoc />
    public string? Version => _python.PackageVersion("markitdown");

    /// <summary> Fichier texte lu directement, sans conversion. </summary>
    public static bool IsPlainText(string file) => TextExtensions.Contains(Path.GetExtension(file).ToLowerInvariant());

    /// <inheritdoc />
    public Task<bool> InstallAsync(ProgressTracker progress, Action<string> log, CancellationToken ct) =>
        _dependencies.SafeInstallAsync([Package], Loc.T("Dep_InstallingMarkItDown"), false, progress, log, ct);

    /// <inheritdoc />
    public async Task<string> ConvertAsync(string file, Action<string>? status, CancellationToken ct)
    {
        if (IsPlainText(file)) return await File.ReadAllTextAsync(file, ct);

        if (!Installed)
        {
            if (!_python.VenvExists) throw new InvalidOperationException(Loc.T("Chat_DocNoVenv"));
            status?.Invoke(Loc.T("Chat_DocInstalling"));
            var log = new StringBuilder();
            if (!await InstallAsync(new ProgressTracker(), l => log.AppendLine(l), ct) || !Installed)
                throw new InvalidOperationException(Loc.T("Chat_DocInstallFailed") + " " + log.ToString().Split('\n').LastOrDefault(l => l.Trim().Length > 0));
        }

        status?.Invoke(Loc.T("Chat_DocConverting"));
        // Sortie dans un fichier (-o) : les avertissements de markitdown (stderr) ne se mêlent pas au texte
        var tmpDir = Path.Combine(AppPaths.DataDir, "tmp");
        Directory.CreateDirectory(tmpDir);
        var output = Path.Combine(tmpDir, $"doc-{Guid.NewGuid():N}.md");
        try
        {
            var env = new Dictionary<string, string?> { ["VIRTUAL_ENV"] = _settings.VenvPath, ["PYTHONIOENCODING"] = "utf-8" };
            var (code, text) = await ProcessRunner.CaptureAsync(Exe, [LinuxHost.ToLinux(file), "-o", LinuxHost.ToLinux(output)], 300_000, env, linux: true);
            if (code != 0 || !File.Exists(output))
                throw new InvalidOperationException(Loc.F("Chat_DocConvertFailed", text.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim() ?? code.ToString()));
            return await File.ReadAllTextAsync(output, ct);
        }
        finally { try { File.Delete(output); } catch { /* suppression facultative (fichier déjà absent ou verrouillé) */ } }
    }
}
