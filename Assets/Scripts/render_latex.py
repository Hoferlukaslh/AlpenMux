"""
Fichier      :  render_latex.py
Projet       :  AlpenMux

Description  :
    Dessine des formules LaTeX en SVG avec ziamath : pur Python, sans installation de TeX, sans navigateur
    ni réseau (police STIX Two Math embarquée, tracés en chemins). AlpenMux convertit le SVG en PNG
    (Svg.Skia) pour l'afficher dans le chat. Lancé avec le Python du venv, une fois pour toutes les
    formules d'une réponse.

        python render_latex.py <requête.json>

    Requête : {"color": "#1C1F26", "items": [{"latex": "...", "inline": false, "size": 16, "out": "/chemin/f.svg"}, ...]}
    Sortie  : une ligne JSON par formule, dans l'ordre : {"ok": true} ou {"ok": false, "error": "..."}
"""

import json
import sys


def main():
    with open(sys.argv[1], encoding="utf-8") as f:
        request = json.load(f)
    import ziamath

    for item in request["items"]:
        try:
            ziamath.Latex(item["latex"], size=item["size"], color=request["color"], inline=item["inline"]).save(item["out"])
            result = {"ok": True}
        except Exception as e:  # syntaxe LaTeX non reconnue par ziamath
            result = {"ok": False, "error": (str(e).strip() or type(e).__name__)}
        print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
