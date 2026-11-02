/*
    Fichier      :  Icons.cs
    Projet       :  AlpenMux

    Description  :
        Codes des icônes Phosphor (police Assets/Fonts/Phosphor.ttf) utilisées côté C#.
        Côté XAML, les mêmes icônes sont déclarées comme ressources dans App.axaml.
*/

namespace AlpenMux.Services;

/// <summary> Glyphes de la police Phosphor utilisés dans le code C#. </summary>
public static class Icons
{
    /// <summary> Cercle coché. </summary>
    public const string CheckCircle = "";
    /// <summary> Cercle barré. </summary>
    public const string XCircle     = "";
    /// <summary> Avertissement. </summary>
    public const string Warning     = "";
    /// <summary> Information. </summary>
    public const string Info        = "";
    /// <summary> Sablier. </summary>
    public const string Hourglass   = "";
    /// <summary> Arrêt. </summary>
    public const string Stop        = "";
    /// <summary> Lecture. </summary>
    public const string Play        = "\uE3D0";
    /// <summary> Haut-parleur. </summary>
    public const string SpeakerHigh = "\uE44A";
    /// <summary> Lune (veille). </summary>
    public const string Moon        = "\uE330";
}
