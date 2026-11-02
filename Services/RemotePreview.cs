/*
    Fichier      :  RemotePreview.cs
    Projet       :  AlpenMux

    Description  :
        Aperçu d'un dépôt Hugging Face avant téléchargement : le config.json distant donne l'architecture
        (cache KV, contexte, vocabulaire) et la compatibilité vLLM ; la taille des fichiers choisis donne les poids.
        Les GGUF (llama.cpp) et les dépôts sans config.json restent sur l'estimation par taille (DownloadFit).
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AlpenMux.Services;

/// <summary> config.json d'un dépôt et sa compatibilité, calculés une fois par dépôt et révision. </summary>
/// <param name="Config"> config.json distant. </param>
/// <param name="Compat"> Problème et avertissement de compatibilité vLLM (d'après tous les fichiers du dépôt). </param>
public sealed record RemoteConfig(JsonElement Config, CompatResult Compat);

/// <summary> Construction de l'aperçu d'un dépôt distant (fonctions pures sauf la vérification de compatibilité). </summary>
public static class RemotePreview
{
    /// <summary> Vrai si le dépôt peut être évalué à partir de son config.json (pas de GGUF, config présente). </summary>
    /// <param name="files"> Fichiers du dépôt. </param>
    public static bool CanPreview(IReadOnlyList<HfFile> files) =>
        files.Any(f => f.Path == "config.json") &&
        !files.Any(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase));

    /// <summary> Lit la compatibilité du dépôt à partir de son config.json et de la liste de ses fichiers. </summary>
    /// <param name="compat"> Vérificateur de compatibilité vLLM. </param>
    /// <param name="cfg"> config.json distant. </param>
    /// <param name="files"> Tous les fichiers du dépôt. </param>
    public static RemoteConfig Prepare(IModelCompat compat, JsonElement cfg, IReadOnlyList<HfFile> files)
    {
        ArgumentNullException.ThrowIfNull(compat);
        var paths = files.Select(f => f.Path).ToList();
        var archs = ModelScanner.ArchitecturesOf(cfg);
        var hasTokenizer = paths.Any(p => p is "tokenizer.json" or "tokenizer.model" or "tokenizer_config.json");
        var result = compat.CheckRemote(paths, cfg, archs, ModelScanner.QuantMethodOf(cfg), hasTokenizer);
        return new RemoteConfig(cfg, result);
    }

    /// <summary> Modèle d'aperçu : poids = taille des fichiers choisis, le reste vient du config.json. </summary>
    /// <param name="repo"> Dépôt (identifiant Hugging Face). </param>
    /// <param name="rc"> config.json et compatibilité du dépôt. </param>
    /// <param name="weightBytes"> Taille des fichiers qui seront téléchargés (octets). </param>
    public static ModelInfo Model(string repo, RemoteConfig rc, long weightBytes)
    {
        var cfg = rc.Config;
        var text = ModelScanner.TextConfig(cfg);
        return new ModelInfo
        {
            Repo = repo,
            Target = repo,
            Root = "",
            Layout = "folder",
            Name = repo.Split('/')[^1],
            WeightsBytes = weightBytes,
            Kv = ModelScanner.KvFromConfig(cfg),
            VocabSize = ModelScanner.FindInt(text, "vocab_size"),
            MaxPositions = ModelScanner.FindInt(text, "max_position_embeddings"),
            AttentionHeads = ModelScanner.FindInt(text, "num_attention_heads"),
            Problem = rc.Compat.Problem,
            Warning = rc.Compat.Warning,
        };
    }
}
