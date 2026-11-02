"""
Fichier      :  convert_single_file.py
Projet       :  AlpenMux

Description  :
    Convertit un modèle d'image en fichier unique (format ComfyUI / Black Forest Labs / Automatic1111)
    au format diffusers, le seul que vLLM-Omni sait charger. Lancé par AlpenMux avec le Python du venv.

        python convert_single_file.py <fichier.safetensors> <dossier de sortie>

    - FLUX.2 (dev, klein 4B / 9B), FLUX.1 (dev, schnell), Z-Image-Turbo : le fichier ne contient que
      le transformer. Il est converti (noms diffusers) et les autres composants (encodeur de texte, VAE,
      tokenizer, configurations) viennent du dépôt officiel.
    - SDXL : le fichier contient tout le pipeline, converti par diffusers.
    Les poids quantifiés (FP8, NVFP4, « scaled fp8 » de ComfyUI) sont décompressés en bf16 :
    vLLM-Omni peut ensuite les requantifier en FP8 au chargement.

    Sortie pour AlpenMux : lignes « PROGRESS <pourcentage> <texte> », « BYTES <reçus> <total> <texte> »
    pendant le téléchargement des composants, « ERROR <code> <détail> », et « ALPENMUX_CONVERT_OK <dépôt de base> » à la fin.
"""

import json
import os
import sys
import threading
import time

import torch
from safetensors import safe_open
from safetensors.torch import save_file

E2M1 = torch.tensor([0, 0.5, 1, 1.5, 2, 3, 4, 6, -0.0, -0.5, -1, -1.5, -2, -3, -4, -6])


def progress(pct, text):
    print(f"PROGRESS {pct:.0f} {text}", flush=True)


def fail(code, detail=""):
    print(f"ERROR {code} {detail}", flush=True)
    sys.exit(2)


