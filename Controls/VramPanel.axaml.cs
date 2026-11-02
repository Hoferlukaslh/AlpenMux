/*
    Fichier      :  VramPanel.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Barre de VRAM avec sa légende (parts, tailles, détails), utilisée par l'Accueil et la page Serveurs.
*/

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace AlpenMux.Controls;

/// <summary> Barre de VRAM avec sa légende. </summary>
public partial class VramPanel : UserControl
{
    /// <summary> Propriété <see cref="Segments"/>. </summary>
    public static readonly StyledProperty<IReadOnlyList<VramSegment>?> SegmentsProperty =
        AvaloniaProperty.Register<VramPanel, IReadOnlyList<VramSegment>?>(nameof(Segments));

    /// <summary> Propriété <see cref="Total"/>. </summary>
    public static readonly StyledProperty<double> TotalProperty =
        AvaloniaProperty.Register<VramPanel, double>(nameof(Total));

    /// <summary> Parts affichées. </summary>
    public IReadOnlyList<VramSegment>? Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }
    /// <summary> Mémoire totale du GPU (octets). </summary>
    public double Total { get => GetValue(TotalProperty); set => SetValue(TotalProperty, value); }

    /// <summary> Crée le panneau. </summary>
    public VramPanel() => InitializeComponent();
}
