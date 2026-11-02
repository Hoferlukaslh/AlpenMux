/*
    Fichier      :  WindowsTrayIcon.cs
    Projet       :  AlpenMux

    Description  :
        Icône de la zone de notification Windows, créée directement avec Shell_NotifyIcon.
        Avalonia ne signale pas le clic droit sur TrayIcon : son menu natif a une taille imposée (flèche de
        défilement). Cette classe reçoit donc les clics elle-même ; le menu est une fenêtre dédiée (TrayMenuWindow).
        Le PNG de l'icône est passé tel quel à CreateIconFromResourceEx (accepté depuis Windows Vista).
*/

using System;
using System.Runtime.InteropServices;
using Avalonia;

namespace AlpenMux.Services;

/// <summary> Icône de la zone de notification sous Windows : clic gauche et clic droit. </summary>
internal sealed class WindowsTrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint TrayMessage = WmApp + 1;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4;
    private const int HwndMessage = -3;
    private const int SmCxSmIcon = 49, SmCySmIcon = 50;
    private const uint IconVersion = 0x00030000;
    private const int TooltipLength = 127;

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ScreenPoint
    {
        public int X;
        public int Y;
    }

    // Le délégué doit rester référencé : le système l'appelle par un pointeur natif
    private readonly WndProcDelegate _wndProc;
    private readonly nint _hwnd;
    private readonly nint _icon;
    private bool _disposed;

    /// <summary> Clic gauche sur l'icône (afficher / masquer la fenêtre). </summary>
    public event Action? LeftClick;

    /// <summary> Clic droit sur l'icône, avec la position du curseur en pixels écran. </summary>
    public event Action<PixelPoint>? RightClick;

    /// <summary> Crée l'icône et sa fenêtre de messages. </summary>
    /// <param name="pngIcon"> Icône au format PNG. </param>
    /// <param name="tooltip"> Info-bulle initiale. </param>
    public WindowsTrayIcon(byte[] pngIcon, string tooltip)
    {
        ArgumentNullException.ThrowIfNull(pngIcon);
        _wndProc = WndProc;
        _hwnd = CreateMessageWindow();
        _icon = CreateIconFromResourceEx(pngIcon, (uint)pngIcon.Length, true, IconVersion,
            GetSystemMetrics(SmCxSmIcon), GetSystemMetrics(SmCySmIcon), 0);
        if (_icon == 0) throw new InvalidOperationException("Icône de la zone de notification illisible.");

        var data = NewData();
        data.uFlags = NifMessage | NifIcon | NifTip;
        data.uCallbackMessage = TrayMessage;
        data.hIcon = _icon;
        data.szTip = Truncate(tooltip);
        Shell_NotifyIconW(NimAdd, ref data);
    }

    /// <summary> Met à jour l'info-bulle (nombre de serveurs actifs). </summary>
    public void SetTooltip(string text)
    {
        if (_disposed) return;
        var data = NewData();
        data.uFlags = NifTip;
        data.szTip = Truncate(text);
        Shell_NotifyIconW(NimModify, ref data);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Sans suppression explicite, l'icône reste affichée jusqu'au survol de la barre des tâches
        var data = NewData();
        Shell_NotifyIconW(NimDelete, ref data);
        DestroyIcon(_icon);
        DestroyWindow(_hwnd);
    }

    private NotifyIconData NewData() => new()
    {
        cbSize = Marshal.SizeOf<NotifyIconData>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = string.Empty,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private static string Truncate(string text) =>
        text.Length <= TooltipLength ? text : text[..TooltipLength];

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if (msg != TrayMessage) return DefWindowProcW(hWnd, msg, wParam, lParam);

        // Pour un message de notification, les bits faibles de lParam contiennent le message souris
        var mouse = (uint)((long)lParam & 0xFFFF);
        if (mouse == WmLButtonUp)
        {
            LeftClick?.Invoke();
        }
        else if (mouse == WmRButtonUp && GetCursorPos(out var p))
        {
            // Sans cela, le menu ne se ferme pas quand on clique ailleurs
            SetForegroundWindow(hWnd);
            RightClick?.Invoke(new PixelPoint(p.X, p.Y));
        }
        return 0;
    }

    private nint CreateMessageWindow()
    {
        const string className = "AlpenMuxTrayWindow";
        var instance = GetModuleHandleW(null);
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = className,
        };
        // Un échec signifie que la classe existe déjà (second appel) : CreateWindowExW l'utilisera
        RegisterClassExW(ref wc);
        var hwnd = CreateWindowExW(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, 0, instance, 0);
        if (hwnd == 0) throw new InvalidOperationException("Fenêtre de messages de la zone de notification impossible à créer.");
        return hwnd;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(nint icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateIconFromResourceEx(byte[] bits, uint bitsLength, bool isIcon, uint version,
        int desiredWidth, int desiredHeight, uint flags);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out ScreenPoint point);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
