/*
    Fichier      :  EmbeddingProfiles.cs
    Projet       :  AlpenMux

    Description  :
        Profils des modèles d'embedding : préfixes à ajouter à la requête et au passage (e5, nomic, Qwen3-Embedding…),
        longueur maximale en tokens. Sans le bon préfixe, la qualité de la recherche baisse sans erreur visible.
        Fonctions pures (aucun état) : le nom du modèle, tel qu'il est servi par le serveur, décide du profil.
*/

using System;

namespace AlpenMux.Services;

/// <summary> Réglages d'un modèle d'embedding pour la recherche : préfixes et longueur maximale. </summary>
/// <param name="Family"> Famille reconnue (ou « inconnue »). </param>
/// <param name="QueryPrefix"> Préfixe de la requête (question de l'utilisateur). </param>
/// <param name="PassagePrefix"> Préfixe de chaque passage indexé. </param>
/// <param name="MaxTokens"> Longueur maximale d'un texte embarqué (passage compris). </param>
/// <param name="Known"> La famille est reconnue : sinon un avertissement est écrit au journal. </param>
public sealed record EmbeddingProfile(string Family, string QueryPrefix, string PassagePrefix, int MaxTokens, bool Known);

/// <summary> Choix du profil d'après le nom du modèle (table statique, comme ArgsPlanner). </summary>
public static class EmbeddingProfiles
{
    /// <summary> Instruction par défaut des modèles qui en attendent une (tâche de recherche documentaire). </summary>
    public const string SearchInstruction = "Given a web search query, retrieve relevant passages that answer the query";

    /// <summary> Profil d'un modèle d'après son nom (insensible à la casse, séparateurs ignorés). </summary>
    /// <param name="modelName"> Nom du modèle servi (ex. « Qwen3-Embedding-0.6B-Q8_0 », « multilingual-e5-large-instruct »). </param>
    public static EmbeddingProfile For(string? modelName)
    {
        var n = (modelName ?? "").ToLowerInvariant();
        // Ordre important : « e5-instruct » avant « e5 », « qwen3-embedding » avant tout le reste
        if (n.Contains("qwen3") && n.Contains("embed"))
            return new("qwen3-embedding", $"Instruct: {SearchInstruction}\nQuery:", "", 32768, true);
        if (n.Contains("e5") && n.Contains("instruct"))
            return new("e5-instruct", $"Instruct: {SearchInstruction}\nQuery: ", "", 512, true);
        if (n.Contains("e5"))
            return new("e5", "query: ", "passage: ", 512, true);
        if (n.Contains("nomic"))
            return new("nomic", "search_query: ", "search_document: ", 8192, true);
        if (n.Contains("bge-m3") || n.Contains("bge_m3"))
            return new("bge-m3", "", "", 8192, true);
        if (n.Contains("bge") && n.Contains("reranker"))
            return new("reranker", "", "", 512, true);
        // Inconnu : pas de préfixe, longueur prudente
        return new("inconnue", "", "", 512, false);
    }

    /// <summary> Le nom suggère un modèle d'embedding (utilisé pour proposer les modèles à lancer). </summary>
    /// <param name="modelName"> Nom du modèle ou du fichier. </param>
    public static bool LooksLikeEmbedding(string? modelName)
    {
        var n = (modelName ?? "").ToLowerInvariant();
        return n.Contains("embed") || n.Contains("bge-m3") || n.Contains("e5-") || n.Contains("multilingual-e5")
            || n.Contains("nomic-embed") || n.Contains("minilm") || n.Contains("gte-");
    }
}
