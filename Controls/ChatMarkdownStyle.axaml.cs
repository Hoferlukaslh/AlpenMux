/*
    Fichier      :  ChatMarkdownStyle.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Styles du rendu Markdown du chat (titres, listes, code, tableaux), chargés depuis ChatMarkdownStyle.axaml.
*/

using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace AlpenMux.Controls;

/// <summary> Style Markdown des messages du chat (voir ChatMarkdownStyle.axaml). </summary>
public partial class ChatMarkdownStyle : Styles
{
    /// <summary> Charge les styles du rendu Markdown du chat. </summary>
    public ChatMarkdownStyle() => AvaloniaXamlLoader.Load(this);
}
