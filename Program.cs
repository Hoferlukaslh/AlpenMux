/*
    Fichier      :  Program.cs
    Projet       :  AlpenMux

    Description  :
        Point d'entrée : instance unique, option --minimized, démarrage d'Avalonia.
*/

using System;
using System.Linq;
using Avalonia;
using AlpenMux.Services;

namespace AlpenMux;

internal sealed class Program
{
    /// <summary> --minimized : démarrer directement en arrière-plan (zone de notification). </summary>
    public static bool StartMinimized { get; private set; }

    [STAThread]
    public static void Main(string[] args)
    {
        StartMinimized = args.Contains("--minimized");
        // Déjà lancée (par exemple en arrière-plan) : on réaffiche la fenêtre existante
        if (SingleInstance.NotifyExisting()) return;
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { SingleInstance.Release(); }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Limite le cache GPU de Skia : la VRAM est réservée aux modèles
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 128 * 1024 * 1024 });
#if DEBUG
        builder.LogToTrace(Avalonia.Logging.LogEventLevel.Warning);
#endif
        return builder;
    }
}
