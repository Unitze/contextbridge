#!/usr/bin/env python3
"""Builds the unsigned sparse MSIX (manifest + logos only) without the Windows SDK.

An MSIX is an OPC zip with two generated parts:
  * AppxBlockMap.xml    SHA-256 of every 64 KiB block of every payload file
  * [Content_Types].xml MIME types per extension / part
Files are stored uncompressed, so each <Block> carries only a hash (no Size attribute).

Usage:
  python scripts/build_msix.py --arch x64 --version 0.1.0 --out dist/ContextBridge-x64.msix
On Windows, scripts/build.ps1 prefers MakeAppx.exe when the SDK is installed and falls back
to this script otherwise.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import re
import sys
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape, quoteattr

ROOT = Path(__file__).resolve().parent.parent
BLOCK = 64 * 1024
FIXED_TIME = (2020, 1, 1, 0, 0, 0)  # deterministic output

CONTENT_TYPES = {
    "png": "image/png",
    "xml": "application/vnd.ms-appx.manifest+xml",
}


def package_version(v: str) -> str:
    parts = [int(p) for p in re.findall(r"\d+", v)][:4]
    while len(parts) < 4:
        parts.append(0)
    for p in parts:
        if p > 65535:
            raise SystemExit(f"version component {p} exceeds 65535")
    return ".".join(str(p) for p in parts)


def render_manifest(arch: str, version: str) -> bytes:
    text = (ROOT / "package/AppxManifest.xml.in").read_text(encoding="utf-8")
    text = text.replace("@VERSION@", package_version(version)).replace("@ARCH@", arch)
    body = re.sub(r"<!--.*?-->", "", text, flags=re.S)
    if re.search(r"@[A-Z0-9_]+@", body):
        raise SystemExit("unreplaced placeholder in manifest")
    # Mirror the MakeAppx schema rule for desktop5:ItemType so the SDK-less path cannot
    # produce a package that Windows would reject at registration time.
    for t in re.findall(r'<desktop5:ItemType\s+Type="([^"]*)"', body):
        if not re.fullmatch(r"(\*|(\.[^.\\]+))|Directory|Directory\\Background", t):
            raise SystemExit(f"invalid desktop5:ItemType Type '{t}'")
    verb_ids = re.findall(r'<desktop5:Verb\s+Id="([^"]*)"', body)
    if len(verb_ids) != len(set(verb_ids)):
        raise SystemExit(f"duplicate desktop5:Verb Id in manifest: {verb_ids}")
    for asset in re.findall(r'Assets\\([^"<\s]+\.png)', body):
        if not (ROOT / "package/Assets" / asset).is_file():
            raise SystemExit(f"manifest references missing asset: Assets/{asset}")
    return text.encode("utf-8")


def build(arch: str, version: str, out: Path) -> None:
    payload: list[tuple[str, bytes]] = [("AppxManifest.xml", render_manifest(arch, version))]
    for png in sorted((ROOT / "package/Assets").glob("*.png")):
        payload.append((f"Assets/{png.name}", png.read_bytes()))

    blockmap = io.StringIO()
    blockmap.write('<?xml version="1.0" encoding="UTF-8" standalone="no"?>\r\n')
    blockmap.write('<BlockMap xmlns="http://schemas.microsoft.com/appx/2010/blockmap" '
                   'HashMethod="http://www.w3.org/2001/04/xmlenc#sha256">')

    out.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(out, "w", compression=zipfile.ZIP_STORED) as z:
        for name, data in payload:
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 0
            z.writestr(info, data)
            lfh = 30 + len(name.encode("utf-8")) + len(info.extra)
            win_name = name.replace("/", "\\")
            blockmap.write(f"<File Name={quoteattr(win_name)} Size=\"{len(data)}\" LfhSize=\"{lfh}\">")
            for off in range(0, len(data), BLOCK):
                digest = base64.b64encode(hashlib.sha256(data[off:off + BLOCK]).digest()).decode()
                blockmap.write(f"<Block Hash=\"{digest}\"/>")
            blockmap.write("</File>")
        blockmap.write("</BlockMap>")

        ct = io.StringIO()
        ct.write('<?xml version="1.0" encoding="UTF-8"?>\r\n')
        ct.write('<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">')
        ct.write(f'<Default Extension="png" ContentType="{CONTENT_TYPES["png"]}"/>')
        ct.write(f'<Override PartName="/AppxManifest.xml" ContentType="{CONTENT_TYPES["xml"]}"/>')
        ct.write('<Override PartName="/AppxBlockMap.xml" ContentType="application/vnd.ms-appx.blockmap+xml"/>')
        ct.write("</Types>")

        for name, text in (("AppxBlockMap.xml", blockmap.getvalue()), ("[Content_Types].xml", ct.getvalue())):
            info = zipfile.ZipInfo(name, FIXED_TIME)
            info.compress_type = zipfile.ZIP_STORED
            info.create_system = 0
            z.writestr(info, text.encode("utf-8"))
    print(f"wrote {out} ({escape(package_version(version))}, {arch})")


def main(argv: list[str]) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--arch", choices=["x64", "arm64"], required=True)
    ap.add_argument("--version", default=(ROOT / "VERSION").read_text().strip())
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--manifest-only", type=Path, help="write the rendered manifest here (for MakeAppx) and exit")
    a = ap.parse_args(argv)
    if a.manifest_only:
        a.manifest_only.parent.mkdir(parents=True, exist_ok=True)
        a.manifest_only.write_bytes(render_manifest(a.arch, a.version))
        return 0
    build(a.arch, a.version, a.out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
