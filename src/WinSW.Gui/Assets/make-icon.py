"""
Regenerates WinSW.Gui.ico.

    python3 make-icon.py

The mark is the one the palette already implies: a rounded square carrying the accent
gradient from Palette.xaml, with a white W. Sizes 16 through 256 are written into one
.ico — the small ones with a heavier stroke and no padding, because at 16 pixels the
framing eats the mark. Kept in the repository so the icon can be changed without a
design tool, and so the .ico beside it is reproducible rather than a mystery binary.
"""
import struct, zlib, math

START = (0x7C, 0x6C, 0xFF)   # accent gradient start, from Palette.xaml
END   = (0x22, 0xD3, 0xEE)   # accent gradient end
SS = 8                        # supersampling factor

def clamp(v, lo=0.0, hi=1.0):
    return lo if v < lo else hi if v > hi else v

def rounded_rect_cover(x, y, w, h, r):
    """Signed distance of (x, y) to a rounded rectangle [0,w]x[0,h], negative inside."""
    cx, cy = abs(x - w / 2) - (w / 2 - r), abs(y - h / 2) - (h / 2 - r)
    outside = math.hypot(max(cx, 0), max(cy, 0))
    return outside + min(max(cx, cy), 0) - r

def seg_distance(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    t = 0.0 if (vx * vx + vy * vy) == 0 else clamp((wx * vx + wy * vy) / (vx * vx + vy * vy))
    return math.hypot(wx - t * vx, wy - t * vy)

def render(size):
    n = size * SS
    # The W, as a polyline in unit space, and the padding of the rounded square.
    pts = [(0.185, 0.285), (0.345, 0.735), (0.5, 0.455), (0.655, 0.735), (0.815, 0.285)]

    # Small sizes need a heavier stroke and less air: at 16 pixels a tenth of the box is a
    # pixel and a half, and the padding and corner radius are eating the mark rather than
    # framing it.
    small = size <= 24
    stroke = (0.125 if small else 0.098) * n
    pad = 0.0 if small else 0.035 * n
    radius = (0.20 if small else 0.235) * n
    poly = [(x * n, y * n) for x, y in pts]

    pixels = bytearray(size * size * 4)
    acc = [[0.0] * 4 for _ in range(size * size)]

    for sy in range(n):
        yc = sy + 0.5
        row = sy // SS
        for sx in range(n):
            xc = sx + 0.5

            d = rounded_rect_cover(xc - pad, yc - pad, n - 2 * pad, n - 2 * pad, radius)
            fill = clamp(0.5 - d)                       # 1 px of antialiasing at the edge
            if fill <= 0:
                continue

            # 45-degree gradient across the square
            t = clamp((xc + yc) / (2 * n))
            r = START[0] + (END[0] - START[0]) * t
            g = START[1] + (END[1] - START[1]) * t
            b = START[2] + (END[2] - START[2]) * t

            dm = min(seg_distance(xc, yc, *poly[i], *poly[i + 1]) for i in range(len(poly) - 1))
            glyph = clamp(0.5 + (stroke / 2 - dm))
            r = r + (255 - r) * glyph
            g = g + (255 - g) * glyph
            b = b + (255 - b) * glyph

            cell = acc[row * size + sx // SS]
            cell[0] += b * fill
            cell[1] += g * fill
            cell[2] += r * fill
            cell[3] += 255 * fill

    per = SS * SS
    for i, cell in enumerate(acc):
        a = cell[3] / per
        if a <= 0.5:
            continue
        # Un-premultiply so partly covered edge pixels keep their colour.
        w = cell[3] / 255.0
        pixels[i * 4 + 0] = int(round(cell[0] / w))
        pixels[i * 4 + 1] = int(round(cell[1] / w))
        pixels[i * 4 + 2] = int(round(cell[2] / w))
        pixels[i * 4 + 3] = int(round(a))
    return bytes(pixels)

def png(size, bgra):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        for x in range(size):
            i = (y * size + x) * 4
            raw += bytes((bgra[i + 2], bgra[i + 1], bgra[i], bgra[i + 3]))
    def chunk(tag, data):
        return struct.pack(">I", len(data)) + tag + data + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9))
            + chunk(b"IEND", b""))

def dib(size, bgra):
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, size * size * 4, 0, 0, 0, 0)
    rows = b"".join(bgra[(size - 1 - y) * size * 4:(size - y) * size * 4] for y in range(size))
    mask_row = ((size + 31) // 32) * 4
    return header + rows + b"\x00" * (mask_row * size)

sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
images = []
for s in sizes:
    bgra = render(s)
    images.append((s, png(s, bgra) if s >= 128 else dib(s, bgra)))
    if s == 256:
        open("icon-preview.png", "wb").write(png(s, bgra))

out = bytearray(struct.pack("<HHH", 0, 1, len(images)))
offset = 6 + 16 * len(images)
for s, blob in images:
    out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(blob), offset)
    offset += len(blob)
for _, blob in images:
    out += blob
open("WinSW.Gui.ico", "wb").write(bytes(out))
print("ico bytes:", len(out), "| sizes:", sizes)
