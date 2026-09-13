import json
from pathlib import Path
import struct
import tempfile
import unittest
import zipfile

from make_store_package import (
    ASSETS, IDENTITY, NS, ROOT, package_version, stage_package, verify_package, verify_resources,
)


class StorePackageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.publish = self.root / "publish"
        self.publish.mkdir()
        self.output = self.root / "package"
        for name in ("UrDatabase.App.dll", "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
                     "e_sqlite3.dll", "libSkiaSharp.dll", "libHarfBuzzSharp.dll",
                     "UrDatabase.App.deps.json"):
            (self.publish / name).write_bytes(b"fixture")
        pe = bytearray(256)
        pe[:2] = b"MZ"
        struct.pack_into("<I", pe, 0x3C, 128)
        pe[128:132] = b"PE\0\0"
        struct.pack_into("<H", pe, 132, 0x8664)
        (self.publish / "UrDatabase.App.exe").write_bytes(pe)
        (self.publish / "UrDatabase.App.runtimeconfig.json").write_text(json.dumps({
            "runtimeOptions": {"tfm": "net8.0", "includedFrameworks": [
                {"name": "Microsoft.NETCore.App", "version": "8.0.0"}]}}))
        (self.publish / "distribution-channel.txt").write_text("MicrosoftStore\n")
        for name in ("Data/schema.sql", "appsettings.example.json"):
            target = self.publish / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes((ROOT / "src/UrDatabase.App" / name).read_bytes())

    def stage(self):
        return stage_package(self.publish, self.output, "0.22.0")

    def archive(self):
        path = self.root / "upload.msix"
        (self.output / "resources.pri").write_bytes(b"SDK-generated PRI fixture")
        with zipfile.ZipFile(path, "w") as archive:
            for file in self.output.rglob("*"):
                if file.is_file():
                    archive.write(file, file.relative_to(self.output).as_posix())
            for name in ("AppxBlockMap.xml", "[Content_Types].xml"):
                archive.writestr(name, "<fixture/>")
        return path

    def test_version_has_store_reserved_fourth_component_and_nonzero_major(self):
        self.assertEqual("1.22.0.0", package_version("0.22.0"))
        self.assertEqual("2.3.7.0", package_version("1.3.7"))
        for value in ("", "1.2", "1.2.3.4", "1.2.3-preview", "65535.0.0", "0.65536.0"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                package_version(value)

    def test_identity_capabilities_assets_and_real_payload_inclusion(self):
        from xml.etree import ElementTree as ET
        (self.publish / "UrDatabase.App.pdb").write_bytes(b"debug only")
        self.stage()
        manifest = ET.parse(self.output / "AppxManifest.xml").getroot()
        identity = manifest.find("p:Identity", NS)
        self.assertEqual(IDENTITY, {k: identity.get(k) for k in IDENTITY})
        self.assertEqual("1.22.0.0", identity.get("Version"))
        self.assertEqual("UrActor", manifest.findtext("p:Properties/p:PublisherDisplayName", namespaces=NS))
        app = manifest.find("p:Applications/p:Application", NS)
        self.assertEqual("Windows.FullTrustApplication", app.get("EntryPoint"))
        self.assertEqual("UrDatabase.App.exe", app.get("Executable"))
        self.assertEqual(["runFullTrust"], [
            c.get("Name") for c in manifest.find("p:Capabilities", NS)])
        self.assertEqual("Windows.Desktop", manifest.find("p:Dependencies/p:TargetDeviceFamily", NS).get("Name"))
        for name, size in ASSETS.items():
            data = (self.output / "Assets" / name).read_bytes()
            self.assertEqual((size, size), struct.unpack(">II", data[16:24]))
            self.assertLess(len(data), 204800)
        self.assertFalse((self.output / "UrDatabase.App.pdb").exists())
        self.assertTrue((self.output / "Data/schema.sql").is_file())
        self.assertTrue((self.output / "coreclr.dll").is_file())
        verify_package(self.archive(), self.output)

    def test_refuses_private_data_or_unexpected_files_before_copying(self):
        for name in ("appsettings.json", "APPSETTINGS.JSON", "movies.db", "movies.db-wal",
                     "posters/a.png", "logs/details.log", "private.pfx", "Data/settings.json"):
            with self.subTest(name=name):
                file = self.publish / name
                file.parent.mkdir(parents=True, exist_ok=True)
                file.write_bytes(b"not for shipping")
                with self.assertRaises(ValueError):
                    self.stage()
                self.assertFalse(self.output.exists())
                file.unlink()

    def test_refuses_a_modified_placeholder_or_schema(self):
        for name in ("appsettings.example.json", "Data/schema.sql"):
            original = (self.publish / name).read_bytes()
            (self.publish / name).write_bytes(b"modified")
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.stage()
            (self.publish / name).write_bytes(original)

    def test_requires_store_marker_x64_launcher_and_self_contained_runtime(self):
        cases = {
            "distribution-channel.txt": b"Standalone",
            "UrDatabase.App.exe": b"MZ not an x64 PE",
            "UrDatabase.App.runtimeconfig.json": b'{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App"}}}',
            "coreclr.dll": None,
        }
        for name, replacement in cases.items():
            file = self.publish / name
            before = file.read_bytes()
            if replacement is None:
                file.unlink()
            else:
                file.write_bytes(replacement)
            with self.subTest(name=name), self.assertRaises(ValueError):
                self.stage()
            file.write_bytes(before)

    def test_does_not_overwrite_an_existing_staging_directory(self):
        self.output.mkdir()
        with self.assertRaises(FileExistsError):
            self.stage()

    def test_refuses_symlinks(self):
        (self.publish / "link.dll").symlink_to(self.publish / "coreclr.dll")
        with self.assertRaises(ValueError):
            self.stage()

    def test_archive_verification_rejects_changed_missing_extra_or_signed_content(self):
        self.stage()
        for name in ("appsettings.json", "AppxSignature.p7x", "../outside.txt", "unexpected.dll"):
            path = self.archive()
            with zipfile.ZipFile(path, "a") as archive:
                archive.writestr(name, b"unexpected")
            with self.subTest(name=name), self.assertRaises(ValueError):
                verify_package(path, self.output)
        path = self.archive()
        (self.output / "UrDatabase.App.dll").write_bytes(b"different")
        with self.assertRaises(ValueError):
            verify_package(path, self.output)

    def test_sdk_resource_index_is_required(self):
        self.stage()
        path = self.archive()
        (self.output / "resources.pri").unlink()
        with self.assertRaises(ValueError):
            verify_package(path, self.output)

    def test_all_scales_must_be_in_one_resource_index(self):
        self.stage()
        self.archive()
        dump = self.root / "resources.xml"
        dump.write_text("<PriInfo>" + "".join(
            f"<Value>Assets\\{name}</Value>" for name in ASSETS) + "</PriInfo>")
        verify_resources(self.output, dump)
        split = self.output / "resources.scale-200.pri"
        split.write_bytes(b"unregistered resource package")
        with self.assertRaises(ValueError):
            verify_resources(self.output, dump)
        split.unlink()
        dump.write_text("<PriInfo><Value>Assets\\StoreLogo.png</Value></PriInfo>")
        with self.assertRaises(ValueError):
            verify_resources(self.output, dump)

    def test_workflow_keeps_store_uploads_separate_from_releases(self):
        workflow = (ROOT / ".github/workflows/store.yml").read_text()
        script = (ROOT / "scripts/package-store.ps1").read_text()
        self.assertIn("branches: [main, larabail-microsoft-store-packaging]", workflow)
        self.assertIn("runs-on: windows-2022", workflow)
        self.assertIn("actions/upload-artifact@v4", workflow)
        self.assertIn("DistributionChannel=MicrosoftStore", script)
        self.assertIn('"--self-contained", "true"', script)
        self.assertIn('Invoke-Checked $makePri', script)
        self.assertIn('Invoke-Checked $makeAppx', script)
        self.assertIn('"verify", "--package"', script)
        self.assertNotIn("Invoke-Checked signtool", script)
        self.assertNotIn("gh release", workflow)
        self.assertNotIn("secrets.", workflow[workflow.index("run: ./scripts/package-store.ps1"):])
        for name in ("pr.yml", "release.yml"):
            original = (ROOT / ".github/workflows" / name).read_text()
            self.assertIn("runs-on: macos-14", original)
            self.assertNotIn("*.msix", original)


if __name__ == "__main__":
    unittest.main()
