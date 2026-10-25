import struct
import tempfile
import unittest
import zlib
from pathlib import Path

from make_icons import (
    ICNS, ICNS_SLOTS, ICO, ICO_SIZES, LARGE_SVG, ROOT, SITE, SMALL_SVG, STORE_ASSETS, STORE_DIR,
    build, favicon_href, master_for, pack_icns, pack_ico, png_size, with_favicon,
)

try:
    import resvg_py  # noqa: F401
except ImportError:
    resvg_py = None


def png(size, colour_type=6):
    """A real, minimal PNG: one transparent row repeated."""
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))
    header = struct.pack(">IIBBBBB", size, size, 8, colour_type, 0, 0, 0)
    pixels = zlib.compress((b"\0" + b"\0" * 4 * size) * size)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", pixels) + chunk(b"IEND", b"")


def read_ico(data):
    reserved, kind, count = struct.unpack_from("<HHH", data)
    frames = []
    for i in range(count):
        w, h, colours, zero, planes, bpp, length, offset = struct.unpack_from("<BBBBHHII", data, 6 + 16 * i)
        frames.append((w or 256, h or 256, colours, zero, planes, bpp, data[offset:offset + length], offset))
    return reserved, kind, frames


def read_icns(data):
    assert data[:4] == b"icns"
    assert struct.unpack(">I", data[4:8])[0] == len(data)
    chunks, i = [], 8
    while i < len(data):
        kind, length = data[i:i + 4], struct.unpack(">I", data[i + 4:i + 8])[0]
        chunks.append((kind, data[i + 8:i + length]))
        i += length
    assert i == len(data)
    return chunks


class PackingTests(unittest.TestCase):
    def test_ico_frames_are_32_bit_pngs_laid_end_to_end(self):
        frames = [(size, png(size)) for size in ICO_SIZES]
        data = pack_ico(frames)
        reserved, kind, parsed = read_ico(data)
        self.assertEqual((reserved, kind), (0, 1))
        offset = 6 + 16 * len(frames)
        for (size, image), (w, h, colours, zero, planes, bpp, body, at) in zip(frames, parsed):
            self.assertEqual((w, h, colours, zero, planes, bpp), (size, size, 0, 0, 1, 32))
            self.assertEqual(body, image)
            self.assertEqual(at, offset)
            offset += len(image)
        self.assertEqual(offset, len(data))

    def test_ico_refuses_a_frame_of_the_wrong_size(self):
        with self.assertRaises(ValueError):
            pack_ico([(16, png(32))])

    def test_icns_chunks_carry_their_own_length(self):
        chunks = [(kind, png(size)) for kind, size in ICNS_SLOTS]
        self.assertEqual(read_icns(pack_icns(chunks)), chunks)

    def test_only_8_bit_rgba_is_accepted(self):
        self.assertEqual(png_size(png(24)), (24, 24))
        with self.assertRaises(ValueError):
            png_size(png(24, colour_type=2))
        with self.assertRaises(ValueError):
            png_size(b"GIF89a" + bytes(30))

    def test_icns_slots_cover_every_point_size_at_1x_and_2x(self):
        sizes = dict(ICNS_SLOTS)
        for one_x, two_x, points in ((b"icp4", b"ic11", 16), (b"icp5", b"ic12", 32),
                                     (b"ic07", b"ic13", 128), (b"ic08", b"ic14", 256),
                                     (b"ic09", b"ic10", 512)):
            self.assertEqual((sizes[one_x], sizes[two_x]), (points, points * 2))


class FaviconTests(unittest.TestCase):
    SVG = '<?xml version="1.0"?>\n<!-- note -->\n<svg viewBox="0 0 32 32">\n  <rect fill="#A83A24"/>\n</svg>\n'

    def test_href_is_a_compact_attribute_safe_data_uri(self):
        href = favicon_href(self.SVG)
        self.assertEqual(href, "data:image/svg+xml,%3Csvg viewBox='0 0 32 32'%3E%3Crect fill='%23A83A24'/%3E%3C/svg%3E")

    def test_exactly_one_favicon_is_replaced(self):
        old = '<head><link rel="icon" href="data:image/svg+xml,old"></head>'
        self.assertIn(favicon_href(self.SVG), with_favicon(old, self.SVG))
        for html in ("<head></head>", old + old):
            with self.assertRaises(ValueError):
                with_favicon(html, self.SVG)

    def test_the_site_favicon_is_the_small_master(self):
        html = (ROOT / SITE).read_text(encoding="utf-8")
        svg = (ROOT / SMALL_SVG).read_text(encoding="utf-8")
        self.assertIn(f'<link rel="icon" href="{favicon_href(svg)}">', html)


