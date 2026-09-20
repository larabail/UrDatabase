#!/usr/bin/env python3
"""Explicit, checksum-locked ffprobe preparation; ordinary .NET builds never download it."""

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import struct
import subprocess
import sys
import tarfile
import urllib.request
import uuid
import wave
import zipfile

ROOT = Path(__file__).resolve().parent.parent
NOTICES = ROOT / "packaging/ffprobe"
MAX_MEMBER_SIZE = 128 * 1024 * 1024


def safe_name(name):
    parts = name.rstrip("/").split("/")
    if (not name or name.startswith("/") or "\\" in name or ":" in name or "\0" in name
            or any(part in ("", ".", "..") for part in parts)):
        raise ValueError(f"Unsafe archive or payload path: {name!r}")
    return PurePosixPath(*parts).as_posix()


def is_link(path):
    return path.is_symlink() or (hasattr(path, "is_junction") and path.is_junction())


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        while block := handle.read(1024 * 1024):
            digest.update(block)
    return digest.hexdigest()


def check_hash(path, expected, size=None):
    if is_link(path) or not path.is_file():
        raise ValueError(f"Missing regular ffprobe payload file: {path}")
    if size is not None and path.stat().st_size != size:
        raise ValueError(f"Unexpected size: {path}")
    if sha256(path) != expected:
        raise ValueError(f"SHA-256 mismatch: {path}")


def read_bundle(rid):
    manifest = json.loads((NOTICES / "manifest.json").read_text(encoding="utf-8"))
    if manifest["schema"] != 1 or rid not in manifest["runtimes"]:
        raise ValueError(f"Unsupported ffprobe runtime: {rid}")
    return manifest["bundles"][manifest["runtimes"][rid]]


def bundle_bytes(bundle):
    return (json.dumps(bundle, indent=2, sort_keys=True) + "\n").encode("utf-8")


def expected_files(bundle):
    files = {}
    folded = set()

    def add(name, digest):
        name = safe_name(name)
        if name.casefold() in folded or not re.fullmatch(r"[a-f0-9]{64}", digest):
            raise ValueError(f"Duplicate destination or invalid SHA-256: {name}")
        folded.add(name.casefold())
        files[name] = digest

    for archive in bundle["archives"]:
        if not archive["url"].startswith("https://") or archive["size"] <= 0:
            raise ValueError("Downloads require HTTPS and a pinned size.")
        if not re.fullmatch(r"[a-f0-9]{64}", archive["sha256"]):
            raise ValueError("Invalid archive SHA-256.")
        if "save_as" in archive:
            add(archive["save_as"], archive["sha256"])
        for member, (name, digest) in archive.get("members", {}).items():
            safe_name(member)
            add(name, digest)
    for name in ("NOTICE.txt", "BUILDING.txt"):
        add(name, sha256(NOTICES / name))
    add("bundle.json", hashlib.sha256(bundle_bytes(bundle)).hexdigest())
    if bundle["executable"] not in files:
        raise ValueError("Manifest has no ffprobe executable.")
    return files


class HttpsRedirects(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, msg, headers, newurl):
        if not newurl.startswith("https://"):
            raise ValueError("Refusing a non-HTTPS download redirect.")
        return super().redirect_request(request, fp, code, msg, headers, newurl)


def open_https(url):
    return urllib.request.build_opener(HttpsRedirects()).open(url, timeout=120)


def fetch_archive(archive, cache):
    if (not archive["url"].startswith("https://")
            or not re.fullmatch(r"[a-f0-9]{64}", archive["sha256"])):
        raise ValueError("Downloads require HTTPS and a pinned SHA-256.")
    if is_link(cache):
        raise ValueError("Archive cache must not be a link.")
    cache.mkdir(parents=True, exist_ok=True)
    target = cache / archive["sha256"]
    if target.exists() or is_link(target):
        check_hash(target, archive["sha256"], archive["size"])
        return target
    partial = cache / f"{uuid.uuid4().hex}.part"
    try:
        print(f"Downloading {archive['url']}", flush=True)
        with open_https(archive["url"]) as response, partial.open("xb") as output:
            remaining = archive["size"]
            while block := response.read(min(1024 * 1024, remaining + 1)):
                remaining -= len(block)
                if remaining < 0:
                    raise ValueError("Download exceeds its pinned size.")
                output.write(block)
        check_hash(partial, archive["sha256"], archive["size"])
        partial.replace(target)
    finally:
        partial.unlink(missing_ok=True)
    return target


