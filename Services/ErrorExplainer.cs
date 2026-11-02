/*
    Fichier      :  ErrorExplainer.cs
    Projet       :  AlpenMux

    Description  :
        Traduit les erreurs de vLLM (traces Python) en explication claire + action proposée.
        On cherche dans les dernières lignes du journal, de la plus spécifique à la plus générale.
*/

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AlpenMux.Services;

/// <summary> Action proposée pour corriger une erreur de démarrage. </summary>
public enum FixAction
{
    /// <summary> Aucune action. </summary>
    None,
    /// <summary> Ouvrir la page Installation. </summary>
    Setup,
    /// <summary> Modifier la configuration. </summary>
    EditConfig,
    /// <summary> Saisir le jeton Hugging Face. </summary>
    Token,
    /// <summary> Ouvrir la page Modèles. </summary>
    Models
}

/// <summary> Erreur de démarrage expliquée à l'utilisateur. </summary>
/// <param name="Title"> Ce qui s'est passé. </param>
/// <param name="Hint"> Comment corriger. </param>
/// <param name="Action"> Bouton proposé. </param>
public sealed record ExplainedError(string Title, string Hint, FixAction Action);

/// <summary> Reconnaît les erreurs courantes de vLLM et llama.cpp dans le journal et les explique. </summary>
public static class ErrorExplainer
{
    private static readonly (Regex Pattern, string Key, FixAction Action)[] Rules =
    [
        // Les plus précises d'abord : leurs traces contiennent souvent aussi ImportError / KeyError
        (new(@"unrecognized arguments|ambiguous option|invalid choice: '", RegexOptions.IgnoreCase), "UnknownArg", FixAction.EditConfig),
        (new(@"could not locate think (start|end)|think start/end tokens|reasoning parser.*(not found|not registered|invalid)|invalid reasoning parser|Unknown reasoning parser", RegexOptions.IgnoreCase), "ReasoningParser", FixAction.EditConfig),
        (new(@"tool call parser.*(not found|not registered|invalid)|invalid tool.?call.?parser|enable-auto-tool-choice.*requires|tool_call_parser.*must be", RegexOptions.IgnoreCase), "ToolParser", FixAction.EditConfig),
        (new(@"Quantization method specified in the model config .* does not match|does not match the quantization method specified", RegexOptions.IgnoreCase), "QuantMismatch", FixAction.EditConfig),
        (new(@"RepositoryNotFoundError|Repository Not Found for url|is not a local folder and is not a valid model identifier", RegexOptions.IgnoreCase), "RepoNotFound", FixAction.Models),
        (new(@"lm_head\.input_scale|KeyError: '[^']*\.(input_scale|weight_scale_2|k_scale)'", RegexOptions.IgnoreCase), "InputScale", FixAction.Setup),
        (new(@"LoRA rank \d+ is greater than max_lora_rank|max_lora_rank.*(must be|should be|exceeds|is greater)", RegexOptions.IgnoreCase), "LoraRank", FixAction.EditConfig),
        (new(@"does not support LoRA|LoRA is not supported|not support lora", RegexOptions.IgnoreCase), "Lora", FixAction.EditConfig),
        (new(@"Sleep mode is not supported|sleep mode.*(not supported|requires)", RegexOptions.IgnoreCase), "Sleep", FixAction.EditConfig),
        (new(@"(Error|Exception).*speculative.*(not supported|unsupported)|Unsupported speculative|num_speculative_tokens.*(must be|should be|exceeds)", RegexOptions.IgnoreCase), "Spec", FixAction.EditConfig),
        (new(@"compiled with different CUDA versions|undefined symbol|libcudart\.so|libcu\w+\.so.*cannot open|ImportError|ModuleNotFoundError|No module named", RegexOptions.IgnoreCase), "Env", FixAction.Setup),
        (new(@"CUDA driver version is insufficient|NVIDIA driver on your system is too old|no CUDA-capable device|Found no NVIDIA driver", RegexOptions.IgnoreCase), "Driver", FixAction.Setup),
        (new(@"no kernel image is available|not compatible with the current PyTorch installation|sm_\d+ is not compatible", RegexOptions.IgnoreCase), "GpuArch", FixAction.Setup),
        (new(@"Bfloat16 is only supported on GPUs with compute capability", RegexOptions.IgnoreCase), "Bf16", FixAction.EditConfig),
        (new(@"greater than the derived max_model_len|max_model_len.*(larger|greater) than|exceeds the maximum.*max_model_len", RegexOptions.IgnoreCase), "Context", FixAction.EditConfig),
        (new(@"(KV cache|cache blocks).*(not enough|insufficient|No available memory)|No available memory for the cache blocks|larger than the available KV cache memory|To serve at least one request", RegexOptions.IgnoreCase), "KvMemory", FixAction.EditConfig),
        (new(@"CUDA out of memory|OutOfMemoryError|Free memory on device .* is less than desired", RegexOptions.IgnoreCase), "Oom", FixAction.EditConfig),
        (new(@"Address already in use|error while attempting to bind", RegexOptions.IgnoreCase), "Port", FixAction.EditConfig),
        (new(@"401 Client Error|403 Client Error|gated repo|Access to model .* is restricted|Cannot access gated repo", RegexOptions.IgnoreCase), "Gated", FixAction.Token),
        // Message d'erreur seulement : la ligne de configuration de vLLM contient aussi « trust_remote_code=False »
        (new(@"requires you to execute|pass the argument .?trust_remote_code|set .?trust_remote_code=True", RegexOptions.IgnoreCase), "RemoteCode", FixAction.EditConfig),
        (new(@"Model architectures? \[.*\] (are|is) not supported|are not supported for now|Cannot find model module", RegexOptions.IgnoreCase), "Arch", FixAction.Models),
        (new(@"Unknown quantization method|quantization method .* is not supported|Unsupported quantization", RegexOptions.IgnoreCase), "Quant", FixAction.Models),
        (new(@"Config' object has no attribute|Config object has no attribute", RegexOptions.IgnoreCase), "ConfigAttr", FixAction.Setup),
        (new(@"is not a valid JSON file", RegexOptions.IgnoreCase), "Format", FixAction.Models),
        // llama.cpp
        (new(@"unknown model architecture|unsupported model architecture", RegexOptions.IgnoreCase), "LlamaArch", FixAction.Setup),
        (new(@"cudaMalloc failed|failed to allocate .* buffer|unable to allocate .*CUDA", RegexOptions.IgnoreCase), "LlamaMemory", FixAction.EditConfig),
        (new(@"tensor_parallel_size|is not divisible by tensor parallel|world size .* greater than", RegexOptions.IgnoreCase), "Tp", FixAction.EditConfig),
        (new(@"No such file or directory|does not appear to have a file named|Can't load tokenizer", RegexOptions.IgnoreCase), "Files", FixAction.Models),
    ];

    /// <summary> Explication de l'échec à partir des dernières lignes du journal (null si rien de reconnu). </summary>
    public static ExplainedError? Explain(IEnumerable<string> lines)
    {
        // Les lignes d'erreur sont en fin de journal : on cherche d'abord dans les 60 dernières
        var tail = lines.TakeLast(400).ToList();
        foreach (var window in new[] { tail.TakeLast(60).ToList(), tail })
            foreach (var (pattern, key, action) in Rules)
                if (window.Any(l => pattern.IsMatch(l)))
                    return new ExplainedError(Loc.T($"Err_{key}"), Loc.T($"Err_{key}_Hint"), action);
        return null;
    }
}