def unswizzle(scale, rows, cols):
    """Échelles NVFP4 rangées en tuiles 128x4 (disposition cuBLAS) -> matrice lignes x blocs."""
    r_pad, c_pad = (rows + 127) // 128 * 128, (cols + 3) // 4 * 4
    x = scale.reshape(r_pad // 128, c_pad // 4, 32, 4, 4).permute(0, 3, 2, 1, 4).reshape(r_pad, c_pad)
    return x[:rows, :cols]


def dequantize(f, keys):
    """Lit le fichier tenseur par tenseur, décompresse les couches quantifiées en bf16."""
    keys = set(keys)
    scaled_fp8 = "scaled_fp8" in keys
    skip = {k for k in keys if k.endswith((".input_scale", ".weight_scale", ".weight_scale_2", ".scale_weight", ".scale_input"))}
    skip.add("scaled_fp8")
    out = {}
    todo = sorted(keys - skip)
    for i, k in enumerate(todo):
        if i % 50 == 0:
            progress(5 + 40 * i / len(todo), "Lecture des poids")
        t = f.get_tensor(k)
        base = k[: -len(".weight")] if k.endswith(".weight") else None
        if base and t.dtype == torch.uint8 and base + ".weight_scale_2" in keys:
            # NVFP4 : deux valeurs E2M1 par octet (poids fort en premier), une échelle FP8 par bloc de 16, échelle globale
            rows, half = t.shape
            vals = E2M1[torch.stack([(t >> 4).long(), (t & 0x0F).long()], -1).reshape(rows, half * 2)]
            sc = unswizzle(f.get_tensor(base + ".weight_scale").float(), rows, half * 2 // 16)
            g = f.get_tensor(base + ".weight_scale_2").float()
            t = (vals.reshape(rows, -1, 16) * sc.unsqueeze(-1) * g).reshape(rows, -1)
        elif base and t.dtype in (torch.float8_e4m3fn, torch.float8_e5m2):
            t = t.float()
            if base + ".weight_scale" in keys:
                t = t * f.get_tensor(base + ".weight_scale").float()
            elif scaled_fp8 and base + ".scale_weight" in keys:
                t = t * f.get_tensor(base + ".scale_weight").float()
        if t.is_floating_point():
            t = t.to(torch.bfloat16)
        out[k] = t
    return out


def strip_prefix(sd):
    for p in ("model.diffusion_model.", "diffusion_model."):
        if any(k.startswith(p) for k in sd):
            return {k[len(p):] if k.startswith(p) else k: v for k, v in sd.items()}
    return sd


def download_components(repo, out, keep_transformer_config=True):
    """Composants du dépôt officiel, sans ses poids de transformer ni ses fichiers uniques à la racine."""
    from huggingface_hub import HfApi, snapshot_download

    token = os.environ.get("HF_TOKEN") or None
    try:
        info = HfApi().model_info(repo, token=token, files_metadata=True)
        sizes = {s.rfilename: s.size or 0 for s in info.siblings}
        files = list(sizes)
    except Exception as e:
        fail("BASE_UNAVAILABLE", f"{repo} : {e}")
    useless = (".onnx", ".onnx_data", ".msgpack", ".h5", ".ckpt", ".gguf", ".xml")
    # Poids PyTorch (.bin / .pt) seulement dans les dossiers sans safetensors
    with_safetensors = {os.path.dirname(p) for p in files if p.endswith(".safetensors")}
    allow = []
    for p in files:
        if "/" not in p:
            if p.endswith((".json", ".txt", ".md")):
                allow.append(p)
            continue
        if p.startswith("transformer/"):
            if keep_transformer_config and p == "transformer/config.json":
                allow.append(p)
            continue
        if p.endswith(useless) or ".fp16." in p or "openvino" in p or p.startswith(("onnx/", "vae_decoder/", "vae_encoder/")):
            continue
        if p.endswith((".bin", ".pt", ".pth")) and os.path.dirname(p) in with_safetensors:
            continue
        allow.append(p)
    text = f"Téléchargement des autres composants ({repo})"
    progress(55, text)
    total = sum(sizes.get(p, 0) for p in allow)

    def on_disk():
        # Fichiers terminés + fichiers partiels de hf (.cache/huggingface/download/**/*.incomplete)
        n = 0
        for p in allow:
            try:
                n += os.path.getsize(os.path.join(out, p))
            except OSError:
                pass
        for root, _, names in os.walk(os.path.join(out, ".cache", "huggingface", "download")):
            n += sum(os.path.getsize(os.path.join(root, x)) for x in names if x.endswith(".incomplete"))
        return min(n, total)

    done = threading.Event()

    def report():
        while not done.wait(1):
            print(f"BYTES {on_disk()} {total} {text}", flush=True)

    threading.Thread(target=report, daemon=True).start()
    try:
        snapshot_download(repo, local_dir=out, allow_patterns=allow, token=token)
    finally:
        done.set()
    print(f"BYTES {total} {total} {text}", flush=True)


def save_transformer(sd, out):
    d = os.path.join(out, "transformer")
    os.makedirs(d, exist_ok=True)
    progress(48, "Écriture du transformer")
    save_file({k: v.contiguous() for k, v in sd.items()}, os.path.join(d, "diffusion_pytorch_model.safetensors"))


def main():
    if len(sys.argv) < 3:
        fail("USAGE", "convert_single_file.py <fichier> <dossier>")
    path, out = sys.argv[1], sys.argv[2]
    os.makedirs(out, exist_ok=True)

    from diffusers.loaders.single_file_utils import infer_diffusers_model_type

    with safe_open(path, framework="pt") as f:
        keys = list(f.keys())
        # Détection sur des tenseurs vides (formes seulement), sans charger les poids
        meta = {}
        for k in keys:
            sl = f.get_slice(k)
            meta[k] = torch.empty(sl.get_shape(), device="meta")
        kind = infer_diffusers_model_type(meta)
        stripped = strip_prefix(meta)
        progress(2, f"Type détecté : {kind}")

        if "single_stream_modulation.lin.weight" in stripped:
            hidden = stripped["double_blocks.0.img_attn.proj.weight"].shape[0]
            if "guidance_in.in_layer.weight" in stripped:
                base = "black-forest-labs/FLUX.2-dev"
            elif hidden == 3072:
                base = "black-forest-labs/FLUX.2-klein-4B"
            else:
                base = "black-forest-labs/FLUX.2-klein-9B"
            from diffusers.loaders.single_file_utils import convert_flux2_transformer_checkpoint_to_diffusers as convert
        elif "double_blocks.0.img_attn.qkv.weight" in stripped:
            base = "black-forest-labs/FLUX.1-dev" if "guidance_in.in_layer.weight" in stripped else "black-forest-labs/FLUX.1-schnell"
            from diffusers.loaders.single_file_utils import convert_flux_transformer_checkpoint_to_diffusers as convert
        elif kind.startswith("z-image"):
            base = "Tongyi-MAI/Z-Image-Turbo"
            from diffusers.loaders.single_file_utils import convert_z_image_transformer_checkpoint_to_diffusers as convert
        elif kind == "xl_base":
            base = None
        else:
            fail("UNSUPPORTED", kind)

        if base is None:
            # SDXL : le fichier contient tout le pipeline
            from diffusers import StableDiffusionXLPipeline

            progress(10, "Conversion du pipeline SDXL")
            pipe = StableDiffusionXLPipeline.from_single_file(path, torch_dtype=torch.float16)
            progress(70, "Écriture")
            pipe.save_pretrained(out, safe_serialization=True)
            print("ALPENMUX_CONVERT_OK stabilityai/stable-diffusion-xl-base-1.0", flush=True)
            return

        sd = strip_prefix(dequantize(f, keys))

    progress(46, "Conversion des noms de tenseurs")
    sd = convert(sd)
    save_transformer(sd, out)
    del sd
    download_components(base, out)
    progress(100, "Terminé")
    print(f"ALPENMUX_CONVERT_OK {base}", flush=True)


if __name__ == "__main__":
    main()
