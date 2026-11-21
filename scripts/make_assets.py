"""Generates the app icon (.ico) and the MSIX logo PNGs from code, so no binary art is
hand-maintained. Requires Pillow. Usage: python scripts/make_assets.py"""
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
ACCENT = (0, 120, 212, 255)
WHITE = (255, 255, 255, 255)


def draw(size: int) -> Image.Image:
    s = 4  # supersample for smooth edges
    big = size * s
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = big * 0.06
    d.rounded_rectangle([pad, pad, big - pad, big - pad], radius=big * 0.2, fill=ACCENT)
    # Three menu rows; the middle one extends into an arrow (a "bridged" item).
    x0, x1 = big * 0.24, big * 0.62
    h = big * 0.085
    for i, y in enumerate((0.30, 0.50, 0.70)):
        cy = big * y
        d.rounded_rectangle([x0, cy - h / 2, x1, cy + h / 2], radius=h / 2, fill=WHITE)
    ax = big * 0.66
    cy = big * 0.50
    d.polygon([(ax, cy - big * 0.11), (big * 0.80, cy), (ax, cy + big * 0.11)], fill=WHITE)
    return img.resize((size, size), Image.LANCZOS)


def main() -> None:
    sizes = [16, 20, 24, 32, 40, 48, 64, 256]
    icons = [draw(sz) for sz in sizes]
    for target in (ROOT / "src/Shell/app.ico", ROOT / "src/ContextBridge.App/Assets/app.ico"):
        target.parent.mkdir(parents=True, exist_ok=True)
        icons[-1].save(target, format="ICO", sizes=[(sz, sz) for sz in sizes], append_images=icons[:-1])
    assets = ROOT / "package/Assets"
    assets.mkdir(parents=True, exist_ok=True)
    for name, sz in {"StoreLogo.png": 50, "Square44x44Logo.png": 44, "Square150x150Logo.png": 150}.items():
        draw(sz).save(assets / name)


if __name__ == "__main__":
    main()
