/*
    Fichier      :  ZoomImageView.cs
    Projet       :  AlpenMux

    Description  :
        Image zoomable et déplaçable : molette pour zoomer autour du pointeur, glisser pour déplacer (quand l'image
        dépasse la fenêtre), double-clic pour zoomer / revenir à la taille ajustée. Le zoom ne descend pas sous la taille
        ajustée (image entière visible) et ne dépasse pas MaxZoom. Un nouveau Source remet la vue à zéro.
*/

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace AlpenMux.Controls;

/// <summary> Image qu'on peut zoomer et déplacer (visionneuse du chat). </summary>
public sealed class ZoomImageView : Control
{
    /// <summary> Zoom maximal, par rapport à la taille ajustée. </summary>
    public const double MaxZoom = 16;

    /// <summary> Image affichée. </summary>
    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomImageView, Bitmap?>(nameof(Source));

    /// <summary> Image affichée. </summary>
    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    private double _zoom = 1;
    private Vector _pan;
    private bool _dragging;
    private Point _lastPoint;

    /// <summary> Facteur de zoom courant (1 = taille ajustée). </summary>
    public double Zoom => _zoom;

    /// <summary> Le zoom a changé (pour afficher le pourcentage). </summary>
    public event EventHandler? ZoomChanged;

    static ZoomImageView()
    {
        SourceProperty.Changed.AddClassHandler<ZoomImageView>((v, _) => v.Reset());
    }

    /// <summary> Crée la visionneuse. </summary>
    public ZoomImageView()
    {
        // Pixels nets en zoom avant
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        ClipToBounds = true;
    }

    /// <summary> Revient à la taille ajustée, centrée. </summary>
    public void Reset()
    {
        _zoom = 1;
        _pan = default;
        UpdateCursor();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary> Zoome (ou dézoome) autour du centre de la fenêtre. </summary>
    public void ZoomBy(double factor) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    /// <summary> Zoome autour d'un point : le pixel sous le pointeur reste sous le pointeur. </summary>
    private void ZoomAt(Point p, double factor)
    {
        if (Source == null) return;
        var newZoom = Math.Clamp(_zoom * factor, 1, MaxZoom);
        var k = newZoom / _zoom;
        if (Math.Abs(k - 1) < 1e-9) return;
        var center = new Point(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
        var d = p - center;
        _pan += new Vector(d.X * (1 - k), d.Y * (1 - k));
        _zoom = newZoom;
        ClampPan();
        UpdateCursor();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary> Taille affichée de l'image, en pixels de la fenêtre. </summary>
    private Size DisplaySize()
    {
        var bmp = Source!;
        var fit = Math.Min(Bounds.Width / bmp.Size.Width, Bounds.Height / bmp.Size.Height);
        return new Size(bmp.Size.Width * fit * _zoom, bmp.Size.Height * fit * _zoom);
    }

    /// <summary> Empêche de sortir l'image de la fenêtre au-delà de son bord. </summary>
    private void ClampPan()
    {
        if (Source == null) return;
        var s = DisplaySize();
        var maxX = Math.Max(0, (s.Width - Bounds.Width) / 2);
        var maxY = Math.Max(0, (s.Height - Bounds.Height) / 2);
        _pan = new Vector(Math.Clamp(_pan.X, -maxX, maxX), Math.Clamp(_pan.Y, -maxY, maxY));
    }

    private void UpdateCursor() =>
        Cursor = _zoom > 1 ? new Cursor(StandardCursorType.SizeAll) : Cursor.Default;

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Source is not { } bmp || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var s = DisplaySize();
        var center = new Point(Bounds.Width / 2 + _pan.X, Bounds.Height / 2 + _pan.Y);
        var dest = new Rect(center.X - s.Width / 2, center.Y - s.Height / 2, s.Width, s.Height);
        context.DrawImage(bmp, new Rect(0, 0, bmp.Size.Width, bmp.Size.Height), dest);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source == null) return;
        ZoomAt(e.GetPosition(this), Math.Pow(1.15, e.Delta.Y));
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Source == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var p = e.GetPosition(this);
        if (e.ClickCount >= 2)
        {
            // Double-clic : revient à la taille ajustée, ou zoome sur le point cliqué
            if (_zoom > 1) Reset();
            else ZoomAt(p, 2.5);
            e.Handled = true;
            return;
        }
        if (_zoom <= 1) return;
        _dragging = true;
        _lastPoint = p;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _pan += p - _lastPoint;
        _lastPoint = p;
        ClampPan();
        InvalidateVisual();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
