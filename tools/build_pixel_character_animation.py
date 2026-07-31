from __future__ import annotations

import argparse
from pathlib import Path

from PIL import Image, ImageChops, ImageFilter

from build_character_animation import normalize, resample_loop, split_sheet


OUTLINE = (31, 22, 38)


def make_global_palette(frames: list[Image.Image], colors: int) -> Image.Image:
    columns = 5
    rows = (len(frames) + columns - 1) // columns
    tile_w, tile_h = frames[0].size
    atlas = Image.new("RGB", (columns * tile_w, rows * tile_h), OUTLINE)
    for index, frame in enumerate(frames):
        rgb = Image.new("RGB", frame.size, OUTLINE)
        rgb.paste(frame.convert("RGB"), mask=frame.getchannel("A"))
        atlas.paste(rgb, ((index % columns) * tile_w, (index // columns) * tile_h))
    return atlas.quantize(colors=colors - 1, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)


def remove_small_components(alpha: Image.Image) -> Image.Image:
    width, height = alpha.size
    source = alpha.tobytes()
    visited = bytearray(width * height)
    components: list[list[int]] = []

    for start, value in enumerate(source):
        if not value or visited[start]:
            continue
        visited[start] = 1
        stack = [start]
        component = []
        while stack:
            current = stack.pop()
            component.append(current)
            x, y = current % width, current // width
            for neighbor in (
                current - 1 if x else -1,
                current + 1 if x + 1 < width else -1,
                current - width if y else -1,
                current + width if y + 1 < height else -1,
            ):
                if neighbor >= 0 and source[neighbor] and not visited[neighbor]:
                    visited[neighbor] = 1
                    stack.append(neighbor)
        components.append(component)

    if not components:
        return alpha
    largest = max(len(component) for component in components)
    threshold = max(12, round(largest * 0.012))
    cleaned = bytearray(width * height)
    for component in components:
        if len(component) >= threshold:
            for index in component:
                cleaned[index] = 255
    return Image.frombytes("L", alpha.size, bytes(cleaned))


def pixelize(frame: Image.Image, palette: Image.Image, size: int) -> Image.Image:
    reduced = frame.resize((size, size), Image.Resampling.LANCZOS)
    alpha = reduced.getchannel("A").point(lambda value: 255 if value >= 112 else 0)
    alpha = remove_small_components(alpha)
    eroded = alpha.filter(ImageFilter.MinFilter(3))
    boundary = ImageChops.subtract(alpha, eroded)

    rgb = Image.new("RGB", reduced.size, OUTLINE)
    rgb.paste(reduced.convert("RGB"), mask=alpha)
    rgb.paste(OUTLINE, mask=boundary)
    indexed = rgb.quantize(palette=palette, dither=Image.Dither.NONE)
    rgba = indexed.convert("RGBA")
    rgba.putalpha(alpha)
    return rgba


def transparent_gif_frames(frames: list[Image.Image], colors: int) -> list[Image.Image]:
    atlas_palette = make_global_palette(frames, colors)
    palette = atlas_palette.getpalette()[: 255 * 3] + [33, 33, 173]
    result = []
    for frame in frames:
        rgb = Image.new("RGB", frame.size, OUTLINE)
        rgb.paste(frame.convert("RGB"), mask=frame.getchannel("A"))
        indexed = rgb.quantize(palette=atlas_palette, dither=Image.Dither.NONE)
        data = bytearray(indexed.tobytes())
        alpha = frame.getchannel("A").tobytes()
        for index, value in enumerate(alpha):
            if value == 0:
                data[index] = 255
        indexed = Image.frombytes("P", indexed.size, bytes(data))
        indexed.putpalette(palette)
        indexed.info["transparency"] = 255
        indexed.info["disposal"] = 2
        result.append(indexed)
    return result


def nearest_frames(frames: list[Image.Image], factor: int) -> list[Image.Image]:
    return [frame.resize((frame.width * factor, frame.height * factor), Image.Resampling.NEAREST) for frame in frames]


def save_animation_set(frames: list[Image.Image], out_dir: Path, stem: str, colors: int) -> None:
    frame_dir = out_dir / f"{stem}_frames"
    frame_dir.mkdir(parents=True, exist_ok=True)
    for index, frame in enumerate(frames, 1):
        frame.save(frame_dir / f"frame_{index:02d}.png")

    gif_frames = transparent_gif_frames(frames, colors)
    gif_frames[0].save(
        out_dir / f"{stem}.gif",
        save_all=True,
        append_images=gif_frames[1:],
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
    frames[0].save(out_dir / f"{stem}-icon.png")

    sheet = Image.new("RGBA", (5 * frames[0].width, 4 * frames[0].height), (0, 0, 0, 0))
    for index, frame in enumerate(frames):
        sheet.alpha_composite(frame, ((index % 5) * frame.width, (index // 5) * frame.height))
    sheet.save(out_dir / f"{stem}-spritesheet.png")

    doubled = nearest_frames(frames, 2)
    doubled_gif = transparent_gif_frames(doubled, colors)
    doubled_gif[0].save(
        out_dir / f"{stem}-2x.gif",
        save_all=True,
        append_images=doubled_gif[1:],
        duration=50,
        loop=0,
        optimize=False,
        disposal=2,
        transparency=255,
    )
    doubled[0].save(
        out_dir / f"{stem}-2x.webp",
        save_all=True,
        append_images=doubled[1:],
        duration=50,
        loop=0,
        lossless=True,
        method=6,
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("sheet", type=Path)
    parser.add_argument("out_dir", type=Path)
    parser.add_argument("stem")
    parser.add_argument("--size", type=int, default=128)
    parser.add_argument("--colors", type=int, default=32)
    args = parser.parse_args()

    key_dir = args.out_dir / f"{args.stem}_keys"
    keys = normalize(split_sheet(args.sheet, key_dir), size=512, padding=24)
    timed = resample_loop(keys)
    downsized = [frame.resize((args.size, args.size), Image.Resampling.LANCZOS) for frame in timed]
    palette = make_global_palette(downsized, args.colors)
    frames = [pixelize(frame, palette, args.size) for frame in timed]
    save_animation_set(frames, args.out_dir, args.stem, args.colors)


if __name__ == "__main__":
    main()
