/*
    Fichier      :  ImagesPage.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Page Images : clavier du carrousel (flèches gauche et droite pour changer d'image, Échap pour fermer),
        suppression rapide des vignettes (Maj ou Ctrl maintenu, voir QuickDeleteWatcher).
        Le clavier passe par la page : le focus reste dans la page après un clic sur une miniature.
*/

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using AlpenMux.Controls;
using AlpenMux.ViewModels;

namespace AlpenMux.Views.Pages;

/// <summary> Page Images : galerie et carrousel de génération d'images. </summary>
public partial class ImagesPage : UserControl
{
    /// <summary> Maj ou Ctrl maintenu : corbeilles de suppression rapide des vignettes. </summary>
    private readonly QuickDeleteWatcher _quickDelete;

    /// <summary> Crée la page. </summary>
    public ImagesPage()
    {
        InitializeComponent();
        _quickDelete = new QuickDeleteWatcher(this, armed =>
        {
            if (DataContext is ImagesViewModel vm) vm.QuickDeleteArmed = armed;
        });
        // Tunnel : les touches arrivent ici avant les contrôles de la page (le carrousel doit les prendre)
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ImagesViewModel { IsViewerOpen: true } vm) return;
        switch (e.Key)
        {
            case Key.Left: vm.PreviousCommand.Execute(null); break;
            case Key.Right: vm.NextCommand.Execute(null); break;
            case Key.Escape: vm.CloseViewerCommand.Execute(null); break;
            default: return;
        }
        e.Handled = true;
    }
}
