/*
    Fichier      :  RagChunker.cs
    Projet       :  AlpenMux

    Description  :
        Découpage d'un document Markdown en passages (chunks) pour le RAG. Les passages ne traversent pas les titres,
        ne coupent pas un tableau ni un bloc de code (sauf s'ils dépassent la taille maximale), et se recouvrent un
        peu (les phrases au bord d'un passage gardent leur contexte). Le chemin de titres (« Chapitre 2 > Budget »)
        et la page (séparateur \f des PDF) sont gardés pour les citations. Fonction pure : aucun état.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace AlpenMux.Services;

/// <summary> Passage d'un document : texte, chemin de titres, page (null si inconnue) et taille estimée. </summary>
/// <param name="Heading"> Chemin de titres (« Chapitre 2 > Budget »), vide si aucun. </param>
/// <param name="Page"> Numéro de page (1 à n), null si le document n'a pas de pages. </param>
/// <param name="Text"> Texte du passage (Markdown). </param>
/// <param name="Tokens"> Taille estimée en tokens. </param>
public sealed record RagPassage(string Heading, int? Page, string Text, int Tokens);

/// <summary> Découpage des documents en passages. </summary>
public static class RagChunker
{
    /// <summary> Taille cible d'un passage, en tokens. </summary>
    public const int DefaultTargetTokens = 500;

    /// <summary> Taille maximale d'un passage, en tokens (au-delà, le sens se dilue). </summary>
    public const int DefaultMaxTokens = 800;

    /// <summary> Part de la taille cible reprise du passage précédent (chevauchement). </summary>
    public const double Overlap = 0.12;

    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex SentenceRegex = new(@"(?<=[.!?;])\s+", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary> Estimation du nombre de tokens (≈ 4 caractères par token, arrondi au supérieur). </summary>
    /// <param name="text"> Texte. </param>
    public static int EstimateTokens(string text) => (text.Length + 3) / 4;

    /// <summary>
    /// Découpe un document Markdown. Les formfeeds (\f, séparateur de pages des PDF) donnent les numéros de page.
    /// </summary>
    /// <param name="markdown"> Texte du document. </param>
    /// <param name="targetTokens"> Taille cible d'un passage. </param>
    /// <param name="maxTokens"> Taille maximale d'un passage (un bloc plus gros est coupé). </param>
    public static List<RagPassage> Split(string markdown, int targetTokens = DefaultTargetTokens, int maxTokens = DefaultMaxTokens)
    {
        var result = new List<RagPassage>();
        if (string.IsNullOrWhiteSpace(markdown)) return result;
        targetTokens = Math.Max(20, targetTokens);
        maxTokens = Math.Max(targetTokens, maxTokens);

        var pages = markdown.Split('\f');
        bool paged = pages.Length > 1;
        // Titres : pile (niveau, texte), gardée entre les pages (un titre peut continuer sur la page suivante)
        var headings = new List<(int Level, string Title)>();

        for (int p = 0; p < pages.Length; p++)
        {
            int? page = paged ? p + 1 : null;
            foreach (var section in ReadSections(pages[p], headings))
            {
                foreach (var passage in PackSection(section, targetTokens, maxTokens))
                    result.Add(passage with { Page = page ?? passage.Page });
            }
        }
        return result;
    }

    /// <summary> Bloc de texte (paragraphe, tableau, bloc de code) avec son chemin de titres. </summary>
    private sealed record Block(string Text, string Heading);

    /// <summary> Une section : le texte entre deux titres. </summary>
    private sealed record Section(string Heading, List<Block> Blocks);

    /// <summary> Lit une page et la découpe en sections (un titre ouvre une nouvelle section). </summary>
    private static List<Section> ReadSections(string page, List<(int Level, string Title)> headings)
    {
        var sections = new List<Section>();
        var blocks = new List<Block>();
        var para = new StringBuilder();
        string Path() => string.Join(" > ", headings.Select(h => h.Title));

        void FlushPara()
        {
            if (para.Length == 0) return;
            blocks.Add(new Block(para.ToString().TrimEnd(), Path()));
            para.Clear();
        }
        void CloseSection()
        {
            FlushPara();
            if (blocks.Count > 0) sections.Add(new Section(Path(), new List<Block>(blocks)));
            blocks.Clear();
        }

        var lines = page.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Bloc de code : tout jusqu'à la fence fermante, en un seul bloc
            if (trimmed.StartsWith("```") || trimmed.StartsWith("~~~"))
            {
                FlushPara();
                var fence = trimmed.StartsWith("```") ? "```" : "~~~";
                var code = new StringBuilder(line);
                while (++i < lines.Length)
                {
                    code.Append('\n').Append(lines[i]);
                    if (lines[i].TrimStart().StartsWith(fence)) break;
                }
                blocks.Add(new Block(code.ToString(), Path()));
                continue;
            }

            var heading = HeadingRegex.Match(line);
            if (heading.Success)
            {
                CloseSection();
                int level = heading.Groups[1].Length;
                while (headings.Count > 0 && headings[^1].Level >= level) headings.RemoveAt(headings.Count - 1);
                headings.Add((level, heading.Groups[2].Value.Trim()));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line)) { FlushPara(); continue; }

            // Tableau : les lignes qui commencent par « | » forment un bloc indivisible
            if (trimmed.StartsWith('|'))
            {
                FlushPara();
                var table = new StringBuilder(line);
                while (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('|')) table.Append('\n').Append(lines[++i]);
                blocks.Add(new Block(table.ToString(), Path()));
                continue;
            }

            if (para.Length > 0) para.Append('\n');
            // Les PDF écrivent des espaces multiples entre les mots : un seul espace (mieux pour l'embedding)
            para.Append(MultiSpaceRegex.Replace(line, " "));
        }
        CloseSection();
        return sections;
    }

    /// <summary> Regroupe les blocs d'une section en passages (sans dépasser la cible, avec chevauchement). </summary>
    private static IEnumerable<RagPassage> PackSection(Section section, int targetTokens, int maxTokens)
    {
        var current = new StringBuilder();
        int currentTokens = 0;
        string heading = section.Heading;
        string? emitted = null;

        // Un passage fini : il devient le texte de référence du chevauchement
        IEnumerable<RagPassage> Emit()
        {
            if (current.Length == 0) yield break;
            var text = current.ToString().Trim();
            if (text.Length > 0) yield return new RagPassage(heading, null, text, EstimateTokens(text));
            emitted = text;
            current.Clear();
            currentTokens = 0;
        }

        foreach (var block in section.Blocks)
        {
            foreach (var piece in BlockPieces(block.Text, maxTokens))
            {
                int tokens = EstimateTokens(piece);
                if (currentTokens + tokens > targetTokens && current.Length > 0)
                {
                    foreach (var p in Emit()) yield return p;
                    // Le passage suivant commence par la fin du précédent (phrases au bord gardées)
                    var tail = Tail(emitted, (int)(targetTokens * 4 * Overlap));
                    if (tail.Length > 0)
                    {
                        current.Append(tail);
                        currentTokens = EstimateTokens(tail);
                    }
                }
                if (current.Length > 0) current.Append("\n\n");
                current.Append(piece);
                currentTokens += tokens;
            }
        }
        foreach (var p in Emit()) yield return p;
    }

    /// <summary> Blocs trop gros coupés par lignes, puis par phrases, puis à la taille (jamais plus que maxTokens). </summary>
    private static IEnumerable<string> BlockPieces(string text, int maxTokens)
    {
        if (EstimateTokens(text) <= maxTokens) { yield return text; yield break; }
        foreach (var unit in Units(text.Split('\n'), maxTokens)) yield return unit;
    }

    /// <summary> Regroupe des morceaux (lignes, puis phrases) sans dépasser la taille maximale. </summary>
    private static IEnumerable<string> Units(IEnumerable<string> parts, int maxTokens)
    {
        var buf = new StringBuilder();
        foreach (var part in parts)
        {
            IEnumerable<string> pieces = EstimateTokens(part) <= maxTokens
                ? new[] { part }
                : SentenceRegex.Split(part).SelectMany(s => HardCut(s, maxTokens));
            foreach (var piece in pieces)
            {
                if (buf.Length > 0 && EstimateTokens(buf + "\n" + piece) > maxTokens)
                {
                    yield return buf.ToString();
                    buf.Clear();
                }
                if (buf.Length > 0) buf.Append('\n');
                buf.Append(piece);
            }
        }
        if (buf.Length > 0) yield return buf.ToString();
    }

    /// <summary> Coupe un texte trop long à la taille maximale, en préférant un espace. </summary>
    private static IEnumerable<string> HardCut(string text, int maxTokens)
    {
        int max = maxTokens * 4;
        while (text.Length > max)
        {
            int cut = text.LastIndexOf(' ', max);
            if (cut <= 0) cut = max;
            yield return text[..cut];
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) yield return text;
    }

    /// <summary> Fin d'un texte, d'environ <paramref name="chars"/> caractères, commençant à un espace. </summary>
    private static string Tail(string? text, int chars)
    {
        if (string.IsNullOrEmpty(text) || chars <= 0) return "";
        if (text.Length <= chars) return text;
        var tail = text[^chars..];
        int space = tail.IndexOfAny([' ', '\n']);
        return space >= 0 && space < tail.Length - 1 ? tail[(space + 1)..] : tail;
    }
}
