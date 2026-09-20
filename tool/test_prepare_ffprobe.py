import copy
import hashlib
import io
import json
import os
from pathlib import Path
import stat
import struct
import subprocess
import tarfile
import tempfile
import unittest
from unittest.mock import patch
import zipfile

from prepare_ffprobe import (
    HttpsRedirects, ROOT, expected_files, fetch_archive, prepare, read_bundle,
    smoke_test, verify_bundle,
)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def executable(rid):
    if rid == "win-x64":
        data = bytearray(256)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, 128)
        data[128:132] = b"PE\0\0"
        struct.pack_into("<H", data, 132, 0x8664)
        return bytes(data)
    return struct.pack(">II", 0xCAFEBABE, 2) + b"".join(
        struct.pack(">IIIII", cpu, 0, 48 + i * 32, 32, 0)
        for i, cpu in enumerate((0x01000007, 0x0100000C))
    ) + b"".join(struct.pack("<IIIIIIII", 0xFEEDFACF, cpu, 0, 2, 0, 0, 0, 0)
                 for cpu in (0x01000007, 0x0100000C))


def seed_fixture(root, rid="win-x64", extra=None):
    cache = root / "cache"
    cache.mkdir(exist_ok=True)
    exe = "ffprobe.exe" if rid == "win-x64" else "ffprobe"
    binary = executable(rid)
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w") as archive:
        archive.writestr(exe, binary)
        archive.writestr("ffmpeg.exe", b"must never ship")
        archive.writestr("COPYING", b"fixture license")
        if extra:
            archive.writestr(*extra)
    archive = buffer.getvalue()
    (cache / digest(archive)).write_bytes(archive)
    sources = b"opaque matching source archive"
    (cache / digest(sources)).write_bytes(sources)
    bundle = {
        "version": "test", "license": "LGPL-2.1-or-later", "executable": exe,
        "archives": [
            {"url": "https://example.invalid/binary.zip", "sha256": digest(archive),
             "size": len(archive), "members": {
                 exe: [exe, digest(binary)],
                 "COPYING": ["licenses/COPYING", digest(b"fixture license")]}},
            {"url": "https://example.invalid/source.tar.xz", "sha256": digest(sources),
             "size": len(sources), "save_as": "sources/source.tar.xz"},
        ],
    }
    return bundle, cache


