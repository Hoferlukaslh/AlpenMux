/*
    Fichier      :  ModelsPage.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Page Modèles (tout est dans ModelsPage.axaml et ModelsViewModel).
*/

using Avalonia.Controls;

namespace AlpenMux.Views.Pages;

/// <summary> Page Modèles : modèles installés et téléchargements. </summary>
public partial class ModelsPage : UserControl
{
    /// <summary> Crée la page. </summary>
    public ModelsPage() => InitializeComponent();
}
