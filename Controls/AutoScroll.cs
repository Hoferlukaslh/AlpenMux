/*
    Fichier      :  AutoScroll.cs
    Projet       :  AlpenMux

    Description  :
        Propriété attachée : fait défiler une ListBox jusqu'au dernier élément
        à chaque ajout (consoles et journaux). Désactivable en direct (case "Suivre").
*/

using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace AlpenMux.Controls;

/// <summary> Propriété attachée : une ListBox défile automatiquement vers le dernier élément ajouté. </summary>
public static class AutoScroll
{
    /// <summary> Propriété attachée IsEnabled. </summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ListBox, bool>("IsEnabled", typeof(AutoScroll));

    private static readonly AttachedProperty<INotifyCollectionChanged?> HookedProperty =
        AvaloniaProperty.RegisterAttached<ListBox, INotifyCollectionChanged?>("Hooked", typeof(AutoScroll));

    /// <summary> Lit la propriété IsEnabled. </summary>
    public static bool GetIsEnabled(ListBox l) => l.GetValue(IsEnabledProperty);
    /// <summary> Écrit la propriété IsEnabled. </summary>
    public static void SetIsEnabled(ListBox l, bool v) => l.SetValue(IsEnabledProperty, v);

    static AutoScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ListBox>((l, _) => Hook(l));
        ItemsControl.ItemsSourceProperty.Changed.AddClassHandler<ListBox>((l, _) => Hook(l));
    }

    private static void Hook(ListBox l)
    {
        if (l.GetValue(HookedProperty) is { } old)
            old.CollectionChanged -= Handler(l);

        if (l.ItemsSource is INotifyCollectionChanged ncc)
        {
            l.SetValue(HookedProperty, ncc);
            ncc.CollectionChanged += Handler(l);
            ScrollToEnd(l);
        }
    }

    // Un délégué par ListBox, pour pouvoir le retirer
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ListBox, NotifyCollectionChangedEventHandler> Handlers = new();

    private static NotifyCollectionChangedEventHandler Handler(ListBox l) =>
        Handlers.GetValue(l, lb => (_, _) => ScrollToEnd(lb));

    private static void ScrollToEnd(ListBox l)
    {
        if (!GetIsEnabled(l)) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (l.ItemCount > 0) l.ScrollIntoView(l.ItemCount - 1);
        }, DispatcherPriority.Background);
    }
}
