/*
    Fichier      :  ChatStore.cs
    Projet       :  AlpenMux

    Description  :
        Conservation des conversations de la page Chat, entre les sessions (comme les dossiers de conversations
        de LM Studio) :
            ~/.local/share/alpenmux/chats/index.json       dossiers, titres, dates, réglages de chaque conversation
            ~/.local/share/alpenmux/chats/<id>.json        messages d'une conversation (images en data URI comprises)
        L'index reste léger : la liste se charge sans relire toutes les images. Les écritures passent par un
        fichier temporaire, pour qu'une coupure de courant n'abîme pas un fichier existant.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace AlpenMux.Services;

/// <summary> Dossier de conversations. </summary>
public sealed class ChatFolder
{
    /// <summary> Identifiant. </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary> Nom affiché. </summary>
    public string Name { get; set; } = "";
    /// <summary> Dossier déplié dans le menu. </summary>
    public bool Expanded { get; set; } = true;
}

/// <summary> Informations d'une conversation (sans les messages). </summary>
public sealed class ChatMeta
{
    /// <summary> Identifiant (nom du fichier des messages). </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary> Titre affiché. </summary>
    public string Title { get; set; } = "";
    /// <summary> Le titre a été donné par l'utilisateur (sinon il suit le premier message). </summary>
    public bool TitleSet { get; set; }
    /// <summary> Dossier (null = sans dossier). </summary>
    public string? FolderId { get; set; }
    /// <summary> Dernière modification (tri de la liste). </summary>
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    /// <summary> Prompt système de la conversation. </summary>
    public string SystemPrompt { get; set; } = "";
    /// <summary> Réflexion du modèle activée (si le modèle sait la désactiver). </summary>
    public bool Think { get; set; } = true;
    /// <summary> Base de connaissances interrogée à chaque message (null = aucune). </summary>
    public string? KnowledgeBaseId { get; set; }
}

/// <summary> Contenu de l'index : dossiers, conversations, conversation ouverte. </summary>
public sealed class ChatIndex
{
    /// <summary> Dossiers. </summary>
    public List<ChatFolder> Folders { get; set; } = new();
    /// <summary> Conversations. </summary>
    public List<ChatMeta> Conversations { get; set; } = new();
    /// <summary> Conversation ouverte à la fermeture. </summary>
    public string? OpenId { get; set; }
}

/// <summary> Image ou document joint, tel qu'enregistré. </summary>
public sealed class ChatAttachment
{
    /// <summary> Nom du document (vide pour une image). </summary>
    public string Name { get; set; } = "";
    /// <summary> Texte Markdown du document (vide pour une image). </summary>
    public string Markdown { get; set; } = "";
    /// <summary> Image en data URI (vide pour un document). </summary>
    public string DataUri { get; set; } = "";
}

/// <summary> Message enregistré. </summary>
public sealed class ChatMessageRecord
{
    /// <summary> "user" ou "assistant". </summary>
    public string Role { get; set; } = "user";
    /// <summary> Texte d'origine. </summary>
    public string Content { get; set; } = "";
    /// <summary> Réflexion du modèle. </summary>
    public string Reasoning { get; set; } = "";
    /// <summary> Statistiques affichées sous la réponse. </summary>
    public string Stats { get; set; } = "";
    /// <summary> Images et documents joints au message. </summary>
    public List<ChatAttachment> Attachments { get; set; } = new();
    /// <summary> Passages de la base de connaissances cités par la réponse (vide si aucun). </summary>
    public List<RagHit> Sources { get; set; } = new();
}

/// <summary> Messages d'une conversation. </summary>
public sealed class ChatRecord
{
    /// <summary> Messages, dans l'ordre. </summary>
    public List<ChatMessageRecord> Messages { get; set; } = new();
}

/// <summary> Lecture et écriture des conversations sur le disque. </summary>
public interface IChatStore
{
    /// <summary> Charge l'index (vide si absent ou illisible). </summary>
    ChatIndex LoadIndex();

    /// <summary> Écrit l'index. </summary>
    void SaveIndex(ChatIndex index);

    /// <summary> Messages d'une conversation (null si absents ou illisibles). </summary>
    ChatRecord? LoadRecord(string id);

    /// <summary> Écrit les messages d'une conversation. </summary>
    void SaveRecord(string id, ChatRecord record);

    /// <summary> Supprime les messages d'une conversation. </summary>
    void DeleteRecord(string id);
}

/// <summary> Implémentation de <see cref="IChatStore"/> : un fichier JSON par conversation et un index. </summary>
public sealed class ChatStore : IChatStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ILogger<ChatStore> _log;

    /// <summary> Crée le service. </summary>
    /// <param name="log"> Journal (fichiers illisibles). </param>
    public ChatStore(ILogger<ChatStore> log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <summary> Dossier des conversations. </summary>
    public static string Dir => Path.Combine(AppPaths.DataDir, "chats");

    private static string IndexPath => Path.Combine(Dir, "index.json");
    private static string RecordPath(string id) => Path.Combine(Dir, Path.GetFileName(id) + ".json");

    /// <inheritdoc />
    public ChatIndex LoadIndex()
    {
        try
        {
            if (File.Exists(IndexPath))
                return JsonSerializer.Deserialize<ChatIndex>(File.ReadAllText(IndexPath), Options) ?? new ChatIndex();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            // Index illisible : on repart d'une liste vide plutôt que d'empêcher l'ouverture de la page
            _log.LogWarning("Index des conversations illisible : {Message}", ex.Message);
        }
        return new ChatIndex();
    }

    /// <inheritdoc />
    public void SaveIndex(ChatIndex index)
    {
        Directory.CreateDirectory(Dir);
        WriteAtomic(IndexPath, JsonSerializer.Serialize(index, Options));
    }

    /// <inheritdoc />
    public ChatRecord? LoadRecord(string id)
    {
        try
        {
            var path = RecordPath(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<ChatRecord>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _log.LogWarning("Conversation {Id} illisible : {Message}", id, ex.Message);
            return null;
        }
    }

    /// <inheritdoc />
    public void SaveRecord(string id, ChatRecord record)
    {
        Directory.CreateDirectory(Dir);
        WriteAtomic(RecordPath(id), JsonSerializer.Serialize(record, Options));
    }

    /// <inheritdoc />
    public void DeleteRecord(string id)
    {
        try { File.Delete(RecordPath(id)); }
        catch (IOException) { /* fichier déjà absent ou verrouillé : la conversation n'est plus dans l'index */ }
    }

    /// <summary> Écrit dans un fichier temporaire, puis le remplace : pas de fichier à moitié écrit. </summary>
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
