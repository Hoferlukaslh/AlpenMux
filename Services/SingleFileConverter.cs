/*
    Fichier      :  SingleFileConverter.cs
    Projet       :  AlpenMux

    Description  :
        Modèles d'image en fichier unique (format ComfyUI / Black Forest Labs / Automatic1111) :
        vLLM-Omni ne charge que le format diffusers. Le script Assets/Scripts/convert_single_file.py,
        lancé avec le Python du venv, convertit le fichier dans son dossier :
            - FLUX.2, FLUX.1, Z-Image : transformer converti, autres composants pris dans le dépôt officiel ;
            - SDXL : pipeline complet converti par diffusers.
        Les poids FP8 / NVFP4 sont décompressés en bf16.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AlpenMux.Services;

/// <summary> Conversion d'un modèle d'image « fichier unique » (.safetensors) au format diffusers. </summary>
public interface ISingleFileConverter
{
    /// <summary> Convertit <paramref name="file"/> au format diffusers dans son dossier. </summary>
    Task<bool> ConvertAsync(string file, ProgressTracker progress, Action<string> log, CancellationToken ct);
}

/// <summary> Implémentation de <see cref="ISingleFileConverter"/>. </summary>
public sealed partial class SingleFileConverter : ISingleFileConverter
{
    private readonly ISettingsService _settings;
    private readonly IPythonEnvironment _python;
    private readonly IHfService _hf;

    /// <summary> Crée le service. </summary>
    /// <param name="settings"> Réglages (chemin du venv). </param>
    /// <param name="python"> Python du venv. </param>
    /// <param name="hf"> Jeton Hugging Face. </param>
    public SingleFileConverter(ISettingsService settings, IPythonEnvironment python, IHfService hf)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(python);
        ArgumentNullException.ThrowIfNull(hf);
        _settings = settings;
        _python = python;
        _hf = hf;
    }

    /// <summary>
    /// Famille reconnue d'après les noms des tenseurs (en-tête seulement) : « FLUX.2 », « FLUX.1 », « Z-Image », « SDXL »,
    /// « SD » (1.x / 2.x, que vLLM-Omni ne sait pas lancer) ou null.
    /// </summary>
    public static string? Detect(string file)
    {
        var keys = ReadKeys(file);
        if (keys == null) return null;
        bool Has(string k) => keys.Contains(k) || keys.Contains("model.diffusion_model." + k);
        if (Has("single_stream_modulation.lin.weight")) return "FLUX.2";
        if (Has("double_blocks.0.img_attn.qkv.weight")) return "FLUX.1";
        if (Has("layers.0.adaLN_modulation.0.weight")) return "Z-Image";
        if (keys.Contains("conditioner.embedders.1.model.transformer.resblocks.9.mlp.c_proj.bias")) return "SDXL";
        if (keys.Any(k => k.StartsWith("model.diffusion_model.input_blocks."))) return "SD";
        return null;
    }

    /// <summary> Ce type de fichier unique peut-il être converti au format diffusers ? </summary>
    public static bool CanConvert(string? kind) => kind is "FLUX.2" or "FLUX.1" or "Z-Image" or "SDXL";

    /// <summary> Fichier complet (encodeurs de texte et VAE inclus) : stable-diffusion.cpp le lance tel quel. </summary>
    public static bool SdCppRunnable(string? kind) => kind is "SDXL" or "SD";

    /// <summary>
    /// Transformer seul (format ComfyUI) lancé avec stable-diffusion.cpp : il faut en plus le VAE et l'encodeur de
    /// texte (Qwen3), donnés à la main ou téléchargés. FLUX.1 n'est pas géré (deux encodeurs : CLIP et T5).
    /// </summary>
    public static bool SdCppNeedsComponents(string? kind) => kind is "FLUX.2" or "Z-Image";

    private static HashSet<string>? ReadKeys(string file)
    {
        try
        {
            using var doc = SafetensorsReader.ReadHeader(file);
            if (doc == null) return null;
            return doc.RootElement.EnumerateObject().Select(p => p.Name).Where(k => k != "__metadata__").ToHashSet();
        }
        catch { return null; }
    }

    [GeneratedRegex(@"^PROGRESS (\d+) (.*)$")]
    private static partial Regex ProgressRegex();

    [GeneratedRegex(@"^BYTES (\d+) (\d+) (.*)$")]
    private static partial Regex BytesRegex();

    /// <inheritdoc />
    public async Task<bool> ConvertAsync(string file, ProgressTracker progress, Action<string> log, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(file)!;
        progress.Start(Loc.F("Conv_Title", Path.GetFileName(file)));
        progress.UnitsAreBytes = true;
        string? baseRepo = null;
        var downloading = false;
        string? error = null;

        var env = new Dictionary<string, string?>
        {
            ["VIRTUAL_ENV"] = _settings.VenvPath,
            ["PYTHONUNBUFFERED"] = "1",
            ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1",
        };
        if (_hf.Token is { } token) env["HF_TOKEN"] = token;

        var args = new List<string> { "-I", LinuxHost.ToLinux(AppPaths.ExtractScript("convert_single_file.py")), LinuxHost.ToLinux(file), LinuxHost.ToLinux(dir) };
        var code = await ProcessRunner.RunAsync(_python.VenvPython, args, (line, isProgress) =>
        {
            if (isProgress) return;
            if (ProgressRegex().Match(line) is { Success: true } m)
            {
                // Étapes de calcul : pourcentage seul, sans vitesse ni temps restant
                var pct = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                progress.SetPercent(pct, m.Groups[2].Value);
                return;
            }
            if (BytesRegex().Match(line) is { Success: true } b)
            {
                // Téléchargement des autres composants : octets reçus, vitesse et temps restant réels
                if (!downloading) { downloading = true; progress.ResetRate(); }
                var got = double.Parse(b.Groups[1].Value, CultureInfo.InvariantCulture);
                var total = double.Parse(b.Groups[2].Value, CultureInfo.InvariantCulture);
                progress.Report(got, total, $"{b.Groups[3].Value} · {Format.Bytes(got)} / {Format.Bytes(total)}");
                return;
            }
            if (line.StartsWith("ALPENMUX_CONVERT_OK")) baseRepo = line["ALPENMUX_CONVERT_OK".Length..].Trim();
            else if (line.StartsWith("ERROR ")) error = line[6..];
            log(line);
        }, env, ct, linux: true);

        if (code == 0 && baseRepo != null)
        {
            HfService.CleanPartials(dir);
            // Origine du modèle : dépôt d'où vient le fichier, et dépôt officiel des autres composants
            try
            {
                var metaPath = Path.Combine(dir, DownloadMetadata.FileName);
                var meta = File.Exists(metaPath)
                    ? JsonSerializer.Deserialize(File.ReadAllText(metaPath), AppJsonContext.Default.DownloadMetadata) ?? new()
                    : new DownloadMetadata { DownloadedAt = DateTime.Now };
                meta.BaseModel = baseRepo;
                File.WriteAllText(metaPath, JsonSerializer.Serialize(meta, AppJsonContext.Default.DownloadMetadata));
            }
            catch { /* métadonnées facultatives */ }
            progress.Finish(Loc.T("Conv_Done"));
            return true;
        }
        progress.Finish(error is { } e ? ErrorText(e) : Loc.F("Common_ExitCode", code));
        if (error != null) log("✘ " + ErrorText(error));
        return false;
    }

    private string ErrorText(string error)
    {
        var code = error.Split(' ', 2)[0];
        var detail = error.Contains(' ') ? error.Split(' ', 2)[1] : "";
        return code switch
        {
            "UNSUPPORTED" => Loc.F("Conv_Unsupported", detail),
            "BASE_UNAVAILABLE" => Loc.F("Conv_BaseUnavailable", detail),
            _ => error,
        };
    }
}
