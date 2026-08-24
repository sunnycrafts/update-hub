"""
Generates UpdateHub.ico.

The mark is the product's own idea drawn small: a six-segment gauge sweeping green to
red, which is the same age scale the app puts next to every application, with an update
arrow through the middle.

Everything is rendered once at 1024px and downsampled with LANCZOS to each icon size, so
edges stay clean at 16px instead of being drawn pixel-by-pixel at each scale. The .ico is
assembled by hand with PNG payloads rather than left to a library resize.
"""

import math
import struct
from io import BytesIO

from PIL import Image, ImageDraw, ImageFilter

M = 1024                      # master render size
SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]

# The app's own status palette, so the icon and the UI agree.
SEGMENT_COLORS = [
    (0x4C, 0xC2, 0x6E),   # current
    (0x8B, 0xC3, 0x4A),
    (0xE8, 0xB3, 0x39),   # minor
    (0xF2, 0x8B, 0x36),   # outdated
    (0xE0, 0x50, 0x50),   # very outdated
    (0xD1, 0x3A, 0x74),   # major
]

TILE_TOP = (0x2A, 0x33, 0x4A)
TILE_BOTTOM = (0x15, 0x19, 0x26)


def rounded_tile() -> Image.Image:
    """Windows 11 style rounded square with a soft vertical gradient."""
    grad = Image.new("RGB", (1, M))
    for y in range(M):
        t = y / (M - 1)
        grad.putpixel((0, y), tuple(
            round(TILE_TOP[i] + (TILE_BOTTOM[i] - TILE_TOP[i]) * t) for i in range(3)))
    grad = grad.resize((M, M), Image.Resampling.BILINEAR).convert("RGBA")

    # Mask at 4x then downsample: gives an antialiased corner without any edge fringe.
    s = 4
    mask = Image.new("L", (M * s, M * s), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        [0, 0, M * s - 1, M * s - 1], radius=int(M * s * 0.225), fill=255)
    mask = mask.resize((M, M), Image.Resampling.LANCZOS)

    tile = Image.new("RGBA", (M, M), (0, 0, 0, 0))
    tile.paste(grad, (0, 0), mask)

    # A faint top highlight keeps the tile from reading as flat.
    gloss = Image.new("RGBA", (M, M), (0, 0, 0, 0))
    ImageDraw.Draw(gloss).ellipse(
        [-M * 0.35, -M * 0.85, M * 1.35, M * 0.42], fill=(255, 255, 255, 22))
    gloss = gloss.filter(ImageFilter.GaussianBlur(M * 0.04))
    tile.alpha_composite(Image.composite(
        gloss, Image.new("RGBA", (M, M), (0, 0, 0, 0)), mask))
    return tile


# Small icons get their own artwork rather than a downscale of the large one: six
# segments and a slim arrow turn into an unreadable smear below about 24px, so at those
# sizes the gauge drops to three fat segments and the arrow grows.
SMALL_COLORS = [
    (0x4C, 0xC2, 0x6E),
    (0xE8, 0xB3, 0x39),
    (0xE0, 0x50, 0x50),
]


