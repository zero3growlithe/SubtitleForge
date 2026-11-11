#!/usr/bin/env python3
"""Generates SubtitleForge/Assets/app.ico.

Every size is rendered natively at 8x and downsampled with LANCZOS, then the ICO
container is assembled by hand (ICONDIR + 16-byte entries + PNG payloads) so each
entry keeps its own quality instead of being a resize of the 256px frame.

Usage:  python3 tools/generate-icon.py
"""

import io
import os
import struct
import sys

from PIL import Image, ImageDraw

SIZES = [16, 24, 32, 48, 64, 128, 256]
SUPERSAMPLE = 8

BACKGROUND = (27, 27, 31, 255)
ACCENT = (47, 129, 247, 255)
WHITE = (240, 244, 250, 255)
SUBTLE = (150, 165, 190, 255)

OUTPUT_PATH = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "SubtitleForge",
    "Assets",
    "app.ico",
)


def render_frame(size):
    """Renders one icon frame: rounded tile, caption bars and a waveform accent."""
    canvas = size * SUPERSAMPLE
    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    inset = max(1, int(canvas * 0.02))
    radius = int(canvas * 0.22)
    draw.rounded_rectangle(
        [inset, inset, canvas - inset, canvas - inset],
        radius=radius,
        fill=BACKGROUND,
    )

    # Three caption bars, the top one shorter, like stacked subtitle lines.
    bar_height = int(canvas * 0.085)
    bar_radius = bar_height // 2
    left = int(canvas * 0.16)
    right = int(canvas * 0.84)

    rows = [
        (left + int(canvas * 0.10), right - int(canvas * 0.10), int(canvas * 0.27)),
        (left, right, int(canvas * 0.42)),
        (left + int(canvas * 0.05), right - int(canvas * 0.05), int(canvas * 0.57)),
    ]

    for x0, x1, y0 in rows:
        draw.rounded_rectangle([x0, y0, x1, y0 + bar_height], radius=bar_radius, fill=WHITE)

    # Accent waveform under the caption lines.
    baseline = int(canvas * 0.76)
    heights = [0.055, 0.11, 0.075, 0.14, 0.09, 0.12, 0.06]
    total = len(heights)
    spacing = (right - left) / float(total)
    bar_width = max(2, int(spacing * 0.42))

    for index, fraction in enumerate(heights):
        center_x = left + (spacing * (index + 0.5))
        half = (canvas * fraction) / 2.0
        draw.rounded_rectangle(
            [center_x - (bar_width / 2.0), baseline - half, center_x + (bar_width / 2.0), baseline + half],
            radius=bar_width // 2,
            fill=ACCENT if index % 2 == 0 else SUBTLE,
        )

    return image.resize((size, size), Image.LANCZOS)


def assemble_ico(frames):
    """Writes a PNG-payload ICO: ICONDIR then one 16-byte entry per frame."""
    entries = []
    payloads = []

    for size, image in frames:
        buffer = io.BytesIO()
        image.save(buffer, format="PNG", optimize=True)
        data = buffer.getvalue()
        payloads.append(data)
        entries.append(
            (
                size if size < 256 else 0,
                size if size < 256 else 0,
                len(data),
            )
        )

    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = len(header) + (16 * len(entries))

    directory = b""
    for (width, height, length) in entries:
        directory += struct.pack("<BBBBHHII", width, height, 0, 0, 1, 32, length, offset)
        offset += length

    return header + directory + b"".join(payloads)


def main():
    frames = [(size, render_frame(size)) for size in SIZES]
    blob = assemble_ico(frames)

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "wb") as handle:
        handle.write(blob)

    print("wrote {} ({} bytes, {} frames)".format(OUTPUT_PATH, len(blob), len(frames)))
    return 0


if __name__ == "__main__":
    sys.exit(main())
