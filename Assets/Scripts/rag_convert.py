#!/usr/bin/env python3
"""
Fichier      :  rag_convert.py
Projet       :  AlpenMux

Description  :
    Conversion par lot des documents d'une base de connaissances (RAG) en Markdown, dans le venv de l'application.
    Un seul processus pour tout le lot (markitdown coûte ~1 s de démarrage par fichier).
    - PDF : pdfminer directement, pour garder les sauts de page (\\f) qui donnent les numéros de page des citations
      (markitdown les supprime) ;
    - autres formats : markitdown ;
    - un fichier qui échoue n'arrête pas le lot.
    Entrée  : un fichier JSON {"outdir": "...", "files": ["...", ...]} (chemin passé en argument).
    Sortie  : une ligne JSON par fichier sur stdout : {"path": ..., "out": ...} ou {"path": ..., "error": ...}.
"""

import json
import os
import sys


def convert_pdf(path):
    from pdfminer.high_level import extract_text

    # Chaque page est suivie d'un \f (même la dernière) : un PDF d'une page garde ainsi son numéro de page.
    # Les pages vides sont gardées, sinon les numéros suivants seraient décalés.
    pages = extract_text(path).split("\f")
    return "\f".join(p.strip() for p in pages)


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    with open(sys.argv[1], encoding="utf-8") as f:
        request = json.load(f)
    outdir = request["outdir"]
    os.makedirs(outdir, exist_ok=True)

    md = None  # MarkItDown est créé au premier fichier qui en a besoin
    for index, path in enumerate(request["files"]):
        try:
            if path.lower().endswith(".pdf"):
                text = convert_pdf(path)
            else:
                if md is None:
                    from markitdown import MarkItDown
                    md = MarkItDown()
                text = md.convert(path).text_content
            out = os.path.join(outdir, f"{index}.md")
            with open(out, "w", encoding="utf-8") as f:
                f.write(text or "")
            result = {"path": path, "out": out}
        except Exception as ex:  # un fichier en erreur ne bloque pas le lot
            result = {"path": path, "error": f"{type(ex).__name__}: {ex}"}
        print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
