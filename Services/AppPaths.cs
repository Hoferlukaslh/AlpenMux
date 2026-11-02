/*
    Fichier      :  AppPaths.cs
    Projet       :  AlpenMux

    Description  :
        Nom de l'application et emplacement de ses fichiers :
            ~/.config/alpenmux/          réglages (settings.json), traductions ajoutées, caches
            ~/.local/share/alpenmux/     historique des serveurs, audios TTS, llama.cpp
*/

using System;
using System.IO;

namespace AlpenMux.Services;

/// <summary> Nom de l'application et emplacement de ses fichiers (configuration, données). </summary>
public static class AppPaths
{
    /// <summary> Nom affiché. </summary>
    public const string DisplayName = "AlpenMux";

    /// <summary> Nom technique : commande, paquet, icône, dossiers. </summary>
    public const string Id = "alpenmux";

    /// <summary> Version de l'application (x.y.z). </summary>
    public static string Version =>
        typeof(AppPaths).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?";

    /// <summary> User-Agent des requêtes HTTP. </summary>
    public static string UserAgent => $"{Id}/{Version}";

    private static string XdgConfig => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string XdgData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary> Dossier de configuration (~/.config/alpenmux). </summary>
    public static string ConfigDir => Path.Combine(XdgConfig, Id);
    /// <summary> Dossier des données (~/.local/share/alpenmux). </summary>
    public static string DataDir => Path.Combine(XdgData, Id);

    /// <summary>
    /// Extrait un script Python embarqué (Assets/Scripts) vers ~/.local/share/alpenmux/scripts, où les programmes
    /// côté Linux peuvent le lire (WSL compris). Le fichier est réécrit à chaque appel (mise à jour de l'application).
    /// </summary>
    /// <param name="fileName"> Nom du script dans Assets/Scripts. </param>
    /// <returns> Chemin local du script extrait. </returns>
    public static string ExtractScript(string fileName)
    {
        var dir = Path.Combine(DataDir, "scripts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using var s = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://alpenmux/Assets/Scripts/{fileName}"));
        using var f = File.Create(path);
        s.CopyTo(f);
        return path;
    }
}
