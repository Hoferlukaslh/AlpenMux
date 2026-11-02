#!/usr/bin/env python3
"""
Images de l'installateur NSIS, à partir du logo (docs/logo) :
    welcome.bmp  164 x 314   pages d'accueil et de fin (dégradé de l'icône, symbole, nom)
    header.bmp   150 x 57    bandeau des autres pages (logo horizontal sur fond blanc)
À relancer si le logo change :  python3 packaging/windows/make_images.py   (outils : rsvg-convert, Pillow)
"""
import subprocess, tempfile, os
from PIL import Image, ImageDraw, ImageFont

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.dirname(os.path.abspath(__file__))
FONT = os.path.join(ROOT, "Assets", "Fonts", "Helvetica-Bold.ttf")

# Symbole seul (version pour fond sombre), tiré de l'icône : traits sans la tuile
MARK = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="65 81 382 346"><g fill="none" stroke-linecap="round" stroke-linejoin="round">
<path d="M172 396 L256 276 M256 396 L256 276 M340 396 L256 276" stroke="#7CC4E8" stroke-width="22"/>
<path d="M96 396 L256 112 L416 396" stroke="#F4F8FB" stroke-width="30"/>
<path d="M256 276 L256 196" stroke="#FF8A3D" stroke-width="22"/></g><circle cx="256" cy="196" r="20" fill="#FF8A3D"/></svg>"""

def render(svg, width):
    with tempfile.TemporaryDirectory() as d:
        src, dst = os.path.join(d, "a.svg"), os.path.join(d, "a.png")
        with open(src, "w") as f: f.write(svg)
        subprocess.run(["rsvg-convert", "-w", str(width), "-o", dst, src], check=True)
        return Image.open(dst).convert("RGBA")

def gradient(w, h, top=(0x14, 0x40, 0x5F), bottom=(0x09, 0x18, 0x26)):
    img = Image.new("RGB", (w, h))
    px = img.load()
    for y in range(h):
        t = y / (h - 1)
        c = tuple(round(top[i] + (bottom[i] - top[i]) * t) for i in range(3))
        for x in range(w): px[x, y] = c
    return img

# --- Page d'accueil / de fin
w, h = 164, 314
img = gradient(w, h)
mark = render(MARK, 112)
img.paste(mark, ((w - mark.width) // 2, 70), mark)
d = ImageDraw.Draw(img)
font = ImageFont.truetype(FONT, 25)
a, m = "Alpen", "mux"
wa, wm = d.textlength(a, font=font), d.textlength(m, font=font)
x = (w - wa - wm) / 2
y = 70 + mark.height + 22
d.text((x, y), a, font=font, fill="#F4F8FB")
d.text((x + wa, y), m, font=font, fill="#7CC4E8")
small = ImageFont.truetype(FONT, 10)
for i, line in enumerate(["vLLM · llama.cpp", "WSL2"]):
    lw = d.textlength(line, font=small)
    d.text(((w - lw) / 2, y + 40 + i * 15), line, font=small, fill="#7CC4E8")
img.save(os.path.join(OUT, "welcome.bmp"))

# --- Bandeau
hw, hh = 150, 57
head = Image.new("RGB", (hw, hh), "white")
with open(os.path.join(ROOT, "docs", "logo", "alpenmux-horizontal.svg")) as f: logo = render(f.read(), 136)
head.paste(logo, ((hw - logo.width) // 2, (hh - logo.height) // 2), logo)
head.save(os.path.join(OUT, "header.bmp"))
print("welcome.bmp, header.bmp")
