/*
    Fichier      :  ChatSidebar.cs
    Projet       :  AlpenMux

    Description  :
        Menu des conversations de la page Chat : dossiers repliables et conversations rangées dedans
        (ou sans dossier). Chaque ligne garde son état d'édition (renommer, déplacer, confirmer la suppression) ;
        la suppression rapide (corbeille affichée quand Maj ou Ctrl est maintenu) n'a pas de confirmation.
        Les actions réelles sont exécutées par ChatViewModel, qui écrit l'index sur le disque.
*/

using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AlpenMux.Services;

namespace AlpenMux.ViewModels;

/// <summary> Choix de dossier (null = sans dossier), dans la liste « Déplacer ». </summary>
/// <param name="Id"> Identifiant du dossier, null pour « sans dossier ». </param>
/// <param name="Name"> Nom affiché. </param>
public sealed record ChatFolderOption(string? Id, string Name);

/// <summary> Une conversation dans le menu. </summary>
public sealed partial class ConversationItemViewModel : ObservableObject
{
    private readonly ChatViewModel _owner;

    /// <summary> Informations enregistrées de la conversation. </summary>
    public ChatMeta Meta { get; }

    /// <summary> Identifiant de la conversation. </summary>
    public string Id => Meta.Id;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _isMoving;
    [ObservableProperty] private ChatFolderOption? _moveOption;
    [ObservableProperty] private bool _isConfirmingDelete;

    /// <summary> Liste des dossiers pour « Déplacer » (partagée avec le menu). </summary>
    public ObservableCollection<ChatFolderOption> FolderOptions => _owner.FolderOptions;

    /// <summary> Date de la dernière modification, lisible. </summary>
    public string UpdatedText => Meta.UpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    /// <summary> Corbeille affichée à la place du menu : Maj ou Ctrl maintenu, suppression sans confirmation. </summary>
    public bool ShowQuickDelete => _owner.QuickDeleteArmed && !IsRenaming;

    /// <summary> Menu « ⋯ » : masqué pendant la suppression rapide et l'édition du titre. </summary>
    public bool ShowMenu => !_owner.QuickDeleteArmed && !IsRenaming;

    /// <summary> Crée la ligne. </summary>
    /// <param name="meta"> Informations de la conversation. </param>
    /// <param name="owner"> Page Chat (ouverture, renommage, suppression). </param>
    public ConversationItemViewModel(ChatMeta meta, ChatViewModel owner)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentNullException.ThrowIfNull(owner);
        Meta = meta;
        _owner = owner;
        _title = meta.Title;
    }

    /// <summary> Affiche la conversation. </summary>
    [RelayCommand]
    private void Open() => _owner.OpenConversation(this);

    /// <summary> Passe le titre en édition. </summary>
    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Title;
        IsRenaming = true;
        IsConfirmingDelete = false;
    }

    /// <summary> Valide le nouveau titre (vide : titre inchangé). </summary>
    [RelayCommand]
    private void CommitRename()
    {
        IsRenaming = false;
        var name = RenameText.Trim();
        if (name.Length > 0 && name != Title) _owner.RenameConversation(this, name);
    }

    /// <summary> Annule l'édition du titre. </summary>
    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    /// <summary> Ouvre le choix du dossier de destination. </summary>
    [RelayCommand]
    private void BeginMove()
    {
        IsConfirmingDelete = false;
        // Choix initial d'abord : la liste ne se referme qu'au choix d'un dossier (voir OnMoveOptionChanged)
        MoveOption = FolderOptions.FirstOrDefault(o => o.Id == Meta.FolderId) ?? FolderOptions[0];
        IsMoving = true;
    }

    /// <summary> Annule le déplacement. </summary>
    [RelayCommand]
    private void CancelMove() => IsMoving = false;

    /// <summary> Le titre suit les informations enregistrées. </summary>
    partial void OnTitleChanged(string value) => Meta.Title = value;

    partial void OnIsRenamingChanged(bool value) => RefreshQuickDelete();

    /// <summary> Relit les conditions d'affichage des boutons (après un changement de Maj/Ctrl). </summary>
    internal void RefreshQuickDelete()
    {
        OnPropertyChanged(nameof(ShowQuickDelete));
        OnPropertyChanged(nameof(ShowMenu));
    }

    /// <summary> Suppression immédiate (corbeille du mode Maj/Ctrl), sans confirmation. </summary>
    [RelayCommand]
    private void DeleteNow() => _owner.DeleteConversation(this);

    /// <summary> Marque la conversation comme modifiée maintenant (tri du menu, date affichée). </summary>
    internal void Touch()
    {
        Meta.UpdatedAt = DateTime.UtcNow;
        OnPropertyChanged(nameof(UpdatedText));
    }

    /// <summary> Choix d'un dossier dans la liste : la conversation y est déplacée. </summary>
    partial void OnMoveOptionChanged(ChatFolderOption? value)
    {
        if (!IsMoving || value is not { } option) return;
        IsMoving = false;
        if (option.Id != Meta.FolderId) _owner.MoveConversation(this, option.Id);
    }

    /// <summary> Demande la confirmation de la suppression. </summary>
    [RelayCommand]
    private void BeginDelete()
    {
        IsMoving = false;
        IsConfirmingDelete = true;
    }

    /// <summary> Supprime la conversation et ses messages. </summary>
    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        _owner.DeleteConversation(this);
    }

    /// <summary> Annule la suppression. </summary>
    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;
}

