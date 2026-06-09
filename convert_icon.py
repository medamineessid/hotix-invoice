"""Draw the HOTIX icon and save as multi-size ICO using only Pillow."""
import io, os, struct
from PIL import Image, ImageDraw

ICO_PATH = os.path.join(os.path.dirname(__file__), "client", "hotix_icon.ico")
SIZES = [16, 32, 48, 64, 128, 256]

BG     = (0x1E, 0x1E, 0x1E, 0xFF)
RED    = (0xC0, 0x39, 0x2B, 0xFF)
RED_LT = (0xE7, 0x4C, 0x3C, 0xFF)


def draw_icon(size: int) -> Image.Image:
    sc = size / 150
    img = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    s = lambda v: int(round(v * sc))

    d.ellipse([0, 0, size - 1, size - 1], fill=BG)
    bw = max(1, s(3))
    d.ellipse([bw, bw, size - 1 - bw, size - 1 - bw], outline=RED, width=bw)
    d.rounded_rectangle([s(37), s(38), s(59), s(112)], radius=max(1, s(4)), fill=RED)
    d.rounded_rectangle([s(91), s(38), s(113), s(112)], radius=max(1, s(4)), fill=RED)
    d.rounded_rectangle([s(37), s(70), s(113), s(84)], radius=max(1, s(3)), fill=RED_LT)
    for cx, cy, a in [(55, 128, 255), (75, 132, 153), (95, 128, 77)]:
        r = max(1, s(3))
        d.ellipse([s(cx)-r, s(cy)-r, s(cx)+r, s(cy)+r], fill=(0xC0, 0x39, 0x2B, a))
    return img


# Build PNG blobs for each size
blobs = []
for sz in SIZES:
    buf = io.BytesIO()
    draw_icon(sz).save(buf, format="PNG")
    blobs.append(buf.getvalue())

# Write ICO manually (ICONDIRENTRY format)
n = len(SIZES)
header = struct.pack("<HHH", 0, 1, n)          # reserved, type=1, count
offset = 6 + n * 16                            # after header + all entries
entries = b""
for i, (sz, blob) in enumerate(zip(SIZES, blobs)):
    w = h = 0 if sz == 256 else sz             # 0 means 256 in ICO spec
    entries += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(blob), offset)
    offset += len(blob)

with open(ICO_PATH, "wb") as f:
    f.write(header + entries)
    for blob in blobs:
        f.write(blob)

print(f"Saved {ICO_PATH} ({os.path.getsize(ICO_PATH):,} bytes, {n} sizes)")
