#!/usr/bin/env bash
# Builds all release artifacts from Linux: handler DLL (llvm-mingw), settings app (.NET SDK),
# sparse MSIX (scripts/build_msix.py), installer (NSIS makensis) and portable zips.
#
# Requirements: python3, .NET 10 SDK, and the toolchain from scripts/bootstrap-tools.sh:
#   eval "$(./scripts/bootstrap-tools.sh)"
#   ./scripts/build.sh [--skip-tests] [x64] [arm64]
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(tr -d '[:space:]' < "$ROOT/VERSION")"
WINVERSION="$(python3 -c 'import re,sys; p=([int(x) for x in re.findall(r"\d+", sys.argv[1])]+[0,0,0,0])[:4]; print(".".join(map(str,p)))' "$VERSION")"
DIST="$ROOT/dist"
SKIP_TESTS=0
ARCHES=()
for a in "$@"; do
  if [[ "$a" == "--skip-tests" ]]; then SKIP_TESTS=1; else ARCHES+=("$a"); fi
done
[[ ${#ARCHES[@]} -eq 0 ]] && ARCHES=(x64 arm64)
: "${LLVM_MINGW:?set LLVM_MINGW (see scripts/bootstrap-tools.sh)}"
MAKENSIS="${MAKENSIS:-$(command -v makensis || true)}"

if [[ $SKIP_TESTS -eq 0 ]]; then
  echo "== native tests (host)"
  cmake -S "$ROOT" -B "$ROOT/build/tests" -G Ninja -DCMAKE_BUILD_TYPE=Release >/dev/null
  cmake --build "$ROOT/build/tests" --target cb_core_tests
  ctest --test-dir "$ROOT/build/tests" --output-on-failure

  echo "== .NET tests"
  dotnet test "$ROOT/tests/ContextBridge.Core.Tests" -c Release
fi

for arch in "${ARCHES[@]}"; do
  case "$arch" in
    x64) triple=x86_64-w64-mingw32 ;;
    arm64) triple=aarch64-w64-mingw32 ;;
    *) echo "unknown arch $arch" >&2; exit 1 ;;
  esac
  echo "== $arch"
  stage="$DIST/stage-$arch"
  rm -rf "$stage" && mkdir -p "$stage"

  tc="$ROOT/build/toolchain-$arch.cmake"
  mkdir -p "$ROOT/build"
  cat > "$tc" <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_C_COMPILER $LLVM_MINGW/bin/$triple-clang)
set(CMAKE_CXX_COMPILER $LLVM_MINGW/bin/$triple-clang++)
set(CMAKE_RC_COMPILER $LLVM_MINGW/bin/$triple-windres)
EOF
  cmake -S "$ROOT" -B "$ROOT/build/shell-$arch" -G Ninja -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_TOOLCHAIN_FILE="$tc" -DCB_BUILD_TESTS=OFF >/dev/null
  cmake --build "$ROOT/build/shell-$arch" --target ContextBridge.Shell
  cp "$ROOT/build/shell-$arch/ContextBridge.Shell.dll" "$stage/"

  dotnet publish "$ROOT/src/ContextBridge.App" -c Release -r "win-$arch" -o "$ROOT/build/app-$arch"
  cp "$ROOT/build/app-$arch/ContextBridge.exe" "$stage/"

  python3 "$ROOT/scripts/build_msix.py" --arch "$arch" --version "$VERSION" --out "$stage/ContextBridge.msix"
  cp "$ROOT/installer/register.ps1" "$stage/"
  cp "$ROOT/LICENSE" "$stage/LICENSE.txt"

  (cd "$stage" && rm -f "$DIST/ContextBridge-$VERSION-$arch-portable.zip" && \
     python3 -m zipfile -c "$DIST/ContextBridge-$VERSION-$arch-portable.zip" ./*)

  if [[ -n "$MAKENSIS" ]]; then
    "$MAKENSIS" -V2 -WX -DARCH="$arch" -DVERSION="$VERSION" -DWINVERSION="$WINVERSION" -DSTAGE="$stage" -DOUTDIR="$DIST" \
      "$ROOT/installer/ContextBridge.nsi"
  else
    echo "warning: makensis not found, installer skipped" >&2
  fi
done
echo "Done. Output: $DIST"
