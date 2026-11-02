/*
    Fichier      :  Localization.cs
    Projet       :  AlpenMux

    Description  :
        Système de traduction de l'application.
        Les textes sont stockés dans des fichiers JSON (clé -> texte) :
            - intégrés : Assets/Lang/<code>.json (fr = référence, en)
            - ajoutés par l'utilisateur : ~/.config/alpenmux/lang/<code>.json
        Pour ajouter une langue : copier fr.json, renommer (ex. de.json), traduire les valeurs
        et changer "_name". La langue apparaît alors dans les paramètres.

    Utilisation :
        - XAML : Text="{l:Tr Nav_Models}"   (se met à jour au changement de langue)
        - C#   : Loc.T("Nav_Models")  /  Loc.F("Models_Count", n)
*/

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

namespace AlpenMux.Services;

/// <summary> Traductions de l'interface (fichiers Assets/Lang/*.json et ~/.config/alpenmux/lang). </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary> Instance unique (utilisée par l'extension de balisage {l:Tr}). </summary>
    public static Loc Instance { get; } = new();

    /// <summary> Langue par défaut. </summary>
    public const string DefaultLanguage = "fr";

    private Dictionary<string, string> _strings  = new();
    private Dictionary<string, string> _fallback = new();

    /// <summary> Levé au changement de langue (indexeur). </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary> Levé après un changement de langue (pour les textes construits en C#). </summary>
    public event Action? LanguageChanged;

    /// <summary> Code de la langue actuelle. </summary>
    public string CurrentLanguage { get; private set; } = DefaultLanguage;

    /// <summary> Dossier des traductions ajoutées par l'utilisateur. </summary>
    public static string UserLangDirectory => Path.Combine(SettingsService.ConfigDirectory, "lang");

    private Loc()
    {
        _fallback = LoadLanguage(DefaultLanguage) ?? new();
        _strings  = _fallback;
    }

    /// <summary> Texte traduit d'une clé (la clé elle-même si elle manque). </summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var v) ? v :
        _fallback.TryGetValue(key, out var f) ? f : $"#{key}";

    /// <summary> Texte traduit d'une clé. </summary>
    public static string T(string key) => Instance[key];

    /// <summary> Texte traduit avec ses paramètres ({0}, {1}…). </summary>
    public static string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, Instance[key], args); }
        catch (FormatException) { return Instance[key]; }
    }

    /// <summary> Langues disponibles : (code, nom affiché). </summary>
    public IReadOnlyList<LanguageInfo> AvailableLanguages()
    {
        var codes = new SortedSet<string> { "fr", "en" };
        if (Directory.Exists(UserLangDirectory))
            foreach (var f in Directory.GetFiles(UserLangDirectory, "*.json"))
                codes.Add(Path.GetFileNameWithoutExtension(f));

        return codes
            .Select(c => new LanguageInfo(c, LoadLanguage(c)?.GetValueOrDefault("_name") ?? c))
            .ToList();
    }

    /// <summary> Change la langue de l'interface. </summary>
    public void SetLanguage(string code)
    {
        var dict = LoadLanguage(code);
        if (dict == null) { code = DefaultLanguage; dict = _fallback; }

        CurrentLanguage = code;
        _strings = dict;
        try { CultureInfo.CurrentUICulture = CultureInfo.CurrentCulture = new CultureInfo(code); }
        catch (CultureNotFoundException) { }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentLanguage)));
        LanguageChanged?.Invoke();
    }

    private static Dictionary<string, string>? LoadLanguage(string code)
    {
        try
        {
            string? json = null;
            var userFile = Path.Combine(UserLangDirectory, code + ".json");
            if (File.Exists(userFile))
                json = File.ReadAllText(userFile);
            else
            {
                var uri = new Uri($"avares://alpenmux/Assets/Lang/{code}.json");
                if (AssetLoader.Exists(uri))
                {
                    using var s = AssetLoader.Open(uri);
                    using var r = new StreamReader(s);
                    json = r.ReadToEnd();
                }
            }
            return json == null ? null
                : JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Loc] Langue '{code}' illisible : {ex.Message}");
            return null;
        }
    }
}

/// <summary> Langue disponible. </summary>
/// <param name="Code"> Code (fr, en…). </param>
/// <param name="Name"> Nom affiché. </param>
public sealed record LanguageInfo(string Code, string Name)
{
    /// <summary> Nom affiché. </summary>
    public override string ToString() => Name;
}

/// <summary> Extension XAML : {l:Tr Cle} -> liaison vers Loc.Instance[Cle]. </summary>
public sealed class TrExtension : MarkupExtension
{
    /// <summary> Crée l'extension (clé fournie par la propriété Key). </summary>
    public TrExtension() { }
    /// <summary> Crée l'extension pour cette clé. </summary>
    public TrExtension(string key) => Key = key;

    /// <summary> Clé de traduction. </summary>
    public string Key { get; set; } = "";

    // Liaison sur CurrentLanguage (notifiée à chaque changement de langue) ;
    // le convertisseur renvoie le texte de la clé dans la langue courante.
    /// <summary> Liaison vers le texte traduit (mis à jour au changement de langue). </summary>
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding(nameof(Loc.CurrentLanguage))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<string, string>(_ => Loc.T(Key)),
        };
}
