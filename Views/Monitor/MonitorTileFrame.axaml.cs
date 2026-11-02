/*
    Fichier      :  MonitorTileFrame.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Cadre d'un widget du moniteur : reçoit le MonitorTileViewModel et le contenu du widget (voir MonitorPage.Build).
*/

using Avalonia.Controls;
using AlpenMux.ViewModels;

namespace AlpenMux.Views.Monitor;

/// <summary> Cadre d'un widget du moniteur (titre, menu, poignées de redimensionnement). </summary>
public partial class MonitorTileFrame : UserControl
{
    /// <summary> Constructeur du concepteur XAML. </summary>
    public MonitorTileFrame() => InitializeComponent();

    /// <summary> Crée le cadre d'un widget. </summary>
    /// <param name="tile"> Widget. </param>
    /// <param name="widget"> Contenu affiché. </param>
    public MonitorTileFrame(MonitorTileViewModel tile, Control widget) : this()
    {
        DataContext = tile;
        Body.Content = widget;
    }
}
