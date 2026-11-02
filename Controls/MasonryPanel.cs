/*
    Fichier      :  MasonryPanel.cs
    Projet       :  AlpenMux

    Description  :
        Panneau en colonnes de même largeur (autant que possible, chacune d'au moins MinColumnWidth) où chaque
        élément est placé dans la colonne la moins haute : les cartes de hauteurs différentes se rangent sans
        trou et se redimensionnent avec la fenêtre. L'ordre des éléments reste lisible (de gauche à droite).
*/

using System;
using Avalonia;

namespace AlpenMux.Controls;

/// <summary> Panneau en maçonnerie : chaque enfant va dans la colonne la moins haute (cartes des Paramètres). </summary>
public sealed class MasonryPanel : ColumnsPanel
{
    static MasonryPanel() => SetDefaults<MasonryPanel>(520, 14, 14);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size available)
    {
        var cols = Columns(available.Width);
        var colW = double.IsInfinity(available.Width) ? double.PositiveInfinity : ColumnWidth(available.Width, cols);
        var heights = new double[cols];
        double maxW = 0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(colW, double.PositiveInfinity));
            maxW = Math.Max(maxW, child.DesiredSize.Width);
            heights[Shortest(heights)] += child.DesiredSize.Height + RowSpacing;
        }
        var height = 0.0;
        foreach (var h in heights) height = Math.Max(height, h - RowSpacing);
        return new Size(double.IsInfinity(available.Width) ? maxW : available.Width, Math.Max(0, height));
    }

    /// <inheritdoc />
    protected override Size ArrangeOverride(Size finalSize)
    {
        var cols = Columns(finalSize.Width);
        var colW = ColumnWidth(finalSize.Width, cols);
        var heights = new double[cols];
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var c = Shortest(heights);
            child.Arrange(new Rect(c * (colW + ColumnSpacing), heights[c], colW, child.DesiredSize.Height));
            heights[c] += child.DesiredSize.Height + RowSpacing;
        }
        return finalSize;
    }

    private static int Shortest(double[] heights)
    {
        var best = 0;
        for (var i = 1; i < heights.Length; i++) if (heights[i] < heights[best] - 0.5) best = i;
        return best;
    }
}
