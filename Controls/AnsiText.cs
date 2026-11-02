/*
    Fichier      :  AnsiText.cs
    Projet       :  AlpenMux

    Description  :
        Ligne de console colorée : interprète les codes couleur ANSI (SGR) écrits par vLLM
        (niveau INFO / WARNING / ERROR, préfixe des processus…) : 16 couleurs, 256 couleurs,
        couleurs RVB, gras, italique, souligné.
        Les lignes sans couleur (traces Python, messages de l'application) sont colorées
        d'après leur contenu : erreurs en rouge, avertissements en jaune.
        La palette est prévue pour le fond sombre de la console (identique dans les deux thèmes).
*/

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AlpenMux.Controls;

/// <summary> Texte sélectionnable qui affiche une ligne de journal avec ses couleurs ANSI. </summary>
public sealed partial class AnsiText : SelectableTextBlock
{
    /// <summary> Propriété <see cref="Line"/>. </summary>
    public static readonly StyledProperty<string?> LineProperty =
        AvaloniaProperty.Register<AnsiText, string?>(nameof(Line));

    /// <summary> Texte brut de la ligne, codes ANSI compris. </summary>
    public string? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    // Les styles de SelectableTextBlock (police de la console, sélection) s'appliquent aussi ici
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    [GeneratedRegex(@"\x1B\[([0-9;]*)m")]
    private static partial Regex SgrRegex();

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*\x07|[@-Z\\-_])")]
    private static partial Regex OtherEscapeRegex();

    [GeneratedRegex(@"Traceback \(most recent call last\)|^\s*(\w+\.)*\w*(Error|Exception)\b(:|$)|^✘|\bERROR\b|CUDA out of memory")]
    private static partial Regex ErrorLineRegex();

    [GeneratedRegex(@"\bWARNING\b|^⚠|\bWarning:|FutureWarning|DeprecationWarning")]
    private static partial Regex WarnLineRegex();

    private static readonly string[] Base16 =
    [
        "#6E7681", "#FF7B72", "#7EE787", "#E3B341", "#79C0FF", "#D2A8FF", "#56D4DD", "#C9D1D9",
        "#8B949E", "#FFA198", "#A5F0B0", "#F2CC60", "#A5D6FF", "#E2C5FF", "#B3F0FF", "#F0F6FC",
    ];
    private const string DimColor = "#8B949E";

    private static readonly Dictionary<Color, IImmutableSolidColorBrush> Brushes = new();

    private static IImmutableSolidColorBrush Brush(Color c)
    {
        lock (Brushes)
        {
            if (!Brushes.TryGetValue(c, out var b)) Brushes[c] = b = new Avalonia.Media.Immutable.ImmutableSolidColorBrush(c);
            return b;
        }
    }

    private static Color Palette256(int n)
    {
        if (n < 16) return Color.Parse(Base16[n]);
        if (n < 232)
        {
            n -= 16;
            static byte Level(int v) => (byte)(v == 0 ? 0 : 55 + v * 40);
            return Color.FromRgb(Level(n / 36), Level(n / 6 % 6), Level(n % 6));
        }
        var g = (byte)(8 + (n - 232) * 10);
        return Color.FromRgb(g, g, g);
    }

    private struct State
    {
        public Color? Fg;
        public bool Bold, Dim, Italic, Underline;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LineProperty) Render(Line ?? "");
    }

    private void Render(string line)
    {
        var inlines = new InlineCollection();
        line = OtherEscapeRegex().Replace(line, m => m.Value.EndsWith('m') && m.Value.StartsWith("\x1B[") ? m.Value : "");

        if (!line.Contains('\x1B'))
        {
            // Pas de couleur : d'après le contenu
            var run = new Run(line);
            if (ErrorLineRegex().IsMatch(line)) run.Foreground = Brush(Color.Parse(Base16[1]));
            else if (WarnLineRegex().IsMatch(line)) run.Foreground = Brush(Color.Parse(Base16[3]));
            inlines.Add(run);
        }
        else
        {
            var st = new State();
            int pos = 0;
            foreach (Match m in SgrRegex().Matches(line))
            {
                if (m.Index > pos) inlines.Add(MakeRun(line[pos..m.Index], st));
                Apply(ref st, m.Groups[1].Value);
                pos = m.Index + m.Length;
            }
            if (pos < line.Length) inlines.Add(MakeRun(line[pos..], st));
        }
        Text = null;
        Inlines = inlines;
    }

    private static Run MakeRun(string text, State st)
    {
        var run = new Run(text);
        if (st.Fg is { } c) run.Foreground = Brush(c);
        else if (st.Dim) run.Foreground = Brush(Color.Parse(DimColor));
        if (st.Bold) run.FontWeight = FontWeight.Bold;
        if (st.Italic) run.FontStyle = FontStyle.Italic;
        if (st.Underline) run.TextDecorations = Avalonia.Media.TextDecorations.Underline;
        return run;
    }

    private static void Apply(ref State st, string codes)
    {
        if (codes.Length == 0) { st = new State(); return; }
        var p = codes.Split(';');
        for (int i = 0; i < p.Length; i++)
        {
            if (!int.TryParse(p[i], out var c)) continue;
            switch (c)
            {
                case 0: st = new State(); break;
                case 1: st.Bold = true; break;
                case 2: st.Dim = true; break;
                case 3: st.Italic = true; break;
                case 4: st.Underline = true; break;
                case 22: st.Bold = st.Dim = false; break;
                case 23: st.Italic = false; break;
                case 24: st.Underline = false; break;
                case 39: st.Fg = null; break;
                case >= 30 and <= 37: st.Fg = Color.Parse(Base16[c - 30 + (st.Bold ? 8 : 0)]); break;
                case >= 90 and <= 97: st.Fg = Color.Parse(Base16[c - 90 + 8]); break;
                case 38 when i + 2 < p.Length && p[i + 1] == "5" && int.TryParse(p[i + 2], out var n):
                    st.Fg = Palette256(Math.Clamp(n, 0, 255)); i += 2; break;
                case 38 when i + 4 < p.Length && p[i + 1] == "2" && byte.TryParse(p[i + 2], out var r)
                                               && byte.TryParse(p[i + 3], out var g) && byte.TryParse(p[i + 4], out var b):
                    st.Fg = Color.FromRgb(r, g, b); i += 4; break;
                case 48 when i + 1 < p.Length: i += p[i + 1] == "5" ? 2 : p[i + 1] == "2" ? 4 : 0; break;   // fonds ignorés
            }
        }
    }
}
