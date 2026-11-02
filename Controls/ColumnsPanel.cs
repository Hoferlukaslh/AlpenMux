/*
    Fichier      :  ColumnsPanel.cs
    Projet       :  AlpenMux

    Description  :
        Base des panneaux en colonnes de même largeur (AdaptiveColumnsPanel, MasonryPanel) : largeur minimale
        d'une colonne, espacements, nombre de colonnes qui tiennent dans la largeur disponible.
        Chaque panneau dérivé garde ses propres valeurs par défaut (OverrideDefaultValue).
*/

using System;
using Avalonia;
using Avalonia.Controls;

namespace AlpenMux.Controls;

/// <summary> Panneau en colonnes de même largeur, chacune d'au moins <see cref="MinColumnWidth"/>. </summary>
public abstract class ColumnsPanel : Panel
{
    /// <summary> Propriété <see cref="MinColumnWidth"/>. </summary>
    public static readonly StyledProperty<double> MinColumnWidthProperty =
        AvaloniaProperty.Register<ColumnsPanel, double>(nameof(MinColumnWidth));
    /// <summary> Propriété <see cref="ColumnSpacing"/>. </summary>
    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<ColumnsPanel, double>(nameof(ColumnSpacing));
    /// <summary> Propriété <see cref="RowSpacing"/>. </summary>
    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<ColumnsPanel, double>(nameof(RowSpacing));

    static ColumnsPanel() =>
        AffectsMeasure<ColumnsPanel>(MinColumnWidthProperty, ColumnSpacingProperty, RowSpacingProperty);

    /// <summary> Largeur minimale d'une colonne. </summary>
    public double MinColumnWidth { get => GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }
    /// <summary> Espace entre les colonnes. </summary>
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    /// <summary> Espace vertical entre deux enfants d'une même colonne. </summary>
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    /// <summary> Fixe les valeurs par défaut d'un panneau dérivé (à appeler dans son constructeur statique). </summary>
    /// <typeparam name="T"> Panneau dérivé. </typeparam>
    /// <param name="minColumnWidth"> Largeur minimale d'une colonne. </param>
    /// <param name="columnSpacing"> Espace entre les colonnes. </param>
    /// <param name="rowSpacing"> Espace entre les lignes. </param>
    protected static void SetDefaults<T>(double minColumnWidth, double columnSpacing, double rowSpacing) where T : ColumnsPanel
    {
        MinColumnWidthProperty.OverrideDefaultValue<T>(minColumnWidth);
        ColumnSpacingProperty.OverrideDefaultValue<T>(columnSpacing);
        RowSpacingProperty.OverrideDefaultValue<T>(rowSpacing);
    }

    /// <summary> Nombre de colonnes qui tiennent dans cette largeur (une seule si elle est infinie ou trop petite). </summary>
    /// <param name="width"> Largeur disponible. </param>
    protected int Columns(double width) =>
        double.IsInfinity(width) ? 1 : Math.Max(1, (int)((width + ColumnSpacing) / (MinColumnWidth + ColumnSpacing)));

    /// <summary> Largeur d'une colonne quand <paramref name="cols"/> colonnes se partagent cette largeur. </summary>
    /// <param name="width"> Largeur disponible (finie). </param>
    /// <param name="cols"> Nombre de colonnes. </param>
    protected double ColumnWidth(double width, int cols) => Math.Max(0, (width - (cols - 1) * ColumnSpacing) / cols);
}
