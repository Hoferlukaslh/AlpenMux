#!/usr/bin/env bash
# Construit les paquets d'installation d'AlpenMux dans ./distribution :
#
#   ./build.sh             les trois paquets
#   ./build.sh deb         Debian / Ubuntu      alpenmux_<version>_amd64.deb        (outils : dotnet, ar, tar, xz)
#   ./build.sh arch        Arch / EndeavourOS   alpenmux-<version>-1-x86_64.pkg.tar.zst  (outils : dotnet, tar, zstd)
#   ./build.sh windows     Windows 10 / 11      AlpenMux_<version>_Setup.exe        (outils : dotnet, makensis)
#
# Installation : sudo apt install ./distribution/alpenmux_<version>_amd64.deb
#                sudo pacman -U ./distribution/alpenmux-<version>-1-x86_64.pkg.tar.zst
# Windows : installateur NSIS (packaging/windows/installer.nsi), à lancer sur le PC Windows ; vLLM y tourne dans WSL2.
#
# La version est la <Version> de AlpenMux.csproj. L'application est publiée « self-contained » (pas besoin de .NET
# sur la machine cible), avec ReadyToRun (démarrage plus rapide) ; si cette publication échoue, le script réessaie
# sous une forme plus simple. Variables : READY_TO_RUN=0 (pas de ReadyToRun), MAINTAINER="Nom <adresse>".
set -euo pipefail

cd "$(dirname "$0")"
ROOT="$PWD"
APP_NAME="alpenmux"                      # paquet, commande, exécutable et icône
DISPLAY_NAME="AlpenMux"
PROJECT="AlpenMux.csproj"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$PROJECT" | head -n 1)"
[ -n "$VERSION" ] || { echo "Version introuvable dans $PROJECT" >&2; exit 1; }

DIST="$ROOT/distribution"
WORK="$ROOT/build"
PUB="$WORK/publish/linux-x64"
MAINTAINER="${MAINTAINER:-Lukas Hofer <lukas@localhost>}"
PKG_DESCRIPTION="Installer vLLM et lancer des serveurs de modeles d'IA derriere une seule adresse"

need() {
  command -v "$1" >/dev/null 2>&1 || { echo "  outil manquant : $1 ($2)" >&2; return 1; }
}

# ---------------------------------------------------------------- Publication .NET (une seule fois pour les deux paquets)
# Pas de Native AOT : les traductions utilisent des liaisons par réflexion (voir AlpenMux.csproj).
# Pas de fichier unique : les paquets installent le dossier publié.
# Chaque cible tourne dans un sous-shell : un fichier témoin (propre à cette exécution, $$ = PID du script)
# évite de publier deux fois.
MARK="$WORK/.published-$$"
publish() {
  [ -f "$MARK" ] && [ -d "$PUB" ] && return 0
  local common=("$PROJECT" -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false
                -p:DebugType=None -p:DebugSymbols=false -o "$PUB")
  rm -rf "${PUB:?}"
  if [ "${READY_TO_RUN:-1}" = "1" ]; then
    if dotnet publish "${common[@]}" -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=true; then
      touch "$MARK"; return 0
    fi
    echo "  ReadyToRun « composite » a échoué : nouvel essai en ReadyToRun simple" >&2
    rm -rf "${PUB:?}"
    if dotnet publish "${common[@]}" -p:PublishReadyToRun=true -p:PublishReadyToRunComposite=false; then
      touch "$MARK"; return 0
    fi
    echo "  ReadyToRun a échoué : nouvel essai sans (démarrage plus lent)" >&2
    rm -rf "${PUB:?}"
  fi
  dotnet publish "${common[@]}" -p:PublishReadyToRun=false
  touch "$MARK"
}

