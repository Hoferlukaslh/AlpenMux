/*
    Fichier      :  VramBar.cs
    Projet       :  AlpenMux

    Description  :
        Barre empilée de la VRAM d'un GPU : ce qui est déjà occupé (autres applications,
        serveurs lancés par l'application), puis ce que le lancement en préparation va réserver
        (encadré en pointillés : poids, surcoût, cache KV…), puis la VRAM qui restera libre.
        Si la demande dépasse la VRAM, un trait marque la fin du GPU et le surplus est en rouge.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace AlpenMux.Controls;

/// <summary> Une part de la VRAM. IsNew = réservée par le lancement en préparation. </summary>
public sealed record VramSegment(string Label, string ShortLabel, double Bytes, IBrush Brush, bool IsNew, string? Detail = null)
{
    /// <summary> Taille lisible de la part. </summary>
    public string SizeText => AlpenMux.Services.Format.Bytes(Bytes);
    /// <summary> La part a une précision affichée sous son libellé. </summary>
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
}

/// <summary> Barre horizontale de la VRAM, découpée en parts colorées. </summary>
public sealed class VramBar : Control
{
    /// <summary> Propriété <see cref="Segments"/>. </summary>
    public static readonly StyledProperty<IReadOnlyList<VramSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<VramBar, IReadOnlyList<VramSegment>?>(nameof(Segments));

    /// <summary> Propriété <see cref="Total"/>. </summary>
    public static readonly StyledProperty<double> TotalProperty =
        AvaloniaProperty.Register<VramBar, double>(nameof(Total));

    /// <summary> Parts affichées. </summary>
    public IReadOnlyList<VramSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    /// <summary> Mémoire totale du GPU (octets). </summary>
    public double Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }

    private const double Radius = 7;

    static VramBar()
    {
        AffectsRender<VramBar>(SegmentsProperty, TotalProperty);
        HeightProperty.OverrideDefaultValue<VramBar>(34);
    }

    private static bool IsLight(IBrush b) =>
        b is ISolidColorBrush { Color: var c } && (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255 > 0.62;

    private IBrush Res(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : fallback;

    /// <inheritdoc />
    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var segs = Segments ?? [];
        if (w <= 0 || h <= 0 || Total <= 0) return;

        var sum = segs.Sum(s => Math.Max(0, s.Bytes));
        var span = Math.Max(Total, sum);          // en cas de dépassement, la barre montre aussi le surplus
        var scale = w / span;
        var gpuEnd = Total * scale;

        var bar = new RoundedRect(new Rect(0, 0, w, h), Radius);
        using (ctx.PushClip(bar))
        {
            ctx.FillRectangle(Res("VramFree", Brushes.DimGray), new Rect(0, 0, gpuEnd, h));

            double x = 0, newStart = -1, newEnd = -1;
            var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyle.Normal, FontWeight.Bold);
            foreach (var s in segs)
            {
                var sw = Math.Max(0, s.Bytes) * scale;
                if (sw <= 0) continue;
                ctx.FillRectangle(s.Brush, new Rect(x, 0, sw, h));
                // Séparation fine entre deux parts
                ctx.FillRectangle(Res("Surface", Brushes.Black), new Rect(x + sw - 1, 0, 1, h));
                if (s.IsNew)
                {
                    if (newStart < 0) newStart = x;
                    newEnd = x + sw;
                }

                // Libellé à l'intérieur si la part est assez large (texte sombre sur une part claire)
                var ink = IsLight(s.Brush) ? new ImmutableSolidColorBrush(Color.FromRgb(0x1C, 0x1F, 0x26)) : Brushes.White;
                var text = new FormattedText($"{s.ShortLabel} {s.SizeText}", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                    typeface, 11, ink);
                if (text.Width + 10 > sw)
                    text = new FormattedText(s.SizeText, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, ink);
                if (text.Width + 10 <= sw)
                    ctx.DrawText(text, new Point(x + (sw - text.Width) / 2, (h - text.Height) / 2));
                x += sw;
            }

            // Surplus au-delà de la VRAM du GPU
            if (sum > Total)
            {
                var err = Res("ErrorBrush", Brushes.Red);
                ctx.FillRectangle(new ImmutableSolidColorBrush(((ISolidColorBrush)err).Color, 0.55), new Rect(gpuEnd, 0, w - gpuEnd, h));
                ctx.FillRectangle(err, new Rect(gpuEnd - 1.5, 0, 3, h));
            }

            // Cadre en pointillés autour de ce que le lancement va réserver
            if (newStart >= 0)
            {
                var pen = new Pen(Res("PrimaryForeground", Brushes.White), 2, new DashStyle([3, 2], 0));
                ctx.DrawRectangle(null, pen, new Rect(newStart + 1, 1, Math.Max(0, newEnd - newStart - 2), h - 2), 4, 4);
            }
        }
        ctx.DrawRectangle(null, new Pen(Res("SurfaceBorder", Brushes.Gray), 1), bar);
    }
}
