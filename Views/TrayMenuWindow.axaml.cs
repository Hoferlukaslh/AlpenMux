/*
    Fichier      :  TrayMenuWindow.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Menu du clic droit sur l'icône de la zone de notification (Windows). Positionné au-dessus du curseur,
        dans la zone de travail de l'écran (jamais sous la barre des tâches), et fermé dès qu'il perd le focus.
*/

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AlpenMux.Views;

/// <summary> Menu de la zone de notification : afficher, état des serveurs, arrêt, quitter. </summary>
public partial class TrayMenuWindow : Window
{
    /// <summary> Demande d'affichage de la fenêtre principale. </summary>
    public event Action? ShowRequested;

    /// <summary> Demande d'arrêt de tous les serveurs. </summary>
    public event Action? StopAllRequested;

    /// <summary> Demande de fermeture de l'application. </summary>
    public event Action? QuitRequested;

    /// <summary> Crée le menu (fermé). </summary>
    public TrayMenuWindow()
    {
        InitializeComponent();
        // Cliquer ailleurs ferme le menu, comme un menu contextuel natif
        Deactivated += (_, _) => Hide();
        KeyDown += OnKeyDown;
    }

    /// <summary> Affiche le menu près du curseur. </summary>
    /// <param name="cursor"> Position du curseur en pixels écran. </param>
    /// <param name="serversText"> Ligne d'état des serveurs. </param>
    /// <param name="canStopAll"> Vrai si au moins un serveur tourne. </param>
    public void OpenAt(PixelPoint cursor, string serversText, bool canStopAll)
    {
        ServersText.Text = serversText;
        // Sans serveur actif, l'action n'a pas de sens : on la retire plutôt que de la griser
        StopAllButton.IsVisible = canStopAll;
        Show();
        SetSquareCorners();
        Activate();
        // La taille n'est connue qu'après la mise en page : on positionne ensuite
        Dispatcher.UIThread.Post(() => PlaceNear(cursor), DispatcherPriority.Loaded);
    }

    /// <summary> Retire l'arrondi que Windows 11 applique aux fenêtres sans décoration. </summary>
    private void SetSquareCorners()
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;
        // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_DONOTROUND = 1 ; un échec laisse simplement l'arrondi
        int preference = 1;
        DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);

    private void PlaceNear(PixelPoint cursor)
    {
        var screen = Screens.ScreenFromPoint(cursor) ?? Screens.Primary;
        if (screen == null) return;

        var scaling = screen.Scaling;
        var width = (int)Math.Ceiling(Bounds.Width * scaling);
        var height = (int)Math.Ceiling(Bounds.Height * scaling);
        var area = screen.WorkingArea;

        // Au-dessus et aligné à droite du curseur, ramené dans la zone de travail
        var x = Math.Clamp(cursor.X - width, area.X, Math.Max(area.X, area.Right - width));
        var y = Math.Clamp(cursor.Y - height, area.Y, Math.Max(area.Y, area.Bottom - height));
        Position = new PixelPoint(x, y);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Hide();
        e.Handled = true;
    }

    private void OnShowClick(object? sender, RoutedEventArgs e)
    {
        Hide();
        ShowRequested?.Invoke();
    }

    private void OnStopAllClick(object? sender, RoutedEventArgs e)
    {
        Hide();
        StopAllRequested?.Invoke();
    }

    private void OnQuitClick(object? sender, RoutedEventArgs e)
    {
        Hide();
        QuitRequested?.Invoke();
    }
}