def extract_selected(path, members, output):
    """Never extract archive paths: validate the whole index, then copy named regular files."""
    is_zip = zipfile.is_zipfile(path)
    with (zipfile.ZipFile(path) if is_zip else tarfile.open(path)) as archive:
        entries = archive.infolist() if is_zip else archive.getmembers()
        index = {}
        folded = set()
        for entry in entries:
            # ZipInfo.filename has already normalized Windows separators and truncated NULs.
            name = safe_name(entry.orig_filename if is_zip else entry.name)
            if name.casefold() in folded:
                raise ValueError(f"Duplicate archive path: {name}")
            folded.add(name.casefold())
            directory = entry.is_dir() if is_zip else entry.isdir()
            mode = entry.external_attr >> 16 if is_zip else entry.mode
            if is_zip:
                regular = stat.S_IFMT(mode) in (0, stat.S_IFREG)
            else:
                regular = entry.isreg()
            if not directory and not regular:
                raise ValueError(f"Archive links and non-regular files are forbidden: {name}")
            if not directory:
                index[name] = entry
        for member, (name, digest) in members.items():
            entry = index.get(safe_name(member))
            if entry is None:
                raise ValueError(f"Missing archive member: {member}")
            size = entry.file_size if is_zip else entry.size
            if size > MAX_MEMBER_SIZE:
                raise ValueError(f"Oversized archive member: {member}")
            target = output / safe_name(name)
            target.parent.mkdir(parents=True, exist_ok=True)
            with (archive.open(entry) if is_zip else archive.extractfile(entry)) as source, target.open("xb") as dest:
                shutil.copyfileobj(source, dest)
            check_hash(target, digest, size)


def validate_executable(path, rid):
    with path.open("rb") as handle:
        header = handle.read(4096)
    if rid == "win-x64":
        offset = struct.unpack_from("<I", header, 60)[0] if len(header) >= 64 else len(header)
        if (header[:2] == b"MZ" and offset + 6 <= len(header)
                and header[offset:offset + 4] == b"PE\0\0"
                and struct.unpack_from("<H", header, offset + 4)[0] == 0x8664):
            return
    elif rid in ("osx-arm64", "osx-x64") and header[:4] == b"\xca\xfe\xba\xbe":
        count = struct.unpack_from(">I", header, 4)[0]
        if count <= 16 and len(header) >= 8 + count * 20:
            cpus = {struct.unpack_from(">I", header, 8 + i * 20)[0] for i in range(count)}
            if {0x01000007, 0x0100000C}.issubset(cpus):
                return
    raise ValueError(f"ffprobe is not the expected {rid} executable: {path}")


def verify_bundle(path, rid, *, bundle=None):
    bundle = read_bundle(rid) if bundle is None else bundle
    files = expected_files(bundle)
    if is_link(path) or not path.is_dir():
        raise ValueError(f"Missing regular ffprobe directory: {path}")
    actual = set()
    for item in path.rglob("*"):
        if is_link(item):
            raise ValueError(f"Links are forbidden in the ffprobe payload: {item}")
        if item.is_file():
            actual.add(item.relative_to(path).as_posix())
    if actual != files.keys():
        raise ValueError(f"Incomplete or unexpected ffprobe payload: {sorted(actual ^ files.keys())}")
    for name, digest in files.items():
        check_hash(path / name, digest)
    validate_executable(path / bundle["executable"], rid)
    if rid.startswith("osx-") and os.name != "nt" and not os.access(path / bundle["executable"], os.X_OK):
        raise ValueError("The macOS ffprobe executable lost its executable permission.")
    return files


