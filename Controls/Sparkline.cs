/*
    Fichier      :  Sparkline.cs
    Projet       :  AlpenMux

    Description  :
        Petite courbe d'historique (aire + ligne) dessinée directement (pas de dépendance).
        Maximum = 100 (pourcentages) ou automatique si Maximum <= 0.
        Arrondi, épaisseur, aire et grille viennent des réglages du moniteur (AppSettings.Monitor*).
*/

using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using AlpenMux.Services;

namespace AlpenMux.Controls;

/// <summary> Courbe d'historique du moniteur (lissage, remplissage et repères selon les réglages). </summary>
public sealed class Sparkline : Control
{
    // Contrôle créé par le XAML : pas d'injection par constructeur possible
    private static ISettingsService Settings => App.Services.GetRequiredService<ISettingsService>();

    /// <summary> Propriété <see cref="Data"/>. </summary>
    public static readonly StyledProperty<RingBuffer?> DataProperty =
        AvaloniaProperty.Register<Sparkline, RingBuffer?>(nameof(Data));

    /// <summary> Propriété <see cref="Stroke"/>. </summary>
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.DodgerBlue);

    /// <summary> Propriété <see cref="Maximum"/>. </summary>
    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<Sparkline, double>(nameof(Maximum), 100);

    /// <summary> Valeurs affichées. </summary>
    public RingBuffer? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    /// <summary> Couleur de la courbe. </summary>
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    /// <summary> Valeur du haut du graphique (100 pour des pourcentages ; automatique si ≤ 0). </summary>
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    static Sparkline()
    {
        AffectsRender<Sparkline>(StrokeProperty, MaximumProperty);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DataProperty)
        {
            if (change.OldValue is RingBuffer o) o.Changed -= InvalidateVisual;
            if (change.NewValue is RingBuffer n && VisualRoot != null) n.Changed += InvalidateVisual;
            InvalidateVisual();
        }
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Settings.Saved += InvalidateVisual;
        if (Data != null) { Data.Changed -= InvalidateVisual; Data.Changed += InvalidateVisual; }
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Settings.Saved -= InvalidateVisual;
        if (Data != null) Data.Changed -= InvalidateVisual;
    }

    /// <inheritdoc />
    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        var stroke = Stroke ?? Brushes.DodgerBlue;
        var color = stroke is ISolidColorBrush sc ? sc.Color : Colors.DodgerBlue;

        // Lignes de repère
        var cfg = Settings.Current;
        if (cfg.MonitorGrid)
        {
            var grid = new Pen(new SolidColorBrush(color, 0.12), 1);
            for (int i = 1; i < 4; i++)
            {
                var y = b.Height * i / 4;
                ctx.DrawLine(grid, new Point(0, y), new Point(b.Width, y));
            }
        }

        var data = Data;
        if (data == null || data.Count < 2 || b.Width <= 0 || b.Height <= 0) return;

        var values = data.Values().ToArray();
        var max = Maximum > 0 ? Maximum : System.Math.Max(1, values.Max() * 1.15);
        var step = b.Width / (data.Capacity - 1);
        var x0 = b.Width - (values.Length - 1) * step;

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        var pts = new Point[values.Length];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new Point(x0 + i * step, b.Height - System.Math.Clamp(values[i] / max, 0, 1) * (b.Height - 2) - 1);
        var k = System.Math.Clamp(cfg.MonitorSmoothing, 0, 1) / 6;   // Catmull-Rom → Bézier

        using (var lc = line.Open())
        using (var ac = area.Open())
        {
            for (int i = 0; i < pts.Length; i++)
            {
                var p = pts[i];
                if (i == 0)
                {
                    lc.BeginFigure(p, false);
                    ac.BeginFigure(new Point(p.X, b.Height), true);
                    ac.LineTo(p);
                }
                else if (k <= 0) { lc.LineTo(p); ac.LineTo(p); }
                else
                {
                    var p0 = pts[System.Math.Max(i - 2, 0)];
                    var p1 = pts[i - 1];
                    var p3 = pts[System.Math.Min(i + 1, pts.Length - 1)];
                    // Les points de contrôle restent entre les deux valeurs : pas de creux sous zéro
                    var lo = System.Math.Min(p1.Y, p.Y); var hi = System.Math.Max(p1.Y, p.Y);
                    var c1 = new Point(p1.X + (p.X - p0.X) * k, System.Math.Clamp(p1.Y + (p.Y - p0.Y) * k, lo, hi));
                    var c2 = new Point(p.X - (p3.X - p1.X) * k, System.Math.Clamp(p.Y - (p3.Y - p1.Y) * k, lo, hi));
                    lc.CubicBezierTo(c1, c2, p);
                    ac.CubicBezierTo(c1, c2, p);
                }
            }
            ac.LineTo(new Point(b.Width, b.Height));
            ac.EndFigure(true);
            lc.EndFigure(false);
        }

        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(90, color.R, color.G, color.B), 0), new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1) },
        };
        if (cfg.MonitorFill) ctx.DrawGeometry(fill, null, area);
        ctx.DrawGeometry(null, new Pen(stroke, System.Math.Clamp(cfg.MonitorLineWidth, 0.5, 6), lineJoin: PenLineJoin.Round), line);
    }
}
