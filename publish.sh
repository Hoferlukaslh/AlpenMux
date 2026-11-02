#!/usr/bin/env bash
# Publie AlpenMux en exécutable autonome (pas besoin de .NET sur la machine cible)
# et l'installe pour l'utilisateur courant (menu des applications).
#
#   ./publish.sh            -> publie dans ./publish
#   ./publish.sh --install  -> publie puis installe dans ~/.local/share/alpenmux/app
set -euo pipefail
cd "$(dirname "$0")"

OUT=publish
dotnet publish AlpenMux.csproj -c Release -r linux-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$OUT"
echo "Exécutable : $OUT/alpenmux"

if [[ "${1:-}" == "--install" ]]; then
    # ~/.local/share/alpenmux contient aussi l'historique des serveurs : l'exécutable va dans un sous-dossier
    DEST="$HOME/.local/share/alpenmux/app"
    mkdir -p "$DEST" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons"
    cp "$OUT/alpenmux" "$DEST/"
    cp -r packaging/linux/icons/hicolor "$HOME/.local/share/icons/"
    ln -sf "$DEST/alpenmux" "$HOME/.local/bin/alpenmux"
    sed "s#^Exec=alpenmux#Exec=$DEST/alpenmux#" packaging/linux/alpenmux.desktop > "$HOME/.local/share/applications/alpenmux.desktop"
    echo "Installé : menu des applications > AlpenMux (ou commande alpenmux)"
fi
