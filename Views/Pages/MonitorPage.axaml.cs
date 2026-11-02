/*
    Fichier      :  MonitorPage.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Page Moniteur : relie la grille des widgets (TileBoard) au ViewModel et construit le contenu de chaque widget.
*/

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using AlpenMux.ViewModels;
using AlpenMux.Views.Monitor;

namespace AlpenMux.Views.Pages;

/// <summary> Page Moniteur : widgets du système et des GPU. </summary>
public partial class MonitorPage : UserControl
{
    /// <summary> Crée la page. </summary>
    public MonitorPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MonitorViewModel vm) Board.Attach(vm, Build);
        };
    }

    /// <summary> Carte d'un widget : contenu choisi selon le type (modèles « Tile_&lt;type&gt; » de MonitorWidgets.axaml). </summary>
    private MonitorTileFrame Build(MonitorTileViewModel tile)
    {
        var widget = this.TryFindResource("Tile_" + tile.Kind, out var res) && res is IDataTemplate t ? t.Build(tile) : new TextBlock { Text = tile.Kind };
        if (widget != null) widget.DataContext = tile;
        return new MonitorTileFrame(tile, widget ?? new Control());
    }
}
