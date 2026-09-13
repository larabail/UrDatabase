#!/usr/bin/env python3
"""Stage a clean desktop MSIX payload and inspect the Windows SDK's actual archive."""

import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
from xml.etree import ElementTree as ET
import zipfile

from check_version_bump import parse_version, version_from_props

ROOT = Path(__file__).resolve().parent.parent
NS = {
    "p": "http://schemas.microsoft.com/appx/manifest/foundation/windows10",
    "uap": "http://schemas.microsoft.com/appx/manifest/uap/windows10",
    "rescap": "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities",
}
IDENTITY = {
    "Name": "UrActor.UrDatabase",
    "Publisher": "CN=53FDA7CE-E84E-4A01-A833-B1C55FBA5540",
    "ProcessorArchitecture": "x64",
}
ASSETS = {
    "Square44x44Logo.png": 44,
    "Square44x44Logo.scale-200.png": 88,
    "Square44x44Logo.scale-400.png": 176,
    "Square44x44Logo.targetsize-44_altform-unplated.png": 44,
    "Square150x150Logo.png": 150,
    "Square150x150Logo.scale-200.png": 300,
    "Square150x150Logo.scale-400.png": 600,
    "StoreLogo.png": 50,
    "StoreLogo.scale-125.png": 63,
    "StoreLogo.scale-150.png": 75,
    "StoreLogo.scale-200.png": 100,
    "StoreLogo.scale-400.png": 200,
}
CONTENT = {"Data/schema.sql", "appsettings.example.json"}
REQUIRED = CONTENT | {
    "UrDatabase.App.exe", "UrDatabase.App.dll", "UrDatabase.App.deps.json",
    "UrDatabase.App.runtimeconfig.json", "distribution-channel.txt",
    "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "e_sqlite3.dll",
    "libSkiaSharp.dll", "libHarfBuzzSharp.dll",
}


def release_version():
    value = version_from_props((ROOT / "Directory.Build.props").read_text(encoding="utf-8"))
    parse_version(value)
    return value


def package_version(version):
    major, minor, patch = parse_version(version)
    # The Store forbids major=0; offset EVERY release, including after 1.0, to stay monotonic.
    if major >= 65535 or minor > 65535 or patch > 65535:
        raise ValueError("Version exceeds the MSIX 16-bit component range.")
    return f"{major + 1}.{minor}.{patch}.0"


def validate_publish(publish):
    files = {}
    for path in sorted(publish.rglob("*")):
        if path.is_symlink() or path.is_junction():
            raise ValueError(f"Links are not package content: {path}")
        if path.is_dir():
            continue
        name = path.relative_to(publish).as_posix()
        if name in REQUIRED or (path.parent == publish and path.suffix.lower() in (".dll", ".exe")):
            files[name] = path
        elif path.parent == publish and path.suffix.lower() == ".pdb":
            continue
        else:
            raise ValueError(f"Unexpected publish content (never package personal data): {name}")
    missing = REQUIRED - files.keys()
    if missing:
        raise ValueError(f"Incomplete self-contained publish: {sorted(missing)}")
    if files["distribution-channel.txt"].read_text(encoding="utf-8-sig").strip() != "MicrosoftStore":
        raise ValueError("Publish with -p:DistributionChannel=MicrosoftStore, not the ZIP updater.")
    for name in CONTENT:
        if files[name].read_bytes() != (ROOT / "src/UrDatabase.App" / name).read_bytes():
            raise ValueError(f"Publish does not contain the repository template: {name}")

    pe = files["UrDatabase.App.exe"].read_bytes()
    offset = struct.unpack_from("<I", pe, 0x3C)[0] if len(pe) >= 64 else len(pe)
    if (pe[:2] != b"MZ" or offset + 6 > len(pe) or pe[offset:offset + 4] != b"PE\0\0"
            or struct.unpack_from("<H", pe, offset + 4)[0] != 0x8664):
        raise ValueError("UrDatabase.App.exe must be a Windows x64 PE launcher.")
    options = json.loads(files["UrDatabase.App.runtimeconfig.json"].read_text(encoding="utf-8"))["runtimeOptions"]
    if (options.get("framework") or options.get("frameworks") or
            not any(f["name"] == "Microsoft.NETCore.App" and f["version"].startswith("8.")
                    for f in options.get("includedFrameworks", []))):
        raise ValueError("The Store payload must include the .NET 8 runtime, not depend on its installation.")
    return files


