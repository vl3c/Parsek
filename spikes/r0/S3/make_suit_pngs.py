#!/usr/bin/env python3
"""Spike S3: writes the PNG textures of the two test SUITCOMBOS sets (stdlib only).

    python make_suit_pngs.py            write the PNGs under gamedata/ParsekSpikeS3/
    python make_suit_pngs.py --check    exit 1 when a committed PNG's pixels differ

The sets (spikes/r0/S3/gamedata/ParsekSpikeS3/*.cfg) point their suitTexture,
normalTexture and sprite keys at these files. The textures are NOT the stock atlas
recolored (the stock art is not ours to commit): each suit texture is one flat color
with darker horizontal bands every 64 px and a white 32 x 32 square in the TOP-LEFT
corner, so a screenshot shows at once whether the texture landed on the suit and which
way up it is (stock applies a (1, -1) scale to a GameDatabase suit texture). The
normal map is flat (128, 128, 255); the icons are a filled disc on transparency.

--check compares decoded pixels, not file bytes, so a different zlib build cannot red it.
"""

import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "gamedata", "ParsekSpikeS3")

# set -> (main color, band color). Hues 46 and 146 deg: far apart on purpose.
SETS = {
    "vintage_like": ((0xC9, 0xA2, 0x27), (0x5A, 0x47, 0x10)),
    "future_like": ((0x2E, 0x8B, 0x57), (0x12, 0x3A, 0x22)),
}

SUIT_SIZE = 256
BAND_EVERY = 64
BAND_HEIGHT = 16
MARK_SIZE = 32
NRM_SIZE = 64
ICON_SIZE = 64


def suit_pixels(main, band):
    """RGB rows, top row first."""
    rows = []
    for y in range(SUIT_SIZE):
        row = bytearray()
        for x in range(SUIT_SIZE):
            if x < MARK_SIZE and y < MARK_SIZE:
                c = (255, 255, 255)
            elif y % BAND_EVERY >= BAND_EVERY - BAND_HEIGHT:
                c = band
            else:
                c = main
            row.extend(c)
        rows.append(bytes(row))
    return rows, 3


def flat_pixels(size, color):
    return [bytes(bytearray(color) * size) for _ in range(size)], 3


def disc_pixels(color):
    """RGBA rows: a filled disc of radius 28 centred in 64 x 64, transparent outside."""
    rows = []
    c = (ICON_SIZE - 1) / 2.0
    for y in range(ICON_SIZE):
        row = bytearray()
        for x in range(ICON_SIZE):
            inside = (x - c) ** 2 + (y - c) ** 2 <= 28 * 28
            row.extend(color + (255,) if inside else (0, 0, 0, 0))
        rows.append(bytes(row))
    return rows, 4


def encode_png(rows, channels):
    height = len(rows)
    width = len(rows[0]) // channels
    color_type = 2 if channels == 3 else 6
    raw = b"".join(b"\x00" + r for r in rows)

    def chunk(kind, data):
        body = kind + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)

    ihdr = struct.pack(">IIBBBBB", width, height, 8, color_type, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def decode_png(data):
    """(width, height, channels, rows) of an 8-bit, non-interlaced RGB / RGBA PNG."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("not a PNG")
    pos, idat, ihdr = 8, b"", None
    while pos < len(data):
        (length,) = struct.unpack(">I", data[pos:pos + 4])
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        pos += 12 + length
        if kind == b"IHDR":
            ihdr = struct.unpack(">IIBBBBB", body)
        elif kind == b"IDAT":
            idat += body
        elif kind == b"IEND":
            break
    width, height, depth, color_type, _, _, interlace = ihdr
    if depth != 8 or interlace != 0 or color_type not in (2, 6):
        raise ValueError("unsupported PNG (depth=%d type=%d interlace=%d)" % (depth, color_type, interlace))
    ch = 3 if color_type == 2 else 4
    raw = zlib.decompress(idat)
    stride = width * ch
    rows, prev, i = [], bytearray(stride), 0
    for _ in range(height):
        f = raw[i]
        line = bytearray(raw[i + 1:i + 1 + stride])
        i += 1 + stride
        for x in range(stride):
            a = line[x - ch] if x >= ch else 0
            b = prev[x]
            c = prev[x - ch] if x >= ch else 0
            if f == 1:
                line[x] = (line[x] + a) & 0xFF
            elif f == 2:
                line[x] = (line[x] + b) & 0xFF
            elif f == 3:
                line[x] = (line[x] + ((a + b) >> 1)) & 0xFF
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[x] = (line[x] + pred) & 0xFF
        rows.append(bytes(line))
        prev = line
    return width, height, ch, rows


def images():
    """Relative path -> (rows, channels)."""
    out = {}
    for name, (main, band) in SETS.items():
        out["Textures/%s_suit.png" % name] = suit_pixels(main, band)
        out["Icons/%s_icon.png" % name] = disc_pixels(main)
    out["Textures/suit_flat_NRM.png"] = flat_pixels(NRM_SIZE, (128, 128, 255))
    return out


def main(argv):
    check = "--check" in argv
    bad = 0
    for rel, (rows, ch) in sorted(images().items()):
        path = os.path.join(ROOT, *rel.split("/"))
        if check:
            try:
                with open(path, "rb") as f:
                    _, _, dch, drows = decode_png(f.read())
                same = dch == ch and drows == rows
            except (OSError, ValueError) as ex:
                same = False
                print("%s: %s" % (rel, ex))
            print("%s %s" % ("OK  " if same else "DIFF", rel))
            bad += 0 if same else 1
        else:
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with open(path, "wb") as f:
                f.write(encode_png(rows, ch))
            print("wrote %s" % rel)
    if check:
        print("RESULT: %s" % ("PASS" if bad == 0 else "FAIL (%d differ; rerun without --check)" % bad))
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
