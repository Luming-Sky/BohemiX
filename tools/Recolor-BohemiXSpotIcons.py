#!/usr/bin/env python3
"""Map BohemiX spot-icon blues to the app's neutral foreground palette."""

from __future__ import annotations

import argparse
from pathlib import Path

import numpy as np
from PIL import Image


SHADOW = np.array([0x64, 0x74, 0x8B], dtype=np.float32)
BASE = np.array([0xA6, 0xAD, 0xBB], dtype=np.float32)
HIGHLIGHT = np.array([0xF8, 0xFA, 0xFC], dtype=np.float32)


def lerp(start: np.ndarray, end: np.ndarray, amount: np.ndarray) -> np.ndarray:
    return start + (end - start) * amount[..., None]


def recolor(source: Path, destination: Path) -> int:
    image = Image.open(source).convert("RGBA")
    rgba = np.array(image)
    rgb = rgba[:, :, :3]
    alpha = rgba[:, :, 3]
    hsv = np.array(Image.fromarray(rgb, "RGB").convert("HSV"))

    # Pillow hue 118..188 is approximately 167..265 degrees: cyan through blue.
    hue = hsv[:, :, 0]
    saturation = hsv[:, :, 1]
    blue_mask = (
        (alpha > 0)
        & (hue >= 118)
        & (hue <= 188)
        & (saturation >= 16)
    )

    rgb_float = rgb.astype(np.float32) / 255.0
    luminance = (
        0.2126 * rgb_float[:, :, 0]
        + 0.7152 * rgb_float[:, :, 1]
        + 0.0722 * rgb_float[:, :, 2]
    )
    tone = np.clip((luminance - 0.12) / 0.76, 0.0, 1.0)

    neutral = np.empty_like(rgb, dtype=np.float32)
    lower = tone <= 0.55
    neutral[lower] = lerp(SHADOW, BASE, tone[lower] / 0.55)
    neutral[~lower] = lerp(BASE, HIGHLIGHT, (tone[~lower] - 0.55) / 0.45)

    result = rgba.copy()
    result[:, :, :3][blue_mask] = np.clip(neutral[blue_mask], 0, 255).astype(np.uint8)
    destination.parent.mkdir(parents=True, exist_ok=True)
    Image.fromarray(result, "RGBA").save(destination)
    return int(blue_mask.sum())


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("input_dir", type=Path)
    parser.add_argument("output_dir", type=Path)
    args = parser.parse_args()

    sources = sorted(args.input_dir.glob("*.png"))
    if not sources:
        raise SystemExit(f"No PNG files found in {args.input_dir}")

    changed = 0
    for source in sources:
        changed += recolor(source, args.output_dir / source.name)

    print(f"Recolored {len(sources)} icons ({changed} blue/cyan pixels).")


if __name__ == "__main__":
    main()
