#!/usr/bin/env python3
"""Build assets/icon.ico from assets/icon.svg (+ icon-16.svg for the 16px entry).

No third-party dependency: an installed Chromium browser (Edge or Chrome, both
headless) does the SVG rasterising, and the downscaling, PNG encoding and ICO
packing are done here with the standard library only.

    python tools/build-icon.py

The browser refuses window sizes below ~128px, so each SVG is rendered once at
512px and box-filtered down to every icon size — which also gives cleaner edges
than rasterising the small sizes directly.
"""

import os
import shutil
import struct
import subprocess
import sys
import time
import tempfile
import zlib

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ASSETS = os.path.join(ROOT, "assets")

# Entry sizes of the .ico, and which artwork each one uses. The simplified
# variant only pays off where the full icon's detail would blur together.
SIZES = [(16, "icon-16.svg"), (32, "icon.svg"), (48, "icon.svg"),
         (64, "icon.svg"), (128, "icon.svg"), (256, "icon.svg")]

RENDER_AT = 512

BROWSERS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
]


def find_browser():
    for path in BROWSERS:
        if os.path.exists(path):
            return path
    for name in ("msedge", "chrome", "chromium", "google-chrome"):
        found = shutil.which(name)
        if found:
            return found
    sys.exit("No Chromium-based browser found — install Edge or Chrome, or add one to PATH.")


WRAPPER = ('<!doctype html><meta charset="utf-8">'
           '<style>html,body{{margin:0;padding:0;background:transparent}}'
           'img{{display:block;width:{size}px;height:{size}px}}</style>'
           '<img src="file:///{svg}">')


def rasterise(browser, svg_path, out_png, size):
    """Screenshot the SVG on a transparent background at size x size.

    The SVG goes through a one-line HTML wrapper: screenshotting an .svg file
    directly only works when the window matches the document's intrinsic size,
    and silently produces no file otherwise.
    """
    with tempfile.TemporaryDirectory() as work:
        # A dedicated profile is what makes the run independent: without it the
        # command is handed to an already-running browser, which exits at once
        # and writes nothing.
        profile = os.path.join(work, "profile")
        page = os.path.join(work, "page.html")
        with open(page, "w", encoding="utf-8") as fh:
            fh.write(WRAPPER.format(size=size, svg=svg_path.replace("\\", "/")))
        subprocess.run(
            [browser, "--headless", "--disable-gpu", "--hide-scrollbars",
             "--no-first-run", "--no-default-browser-check",
             "--force-device-scale-factor=1", "--default-background-color=00000000",
             "--allow-file-access-from-files", f"--user-data-dir={profile}",
             f"--window-size={size},{size}",
             f"--screenshot={out_png}", "file:///" + page.replace("\\", "/")],
            check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

        # The launcher process returns before the browser it spawned has written
        # the file, so wait for the size to stop growing instead of trusting the
        # exit code.
        deadline, stable, last = time.time() + 60, 0, -1
        while time.time() < deadline:
            current = os.path.getsize(out_png) if os.path.exists(out_png) else -1
            stable = stable + 1 if current == last and current > 0 else 0
            if stable >= 3:
                return
            last = current
            time.sleep(0.25)
    sys.exit(f"{browser} produced no screenshot for {svg_path}")


# ── Minimal PNG read/write (RGBA, 8-bit, no interlace — what Chromium emits) ──

def png_read(path):
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        sys.exit(f"{path} is not a PNG")
    pos, idat, w = 8, b"", None
    while pos < len(data):
        (length,) = struct.unpack_from(">I", data, pos)
        kind = data[pos + 4:pos + 8]
        body = data[pos + 8:pos + 8 + length]
        if kind == b"IHDR":
            w, h, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            if (depth, color, interlace) != (8, 6, 0):
                sys.exit(f"{path}: expected 8-bit RGBA, non-interlaced")
        elif kind == b"IDAT":
            idat += body
        pos += 12 + length

    raw = zlib.decompress(idat)
    stride = w * 4
    out = bytearray(stride * h)
    prev = bytearray(stride)
    src = 0
    for y in range(h):
        filt = raw[src]; src += 1
        line = bytearray(raw[src:src + stride]); src += stride
        if filt == 1:
            for i in range(4, stride):
                line[i] = (line[i] + line[i - 4]) & 0xFF
        elif filt == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 0xFF
        elif filt == 3:
            for i in range(stride):
                left = line[i - 4] if i >= 4 else 0
                line[i] = (line[i] + ((left + prev[i]) >> 1)) & 0xFF
        elif filt == 4:
            for i in range(stride):
                a = line[i - 4] if i >= 4 else 0
                b = prev[i]
                c = prev[i - 4] if i >= 4 else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pred = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[i] = (line[i] + pred) & 0xFF
        elif filt != 0:
            sys.exit(f"{path}: unknown PNG filter {filt}")
        out[y * stride:(y + 1) * stride] = line
        prev = line
    return w, h, out


def png_write(path, w, h, pixels):
    raw = bytearray()
    stride = w * 4
    for y in range(h):
        raw.append(0)                                   # filter: none
        raw += pixels[y * stride:(y + 1) * stride]

    def chunk(kind, body):
        return (struct.pack(">I", len(body)) + kind + body
                + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF))

    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    blob = (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(bytes(raw), 9)) + chunk(b"IEND", b""))
    open(path, "wb").write(blob)
    return blob


def downscale(w, h, pixels, size):
    """Box filter, on premultiplied alpha so transparent edges stay clean."""
    out = bytearray(size * size * 4)
    for oy in range(size):
        y0, y1 = oy * h // size, (oy + 1) * h // size
        for ox in range(size):
            x0, x1 = ox * w // size, (ox + 1) * w // size
            r = g = b = a = n = 0
            for y in range(y0, y1):
                base = (y * w + x0) * 4
                for _ in range(x1 - x0):
                    pa = pixels[base + 3]
                    r += pixels[base] * pa
                    g += pixels[base + 1] * pa
                    b += pixels[base + 2] * pa
                    a += pa
                    n += 1
                    base += 4
            i = (oy * size + ox) * 4
            if a:
                out[i] = min(255, r // a)
                out[i + 1] = min(255, g // a)
                out[i + 2] = min(255, b // a)
            out[i + 3] = a // n if n else 0
    return out


def main():
    browser = find_browser()
    print(f"rasterising with {os.path.basename(browser)}")

    with tempfile.TemporaryDirectory() as tmp:
        rendered = {}
        for svg in sorted({name for _, name in SIZES}):
            png = os.path.join(tmp, svg + ".png")
            rasterise(browser, os.path.join(ASSETS, svg), png, RENDER_AT)
            rendered[svg] = png_read(png)

        entries = []
        for size, svg in SIZES:
            w, h, pixels = rendered[svg]
            scaled = pixels if size == w else downscale(w, h, pixels, size)
            blob = png_write(os.path.join(tmp, f"{size}.png"), size, size, scaled)
            entries.append((size, blob))
            print(f"  {size:>3}px  {len(blob):>6} bytes  ({svg})")

        header = struct.pack("<HHH", 0, 1, len(entries))
        offset = len(header) + 16 * len(entries)
        directory, payload = b"", b""
        for size, blob in entries:
            directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0,
                                     1, 32, len(blob), offset)
            offset += len(blob)
            payload += blob

        ico = os.path.join(ASSETS, "icon.ico")
        open(ico, "wb").write(header + directory + payload)
        print(f"wrote {os.path.relpath(ico, ROOT)} ({len(header + directory + payload)} bytes)")


if __name__ == "__main__":
    main()
