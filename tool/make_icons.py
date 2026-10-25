#!/usr/bin/env python3
"""Regenerate every copy of the app icon from the two SVG masters.

    python tool/make_icons.py           write the assets
    python tool/make_icons.py --check   fail if any committed asset is stale

The masters are assets/logo/UrDatabase.svg, for everything 48px and up, and
assets/logo/UrDatabase-small.svg, redrawn on a pixel grid for the sizes below
that. From them this writes the Windows .ico, the macOS .icns, the Microsoft
Store PNGs and the downloads site's inline favicon, so all four always show the
same picture. They used to be made by hand, one from the other, and the Store
PNGs only on a Mac.

Rendering needs resvg (`pip install -r tool/requirements-icons.txt`); it is a
self-contained wheel on every platform, so this runs the same on Windows, macOS
and Linux. Nothing else here imports it: packing the .ico and .icns, and the
favicon, are plain functions that the unit tests exercise without a renderer.
"""

import argparse
from pathlib import Path
import re
import struct
import sys
from urllib.parse import quote

from make_store_package import ASSETS as STORE_ASSETS

ROOT = Path(__file__).resolve().parent.parent
LOGO = Path("assets/logo")
LARGE_SVG = LOGO / "UrDatabase.svg"
SMALL_SVG = LOGO / "UrDatabase-small.svg"
# Below this the large master's stripes and rules fall between pixels.
SMALL_BELOW = 48

ICO = Path("src/UrDatabase.App/Assets/UrDatabase.ico")
ICNS = Path("src/UrDatabase.App/Assets/UrDatabase.icns")
STORE_DIR = Path("packaging/windows/Assets")
SITE = Path("web/downloads/index.html")

ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
# Every PNG-capable slot macOS 11+ reads. The @2x slots carry the pixel size,
# not the point size: ic11 is 16pt at 2x, so it is a 32px image.
ICNS_SLOTS = (
    (b"icp4", 16), (b"icp5", 32), (b"ic11", 32), (b"ic12", 64), (b"ic07", 128),
    (b"ic08", 256), (b"ic13", 256), (b"ic09", 512), (b"ic14", 512), (b"ic10", 1024),
)

PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
FAVICON = re.compile(r'<link rel="icon" href="data:image/svg\+xml,[^"]*">')


def master_for(size):
    return SMALL_SVG if size < SMALL_BELOW else LARGE_SVG


def png_size(png):
    """Width and height of an 8-bit RGBA PNG, refusing anything else."""
    if png[:8] != PNG_SIGNATURE or png[12:16] != b"IHDR":
        raise ValueError("not a PNG")
    width, height, depth, colour = struct.unpack(">IIBB", png[16:26])
    if (depth, colour) != (8, 6):
        raise ValueError(f"PNG is depth {depth} colour type {colour}, not 8-bit RGBA")
    return width, height


def _square(png, size):
    if png_size(png) != (size, size):
        raise ValueError(f"expected a {size}px PNG, got {png_size(png)}")


def pack_ico(frames):
    """An .ico of PNG frames, given (size, png) pairs, smallest first."""
    header = struct.pack("<HHH", 0, 1, len(frames))
    offset = len(header) + 16 * len(frames)
    entries, data = [], []
    for size, png in frames:
        _square(png, size)
        side = 0 if size == 256 else size
        entries.append(struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(png), offset))
        data.append(png)
        offset += len(png)
    return header + b"".join(entries) + b"".join(data)


def pack_icns(chunks):
    """An .icns of (type, png) chunks, in the order given."""
    body = b"".join(kind + struct.pack(">I", 8 + len(png)) + png for kind, png in chunks)
    return b"icns" + struct.pack(">I", 8 + len(body)) + body


def favicon_href(svg):
    """The small master as a data URI: comments and layout stripped, quotes
    made single so it sits inside a double-quoted attribute."""
    text = re.sub(r"<\?xml.*?\?>|<!--.*?-->", "", svg, flags=re.S)
    text = re.sub(r"\s+", " ", text).replace("> <", "><").strip().replace('"', "'")
    return "data:image/svg+xml," + quote(text, safe=" /=:;,'()-.")


def with_favicon(html, svg):
    link = f'<link rel="icon" href="{favicon_href(svg)}">'
    updated, count = FAVICON.subn(lambda _: link, html)
    if count != 1:
        raise ValueError(f"expected one inline favicon in {SITE}, found {count}")
    return updated


def build(render, root=ROOT):
    """Every generated file, as {relative path: bytes}.

    `render(svg_text, size)` returns a size x size RGBA PNG.
    """
    masters = {path: (root / path).read_text(encoding="utf-8") for path in (LARGE_SVG, SMALL_SVG)}
    cache = {}

    def png(size):
        if size not in cache:
            cache[size] = render(masters[master_for(size)], size)
            _square(cache[size], size)
        return cache[size]

    outputs = {
        ICO: pack_ico([(size, png(size)) for size in ICO_SIZES]),
        ICNS: pack_icns([(kind, png(size)) for kind, size in ICNS_SLOTS]),
    }
    for name, size in STORE_ASSETS.items():
        outputs[STORE_DIR / name] = png(size)
    # Bytes, not read_text: universal newlines would turn a CRLF checkout's
    # page into LF and report it stale on every Windows clone.
    html = (root / SITE).read_bytes().decode("utf-8")
    outputs[SITE] = with_favicon(html, masters[SMALL_SVG]).encode("utf-8")
    return outputs


def resvg_render(svg, size):
    try:
        import resvg_py
    except ImportError:
        sys.exit("resvg is not installed: pip install -r tool/requirements-icons.txt")
    return bytes(resvg_py.svg_to_bytes(svg_string=svg, width=size, height=size))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--check", action="store_true",
                        help="report stale assets instead of writing them")
    args = parser.parse_args(argv)

    stale = []
    for path, data in build(resvg_render).items():
        target = ROOT / path
        if target.is_file() and target.read_bytes() == data:
            continue
        stale.append(path.as_posix())
        if not args.check:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)

    if args.check and stale:
        print("Out of date with assets/logo; run python tool/make_icons.py:", file=sys.stderr)
        for path in stale:
            print(f"  {path}", file=sys.stderr)
        return 1
    print("wrote " + ", ".join(stale) if stale else "every icon asset is up to date")
    return 0


if __name__ == "__main__":
    sys.exit(main())
