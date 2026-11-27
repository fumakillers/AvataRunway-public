from __future__ import annotations

import argparse
import io
import shutil
import struct
from pathlib import Path

from PIL import Image, ImageFilter


ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def resize_icon(source: Image.Image, size: int) -> Image.Image:
    image = source.resize((size, size), Image.Resampling.LANCZOS)
    if size <= 48:
        image = image.filter(ImageFilter.UnsharpMask(radius=0.6, percent=90, threshold=2))
    return image


def write_ico(source: Image.Image, destination: Path) -> None:
    payloads: list[tuple[int, bytes]] = []
    for size in ICO_SIZES:
        buffer = io.BytesIO()
        resize_icon(source, size).save(buffer, format="PNG", optimize=True)
        payloads.append((size, buffer.getvalue()))

    header_size = 6 + 16 * len(payloads)
    offset = header_size
    with destination.open("wb") as stream:
        stream.write(struct.pack("<HHH", 0, 1, len(payloads)))
        for size, payload in payloads:
            dimension = 0 if size == 256 else size
            stream.write(
                struct.pack(
                    "<BBBBHHII",
                    dimension,
                    dimension,
                    0,
                    0,
                    1,
                    32,
                    len(payload),
                    offset,
                )
            )
            offset += len(payload)
        for _, payload in payloads:
            stream.write(payload)


def save_square(source: Image.Image, destination: Path, size: int) -> None:
    resize_icon(source, size).save(destination, format="PNG", optimize=True)


def save_centered(source: Image.Image, destination: Path, size: tuple[int, int], icon_size: int) -> None:
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    icon = resize_icon(source, icon_size)
    position = ((size[0] - icon_size) // 2, (size[1] - icon_size) // 2)
    canvas.alpha_composite(icon, position)
    canvas.save(destination, format="PNG", optimize=True)


def main() -> None:
    parser = argparse.ArgumentParser(description="Generate AvataRunway Windows icon assets.")
    parser.add_argument("source", type=Path)
    parser.add_argument("assets", type=Path)
    args = parser.parse_args()

    args.assets.mkdir(parents=True, exist_ok=True)
    with Image.open(args.source) as loaded:
        source = loaded.convert("RGBA")
    if source.width != source.height:
        raise ValueError("The source icon must be square.")

    shutil.copyfile(args.source, args.assets / "AppIcon.png")
    write_ico(source, args.assets / "AvataRunway.ico")

    save_square(source, args.assets / "LockScreenLogo.scale-200.png", 48)
    save_square(source, args.assets / "Square150x150Logo.scale-200.png", 300)
    save_square(source, args.assets / "Square44x44Logo.scale-200.png", 88)
    save_square(source, args.assets / "Square44x44Logo.targetsize-24_altform-unplated.png", 24)
    save_square(source, args.assets / "StoreLogo.png", 50)
    save_centered(source, args.assets / "Wide310x150Logo.scale-200.png", (620, 300), 252)


if __name__ == "__main__":
    main()