# ---------------------------------------------------------------- Arborescence commune aux deux paquets
# /usr/lib/alpenmux (application), /usr/bin/alpenmux (lanceur), entrée de menu, icônes (toutes tailles + SVG).
stage_root() { # <dossier racine>
  local root="$1"
  rm -rf "${root:?}"
  mkdir -p "$root/usr/lib" "$root/usr/bin" "$root/usr/share/applications" "$root/usr/share/icons"
  cp -a "$PUB" "$root/usr/lib/$APP_NAME"
  # Dossiers 755, fichiers 644, programmes 755 (quel que soit l'umask de la machine)
  find "$root" -type d -exec chmod 755 {} +
  find "$root" -type f ! -perm /111 -exec chmod 644 {} +
  find "$root" -type f -perm /111 -exec chmod 755 {} +

  cat > "$root/usr/bin/$APP_NAME" <<LAUNCHER
#!/bin/sh
# .NET a besoin d'ICU pour les formats (dates, nombres) ; sans ICU, culture invariante plutôt qu'un plantage.
if ! ldconfig -p 2>/dev/null | grep -q 'libicuuc'; then
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi
exec /usr/lib/$APP_NAME/$APP_NAME "\$@"
LAUNCHER
  chmod 755 "$root/usr/bin/$APP_NAME"

  install -m 644 "packaging/linux/$APP_NAME.desktop" "$root/usr/share/applications/$APP_NAME.desktop"
  cp -r packaging/linux/icons/hicolor "$root/usr/share/icons/"
  find "$root/usr/share/icons" -type d -exec chmod 755 {} +
  find "$root/usr/share/icons" -type f -exec chmod 644 {} +
}

# ---------------------------------------------------------------- Debian / Ubuntu (.deb)
build_deb() {
  need dotnet "SDK .NET 10" && need ar "paquet binutils" && need tar "paquet tar" && need xz "paquet xz" || return 1

  local dir="$WORK/deb" root="$WORK/deb/root" size file="$DIST/${APP_NAME}_${VERSION}_amd64.deb"
  publish
  rm -rf "${dir:?}"
  mkdir -p "$dir/control"
  stage_root "$root"

  size="$(du -sk "$root" | cut -f1)"
  # Recommends : ce que la page Installation de l'application installe sinon elle-même (pkexec pour la fenêtre de
  # mot de passe, git / gcc / curl). Suggests : lecteurs audio pour les modèles de synthèse vocale.
  cat > "$dir/control/control" <<CONTROL
Package: $APP_NAME
Version: $VERSION
Architecture: amd64
Maintainer: $MAINTAINER
Installed-Size: $size
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libx11-6, libice6, libsm6, libssl3 | libssl3t64
Recommends: libicu76 | libicu74 | libicu72 | libicu70, pkexec | policykit-1, git, gcc, curl
Suggests: pipewire-bin | pulseaudio-utils | ffmpeg | mpv
Section: devel
Priority: optional
Homepage: https://docs.vllm.ai
Description: $PKG_DESCRIPTION
 Installe vLLM dans un environnement Python dedie (uv), telecharge des modeles
 depuis Hugging Face, calcule automatiquement les parametres selon la VRAM,
 lance et surveille des serveurs compatibles OpenAI (vLLM, vLLM-Omni pour la
 synthese vocale et les images, llama.cpp pour les fichiers GGUF), tous
 joignables par une seule adresse API (routeur integre, mise en veille).
CONTROL
  (cd "$root" && find . -type f -printf '%P\0' | sort -z | xargs -0 md5sum) > "$dir/control/md5sums"

  printf '2.0\n' > "$dir/debian-binary"
  tar --owner=0 --group=0 --numeric-owner -C "$dir/control" -cJf "$dir/control.tar.xz" .
  tar --owner=0 --group=0 --numeric-owner -C "$root" -cJf "$dir/data.tar.xz" .
  rm -f "${file:?}"
  # L'ordre des membres compte : debian-binary, control, data.
  (cd "$dir" && ar rcD "$file" debian-binary control.tar.xz data.tar.xz)
  echo "  -> distribution/$(basename "$file")"
}

