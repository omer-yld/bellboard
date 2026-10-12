"""Builds src/Bellboard.App/bellboard.ico from the SVGs in this folder.

Runs from WSL: Edge (headless, on the Windows side) rasterizes each size, and Pillow packs the
frames as PNGs into the .ico. Small sizes use their own drawings (bellboard-16.svg, bellboard-24.svg)
instead of a downscale, so the bell stays crisp in the taskbar and tray.

    python3 -m venv /tmp/venv && /tmp/venv/bin/pip install pillow
    /tmp/venv/bin/python art/make-icon.py
"""
import io
import os
import shutil
import struct
import subprocess
import sys
import tempfile

from PIL import Image

ART = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(ART)
EDGE = "/mnt/c/Program Files (x86)/Microsoft/Edge/Application/msedge.exe"
WORK = "/mnt/c/Users/Public/bellboard-icon"  # Edge needs a Windows path
WORK_WIN = r"C:\Users\Public\bellboard-icon"

# size -> drawing
FRAMES = {16: "bellboard-16.svg", 20: "bellboard-16.svg", 24: "bellboard-24.svg", 32: "bellboard-24.svg",
          40: "bellboard.svg", 48: "bellboard.svg", 64: "bellboard.svg", 128: "bellboard.svg", 256: "bellboard.svg"}


def render():
    os.makedirs(WORK, exist_ok=True)
    for name in set(FRAMES.values()):
        shutil.copy(os.path.join(ART, name), os.path.join(WORK, name))
    x, cells = 0, []
    for size, name in FRAMES.items():
        cells.append((size, x))
    imgs = "".join(f'<img src="{FRAMES[s]}" width="{s}" height="{s}" style="position:absolute;left:{x}px;top:0">' for s, x in
                   [(s, sum(t + 8 for t in list(FRAMES)[:i])) for i, s in enumerate(FRAMES)])
    with open(os.path.join(WORK, "sheet.html"), "w") as f:
        f.write(f"<!doctype html><style>html,body{{margin:0;background:transparent}}</style>{imgs}")
    width = sum(s + 8 for s in FRAMES)
    subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                    "--default-background-color=00000000", f"--window-size={width},300",
                    f"--screenshot={WORK_WIN}\\sheet.png", f"file:///{WORK_WIN.replace(chr(92), '/')}/sheet.html"],
                   check=True, capture_output=True)
    sheet = Image.open(os.path.join(WORK, "sheet.png")).convert("RGBA")
    frames, x = {}, 0
    for size in FRAMES:
        frames[size] = sheet.crop((x, 0, x + size, size))
        x += size + 8
    return frames


def pack(frames, path):
    pngs = []
    for size, img in frames.items():
        buf = io.BytesIO()
        img.save(buf, "PNG", optimize=True)
        pngs.append((size, buf.getvalue()))
    out = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    for size, data in pngs:
        out += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    out += b"".join(d for _, d in pngs)
    with open(path, "wb") as f:
        f.write(out)
    return len(out)


if __name__ == "__main__":
    frames = render()
    ico = os.path.join(REPO, "src", "Bellboard.App", "bellboard.ico")
    print("wrote", ico, pack(frames, ico), "bytes")
    if len(sys.argv) > 1:  # optional: dump the frames for a look
        for size, img in frames.items():
            img.save(os.path.join(sys.argv[1], f"icon-{size}.png"))