class BuildTests(unittest.TestCase):
    def setUp(self):
        self.calls = []

        def render(svg, size):
            self.calls.append((svg, size))
            return png(size)

        self.outputs = build(render)

    def test_small_sizes_come_from_the_pixel_grid_master(self):
        large = (ROOT / LARGE_SVG).read_text(encoding="utf-8")
        small = (ROOT / SMALL_SVG).read_text(encoding="utf-8")
        for svg, size in self.calls:
            self.assertEqual(svg, small if size < 48 else large, f"{size}px")
        self.assertEqual(master_for(32), SMALL_SVG)
        self.assertEqual(master_for(48), LARGE_SVG)

    def test_every_icon_destination_is_written(self):
        expected = {ICO, ICNS, SITE} | {STORE_DIR / name for name in STORE_ASSETS}
        self.assertEqual(set(self.outputs), expected)
        for name, size in STORE_ASSETS.items():
            self.assertEqual(png_size(self.outputs[STORE_DIR / name]), (size, size), name)

    def test_each_size_is_rendered_once(self):
        sizes = [size for _, size in self.calls]
        self.assertEqual(len(sizes), len(set(sizes)))

    def test_a_render_of_the_wrong_size_is_refused(self):
        with self.assertRaises(ValueError):
            build(lambda svg, size: png(size + 1))

    def test_build_reads_masters_from_the_root_it_is_given(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for path in (LARGE_SVG, SMALL_SVG, SITE):
                (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / LARGE_SVG).write_text("large", encoding="utf-8")
            (root / SMALL_SVG).write_text("<svg/>", encoding="utf-8")
            (root / SITE).write_text('<link rel="icon" href="data:image/svg+xml,x">', encoding="utf-8")
            seen = set()
            build(lambda svg, size: seen.add(svg) or png(size), root=root)
            self.assertEqual(seen, {"large", "<svg/>"})

    def test_the_page_keeps_its_line_endings(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for path in (LARGE_SVG, SMALL_SVG, SITE):
                (root / path).parent.mkdir(parents=True, exist_ok=True)
            (root / LARGE_SVG).write_text("<svg/>", encoding="utf-8")
            (root / SMALL_SVG).write_text("<svg/>", encoding="utf-8")
            (root / SITE).write_bytes(b'<head>\r\n<link rel="icon" href="data:image/svg+xml,x">\r\n</head>\r\n')
            page = build(lambda svg, size: png(size), root=root)[SITE]
            self.assertEqual(page.count(b"\r\n"), 3)


class CommittedAssetTests(unittest.TestCase):
    def test_committed_ico_has_every_frame(self):
        _, _, frames = read_ico((ROOT / ICO).read_bytes())
        self.assertEqual([frame[0] for frame in frames], list(ICO_SIZES))
        for w, h, *_, body, _ in frames:
            self.assertEqual(png_size(body), (w, h))

    def test_committed_icns_has_every_slot(self):
        chunks = read_icns((ROOT / ICNS).read_bytes())
        self.assertEqual([kind for kind, _ in chunks], [kind for kind, _ in ICNS_SLOTS])
        for (kind, body), (_, size) in zip(chunks, ICNS_SLOTS):
            self.assertEqual(png_size(body), (size, size), kind)

    @unittest.skipIf(resvg_py is None, "resvg is not installed (tool/requirements-icons.txt)")
    def test_committed_assets_match_a_fresh_render(self):
        from make_icons import resvg_render
        for path, data in build(resvg_render).items():
            self.assertEqual((ROOT / path).read_bytes(), data,
                             f"{path.as_posix()} is stale; run python tool/make_icons.py")


if __name__ == "__main__":
    unittest.main()