def prepare(rid, output, cache, *, bundle=None):
    bundle = read_bundle(rid) if bundle is None else bundle
    if rid not in ("osx-arm64", "osx-x64", "win-x64"):
        raise ValueError(f"Unsupported ffprobe runtime: {rid}")
    expected_files(bundle)
    destination = output / rid
    if (is_link(output) or is_link(destination)
            or (destination.exists() and not destination.is_dir())):
        raise ValueError("The ffprobe output must be a regular directory, not a file or link.")
    output.mkdir(parents=True, exist_ok=True)
    staging = output / f".{rid}-{uuid.uuid4().hex}"
    backup = output / f".{rid}-{uuid.uuid4().hex}.old"
    installed = False
    staging.mkdir()
    try:
        for archive in bundle["archives"]:
            downloaded = fetch_archive(archive, cache)
            if "save_as" in archive:
                target = staging / safe_name(archive["save_as"])
                target.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(downloaded, target)
            if archive.get("members"):
                extract_selected(downloaded, archive["members"], staging)
        for name in ("NOTICE.txt", "BUILDING.txt"):
            shutil.copyfile(NOTICES / name, staging / name)
        (staging / "bundle.json").write_bytes(bundle_bytes(bundle))
        (staging / bundle["executable"]).chmod(0o755)
        verify_bundle(staging, rid, bundle=bundle)
        if destination.exists():
            destination.rename(backup)
        try:
            staging.rename(destination)
            installed = True
        except OSError:
            if backup.exists():
                backup.rename(destination)
            raise
    finally:
        if staging.exists():
            shutil.rmtree(staging)
        if installed and backup.exists():
            shutil.rmtree(backup)
    print(f"Prepared ffprobe {bundle['version']}: {destination}")
    return destination


def smoke_test(executable, scratch):
    if is_link(scratch):
        raise ValueError("The ffprobe smoke directory must not be a link.")
    scratch.mkdir(parents=True, exist_ok=True)
    fixture = scratch / f"{uuid.uuid4().hex}.wav"
    try:
        with wave.open(str(fixture), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(8000)
            audio.writeframes(b"\0" * 1600)
        result = subprocess.run([
            str(executable.resolve()), "-v", "error", "-hide_banner", "-print_format", "json",
            "-probesize", "10000000", "-analyzeduration", "5000000",
            "-protocol_whitelist", "file", "-show_entries",
            "stream=codec_type,codec_name,width,height,channels,color_transfer,profile:"
            "stream_tags=language:stream_disposition=default,attached_pic:"
            "stream_side_data=side_data_type,dv_profile:format=format_name",
            "-i", str(fixture.resolve()),
        ], check=True, capture_output=True, text=True, timeout=20)
        data = json.loads(result.stdout)
        if (data.get("format", {}).get("format_name") != "wav"
                or not any(stream.get("codec_type") == "audio" and stream.get("channels") == 1
                           for stream in data.get("streams", []))):
            raise ValueError("ffprobe could not read the synthetic mono WAV with the runtime arguments.")
    except subprocess.CalledProcessError as error:
        raise ValueError(f"ffprobe smoke failed: {error.stderr.strip()}") from error
    finally:
        fixture.unlink(missing_ok=True)
    print("Native ffprobe runtime-argument smoke test passed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", required=True, choices=("osx-arm64", "osx-x64", "win-x64"))
    parser.add_argument("--output", type=Path, default=ROOT / "artifacts/ffprobe")
    parser.add_argument("--cache", type=Path, default=ROOT / "artifacts/ffprobe-cache")
    parser.add_argument("--verify", type=Path, help="Verify an already published tools/ffprobe directory; no downloads.")
    parser.add_argument("--smoke-test", action="store_true", help="After --verify, probe a synthetic WAV on the native host.")
    args = parser.parse_args()
    if args.smoke_test and not args.verify:
        parser.error("--smoke-test requires --verify.")
    try:
        if args.verify:
            verify_bundle(args.verify, args.runtime)
            print(f"Verified ffprobe payload: {args.verify}")
            if args.smoke_test:
                smoke_test(args.verify / read_bundle(args.runtime)["executable"], ROOT / "artifacts/ffprobe-smoke")
        else:
            prepare(args.runtime, args.output, args.cache)
    except (ValueError, OSError, tarfile.TarError, zipfile.BadZipFile, subprocess.SubprocessError) as error:
        print(f"ffprobe preparation failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
