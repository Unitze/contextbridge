#!/usr/bin/env bash
# Fetches the pinned cross-build toolchain into a per-user cache (no root needed) and prints
# the environment to use it. Used by CI (GitHub-hosted Linux runner); also handy locally.
#
#   eval "$(./scripts/bootstrap-tools.sh)"     # exports LLVM_MINGW, NSISDIR, MAKENSIS, PATH
#   ./scripts/bootstrap-tools.sh --github      # writes them to $GITHUB_ENV / $GITHUB_PATH instead
#
# Tools (x86_64 Linux host):
#   llvm-mingw  : Windows cross compiler for the handler DLL (x64 + ARM64)
#   NSIS        : makensis, for the installer (Ubuntu package, extracted without installing)
#   cmake/ninja : from PyPI into a venv, only when not already on PATH
set -euo pipefail

TOOLS="${CB_TOOLS_DIR:-${RUNNER_TOOL_CACHE:-$HOME/.cache}/contextbridge-tools}"
mkdir -p "$TOOLS"

LLVM_MINGW_VER=20261117
LLVM_MINGW_NAME="llvm-mingw-${LLVM_MINGW_VER}-ucrt-ubuntu-22.04-x86_64"
LLVM_MINGW_URL="https://github.com/mstorsjo/llvm-mingw/releases/download/${LLVM_MINGW_VER}/${LLVM_MINGW_NAME}.tar.xz"
LLVM_MINGW_SHA=e7a15a49a2ea95e9f3d8a9d0cfabe5052a4b10fb4777cc54de5a8c27b64bb701

NSIS_VER=3.09-4ubuntu1
NSIS_BASE="http://archive.ubuntu.com/ubuntu/pool/universe/n/nsis"
NSIS_DEBS=(
  "nsis_${NSIS_VER}_amd64.deb 9e061bef1a8b611aeb2ea76d9a3bd7722b960ca1a9dff8e38d2ab51e5a8f5e5b"
  "nsis-common_${NSIS_VER}_all.deb 49471b9debdee4751ebb4caf6b705cc4c678eebfaecefbe56362258893f85859"
)

log() { echo "$*" >&2; }

fetch() {  # url sha256 dest
  local url=$1 sha=$2 dest=$3
  if [[ -f "$dest" ]] && echo "$sha  $dest" | sha256sum -c --status; then return; fi
  log "downloading $url"
  curl -fsSL --retry 3 -o "$dest.part" "$url"
  echo "$sha  $dest.part" | sha256sum -c --status || { log "checksum mismatch: $url"; rm -f "$dest.part"; exit 1; }
  mv "$dest.part" "$dest"
}

extract_deb() {  # deb dest
  if command -v dpkg-deb >/dev/null; then
    dpkg-deb -x "$1" "$2"
  else
    local tmp; tmp="$(mktemp -d)"
    (cd "$tmp" && ar x "$1" && tar -xf data.tar.* -C "$2")
    rm -rf "$tmp"
  fi
}

# llvm-mingw
if [[ ! -x "$TOOLS/$LLVM_MINGW_NAME/bin/x86_64-w64-mingw32-clang++" ]]; then
  fetch "$LLVM_MINGW_URL" "$LLVM_MINGW_SHA" "$TOOLS/$LLVM_MINGW_NAME.tar.xz"
  tar -xf "$TOOLS/$LLVM_MINGW_NAME.tar.xz" -C "$TOOLS"
  rm -f "$TOOLS/$LLVM_MINGW_NAME.tar.xz"
fi

# NSIS
NSIS_ROOT="$TOOLS/nsis-$NSIS_VER"
if [[ ! -x "$NSIS_ROOT/usr/bin/makensis" ]]; then
  mkdir -p "$NSIS_ROOT"
  for entry in "${NSIS_DEBS[@]}"; do
    read -r file sha <<<"$entry"
    fetch "$NSIS_BASE/$file" "$sha" "$TOOLS/$file"
    extract_deb "$TOOLS/$file" "$NSIS_ROOT"
  done
fi

# cmake / ninja
EXTRA_PATH=""
if ! command -v cmake >/dev/null || ! command -v ninja >/dev/null; then
  if [[ ! -x "$TOOLS/venv/bin/cmake" || ! -x "$TOOLS/venv/bin/ninja" ]]; then
    python3 -m venv "$TOOLS/venv"
    "$TOOLS/venv/bin/pip" install -q 'cmake>=3.20' ninja >&2
  fi
  EXTRA_PATH="$TOOLS/venv/bin:"
fi

if [[ "${1:-}" == "--github" ]]; then
  {
    echo "LLVM_MINGW=$TOOLS/$LLVM_MINGW_NAME"
    echo "NSISDIR=$NSIS_ROOT/usr/share/nsis"
    echo "MAKENSIS=$NSIS_ROOT/usr/bin/makensis"
  } >> "$GITHUB_ENV"
  [[ -n "$EXTRA_PATH" ]] && echo "${EXTRA_PATH%:}" >> "$GITHUB_PATH"
  exit 0
fi

cat <<EOF
export LLVM_MINGW="$TOOLS/$LLVM_MINGW_NAME"
export NSISDIR="$NSIS_ROOT/usr/share/nsis"
export MAKENSIS="$NSIS_ROOT/usr/bin/makensis"
export PATH="${EXTRA_PATH}\$PATH"
EOF
