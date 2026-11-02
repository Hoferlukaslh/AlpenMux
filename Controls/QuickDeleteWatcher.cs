/*
    Fichier      :  QuickDeleteWatcher.cs
    Projet       :  AlpenMux

    Description  :
        Suivi de Maj / Ctrl pour la suppression rapide (chat, galerie d'images). Tant qu'une de ces touches est enfoncée,
        la page affiche les corbeilles et la suppression n'est plus confirmée. Écoute la fenêtre entière (le focus peut
        être ailleurs), et se recale sur les modificateurs du pointeur (corrige un relâchement manqué).
*/

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace AlpenMux.Controls;

/// <summary> Indique à une page si Maj ou Ctrl est enfoncée. </summary>
public sealed class QuickDeleteWatcher
{
    private readonly UserControl _host;
    private readonly Action<bool> _setArmed;
    private readonly HashSet<Key> _held = new();
    private TopLevel? _top;

    /// <summary> Suit les touches de la page. </summary>
    /// <param name="host"> Page suivie. </param>
    /// <param name="setArmed"> Reçoit true quand Maj ou Ctrl est enfoncée, false quand elles sont relâchées. </param>
    public QuickDeleteWatcher(UserControl host, Action<bool> setArmed)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(setArmed);
        _host = host;
        _setArmed = setArmed;
        host.AttachedToVisualTree += (_, _) => Attach();
        host.DetachedFromVisualTree += (_, _) => Detach();
        // Tunnel et handledEventsToo : les contrôles de la page ne doivent pas masquer ces événements
        host.AddHandler(InputElement.PointerPressedEvent, (_, e) => Sync(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
        host.AddHandler(InputElement.PointerMovedEvent, (_, e) => Sync(e.KeyModifiers), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void Attach()
    {
        _top = TopLevel.GetTopLevel(_host);
        if (_top == null) return;
        _top.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        _top.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        if (_top is Window window) window.Deactivated += OnDeactivated;
    }

    private void Detach()
    {
        if (_top != null)
        {
            _top.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            _top.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
            if (_top is Window window) window.Deactivated -= OnDeactivated;
        }
        _top = null;
        _held.Clear();
        _setArmed(false);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsModifierKey(e.Key)) return;
        _held.Add(e.Key);
        _setArmed(true);
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (!IsModifierKey(e.Key)) return;
        _held.Remove(e.Key);
        _setArmed(_held.Count > 0);
    }

    /// <summary> Fenêtre désactivée : les touches relâchées ailleurs ne sont plus vues, on repart à zéro. </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        _held.Clear();
        _setArmed(false);
    }

    /// <summary> Recale l'état sur les modificateurs du pointeur. </summary>
    private void Sync(KeyModifiers modifiers)
    {
        var held = modifiers.HasFlag(KeyModifiers.Shift) || modifiers.HasFlag(KeyModifiers.Control);
        if (!held) _held.Clear();
        _setArmed(held);
    }

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl;
}
