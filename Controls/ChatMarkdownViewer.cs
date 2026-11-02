/*
    Fichier      :  ChatMarkdownViewer.cs
    Projet       :  AlpenMux

    Description  :
        Affichage Markdown d'un message du chat : MarkdownScrollViewer (Markdown.Avalonia) avec le style de
        l'application (ChatMarkdownStyle) au lieu de ses thèmes intégrés. Une instance de style par contrôle.
        Blocs de code ``` dessinés par l'application : en-tête avec la langue et un bouton « Copier »,
        code sélectionnable, défilement horizontal pour les lignes longues. Un bloc pas encore fermé
        (réponse en cours) s'affiche déjà jusqu'à la fin du texte.
*/

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using AlpenMux.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Markdown.Avalonia;
using Markdown.Avalonia.Parsers;
using Markdown.Avalonia.Plugins;

namespace AlpenMux.Controls;

/// <summary> Rendu Markdown des messages du chat (blocs de code avec bouton « Copier »). </summary>
public sealed class ChatMarkdownViewer : MarkdownScrollViewer
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(MarkdownScrollViewer);

    /// <summary> Crée le contrôle avec le plugin des blocs de code. </summary>
    public ChatMarkdownViewer()
    {
        MarkdownStyle = new ChatMarkdownStyle();
        SelectionEnabled = true;
        Plugins = new MdAvPlugins { Plugins = { new CodeBlockPlugin() } };
    }

    private sealed class CodeBlockPlugin : IMdAvPlugin
    {
        public void Setup(SetupInfo info) => info.Register(new FencedCodeOverride());
    }

    /// <summary> Remplace le bloc ```langue de Markdown.Avalonia (même expression d'ouverture, $1 = ```, $2 = langue). </summary>
    private sealed class FencedCodeOverride : IBlockOverride
    {
        public string ParserName => "CodeBlocksWithLangEvaluator";

        public IEnumerable<Control> Convert(string text, Match match, ParseStatus status, IMarkdownEngine engine,
            out int parseTextBegin, out int parseTextEnd)
        {
            var fence = match.Groups[1].Value;
            var codeStart = match.Index + match.Length;
            // Fermeture : ligne de ` au moins aussi longue que l'ouverture ; absente pendant la réponse → fin du texte
            var close = new Regex($@"^[ ]{{0,3}}{fence}`*[ \t]*$", RegexOptions.Multiline).Match(text, codeStart);
            var codeEnd = close.Success ? close.Index : text.Length;
            parseTextBegin = match.Index;
            parseTextEnd = close.Success ? Math.Min(text.Length, close.Index + close.Length + 1) : text.Length;
            var code = text[codeStart..codeEnd].TrimEnd('\n', '\r');
            return [Build(match.Groups[2].Value.Trim(), code)];
        }
    }

    /// <summary> Bloc de code : langue et bouton « Copier » en haut, code en dessous. </summary>
    private static Control Build(string lang, string code)
    {
        var copyIcon = new TextBlock { Classes = { "icon" }, FontSize = 13, Text = (string)Application.Current!.FindResource("IconCopy")! };
        var copyLabel = new TextBlock { Classes = { "small" }, Text = Loc.T("Common_Copy"), VerticalAlignment = VerticalAlignment.Center };
        var copy = new Button
        {
            Classes = { "flat", "CodeCopy" },
            Padding = new Thickness(6, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { copyIcon, copyLabel } },
        };
        ToolTip.SetTip(copy, Loc.T("Chat_CopyCode"));
        copy.Click += async (_, _) =>
        {
            await App.Services.GetRequiredService<IUiServices>().CopyAsync(code);
            copyIcon.Text = (string)Application.Current!.FindResource("IconCheck")!;
            copyLabel.Text = Loc.T("Chat_Copied");
            await Task.Delay(1500);
            copyIcon.Text = (string)Application.Current!.FindResource("IconCopy")!;
            copyLabel.Text = Loc.T("Common_Copy");
        };

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Classes = { "CodeHeader" } };
        header.Children.Add(new TextBlock { Classes = { "CodeLang" }, Text = lang, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);

        var body = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new SelectableTextBlock { Classes = { "CodeBlock" }, Text = code, TextWrapping = TextWrapping.NoWrap },
        };
        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        panel.Children.Add(header);
        panel.Children.Add(body);
        return new Border { Classes = { "CodeBlock" }, Child = panel };
    }
}
