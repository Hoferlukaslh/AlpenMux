/*
    Fichier      :  AudioPlayer.cs
    Projet       :  AlpenMux

    Description  :
        Lecture des fichiers audio (WAV) sans bibliothèque supplémentaire : le premier lecteur
        en ligne de commande disponible est utilisé (pw-play, paplay, ffplay, mpv, aplay).
        Windows : ffplay ou mpv s'ils sont installés, sinon le lecteur WAV intégré à Windows (via PowerShell).
        Un seul son à la fois : une nouvelle lecture arrête la précédente.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia.Threading;

namespace AlpenMux.Services;

/// <summary> Lecture des audios générés avec un lecteur du système (pw-play, paplay, aplay, ffplay…). </summary>
public interface IAudioPlayer
{
    /// <summary> Fichier en cours de lecture (null = silence). Toujours modifié sur le thread de l'interface. </summary>
    string? Current { get; }

    /// <summary> Levé quand la lecture commence ou s'arrête. </summary>
    event Action? CurrentChanged;

    /// <summary> Lance la lecture. Retourne false si aucun lecteur n'est installé. </summary>
    bool Play(string path);

    /// <summary> Arrête la lecture en cours. </summary>
    void Stop();
}

/// <summary> Implémentation de <see cref="IAudioPlayer"/>. </summary>
public sealed class AudioPlayer : IAudioPlayer
{

    private static readonly (string Exe, string[] Args)[] Players =
    [
        ("pw-play", []),
        ("paplay",  []),
        ("ffplay",  ["-nodisp", "-autoexit", "-loglevel", "quiet"]),
        ("mpv",     ["--no-video", "--really-quiet"]),
        ("aplay",   ["-q"]),
        // Windows : lecteur WAV de Windows ({0} = fichier, guillemets simples doublés)
        ("powershell", ["-NoProfile", "-NonInteractive", "-Command", "(New-Object Media.SoundPlayer '{0}').PlaySync()"]),
    ];

    /// <summary> Lecteurs utilisables sur ce système (PowerShell seulement sous Windows). </summary>
    private static IEnumerable<(string Exe, string[] Args)> Available =>
        Players.Where(p => (p.Exe != "powershell" || OperatingSystem.IsWindows()) && ProcessRunner.Which(p.Exe) != null);

    private Process? _process;

    /// <inheritdoc />
    public string? Current { get; private set; }

    /// <inheritdoc />
    public event Action? CurrentChanged;

    /// <summary> Lecteur trouvé (null = aucun : la lecture est impossible). </summary>
    public static string? PlayerName => Available.FirstOrDefault().Exe;

    /// <inheritdoc />
    public bool Play(string path)
    {
        Stop();
        var player = Available.FirstOrDefault();
        if (player.Exe == null) return false;
        try
        {
            var psi = new ProcessStartInfo(player.Exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            var templated = player.Args.Any(a => a.Contains("{0}"));
            foreach (var a in player.Args) psi.ArgumentList.Add(templated ? a.Replace("{0}", path.Replace("'", "''")) : a);
            if (!templated) psi.ArgumentList.Add(path);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Exited += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_process != p) return;
                _process = null;
                SetCurrent(null);
            });
            p.Start();
            p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEndAsync();
            _process = p;
            SetCurrent(path);
            return true;
        }
        catch { return false; }
    }

    /// <inheritdoc />
    public void Stop()
    {
        var p = _process;
        _process = null;
        if (p != null)
        {
            try { if (!p.HasExited) p.Kill(); } catch { /* processus déjà terminé */ }
            p.Dispose();
        }
        SetCurrent(null);
    }

    private void SetCurrent(string? path)
    {
        if (Current == path) return;
        Current = path;
        CurrentChanged?.Invoke();
    }
}
