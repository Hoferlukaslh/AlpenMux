/*
    Fichier      :  ProgressTracker.cs
    Projet       :  AlpenMux

    Description  :
        État d'une opération longue, lié directement aux barres de progression :
        pourcentage, vitesse (lissée) et estimation du temps restant.
        Les mises à jour peuvent venir de n'importe quel thread.
*/

using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlpenMux.Services;

public sealed partial class ProgressTracker : ObservableObject
{
    [ObservableProperty] private bool   _isActive;
    [ObservableProperty] private bool   _isIndeterminate = true;
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _title  = "";
    [ObservableProperty] private string _detail = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _speedText = "";

    /// <summary> Les quantités sont des octets (vitesse en Mo/s) ou des unités (vitesse en /s). </summary>
    public bool UnitsAreBytes { get; set; } = true;

    /// <summary> En dessous de ce pourcentage, le temps restant n'est pas affiché (estimation peu fiable). </summary>
    public double EtaMinPercent { get; set; }

    private DateTime _start;

    // Débit moyen sur une fenêtre glissante : les données arrivent parfois par à-coups
    // (gros blocs écrits d'un coup), une moyenne instantanée tomberait à zéro entre deux blocs.
    private readonly System.Collections.Generic.Queue<(DateTime At, double Done)> _samples = new();
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(15);

    /// <summary> Démarre une nouvelle progression. </summary>
    public void Start(string title, bool indeterminate = true)
    {
        _start = DateTime.UtcNow;
        lock (_samples) _samples.Clear();
        Ui(() =>
        {
            Title = title;
            Detail = "";
            EtaText = SpeedText = "";
            Percent = 0;
            IsIndeterminate = indeterminate;
            IsActive = true;
        });
    }

    /// <summary> Change le détail affiché. </summary>
    public void SetDetail(string detail) => Ui(() => Detail = detail);

    /// <summary> Passe en progression indéterminée. </summary>
    public void SetIndeterminate(string? detail = null) => Ui(() =>
    {
        IsIndeterminate = true;
        EtaText = SpeedText = "";
        if (detail != null) Detail = detail;
    });

    /// <summary> Met à jour la progression ; calcule vitesse et temps restant. </summary>
    public void Report(double done, double total, string? detail = null)
    {
        if (total <= 0) { SetIndeterminate(detail); return; }
        done = Math.Clamp(done, 0, total);

        var now = DateTime.UtcNow;
        double windowRate;
        lock (_samples)
        {
            _samples.Enqueue((now, done));
            while (_samples.Count > 2 && now - _samples.Peek().At > Window) _samples.Dequeue();
            var first = _samples.Peek();
            var dt = (now - first.At).TotalSeconds;
            windowRate = dt >= 1 ? (done - first.Done) / dt : 0;
        }

        // Sans progrès récent, on retombe sur la moyenne depuis le début
        var avg  = done / Math.Max(0.001, (now - _start).TotalSeconds);
        var rate = windowRate > 0 ? windowRate : avg;
        var remaining = rate > 0 ? TimeSpan.FromSeconds((total - done) / rate) : (TimeSpan?)null;

        var pct = done / total * 100.0;
        Ui(() =>
        {
            IsIndeterminate = false;
            Percent = pct;
            if (detail != null) Detail = detail;
            SpeedText = rate <= 0 ? "" : UnitsAreBytes ? $"{Format.Bytes(rate)}/s" : $"{rate:0.0}/s";
            // Les premières secondes, le débit n'est pas représentatif : pas d'estimation
            EtaText = remaining is { } r && done < total && pct >= Math.Max(EtaMinPercent, 1) && (now - _start).TotalSeconds >= 3
                ? Loc.F("Progress_Eta", Format.Duration(r))
                : "";
        });
    }

    /// <summary> Pourcentage d'une étape sans quantité mesurable : ni vitesse ni temps restant. </summary>
    public void SetPercent(double pct, string? detail = null) => Ui(() =>
    {
        IsIndeterminate = false;
        Percent = Math.Clamp(pct, 0, 100);
        EtaText = SpeedText = "";
        if (detail != null) Detail = detail;
    });

    /// <summary> Repart de zéro pour la vitesse et le temps restant (nouvelle étape mesurée en octets). </summary>
    public void ResetRate()
    {
        _start = DateTime.UtcNow;
        lock (_samples) _samples.Clear();
    }

    /// <summary> Termine la progression (barre pleine) avec un détail facultatif. </summary>
    public void Finish(string? detail = null) => Ui(() =>
    {
        IsIndeterminate = false;
        Percent = 100;
        EtaText = SpeedText = "";
        if (detail != null) Detail = detail;
        IsActive = false;
    });

    /// <summary> Temps écoulé depuis le début. </summary>
    public TimeSpan Elapsed => DateTime.UtcNow - _start;

    private static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}

/// <summary> Mise en forme des tailles et durées affichées. </summary>
public static class Format
{
    /// <summary> Taille lisible (Kio, Mio, Gio…). </summary>
    public static string Bytes(double b)
    {
        string[] units = ["o", "Kio", "Mio", "Gio", "Tio"];
        if (Loc.Instance.CurrentLanguage != "fr") units = ["B", "KiB", "MiB", "GiB", "TiB"];
        int i = 0;
        while (Math.Abs(b) >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {units[i]}" : $"{b:0.0} {units[i]}";
    }

    /// <summary> Taille lisible (même format que <see cref="Bytes"/>). </summary>
    public static string Gib(double b) => Bytes(b);

    /// <summary> Durée lisible (1 h 05 min, 3 min 20 s…). </summary>
    public static string Duration(TimeSpan t)
    {
        if (t.TotalSeconds < 1) return "< 1 s";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes:00} min";
        if (t.TotalMinutes >= 1) return $"{t.Minutes} min {t.Seconds:00} s";
        return $"{t.Seconds} s";
    }
}