def gauge(img: Image.Image, detail: bool = True) -> None:
    """Arc segments, open at the bottom, sweeping green through red."""
    s = 4
    layer = Image.new("RGBA", (M * s, M * s), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    colours = SEGMENT_COLORS if detail else SMALL_COLORS
    inset = M * s * (0.135 if detail else 0.105)
    box = [inset, inset, M * s - inset, M * s - inset]
    width = int(M * s * (0.085 if detail else 0.125))

    start, total = 145.0, 250.0
    gap = 5.0 if detail else 9.0
    span = (total - gap * (len(colours) - 1)) / len(colours)

    for i, colour in enumerate(colours):
        a0 = start + i * (span + gap)
        d.arc(box, a0, a0 + span, fill=colour + (255,), width=width)

    img.alpha_composite(layer.resize((M, M), Image.Resampling.LANCZOS))


def arrow(img: Image.Image, detail: bool = True) -> None:
    """Update arrow: a thick upward shaft with a broad head, centred in the gauge."""
    s = 4
    layer = Image.new("RGBA", (M * s, M * s), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    cx = M * s / 2
    if detail:
        head_top = M * s * 0.335
        head_bottom = M * s * 0.560
        half_head = M * s * 0.205
        half_shaft = M * s * 0.083
        shaft_bottom = M * s * 0.760
    else:
        head_top = M * s * 0.310
        head_bottom = M * s * 0.575
        half_head = M * s * 0.235
        half_shaft = M * s * 0.105
        shaft_bottom = M * s * 0.790

    d.polygon(
        [
            (cx, head_top),
            (cx + half_head, head_bottom),
            (cx + half_shaft, head_bottom),
            (cx + half_shaft, shaft_bottom),
            (cx - half_shaft, shaft_bottom),
            (cx - half_shaft, head_bottom),
            (cx - half_head, head_bottom),
        ],
        fill=(255, 255, 255, 255),
    )

    # Round the shaft's bottom corners so the arrow does not read as a cut-off rectangle.
    r = half_shaft
    d.ellipse([cx - half_shaft, shaft_bottom - r, cx + half_shaft, shaft_bottom + r],
              fill=(255, 255, 255, 255))

    img.alpha_composite(layer.resize((M, M), Image.Resampling.LANCZOS))


def build_master(detail: bool = True) -> Image.Image:
    img = rounded_tile()
    gauge(img, detail)
    arrow(img, detail)
    return img


#: Below this, the simplified artwork is used.
DETAIL_THRESHOLD = 32


def frame_for(size: int, detailed: Image.Image, simple: Image.Image) -> Image.Image:
    src = detailed if size >= DETAIL_THRESHOLD else simple
    return src.resize((size, size), Image.Resampling.LANCZOS)


def write_ico(detailed: Image.Image, simple: Image.Image, path: str) -> None:
    """
    Assembles a multi-resolution .ico with PNG payloads. Resizing each entry here keeps
    LANCZOS quality; leaving it to the encoder does not.
    """
    entries = []
    for size in SIZES:
        buf = BytesIO()
        frame_for(size, detailed, simple).save(buf, format="PNG", optimize=True)
        entries.append((size, buf.getvalue()))

    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = len(header) + 16 * len(entries)

    directory, blobs = b"", b""
    for size, data in entries:
        # 0 encodes 256 in the single-byte width/height fields.
        dim = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
        blobs += data

    with open(path, "wb") as f:
        f.write(header + directory + blobs)


if __name__ == "__main__":
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(here)

    detailed = build_master(detail=True)
    simple = build_master(detail=False)

    ico = os.path.join(root, "Assets", "UpdateHub.ico")
    os.makedirs(os.path.dirname(ico), exist_ok=True)
    write_ico(detailed, simple, ico)
    detailed.save(os.path.join(here, "icon-preview.png"))

    # Contact sheet of the real entries, at 1:1 and again magnified, on mid grey so both
    # light and dark edge artefacts show. The magnified strip is where small-size
    # decisions actually get judged.
    pad, zoom = 18, 6
    small = [s for s in SIZES if s <= 48]
    sheet_w = max(sum(s + pad for s in SIZES) + pad,
                  sum(s * zoom + pad for s in small) + pad)
    sheet = Image.new("RGBA", (sheet_w, 150 + 48 * zoom + pad * 3), (128, 128, 128, 255))

    x = pad
    for s in SIZES:
        sheet.alpha_composite(frame_for(s, detailed, simple), (x, 75 - s // 2))
        x += s + pad

    x, base = pad, 150 + pad
    for s in small:
        f = frame_for(s, detailed, simple).resize(
            (s * zoom, s * zoom), Image.Resampling.NEAREST)
        sheet.alpha_composite(f, (x, base))
        x += s * zoom + pad

    sheet.save(os.path.join(here, "icon-sizes.png"))

    print(f"wrote {ico} ({os.path.getsize(ico):,} bytes, {len(SIZES)} sizes; "
          f"simplified artwork below {DETAIL_THRESHOLD}px)")
