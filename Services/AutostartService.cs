/*
    Fichier      :  AutostartService.cs
    Projet       :  AlpenMux

    Description  :
        Lancement automatique à l'ouverture de la session (norme XDG) :
        ~/.config/autostart/alpenmux.desktop, démarrage directement en arrière-plan (--minimized).
        Windows : valeur « AlpenMux » de HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
*/

using System;
using System.IO;

namespace AlpenMux.Services;

/// <summary> Lancement automatique à l'ouverture de session (fichier .desktop sous Linux, registre sous Windows). </summary>
public static class AutostartService
{
    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart", AppPaths.Id + ".desktop");

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary> Le lancement automatique est activé. </summary>
    public static bool IsEnabled => OperatingSystem.IsWindows()
        ? Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(AppPaths.DisplayName) != null
        : File.Exists(FilePath);

    /// <summary> Active ou désactive le lancement automatique. </summary>
    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
                if (enabled) key.SetValue(AppPaths.DisplayName, $"\"{Environment.ProcessPath}\" --minimized");
                else key.DeleteValue(AppPaths.DisplayName, throwOnMissingValue: false);
                return;
            }
            if (!enabled) { if (File.Exists(FilePath)) File.Delete(FilePath); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var exe = Environment.ProcessPath ?? AppPaths.Id;
            File.WriteAllText(FilePath, $"""
                [Desktop Entry]
                Type=Application
                Name={AppPaths.DisplayName}
                Comment={AppPaths.DisplayName} (arrière-plan)
                Exec="{exe}" --minimized
                Icon={AppPaths.Id}
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """ + "\n");
        }
        catch (Exception ex) { Console.Error.WriteLine($"[Autostart] {ex.Message}"); }
    }
}
