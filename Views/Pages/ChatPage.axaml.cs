/*
    Fichier      :  ChatPage.axaml.cs
    Projet       :  AlpenMux

    Description  :
        Page Chat : défilement automatique de la conversation pendant la génération, envoi avec Entrée
        (Maj+Entrée pour une nouvelle ligne), et ouverture des menus « ⋯ » du menu des conversations.
        Glisser-déposer d'une conversation sur un dossier (ou « Sans dossier ») : la vue démarre le geste
        et reçoit le dépôt, le ViewModel fait le déplacement. Maj ou Ctrl maintenu : corbeille de suppression rapide.
*/

using System;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AlpenMux.Controls;
using AlpenMux.ViewModels;
using ColorTextBlock.Avalonia;

namespace AlpenMux.Views.Pages;

/// <summary> Page Chat : conversations, discussion avec un modèle de texte. </summary>
public partial class ChatPage : UserControl
{
    /// <summary> Préfixe du texte porté par le glisser-déposer d'une conversation (les autres textes sont ignorés). </summary>
    private const string DragPrefix = "alpenmux-conversation:";

    /// <summary> Distance (pixels) au-delà de laquelle un appui devient un glisser-déposer. </summary>
    private const double DragThreshold = 6;

    private PointerPressedEventArgs? _pressedForDrag;
    private Point _pressPoint;
    private bool _dragging;

    /// <summary> Maj ou Ctrl enfoncée : corbeilles de suppression rapide du menu (voir Controls/QuickDeleteWatcher.cs). </summary>
    private readonly QuickDeleteWatcher _quickDelete;

    /// <summary> La conversation suit le bas (true tant que l'utilisateur ne remonte pas). </summary>
    private bool _followEnd = true;
    private double _lastOffsetY;

