from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image


def trim_key_background(image: Image.Image, key=(128, 128, 128)) -> Image.Image:
    rgba = image.convert("RGBA")
    px = rgba.load()
    width, height = rgba.size
    for y in range(height):
        for x in range(width):
            r, g, b, _ = px[x, y]
            distance = ((r - key[0]) ** 2 + (g - key[1]) ** 2 + (b - key[2]) ** 2) ** 0.5
            alpha = 0 if distance <= 14 else 255 if distance >= 75 else round((distance - 14) / 61 * 255)
            if alpha < 255:
                # Suppress the neutral key spill without damaging metallic highlights.
                mean = (r + g + b) // 3
                spill = (255 - alpha) / 255
                r = round(r * (1 - spill * 0.35) + mean * spill * 0.35)
                g = round(g * (1 - spill * 0.35) + mean * spill * 0.35)
                b = round(b * (1 - spill * 0.35) + mean * spill * 0.35)
            px[x, y] = (r, g, b, alpha)
    return rgba


def content_bbox(image: Image.Image):
    alpha = image.getchannel("A")
    return alpha.getbbox()


def split_sheet(sheet_path: Path, out_dir: Path, grid: int = 4) -> list[Image.Image]:
    sheet = Image.open(sheet_path).convert("RGB")
    frames = []
    out_dir.mkdir(parents=True, exist_ok=True)
    for index in range(grid * grid):
        row, col = divmod(index, grid)
        left = round(col * sheet.width / grid)
        top = round(row * sheet.height / grid)
        right = round((col + 1) * sheet.width / grid)
        bottom = round((row + 1) * sheet.height / grid)
        cell = sheet.crop((left, top, right, bottom))
        cutout = trim_key_background(cell)
        cutout.save(out_dir / f"key_{index + 1:02d}.png")
        frames.append(cutout)
    return frames


def normalize(frames: list[Image.Image], size: int = 512, padding: int = 22) -> list[Image.Image]:
    boxes = [content_bbox(frame) for frame in frames]
    valid = [box for box in boxes if box]
    if not valid:
        raise ValueError("No visible character pixels found after background removal")

    max_w = max(box[2] - box[0] for box in valid)
    max_h = max(box[3] - box[1] for box in valid)
    scale = min((size - 2 * padding) / max_w, (size - 2 * padding) / max_h)
    result = []
    for frame, box in zip(frames, boxes):
        if not box:
            result.append(Image.new("RGBA", (size, size), (0, 0, 0, 0)))
            continue
        subject = frame.crop(box)
        target = (max(1, round(subject.width * scale)), max(1, round(subject.height * scale)))
        subject = subject.resize(target, Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (size, size), (0, 0, 0, 0))
        x = (size - subject.width) // 2
        y = size - padding - subject.height
        canvas.alpha_composite(subject, (x, y))
        result.append(canvas)
    return result


def resample_loop(keys: list[Image.Image], count: int = 19) -> list[Image.Image]:
    if len(keys) < 2:
        raise ValueError("At least two key frames are required")
    # Preserve crisp generated linework. Repeating three timing poses is cleaner
    # than alpha-dissolving anatomy and matches hand-authored anime timing.
    indices = [round(i * (len(keys) - 1) / (count - 1)) for i in range(count)]
    return [keys[index].copy() for index in indices]


def gif_palette(frames: list[Image.Image]) -> list[Image.Image]:
    thumb_size = 192
    columns = 5
    rows = (len(frames) + columns - 1) // columns
    atlas = Image.new("RGB", (columns * thumb_size, rows * thumb_size), (36, 28, 48))
    for index, frame in enumerate(frames):
        thumb = frame.copy()
        thumb.thumbnail((thumb_size, thumb_size), Image.Resampling.LANCZOS)
        tile = Image.new("RGBA", (thumb_size, thumb_size), (36, 28, 48, 255))
        tile.alpha_composite(thumb, ((thumb_size - thumb.width) // 2, (thumb_size - thumb.height) // 2))
        atlas.paste(tile.convert("RGB"), ((index % columns) * thumb_size, (index // columns) * thumb_size))

    master = atlas.quantize(colors=255, method=Image.Quantize.MEDIANCUT)
    palette = master.getpalette()[: 255 * 3] + [33, 33, 173]
    paletted = []
    for frame in frames:
        rgb = Image.new("RGB", frame.size, (36, 28, 48))
        rgb.paste(frame.convert("RGB"), mask=frame.getchannel("A"))
        indexed = rgb.quantize(palette=master)
        alpha = frame.getchannel("A")
        pixels = bytearray(indexed.tobytes())
        mask = alpha.tobytes()
        for i, value in enumerate(mask):
            if value < 96:
                pixels[i] = 255
        indexed = Image.frombytes("P", indexed.size, bytes(pixels))
        indexed.putpalette(palette)
        indexed.info["transparency"] = 255
        indexed.info["disposal"] = 2
        paletted.append(indexed)
    return paletted


def write_outputs(frames: list[Image.Image], out_dir: Path, stem: str) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    frame_dir = out_dir / f"{stem}_frames"
    frame_dir.mkdir(parents=True, exist_ok=True)
    for index, frame in enumerate(frames, 1):
        frame.save(frame_dir / f"frame_{index:02d}.png")

    paletted = gif_palette(frames)
    paletted[0].save(
        out_dir / f"{stem}.gif",
        save_all=True,
        append_images=paletted[1:],
        duration=50,
        loop=0,
        optimize=False,
        disposal=2,
        transparency=255,
    )
    frames[0].save(
        out_dir / f"{stem}.webp",
        save_all=True,
        append_images=frames[1:],
        duration=50,
        loop=0,
        lossless=True,
        method=6,
    )
    frames[0].save(
        out_dir / f"{stem}.png",
        save_all=True,
        append_images=frames[1:],
        duration=50,
        loop=0,
        disposal=2,
        blend=0,
    )
    frames[0].save(out_dir / f"{stem}_preview.png")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("sheet", type=Path)
    parser.add_argument("out_dir", type=Path)
    parser.add_argument("stem")
    args = parser.parse_args()

    key_dir = args.out_dir / f"{args.stem}_keys"
    keys = normalize(split_sheet(args.sheet, key_dir))
    write_outputs(resample_loop(keys), args.out_dir, args.stem)


if __name__ == "__main__":
    main()
