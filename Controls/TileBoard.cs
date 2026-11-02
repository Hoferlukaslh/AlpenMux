/*
    Fichier      :  TileBoard.cs
    Projet       :  AlpenMux

    Description  :
        Tableau de widgets du moniteur : grille de 12 colonnes, lignes de hauteur fixe. Chaque carte est placée selon
        le Col / Row / W / H de son MonitorTileViewModel. En mode édition (MonitorViewModel.IsEditing), on déplace
        une carte en la glissant et on la redimensionne par ses bords (zones de classe « rz », Tag r / b / rb) ;
        un cadre indique où elle sera posée et les autres cartes s'écartent (calcul : MonitorLayout.Resolve).
        Les boutons (Button) d'une carte restent cliquables. Le calcul n'est validé qu'au lâcher.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using AlpenMux.Services;
using AlpenMux.ViewModels;

namespace AlpenMux.Controls;

/// <summary> Grille de 12 colonnes des widgets du moniteur : placement, glisser-déposer et redimensionnement en mode Personnaliser. </summary>
public sealed class TileBoard : Panel
{
    /// <summary> Hauteur d'une ligne de la grille (px). </summary>
    public const double RowHeight = 40;
    /// <summary> Espace entre deux widgets (px). </summary>
    public const double Gap = 12;

    private MonitorViewModel? _vm;
    private Func<MonitorTileViewModel, Control>? _factory;
    private readonly Dictionary<MonitorTileViewModel, Control> _frames = new();
    private readonly Border _ghost = new() { IsVisible = false, IsHitTestVisible = false };
    private double _cellW = 80;
    private Drag? _drag;
    private readonly Backdrop _backdrop;

    private sealed class Drag
    {
        public required MonitorTileViewModel Tile;
        public required Control Frame;
        public bool Move, ResizeX, ResizeY;
        public Point Start;
        public Rect StartPx, FloatPx;
        public TileRect Target;
        public Dictionary<string, TileRect> Preview = new();
    }

    /// <summary> Crée la grille. </summary>
    public TileBoard()
    {
        _backdrop = new Backdrop(this) { IsHitTestVisible = false };
        _ghost.Classes.Add("tileGhost");
        Children.Add(_backdrop);
        Children.Add(_ghost);
    }

    /// <summary> Branche le tableau sur le moniteur ; <paramref name="factory"/> construit le contenu d'une carte. </summary>
    public void Attach(MonitorViewModel vm, Func<MonitorTileViewModel, Control> factory)
    {
        _vm = vm;
        _factory = factory;
        vm.Tiles.CollectionChanged += (_, _) => Sync();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MonitorViewModel.IsEditing)) return;
            _drag = null;
            _ghost.IsVisible = false;
            InvalidateMeasure();
            _backdrop.InvalidateVisual();
        };
        Sync();
    }

    private void Sync()
    {
        if (_vm == null || _factory == null) return;
        foreach (var old in _frames.Keys.Where(t => !_vm.Tiles.Contains(t)).ToList())
        {
            old.PropertyChanged -= OnTileChanged;
            Children.Remove(_frames[old]);
            _frames.Remove(old);
        }
        foreach (var t in _vm.Tiles.Where(t => !_frames.ContainsKey(t)))
        {
            var frame = _factory(t);
            _frames[t] = frame;
            t.PropertyChanged += OnTileChanged;
            Children.Add(frame);
        }
        InvalidateMeasure();
    }

    private void OnTileChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MonitorTileViewModel.Col) or nameof(MonitorTileViewModel.Row)
            or nameof(MonitorTileViewModel.W) or nameof(MonitorTileViewModel.H))
            InvalidateMeasure();
    }

    // ---------------------------------------------------------------- Géométrie

    private double CellW(double width) => Math.Max(10, (width - (MonitorLayout.Columns - 1) * Gap) / MonitorLayout.Columns);

    private Rect Px(TileRect r) => new(r.Col * (_cellW + Gap), r.Row * (RowHeight + Gap),
        r.W * _cellW + (r.W - 1) * Gap, r.H * RowHeight + (r.H - 1) * Gap);

    private TileRect RectOf(MonitorTileViewModel t) =>
        _drag != null && _drag.Preview.TryGetValue(t.Id, out var p) ? p : t.Rect;

    /// <inheritdoc />
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 1200 : available.Width;
        _cellW = CellW(width);
        var rows = 0;
        foreach (var (tile, frame) in _frames)
        {
            var r = RectOf(tile);
            rows = Math.Max(rows, r.Bottom);
            frame.Measure(_drag?.Tile == tile ? _drag.FloatPx.Size : Px(r).Size);
        }
        _ghost.Measure(_drag == null ? default : Px(_drag.Target).Size);
        _backdrop.Measure(default);
        if (_vm?.IsEditing == true) rows += 2;   // de la place pour poser une carte plus bas
        return new Size(width, rows == 0 ? 0 : rows * (RowHeight + Gap) - Gap);
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        _cellW = CellW(finalSize.Width);
        foreach (var (tile, frame) in _frames)
        {
            var floating = _drag?.Tile == tile;
            frame.ZIndex = floating ? 10 : 1;
            frame.Arrange(floating ? _drag!.FloatPx : Px(RectOf(tile)));
        }
        _ghost.Arrange(_drag == null ? default : Px(_drag.Target));
        _backdrop.Arrange(new Rect(finalSize));
        _backdrop.InvalidateVisual();
        return finalSize;
    }

    /// <summary> En édition : les cases de la grille apparaissent en filigrane (Panel.Render est scellé : un enfant les dessine). </summary>
    private sealed class Backdrop(TileBoard board) : Control
    {
        public override void Render(DrawingContext ctx)
        {
            if (board._vm?.IsEditing != true || !this.TryFindResource("SurfaceAlt", ActualThemeVariant, out var res) || res is not IBrush brush) return;
            var rows = (int)Math.Ceiling((Bounds.Height + Gap) / (RowHeight + Gap));
            using (ctx.PushOpacity(0.55))
                for (var r = 0; r < rows; r++)
                    for (var c = 0; c < MonitorLayout.Columns; c++)
                        ctx.DrawRectangle(brush, null, new RoundedRect(board.Px(new TileRect(c, r, 1, 1)), 6));
        }
    }

    // ---------------------------------------------------------------- Souris

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_vm?.IsEditing != true || _drag != null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Quelle carte, quelle zone ? Un bouton (modifier, supprimer) garde son clic.
        Control? zone = null;
        Control? frame = null;
        foreach (var v in (e.Source as Visual)?.GetSelfAndVisualAncestors() ?? [])
        {
            if (v is Button or TextBox or ComboBox) return;
            if (v is Control c && zone == null && c.Classes.Contains("rz")) zone = c;
            if (v is Control f && _frames.ContainsValue(f)) { frame = f; break; }
        }
        if (frame == null) return;
        var tile = _frames.First(p => p.Value == frame).Key;

        var tag = zone?.Tag as string ?? "";
        var start = e.GetPosition(this);
        var px = Px(tile.Rect);
        _drag = new Drag
        {
            Tile = tile, Frame = frame, Start = start, StartPx = px, FloatPx = px, Target = tile.Rect,
            Move = tag == "", ResizeX = tag.Contains('r'), ResizeY = tag.Contains('b'),
            Preview = _frames.Keys.ToDictionary(t => t.Id, t => t.Rect),
        };
        frame.Classes.Add("dragging");
        _ghost.IsVisible = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateMeasure();
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } d) return;
        var delta = e.GetPosition(this) - d.Start;
        var info = d.Tile.Info;
        var stepX = _cellW + Gap;
        var stepY = RowHeight + Gap;
        var cur = d.Tile.Rect;

        TileRect target;
        if (d.Move)
        {
            var x = Math.Clamp(d.StartPx.X + delta.X, 0, Math.Max(0, Bounds.Width - d.StartPx.Width));
            var y = Math.Max(0, d.StartPx.Y + delta.Y);
            d.FloatPx = new Rect(x, y, d.StartPx.Width, d.StartPx.Height);
            target = MonitorLayout.Clamp(cur with { Col = (int)Math.Round(x / stepX), Row = (int)Math.Round(y / stepY) }, info);
        }
        else
        {
            var w = d.ResizeX ? Math.Clamp(d.StartPx.Width + delta.X, (info?.MinW ?? 1) * stepX - Gap, Bounds.Width - d.StartPx.X) : d.StartPx.Width;
            var h = d.ResizeY ? Math.Max(d.StartPx.Height + delta.Y, (info?.MinH ?? 1) * stepY - Gap) : d.StartPx.Height;
            d.FloatPx = new Rect(d.StartPx.X, d.StartPx.Y, w, h);
            target = MonitorLayout.Clamp(cur with { W = (int)Math.Round((w + Gap) / stepX), H = (int)Math.Round((h + Gap) / stepY) }, info);
        }
        d.Target = target;
        d.Preview = MonitorLayout.Resolve(_frames.Keys.ToDictionary(t => t.Id, t => t.Rect), d.Tile.Id, target);
        InvalidateMeasure();
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag is not { } d) return;
        _drag = null;
        e.Pointer.Capture(null);
        End(d);
        _vm?.ApplyLayout(MonitorLayout.Compact(d.Preview));
    }

    /// <summary> Capture perdue (changement de fenêtre…) : on annule, la carte retrouve sa place. </summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_drag is not { } d) return;
        _drag = null;
        End(d);
    }

    private void End(Drag d)
    {
        d.Frame.Classes.Remove("dragging");
        _ghost.IsVisible = false;
        InvalidateMeasure();
    }
}
