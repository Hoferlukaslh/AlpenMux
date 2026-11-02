/*
    Fichier      :  SettingsPage.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Page Paramètres : l'aperçu animé des courbes du moniteur ne tourne que pendant que la page est affichée.
*/

using Avalonia;
using Avalonia.Controls;
using AlpenMux.ViewModels;

namespace AlpenMux.Views.Pages;

/// <summary> Page Paramètres. </summary>
public partial class SettingsPage : UserControl
{
    /// <summary> Crée la page. </summary>
    public SettingsPage() => InitializeComponent();

    // Les courbes d'exemple du moniteur ne tournent que pendant que la page est affichée
    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as SettingsViewModel)?.MonitorSettings.StartPreview();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        (DataContext as SettingsViewModel)?.MonitorSettings.StopPreview();
    }
}