def stage_package(publish, output, version):
    if output.exists():
        raise FileExistsError(f"Staging directory already exists: {output}")
    files = validate_publish(publish)
    template = (ROOT / "packaging/windows/AppxManifest.xml").read_text(encoding="utf-8")
    manifest = template.replace("@VERSION@", package_version(version))
    identity = ET.fromstring(manifest).find("p:Identity", NS)
    if any(identity.get(key) != value for key, value in IDENTITY.items()):
        raise ValueError("Manifest identity differs from the reserved Partner Center product.")
    for name, size in ASSETS.items():
        data = (ROOT / "packaging/windows/Assets" / name).read_bytes()
        if data[:8] != b"\x89PNG\r\n\x1a\n" or struct.unpack(">II", data[16:24]) != (size, size):
            raise ValueError(f"Incorrect Store icon: {name}")

    output.mkdir(parents=True)
    for name, source in files.items():
        dest = output / name
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, dest)
    (output / "Assets").mkdir()
    for name in ASSETS:
        shutil.copyfile(ROOT / "packaging/windows/Assets" / name, output / "Assets" / name)
    (output / "AppxManifest.xml").write_text(manifest, encoding="utf-8")
    return output


def validate_resource_index(staged):
    if list(staged.rglob("*.pri")) != [staged / "resources.pri"]:
        raise ValueError("Use one resources.pri, not unregistered split resource packages.")
    if (staged / "resources.pri").stat().st_size == 0:
        raise ValueError("Generate resources.pri with MakePri before packaging the qualified icons.")


def verify_resources(staged, dump):
    validate_resource_index(staged)
    values = {
        node.text.replace("\\", "/")
        for node in ET.parse(dump).iter()
        if node.tag.rsplit("}", 1)[-1] == "Value" and node.text
    }
    missing = {f"Assets/{name}" for name in ASSETS} - values
    if missing:
        raise ValueError(f"resources.pri does not index every icon variant: {sorted(missing)}")


def verify_package(package, staged):
    validate_resource_index(staged)
    expected = {p.relative_to(staged).as_posix(): p for p in staged.rglob("*") if p.is_file()}
    footprints = {"AppxBlockMap.xml", "[Content_Types].xml"}
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or set(names) != expected.keys() | footprints:
            raise ValueError("MSIX must contain exactly the staged payload and SDK footprint files; no signature.")
        for name, path in expected.items():
            if hashlib.sha256(archive.read(name)).digest() != hashlib.sha256(path.read_bytes()).digest():
                raise ValueError(f"MSIX content differs from the staged file: {name}")
        if archive.testzip() is not None:
            raise ValueError("MSIX failed its ZIP CRC check.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("version")
    stage = commands.add_parser("stage")
    stage.add_argument("--publish-dir", type=Path, required=True)
    stage.add_argument("--output", type=Path, required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("--package", type=Path, required=True)
    verify.add_argument("--staged", type=Path, required=True)
    resources = commands.add_parser("verify-resources")
    resources.add_argument("--staged", type=Path, required=True)
    resources.add_argument("--dump", type=Path, required=True)
    args = parser.parse_args()
    if args.command == "version":
        print(release_version())
    elif args.command == "stage":
        stage_package(args.publish_dir.resolve(), args.output.resolve(), release_version())
        print(f"Staged Store payload: {args.output}")
    elif args.command == "verify":
        verify_package(args.package, args.staged)
        print(f"Verified unsigned Store upload: {args.package}")
    else:
        verify_resources(args.staged, args.dump)
        print("Verified all icon variants in the single resource index.")


if __name__ == "__main__":
    main()
