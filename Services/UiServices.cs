/*
    Fichier      :  UiServices.cs
    Projet       :  AlpenMux

    Description  :
        Services d'interface utilisables depuis les ViewModels sans référencer les vues :
        choix de dossier, presse-papiers, ouverture d'un dossier ou d'un lien, thème.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Services de l'interface pour les ViewModels : sélecteurs de fichiers, presse-papiers, liens, thème. </summary>
public interface IUiServices
{
    /// <summary> Choix d'un dossier (null = annulé). </summary>
    Task<string?> PickFolderAsync(string title, string? startPath = null);

    /// <summary> Choix de fichiers à ouvrir (vide = annulé). </summary>
    Task<List<string>> PickFilesAsync(string title, string typeName, params string[] patterns);

    /// <summary> Demande où enregistrer un fichier (navigateur de fichiers du système). null = annulé. </summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string typeName, string pattern);

    /// <summary> Copie un texte dans le presse-papiers. </summary>
    Task CopyAsync(string text);

    /// <summary> Copie une image dans le presse-papiers (collable dans un éditeur d'images). </summary>
    Task CopyImageAsync(Avalonia.Media.Imaging.Bitmap bitmap);

    /// <summary> Enregistre une image en PNG, à l'emplacement choisi (null = annulé). </summary>
    Task SaveImageAsync(Avalonia.Media.Imaging.Bitmap bitmap, string suggestedName);

    /// <summary> Ouvre un dossier ou une URL avec l'application par défaut (xdg-open). </summary>
    void Open(string pathOrUrl);

    /// <summary> Applique le thème ("System", "Light" ou "Dark"). </summary>
    void ApplyTheme(string theme);
}

/// <summary> Implémentation de <see cref="IUiServices"/>. </summary>
public sealed class UiServices : IUiServices
{
    private readonly ILogger<UiServices> _logger;

    /// <summary> Crée le service. </summary>
    /// <param name="logger"> Journal des erreurs. </param>
    public UiServices(ILogger<UiServices> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    private static TopLevel? Top =>
        (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;

    /// <inheritdoc />
    public async Task<string?> PickFolderAsync(string title, string? startPath = null)
    {
        if (Top?.StorageProvider is not { } sp) return null;
        IStorageFolder? start = null;
        if (!string.IsNullOrWhiteSpace(startPath) && System.IO.Directory.Exists(startPath))
            start = await sp.TryGetFolderFromPathAsync(startPath);

        var res = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });
        return res.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task<List<string>> PickFilesAsync(string title, string typeName, params string[] patterns)
    {
        if (Top?.StorageProvider is not { } sp) return [];
        var res = await sp.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = patterns }],
        });
        return res.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
    }

    /// <inheritdoc />
    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string typeName, string pattern)
    {
        if (Top?.StorageProvider is not { } sp) return null;
        var file = await sp.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = System.IO.Path.GetExtension(pattern).TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = [pattern] }],
            ShowOverwritePrompt = true,
        });
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc />
    public async Task CopyAsync(string text)
    {
        if (Top?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    /// <inheritdoc />
    public async Task CopyImageAsync(Avalonia.Media.Imaging.Bitmap bitmap)
    {
        if (Top?.Clipboard is { } cb) await cb.SetBitmapAsync(bitmap);
    }

    /// <inheritdoc />
    public async Task SaveImageAsync(Avalonia.Media.Imaging.Bitmap bitmap, string suggestedName)
    {
        var path = await PickSaveFileAsync(Loc.T("Ui_SaveImageTitle"), suggestedName, "PNG", "*.png");
        if (path == null) return;
        await using var stream = System.IO.File.Create(path);
        bitmap.Save(stream);
    }

    /// <inheritdoc />
    public void Open(string pathOrUrl)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(pathOrUrl) { UseShellExecute = true });   // explorateur ou navigateur par défaut
            else
                Process.Start(new ProcessStartInfo("xdg-open") { ArgumentList = { pathOrUrl }, UseShellExecute = false });
        }
        catch (Exception ex) { _logger.LogWarning("[xdg-open] {Message}", ex.Message); }
    }

    /// <inheritdoc />
    public void ApplyTheme(string theme)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = theme switch
            {
                "Light" => ThemeVariant.Light,
                "Dark"  => ThemeVariant.Dark,
                _       => ThemeVariant.Default,
            };
    }

    /// <summary> Découpe une ligne d'arguments comme un shell (guillemets simples et doubles). </summary>
    public static List<string> SplitArgs(string? s)
    {
        var res = new List<string>();
        if (string.IsNullOrWhiteSpace(s)) return res;
        var sb = new StringBuilder();
        bool inArg = false;
        char quote = '\0';
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else if (c == '\\' && quote == '"' && i + 1 < s.Length) sb.Append(s[++i]);
                else sb.Append(c);
            }
            else if (c is '\'' or '"') { quote = c; inArg = true; }
            else if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); inArg = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (inArg) { res.Add(sb.ToString()); sb.Clear(); inArg = false; }
            }
            else { sb.Append(c); inArg = true; }
        }
        if (inArg) res.Add(sb.ToString());
        return res;
    }
}