/// <summary> Un groupe du menu : un dossier, ou les conversations sans dossier. </summary>
public sealed partial class ChatGroupViewModel : ObservableObject
{
    private readonly ChatViewModel _owner;

    /// <summary> Identifiant du dossier (null = sans dossier). </summary>
    public string? FolderId { get; }

    /// <summary> Le groupe est un dossier (en-tête, menu, repliage). </summary>
    public bool IsFolder => FolderId != null;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _isExpanded = true;
    [ObservableProperty] private bool _isRenaming;
    [ObservableProperty] private string _renameText = "";
    [ObservableProperty] private bool _isConfirmingDelete;

    /// <summary> Conversations du groupe, les plus récentes d'abord. </summary>
    public ObservableCollection<ConversationItemViewModel> Items { get; } = new();

    /// <summary> Titre affiché au-dessus des conversations sans dossier (quand il existe des dossiers). </summary>
    public bool ShowLabel { get; }

    /// <summary> Nombre de conversations du groupe (affiché dans l'en-tête d'un dossier). </summary>
    public int Count => Items.Count;

    /// <summary> Nombre de conversations, en texte (en-tête d'un dossier). </summary>
    public string CountText => Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary> Le groupe est visé par un glisser-déposer de conversation (fond mis en évidence). </summary>
    [ObservableProperty] private bool _isDropTarget;

    /// <summary> Corbeille du dossier affichée à la place du menu (Maj ou Ctrl maintenu). </summary>
    public bool ShowQuickDelete => IsFolder && _owner.QuickDeleteArmed && !IsRenaming;

    /// <summary> Menu « ⋯ » du dossier : masqué pendant la suppression rapide et l'édition du nom. </summary>
    public bool ShowMenu => !_owner.QuickDeleteArmed && !IsRenaming;

    /// <summary> Trait vertical à gauche des conversations d'un dossier : elles sont rangées dedans. </summary>
    public Thickness GuideThickness => IsFolder ? new Thickness(2, 0, 0, 0) : default;

    /// <summary> Retrait des conversations d'un dossier. </summary>
    public Thickness ItemsPadding => IsFolder ? new Thickness(10, 2, 0, 2) : default;

    /// <summary> Crée le groupe. </summary>
    /// <param name="folderId"> Dossier (null = sans dossier). </param>
    /// <param name="name"> Nom affiché. </param>
    /// <param name="expanded"> Dossier déplié. </param>
    /// <param name="owner"> Page Chat (actions sur les dossiers). </param>
    /// <param name="showLabel"> Afficher le titre (groupe sans dossier, quand il existe des dossiers). </param>
    public ChatGroupViewModel(string? folderId, string name, bool expanded, ChatViewModel owner, bool showLabel = false)
    {
        ArgumentNullException.ThrowIfNull(owner);
        FolderId = folderId;
        _name = name;
        _isExpanded = expanded;
        _owner = owner;
        ShowLabel = showLabel;
    }

    /// <summary> Le contenu du groupe est affiché (sans dossier : toujours). </summary>
    public bool ShowItems => IsExpanded || !IsFolder;

    partial void OnIsRenamingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowQuickDelete));
        OnPropertyChanged(nameof(ShowMenu));
    }

    /// <summary> Relit les conditions d'affichage des boutons (après un changement de Maj/Ctrl). </summary>
    internal void RefreshQuickDelete()
    {
        OnPropertyChanged(nameof(ShowQuickDelete));
        OnPropertyChanged(nameof(ShowMenu));
    }

    /// <summary> Suppression immédiate du dossier (corbeille du mode Maj/Ctrl) ; ses conversations restent. </summary>
    [RelayCommand]
    private void DeleteNow()
    {
        if (FolderId != null) _owner.DeleteFolder(FolderId);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowItems));
        if (FolderId != null) _owner.SetFolderExpanded(FolderId, value);
    }

    /// <summary> Replie ou déplie le dossier. </summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary> Crée une conversation dans ce dossier. </summary>
    [RelayCommand]
    private void NewConversation() => _owner.NewConversation(FolderId);

    /// <summary> Passe le nom du dossier en édition. </summary>
    [RelayCommand]
    private void BeginRename()
    {
        RenameText = Name;
        IsRenaming = true;
        IsConfirmingDelete = false;
    }

    /// <summary> Valide le nouveau nom du dossier. </summary>
    [RelayCommand]
    private void CommitRename()
    {
        IsRenaming = false;
        var name = RenameText.Trim();
        if (FolderId != null && name.Length > 0 && name != Name) _owner.RenameFolder(FolderId, name);
    }

    /// <summary> Annule l'édition du nom. </summary>
    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    /// <summary> Demande la confirmation de la suppression du dossier. </summary>
    [RelayCommand]
    private void BeginDelete()
    {
        IsRenaming = false;
        IsConfirmingDelete = true;
    }

    /// <summary> Supprime le dossier ; ses conversations restent, sans dossier. </summary>
    [RelayCommand]
    private void ConfirmDelete()
    {
        IsConfirmingDelete = false;
        if (FolderId != null) _owner.DeleteFolder(FolderId);
    }

    /// <summary> Annule la suppression du dossier. </summary>
    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;
}
