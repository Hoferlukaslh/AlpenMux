/*
    Fichier      :  AdaptiveColumnsPanel.cs
    Projet       :  AlpenMux

    Description  :
        Panneau en colonnes adaptées à la largeur : autant de colonnes de même largeur que possible, chacune
        d'au moins MinColumnWidth (une seule si la place manque). Les éléments d'une même ligne prennent la
        hauteur du plus haut, pour que les cartes restent alignées.
*/

using System;
using Avalonia;
using Avalonia.Controls;

namespace AlpenMux.Controls;

/// <summary> Panneau qui répartit ses enfants sur autant de colonnes que la largeur le permet (lignes de hauteur commune). </summary>
public sealed class AdaptiveColumnsPanel : ColumnsPanel
{
    static AdaptiveColumnsPanel() => SetDefaults<AdaptiveColumnsPanel>(560, 12, 10);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size available)
    {
        var cols = Columns(available.Width);
        var colW = double.IsInfinity(available.Width) ? double.PositiveInfinity : ColumnWidth(available.Width, cols);
        double height = 0, rowH = 0, maxW = 0;
        var visible = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(colW, double.PositiveInfinity));
            maxW = Math.Max(maxW, child.DesiredSize.Width);
            rowH = Math.Max(rowH, child.DesiredSize.Height);
            if (++visible % cols == 0) { height += rowH + RowSpacing; rowH = 0; }
        }
        if (visible % cols != 0) height += rowH + RowSpacing;
        if (visible > 0) height -= RowSpacing;
        var width = double.IsInfinity(available.Width) ? maxW : available.Width;
        return new Size(width, Math.Max(0, height));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = Columns(finalSize.Width);
        var colW = ColumnWidth(finalSize.Width, cols);
        var visible = new System.Collections.Generic.List<Control>();
        foreach (var child in Children) if (child.IsVisible) visible.Add(child);

        double y = 0;
        for (var start = 0; start < visible.Count; start += cols)
        {
            var count = Math.Min(cols, visible.Count - start);
            double rowH = 0;
            for (var i = 0; i < count; i++) rowH = Math.Max(rowH, visible[start + i].DesiredSize.Height);
            for (var i = 0; i < count; i++)
                visible[start + i].Arrange(new Rect(i * (colW + ColumnSpacing), y, colW, rowH));
            y += rowH + RowSpacing;
        }
        return finalSize;
    }
}
