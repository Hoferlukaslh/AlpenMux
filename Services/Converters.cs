/*
    Fichier      :  Converters.cs
    Projet       :  AlpenMux

    Description  :  Convertisseurs de valeurs utilisés dans les vues.
*/

using Avalonia.Data.Converters;
using AlpenMux.ViewModels;

namespace AlpenMux.Services;

/// <summary> Convertisseurs de valeurs utilisés par le XAML. </summary>
public static class Converters
{
    /// <summary> Vrai si la dépendance manque. </summary>
    public static readonly IValueConverter IsMissing =
        new FuncValueConverter<DepState, bool>(s => s == DepState.Missing);

    /// <summary> Vrai si la dépendance est présente avec un avertissement. </summary>
    public static readonly IValueConverter IsWarning =
        new FuncValueConverter<DepState, bool>(s => s == DepState.Warning);

    /// <summary> Pourcentage (0-100) -> largeur relative pour les mini-barres. </summary>
    public static readonly IValueConverter PercentToHeight =
        new FuncValueConverter<double, double>(v => System.Math.Clamp(v, 0, 100) / 100.0 * 40);

    /// <summary> Légende de la VRAM : les parts réservées par le lancement ont un contour (comme les pointillés de la barre). </summary>
    public static readonly IValueConverter NewSegmentBorder =
        new FuncValueConverter<bool, Avalonia.Thickness>(isNew => new Avalonia.Thickness(isNew ? 1.5 : 0));

    /// <summary> Classe de style « ok » (vrai si la valeur vaut "ok"). </summary>
    public static readonly IValueConverter IsOkClass =
        new FuncValueConverter<string?, bool>(s => s == "ok");

    /// <summary> Classe de style « warn ». </summary>
    public static readonly IValueConverter IsWarnClass =
        new FuncValueConverter<string?, bool>(s => s == "warn");

    /// <summary> Classe de style « error ». </summary>
    public static readonly IValueConverter IsErrorClass =
        new FuncValueConverter<string?, bool>(s => s == "error");

    /// <summary> Classe de style « muted ». </summary>
    public static readonly IValueConverter IsMutedClass =
        new FuncValueConverter<string?, bool>(s => s == "muted");

    /// <summary> Vrai si l'entier vaut 0. </summary>
    public static readonly IValueConverter IsZero =
        new FuncValueConverter<int, bool>(v => v == 0);
}
