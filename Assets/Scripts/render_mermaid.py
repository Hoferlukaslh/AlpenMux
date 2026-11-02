"""
Fichier      :  render_mermaid.py
Projet       :  AlpenMux

Description  :
    Dessine des diagrammes Mermaid en PNG avec mermaidx (la vraie bibliothèque Mermaid dans un moteur
    JavaScript embarqué, QuickJS) : sans navigateur ni Node.js, hors ligne. Lancé par AlpenMux avec le
    Python du venv, une fois pour tous les diagrammes d'une réponse.

        python render_mermaid.py <requête.json>

    Requête : {"dark": false | true, "items": [{"code": "...", "out": "/chemin/image.png"}, ...]}
    Thème clair : thème par défaut de Mermaid sur fond blanc. Thème sombre : thème sombre de Mermaid sur le fond des
    bulles de l'app, avec les fonds clairs donnés par le modèle assombris (texte lisible).
    Sortie  : une ligne JSON par diagramme, dans l'ordre : {"ok": true} ou {"ok": false, "error": "..."}
"""

import json
import re
import sys

LIGHT = {"theme": "default", "background": "#ffffff"}
DARK = {"theme": "dark", "background": "#1F2330"}   # fond : SurfaceAlt du thème sombre de l'application

_BG = (0x1F, 0x23, 0x30)
_LINE = re.compile(r"^\s*(?:style|classDef)\s+\S+\s+")
_FILL = re.compile(r"fill:#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})(?![0-9a-fA-F])")


def _rgb(hex_color):
    hex_color = hex_color if len(hex_color) == 6 else "".join(c * 2 for c in hex_color)
    return tuple(int(hex_color[i:i + 2], 16) for i in (0, 2, 4))


def _darken_fill(match):
    """Couleur de fond claire (donnée par le modèle) : assombrie vers le fond, même teinte. Le texte clair du thème sombre reste lisible."""
    r, g, b = _rgb(match.group(1))
    if (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255 < 0.55:
        return match.group(0)
    t = 0.55
    mix = [round(_BG[i] * (1 - t) + c * t) for i, c in enumerate((r, g, b))]
    return "fill:#%02x%02x%02x" % tuple(mix)


def dark_source(code):
    """Dans le thème sombre, les styles de nœuds clairs (pastel) reçoivent un fond plus sombre."""
    return "\n".join(_FILL.sub(_darken_fill, line) if _LINE.match(line) else line for line in code.split("\n"))


def main():
    with open(sys.argv[1], encoding="utf-8") as f:
        request = json.load(f)
    import mermaidx

    style = DARK if request.get("dark") else LIGHT
    theme, background = style["theme"], style["background"]
    prepare = dark_source if request.get("dark") else (lambda code: code)
    for item in request["items"]:
        try:
            diagram = mermaidx.render(prepare(item["code"]), theme=theme)
            diagram.save(item["out"], background=background)
            result = {"ok": True}
        except Exception as e:  # erreur de syntaxe Mermaid ou de rendu
            message = str(e).strip()
            prefix = "Mermaid rendering failed:"
            if message.startswith(prefix):
                message = message[len(prefix):].strip()
            result = {"ok": False, "error": message}
        print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