    /// <summary> Crée la page. </summary>
    public ChatPage()
    {
        InitializeComponent();
        _quickDelete = new QuickDeleteWatcher(this, armed =>
        {
            if (DataContext is ChatViewModel vm) vm.QuickDeleteArmed = armed;
        });
        // Défilement automatique pendant la génération : seulement si l'utilisateur est déjà en bas
        MessagesScroll.ScrollChanged += OnMessagesScrolled;
        // Clic sur une image (ou un diagramme) du chat : visionneuse. Tunnel : le Markdown ne masque pas l'événement.
        MessagesScroll.AddHandler(PointerPressedEvent, OnMessagesPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Visionneuse : Échap pour fermer
        AddHandler(KeyDownEvent, OnPageKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ChatViewModel vm) return;
            vm.Messages.CollectionChanged += (_, e) =>
            {
                // Nouvelle conversation ou message envoyé : on repart du bas
                if (e.Action == NotifyCollectionChangedAction.Reset || (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?[0] is ChatMessage { IsUser: true }))
                    _followEnd = true;
                if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems?[0] is ChatMessage m)
                    m.PropertyChanged += (_, _) => FollowEnd();
                FollowEnd();
            };
        };
        // Entrée envoie ; Maj+Entrée ajoute une ligne. Tunnel : la zone de texte ne traite pas la touche avant.
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);

        // Menu des conversations : démarrage du glisser-déposer, dépôt sur un dossier.
        // Tunnel et handledEventsToo : les boutons du menu ne doivent pas masquer ces événements.
        SidebarHost.AddHandler(PointerPressedEvent, OnSidebarPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        SidebarHost.AddHandler(PointerMovedEvent, OnSidebarPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        SidebarHost.AddHandler(PointerReleasedEvent, OnSidebarPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        SidebarHost.AddHandler(DragDrop.DragOverEvent, OnSidebarDragOver, handledEventsToo: true);
        SidebarHost.AddHandler(DragDrop.DragLeaveEvent, OnSidebarDragLeave, handledEventsToo: true);
        SidebarHost.AddHandler(DragDrop.DropEvent, OnSidebarDrop, handledEventsToo: true);
    }

    // ---------------------------------------------------------------- Glisser-déposer d'une conversation

    private void OnSidebarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressedForDrag = null;
        if (!e.GetCurrentPoint(SidebarHost).Properties.IsLeftButtonPressed) return;
        // Seule la zone du titre (bouton taggé « drag ») démarre le geste : le menu ⋯ et la corbeille restent des clics
        if (DraggedConversation(e.Source) is not { } item || item.IsRenaming) return;
        _pressedForDrag = e;
        _pressPoint = e.GetPosition(SidebarHost);
    }

    private void OnSidebarPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedForDrag is not { } press || _dragging) return;
        if (!e.GetCurrentPoint(SidebarHost).Properties.IsLeftButtonPressed) { _pressedForDrag = null; return; }
        var pos = e.GetPosition(SidebarHost);
        if (Math.Abs(pos.X - _pressPoint.X) < DragThreshold && Math.Abs(pos.Y - _pressPoint.Y) < DragThreshold) return;

        var item = DraggedConversation(press.Source);
        _pressedForDrag = null;
        if (item == null) return;
        _ = DragConversationAsync(press, item);
    }

    private void OnSidebarPointerReleased(object? sender, PointerReleasedEventArgs e) => _pressedForDrag = null;

    /// <summary> Lance le glisser-déposer ; le texte porte l'identifiant de la conversation. </summary>
    private async Task DragConversationAsync(PointerPressedEventArgs press, ConversationItemViewModel item)
    {
        _dragging = true;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.CreateText(DragPrefix + item.Id));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        }
        finally
        {
            _dragging = false;
            (DataContext as ChatViewModel)?.SetDropTarget(null);
        }
    }

    private void OnSidebarDragOver(object? sender, DragEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        if (FindGroup(e.Source) is not { } group || DraggedId(e) == null)
        {
            e.DragEffects = DragDropEffects.None;
            vm.SetDropTarget(null);
            return;
        }
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
        vm.SetDropTarget(group);
    }

    /// <summary> Quitte la zone : la mise en évidence disparaît (elle revient au prochain survol de cible). </summary>
    private void OnSidebarDragLeave(object? sender, DragEventArgs e) => (DataContext as ChatViewModel)?.SetDropTarget(null);

    private void OnSidebarDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        vm.SetDropTarget(null);
        if (FindGroup(e.Source) is not { } group || DraggedId(e) is not { } id) return;
        vm.MoveConversationTo(id, group.FolderId);
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary> Identifiant de la conversation portée par le glisser-déposer, null pour tout autre contenu. </summary>
    private static string? DraggedId(DragEventArgs e)
    {
        var text = e.DataTransfer.TryGetText();
        return text != null && text.StartsWith(DragPrefix, StringComparison.Ordinal) ? text[DragPrefix.Length..] : null;
    }

    /// <summary> Conversation dont le titre est sous le pointeur (bouton taggé « drag »), null sinon. </summary>
    private static ConversationItemViewModel? DraggedConversation(object? source)
    {
        if (source is not Visual visual) return null;
        foreach (var v in visual.GetSelfAndVisualAncestors())
        {
            if (v is Control { Tag: "drag" } handle) return handle.DataContext as ConversationItemViewModel;
        }
        return null;
    }

    /// <summary> Groupe (dossier ou « Sans dossier ») sous le pointeur de glisser-déposer. </summary>
    private static ChatGroupViewModel? FindGroup(object? source)
    {
        if (source is not Visual visual) return null;
        foreach (var v in visual.GetSelfAndVisualAncestors())
        {
            if (v is StyledElement { DataContext: ChatGroupViewModel group }) return group;
        }
        return null;
    }

    // ---------------------------------------------------------------- Autres événements

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not ChatViewModel vm) return;
        e.Handled = true;
        if (vm.SendCommand.CanExecute(null)) vm.SendCommand.Execute(null);
    }

    /// <summary> Clic sur une image de la conversation : elle s'ouvre dans la visionneuse. </summary>
    private void OnMessagesPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(MessagesScroll).Properties.IsLeftButtonPressed) return;
        if (DataContext is ChatViewModel vm && ImageUnder(e.Source) is { } bitmap) vm.PreviewImage = bitmap;
    }

    /// <summary> Image sous le pointeur : une Image Avalonia (pièce jointe) ou une image Markdown (diagramme). </summary>
    private static Bitmap? ImageUnder(object? source)
    {
        if (source is not Visual visual) return null;
        foreach (var v in visual.GetSelfAndVisualAncestors())
        {
            if (v is Image { Source: Bitmap bitmap }) return bitmap;
            // Image Markdown : le moteur la dessine dans le bloc de texte (CTextBlock), pas comme contrôle enfant.
            // Un bloc qui ne contient que cette image (un diagramme = un paragraphe) l'ouvre.
            if (v is CTextBlock block && MarkdownImageOf(block) is { } markdownImage) return markdownImage;
        }
        return null;
    }

    private static Bitmap? MarkdownImageOf(CTextBlock block)
    {
        var parts = block.Content;
        var onlyImages = parts.All(p => p is CImage || p is CRun { Text: var t } && string.IsNullOrWhiteSpace(t));
        return onlyImages ? parts.OfType<CImage>().Select(i => i.Image).OfType<Bitmap>().FirstOrDefault() : null;
    }

    /// <summary> Échap ferme la visionneuse. </summary>
    private void OnPageKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ChatViewModel { IsImagePreviewOpen: true } vm) return;
        vm.ClosePreviewCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary> Ouvre le menu « ⋯ » attaché au bouton cliqué. </summary>
    private void OpenMenu(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Open();
    }

    /// <summary>
    /// Suit la fin de la conversation tant que l'utilisateur y est. Seul un changement de position compte :
    /// une conversation qui grandit ne déplace pas la barre, donc ne désactive pas le suivi.
    /// </summary>
    private void OnMessagesScrolled(object? sender, ScrollChangedEventArgs e)
    {
        var y = MessagesScroll.Offset.Y;
        if (Math.Abs(y - _lastOffsetY) < 0.5)
        {
            // Contenu qui grandit (texte streamé, diagramme redessiné) : ce gestionnaire vient après la mise en page,
            // la fin est donc connue. Demander le défilement plus tôt visait l'ancienne hauteur.
            if (_followEnd) MessagesScroll.ScrollToEnd();
            return;
        }
        _lastOffsetY = y;
        _followEnd = y >= MessagesScroll.Extent.Height - MessagesScroll.Viewport.Height - 24;
    }

    /// <summary> Descend en bas si le suivi est actif (sinon l'utilisateur est en train de relire). </summary>
    private void FollowEnd()
    {
        if (_followEnd) ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => MessagesScroll.ScrollToEnd(), DispatcherPriority.Background);
}