class PrepareTests(unittest.TestCase):
    def setUp(self):
        scratch = ROOT / "artifacts/tool-tests"
        scratch.mkdir(parents=True, exist_ok=True)
        self.temp = tempfile.TemporaryDirectory(dir=scratch)
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bundle, self.cache = seed_fixture(self.root)
        self.output = self.root / "prepared"

    def prepare(self, bundle=None, rid="win-x64"):
        with patch("prepare_ffprobe.open_https", side_effect=AssertionError("network forbidden")):
            return prepare(rid, self.output, self.cache, bundle=bundle or self.bundle)

    def test_extracts_only_probe_notices_and_matching_sources_offline(self):
        path = self.prepare()
        self.assertEqual(self.output / "win-x64", path)
        self.assertEqual(executable("win-x64"), (path / "ffprobe.exe").read_bytes())
        self.assertTrue((path / "sources/source.tar.xz").is_file())
        self.assertTrue((path / "NOTICE.txt").is_file())
        self.assertFalse((path / "ffmpeg.exe").exists())
        verify_bundle(path, "win-x64", bundle=self.bundle)

    def test_rejects_corrupt_cached_archive_before_writing_output(self):
        archive = self.cache / self.bundle["archives"][0]["sha256"]
        archive.write_bytes(b"corrupt")
        with self.assertRaisesRegex(ValueError, "SHA-256|size"):
            self.prepare()
        self.assertFalse((self.output / "win-x64").exists())

    def test_failed_replacement_preserves_previous_bundle(self):
        path = self.prepare()
        before = (path / "ffprobe.exe").read_bytes()
        bad = copy.deepcopy(self.bundle)
        bad["archives"][0]["members"]["ffprobe.exe"][1] = "0" * 64
        with self.assertRaisesRegex(ValueError, "SHA-256"):
            self.prepare(bad)
        self.assertEqual(before, (path / "ffprobe.exe").read_bytes())
        self.assertEqual(["win-x64"], [p.name for p in self.output.iterdir()])

    def test_successful_replacement_drops_stale_files(self):
        path = self.prepare()
        (path / "stale.exe").write_bytes(b"old")
        self.prepare()
        self.assertFalse((path / "stale.exe").exists())

    def test_rejects_traversal_absolute_windows_paths_and_links_even_if_not_selected(self):
        names = ("../escape", "/absolute", "C:/drive", "a\\b", "a/../b", "a/./b", "a//b", "a:stream")
        for name in names:
            with self.subTest(name=name):
                bundle, _ = seed_fixture(self.root, extra=(name, b"bad"))
                with self.assertRaisesRegex(ValueError, "Unsafe"):
                    self.prepare(bundle)
        info = zipfile.ZipInfo("link")
        info.create_system = 3
        info.external_attr = (stat.S_IFLNK | 0o777) << 16
        bundle, _ = seed_fixture(self.root, extra=(info, b"ffprobe.exe"))
        with self.assertRaisesRegex(ValueError, "link|regular"):
            self.prepare(bundle)
        self.assertFalse((self.root / "escape").exists())

    def test_rejects_missing_members_wrong_architecture_and_duplicate_destination(self):
        for change in ("missing", "architecture", "destination"):
            bad = copy.deepcopy(self.bundle)
            members = bad["archives"][0]["members"]
            if change == "missing":
                members["missing"] = ["missing", digest(b"")]
            elif change == "architecture":
                bad["executable"] = "licenses/COPYING"
            else:
                members["COPYING"][0] = "ffprobe.exe"
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.prepare(bad)

    def test_does_not_follow_existing_output_or_cache_symlinks(self):
        external = self.root / "external"
        external.mkdir()
        self.output.mkdir()
        (self.output / "win-x64").symlink_to(external, target_is_directory=True)
        with self.assertRaises(ValueError):
            self.prepare()
        (self.output / "win-x64").unlink()
        entry = self.cache / self.bundle["archives"][0]["sha256"]
        data = entry.read_bytes()
        entry.unlink()
        outside = external / "archive"
        outside.write_bytes(data)
        entry.symlink_to(outside)
        with self.assertRaises(ValueError):
            self.prepare()
        self.assertEqual(data, outside.read_bytes())

    def test_tar_extraction_keeps_the_mac_helper_executable(self):
        bundle, cache = seed_fixture(self.root, "osx-arm64")
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w:gz") as tar:
            for name, data in (("ffprobe", executable("osx-arm64")), ("COPYING", b"fixture license")):
                info = tarfile.TarInfo(name)
                info.size = len(data)
                info.mode = 0o644
                tar.addfile(info, io.BytesIO(data))
        data = archive.getvalue()
        bundle["archives"][0].update(sha256=digest(data), size=len(data))
        (cache / digest(data)).write_bytes(data)
        path = self.prepare(bundle, "osx-arm64")
        if os.name != "nt":
            self.assertTrue(os.access(path / "ffprobe", os.X_OK))
        verify_bundle(path, "osx-x64", bundle=bundle)

    def test_tar_links_are_rejected(self):
        archive = io.BytesIO()
        with tarfile.open(fileobj=archive, mode="w:gz") as tar:
            info = tarfile.TarInfo("ffprobe.exe")
            info.type = tarfile.SYMTYPE
            info.linkname = "../outside"
            tar.addfile(info)
        data = archive.getvalue()
        self.bundle["archives"][0].update(sha256=digest(data), size=len(data))
        (self.cache / digest(data)).write_bytes(data)
        with self.assertRaisesRegex(ValueError, "link|regular"):
            self.prepare()

    def test_verification_refuses_missing_sources_changed_probe_extra_files_or_symlinks(self):
        path = self.prepare()
        for name in ("sources/source.tar.xz", "ffprobe.exe", "NOTICE.txt"):
            file = path / name
            before = file.read_bytes()
            file.unlink()
            with self.subTest(name=name), self.assertRaises(ValueError):
                verify_bundle(path, "win-x64", bundle=self.bundle)
            file.write_bytes(before)
        (path / "ffprobe.exe").write_bytes(b"tampered")
        with self.assertRaises(ValueError):
            verify_bundle(path, "win-x64", bundle=self.bundle)
        self.prepare()
        (path / "extra").write_bytes(b"not shipped")
        with self.assertRaises(ValueError):
            verify_bundle(path, "win-x64", bundle=self.bundle)

    def test_download_is_https_only_size_bounded_and_verified_before_caching(self):
        data = b"download"
        descriptor = {"url": "https://example.invalid/pin", "sha256": digest(data), "size": len(data)}
        with patch("prepare_ffprobe.open_https", return_value=io.BytesIO(data)):
            path = fetch_archive(descriptor, self.cache)
        self.assertEqual(data, path.read_bytes())
        path.unlink()
        for response in (b"wrong!!!", b"short", data + b"oversized"):
            with self.subTest(response=response):
                with patch("prepare_ffprobe.open_https", return_value=io.BytesIO(response)):
                    with self.assertRaises(ValueError):
                        fetch_archive(descriptor, self.cache)
                self.assertFalse(path.exists())
                self.assertFalse(list(self.cache.glob("*.part")))
        descriptor["url"] = "http://example.invalid/pin"
        with self.assertRaisesRegex(ValueError, "HTTPS"):
            fetch_archive(descriptor, self.cache)

    def test_real_manifest_covers_every_runtime_without_mutable_latest_urls(self):
        for rid in ("osx-x64", "osx-arm64", "win-x64"):
            bundle = read_bundle(rid)
            files = expected_files(bundle)
            self.assertIn(bundle["executable"], files)
            self.assertTrue(any(name.startswith("sources/ffmpeg-") for name in files))
            for archive in bundle["archives"]:
                self.assertTrue(archive["url"].startswith("https://"))
                self.assertNotIn("/latest/", archive["url"])
                self.assertEqual(64, len(archive["sha256"]))
        with self.assertRaisesRegex(ValueError, "Unsupported"):
            read_bundle("linux-x64")

    def test_https_redirects_cannot_downgrade_to_plain_http(self):
        with self.assertRaisesRegex(ValueError, "HTTPS"):
            HttpsRedirects().redirect_request(None, None, 302, "", {}, "http://example.invalid/probe")

    def test_native_smoke_uses_runtime_flags_and_cleans_its_synthetic_file(self):
        response = json.dumps({"streams": [{"codec_type": "audio", "channels": 1}],
                               "format": {"format_name": "wav"}})
        with patch("prepare_ffprobe.subprocess.run",
                   return_value=subprocess.CompletedProcess([], 0, response, "")) as runner:
            smoke_test(self.root / "ffprobe.exe", self.root / "smoke")
        args = runner.call_args.args[0]
        self.assertEqual("file", args[args.index("-protocol_whitelist") + 1])
        self.assertIn("stream_side_data=side_data_type,dv_profile", args[args.index("-show_entries") + 1])
        self.assertTrue(Path(args[-1]).is_absolute())
        self.assertFalse(list((self.root / "smoke").iterdir()))
        with patch("prepare_ffprobe.subprocess.run", side_effect=subprocess.TimeoutExpired([], 20)):
            with self.assertRaises(subprocess.TimeoutExpired):
                smoke_test(self.root / "ffprobe.exe", self.root / "smoke")
        self.assertFalse(list((self.root / "smoke").iterdir()))

    def test_release_store_and_msbuild_require_the_prepared_bundle_without_implicit_downloads(self):
        action = (ROOT / ".github/actions/package-app/action.yml").read_text()
        store = (ROOT / "scripts/package-store.ps1").read_text()
        project = (ROOT / "src/UrDatabase.App/UrDatabase.App.csproj").read_text()
        self.assertLess(action.index("tool/prepare_ffprobe.py"), action.index('dotnet publish "$PROJECT"'))
        self.assertLess(store.index("tool/prepare_ffprobe.py"), store.index("Invoke-Checked dotnet"))
        self.assertIn("RequireBundledFfprobe=true", action)
        self.assertIn("RequireBundledFfprobe=true", store)
        self.assertIn('"--smoke-test"', store)
        self.assertIn("tools/ffprobe/", project)
        self.assertIn("CopyToPublishDirectory", project)
        self.assertNotIn("<Exec", project)


if __name__ == "__main__":
    unittest.main()
