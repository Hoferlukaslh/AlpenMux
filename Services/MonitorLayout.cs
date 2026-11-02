/*
    Fichier      :  MonitorLayout.cs
    Projet       :  AlpenMux

    Description  :
        Disposition modulable de la page Moniteur : catalogue des widgets, disposition par défaut et
        calcul des positions sur une grille de 12 colonnes (collisions : les cartes gênées sont poussées vers
        le bas ; compactage : elles remontent combler les trous). Logique pure, sans interface.
*/

using System;
using System.Collections.Generic;
using System.Linq;

namespace AlpenMux.Services;

/// <summary> Un widget tel qu'il est enregistré dans les réglages. </summary>
public sealed class MonitorTileSetting
{
    /// <summary> Identifiant du widget. </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary> Type de widget (voir MonitorLayout.Kinds). </summary>
    public string Kind { get; set; } = "";
    /// <summary> Colonne (0 à 11). </summary>
    public int Col { get; set; }
    /// <summary> Ligne. </summary>
    public int Row { get; set; }
    /// <summary> Largeur en colonnes. </summary>
    public int W { get; set; } = 4;
    /// <summary> Hauteur en lignes. </summary>
    public int H { get; set; } = 4;
    /// <summary> Titre choisi par l'utilisateur ; vide = titre par défaut du widget. </summary>
    public string? Title { get; set; }
    /// <summary> Numéro du GPU affiché (widgets GPU). </summary>
    public int Gpu { get; set; }
}

/// <summary> Type de widget proposé dans « Ajouter » (taille par défaut et minimale en cases de la grille). </summary>
public sealed record TileKind(string Kind, string Icon, int W, int H, int MinW, int MinH, bool NeedsGpu = false)
{
    /// <summary> Nom affiché du type. </summary>
    public string Name => Loc.T("Mon_K_" + Kind);
    /// <summary> Description affichée du type. </summary>
    public string Description => Loc.T("Mon_KD_" + Kind);
}

/// <summary> Rectangle sur la grille (colonne, ligne, largeur, hauteur en cases). </summary>
public readonly record struct TileRect(int Col, int Row, int W, int H)
{
    /// <summary> Ligne qui suit le rectangle. </summary>
    public int Bottom => Row + H;
    /// <summary> Les deux rectangles se chevauchent-ils ? </summary>
    public bool Overlaps(TileRect o) => Col < o.Col + o.W && o.Col < Col + W && Row < o.Row + o.H && o.Row < Row + H;
}

/// <summary> Disposition des widgets du moniteur : catalogue des types, grille de 12 colonnes, collisions. </summary>
public static class MonitorLayout
{
    /// <summary> Nombre de colonnes de la grille. </summary>
    public const int Columns = 12;

    /// <summary> Types de widgets disponibles. </summary>
    public static readonly TileKind[] Kinds =
    [
        new("gpu",      "IconGpu",    12, 6, 6, 5, true),
        new("gpuutil",  "IconPulse",   6, 4, 3, 3, true),
        new("gpuvram",  "IconMemory",  6, 4, 3, 3, true),
        new("gpustats", "IconThermo", 12, 2, 4, 2, true),
        new("cpu",      "IconCpu",     7, 9, 4, 6),
        new("ram",      "IconMemory",  5, 4, 3, 3),
        new("net",      "IconGlobe",   5, 3, 3, 3),
        new("disk",     "IconDisk",    5, 2, 3, 2),
        new("gpuproc",  "IconList",   12, 4, 4, 3),
    ];

    /// <summary> Type de widget d'après son nom, ou null. </summary>
    public static TileKind? KindOf(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind);

    /// <summary> Disposition d'origine (celle de l'ancienne page fixe). </summary>
    public static List<MonitorTileSetting> Default() =>
    [
        T("gpu", 0, 0, 12, 6), T("cpu", 0, 6, 7, 9), T("ram", 7, 6, 5, 4), T("net", 7, 10, 5, 3),
        T("disk", 7, 13, 5, 2), T("gpuproc", 0, 15, 12, 4),
    ];

    private static MonitorTileSetting T(string kind, int c, int r, int w, int h) => new() { Kind = kind, Col = c, Row = r, W = w, H = h };

    /// <summary> Rectangle valide : dans la grille, au moins la taille minimale du widget. </summary>
    public static TileRect Clamp(TileRect r, TileKind? k)
    {
        var w = Math.Clamp(r.W, k?.MinW ?? 1, Columns);
        var h = Math.Max(r.H, k?.MinH ?? 1);
        return new(Math.Clamp(r.Col, 0, Columns - w), Math.Max(0, r.Row), w, h);
    }

    /// <summary>
    /// Positions de toutes les cartes quand <paramref name="movingId"/> occupe <paramref name="target"/> : la carte
    /// déplacée garde sa place, les autres sont poussées vers le bas si elles la gênent, ou remontent si elles le peuvent.
    /// </summary>
    public static Dictionary<string, TileRect> Resolve(IReadOnlyDictionary<string, TileRect> current, string? movingId, TileRect target)
    {
        var placed = new List<TileRect>();
        var result = new Dictionary<string, TileRect>();
        if (movingId != null) { placed.Add(target); result[movingId] = target; }

        foreach (var (id, r) in current.Where(p => p.Key != movingId).OrderBy(p => p.Value.Row).ThenBy(p => p.Value.Col))
        {
            var y = r.Row;
            // Gêné : on descend sous l'obstacle ; libre : on remonte tant qu'il reste de la place
            for (var guard = 0; guard < 1000; guard++)
            {
                var hit = placed.Where(p => p.Overlaps(r with { Row = y })).ToList();
                if (hit.Count == 0) break;
                y = hit.Max(p => p.Bottom);
            }
            while (y > 0 && !placed.Any(p => p.Overlaps(r with { Row = y - 1 }))) y--;
            var fixedRect = r with { Row = y };
            placed.Add(fixedRect);
            result[id] = fixedRect;
        }
        return result;
    }

    /// <summary> Remonte toutes les cartes au plus haut (après un lâcher ou une suppression). </summary>
    public static Dictionary<string, TileRect> Compact(IReadOnlyDictionary<string, TileRect> current) => Resolve(current, null, default);

    /// <summary> Première place libre pour un nouveau widget (parcours de haut en bas, de gauche à droite). </summary>
    public static TileRect FindFree(IEnumerable<TileRect> existing, int w, int h)
    {
        var list = existing.ToList();
        for (var row = 0; ; row++)
            for (var col = 0; col + w <= Columns; col++)
            {
                var r = new TileRect(col, row, w, h);
                if (!list.Any(p => p.Overlaps(r))) return r;
            }
    }
}