# ---------------------------------------------------------------- Arch / EndeavourOS (.pkg.tar.zst)
build_arch() {
  need dotnet "SDK .NET 10" && need tar "paquet tar" && need zstd "paquet zstd" || return 1

  local dir="$WORK/arch" root="$WORK/arch/root" size file="$DIST/${APP_NAME}-${VERSION}-1-x86_64.pkg.tar.zst"
  publish
  rm -rf "${dir:?}"
  mkdir -p "$dir"
  stage_root "$root"

  size="$(du -sb "$root" | cut -f1)"
  cat > "$root/.PKGINFO" <<PKGINFO
pkgname = $APP_NAME
pkgbase = $APP_NAME
pkgver = $VERSION-1
pkgdesc = $PKG_DESCRIPTION
url = https://docs.vllm.ai
builddate = $(date +%s)
packager = $MAINTAINER
size = $size
arch = x86_64
license = custom
depend = glibc
depend = gcc-libs
depend = zlib
depend = icu
depend = openssl
depend = fontconfig
depend = libx11
depend = libice
depend = libsm
optdepend = polkit: fenetre de mot de passe pour installer les paquets
optdepend = uv: environnement Python de vLLM
optdepend = git: depots de modeles
optdepend = gcc: compilation des noyaux Triton
optdepend = python-huggingface-hub: telechargement des modeles (hf)
optdepend = nvidia-utils: GPU NVIDIA (nvidia-smi)
optdepend = pipewire: lecture des audios de synthese vocale (pw-play)
PKGINFO
  chmod 644 "$root/.PKGINFO"

  # .MTREE (empreintes des fichiers, comme makepkg) quand bsdtar est disponible
  local meta=(.PKGINFO)
  if command -v bsdtar >/dev/null 2>&1; then
    (cd "$root" && LANG=C bsdtar -czf .MTREE --format=mtree \
      --options='!all,use-set,type,uid,gid,mode,time,size,md5,sha256,link' .PKGINFO usr)
    chmod 644 "$root/.MTREE"
    meta+=(.MTREE)
  fi

  rm -f "${file:?}"
  # .PKGINFO en premier : pacman lit les informations du paquet au début de l'archive.
  tar --owner=0 --group=0 --numeric-owner -C "$root" -cf - "${meta[@]}" usr \
    | zstd -19 -T0 -q -o "$file"
  echo "  -> distribution/$(basename "$file")"
}

# ---------------------------------------------------------------- Windows (installateur NSIS)
# Compilation croisée depuis Linux (win-x64, ReadyToRun si possible), puis makensis.
build_windows() {
  need dotnet "SDK .NET 10" && need makensis "paquet nsis" || return 1

  local pub="$WORK/publish/win-x64" file="$DIST/${DISPLAY_NAME}_${VERSION}_Setup.exe"
  local common=("$PROJECT" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
                -p:DebugType=None -p:DebugSymbols=false -o "$pub")
  rm -rf "${pub:?}"
  if [ "${READY_TO_RUN:-1}" = "1" ] && dotnet publish "${common[@]}" -p:PublishReadyToRun=true; then
    :
  else
    [ "${READY_TO_RUN:-1}" = "1" ] && echo "  ReadyToRun a échoué : nouvel essai sans (démarrage plus lent)" >&2
    rm -rf "${pub:?}"
    dotnet publish "${common[@]}" -p:PublishReadyToRun=false
  fi
  find "$pub" -name '*.pdb' -delete

  rm -f "${file:?}"
  makensis -V2 -DVERSION="$VERSION" -DSRCDIR="$pub" -DOUTFILE="$file" packaging/windows/installer.nsi
  echo "  -> distribution/$(basename "$file")"
}

# ---------------------------------------------------------------- Principal
targets=("$@")
[ ${#targets[@]} -gt 0 ] || targets=(deb arch windows)

mkdir -p "$DIST" "$WORK"
rm -f "$WORK"/.published-*
trap 'rm -f "$MARK"' EXIT
failed=()
for t in "${targets[@]}"; do
  case "$t" in
    deb|arch|windows) ;;
    *) echo "Cible inconnue : $t (deb | arch | windows)" >&2; exit 2 ;;
  esac
  echo "==> $t ($DISPLAY_NAME $VERSION)"
  # Sous-shell avec son propre « set -e » : la première commande en échec arrête cette cible, la suivante est quand même construite.
  set +e
  ( set -e; "build_$t" )
  rc=$?
  set -e
  [ "$rc" -eq 0 ] || { failed+=("$t"); echo "!! $t a échoué" >&2; }
done

echo
echo "Fichiers dans $DIST :"
ls -1 "$DIST"
if [ ${#failed[@]} -gt 0 ]; then
  echo "Échecs : ${failed[*]}" >&2
  exit 1
fi
