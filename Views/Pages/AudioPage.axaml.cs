/*
    Fichier      :  AudioPage.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Page Audio : code-behind vide, la page est entièrement liée à AudioViewModel (synthèse vocale).
*/

using Avalonia.Controls;

namespace AlpenMux.Views.Pages;

/// <summary> Page Audio : synthèse vocale. </summary>
public partial class AudioPage : UserControl
{
    /// <summary> Crée la page. </summary>
    public AudioPage() => InitializeComponent();
}
