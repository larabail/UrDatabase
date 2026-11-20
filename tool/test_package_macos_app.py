import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parent.parent


@unittest.skipUnless(os.name != "nt" and shutil.which("bash"), "Requires a POSIX shell")
class DiskImageTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.app = self.root / "UrDatabase.app"
        self.app.mkdir()
        (self.app / "fixture").write_text("unchanged application")
        self.image = self.root / "output" / "UrDatabase.dmg"
        self.bin = self.root / "bin"
        self.bin.mkdir()
        (self.root / "work").mkdir()
        self.command("sleep", 'printf "%s\\n" "$1" >> "$FAKE_SLEEPS"\n')
        self.command("hdiutil", """
count=0
if [ -f "$FAKE_STATE" ]; then count=$(cat "$FAKE_STATE"); fi
count=$((count + 1))
printf '%s' "$count" > "$FAKE_STATE"
printf '%s\\n' "$@" > "$FAKE_ARGS"
for output do :; done
if [ -e "$output" ]; then echo 'partial image was not removed' >&2; exit 99; fi
printf 'partial' > "$output"
if [ "$FAKE_MODE" = busy ] || { [ "$FAKE_MODE" = recover ] && [ "$count" -lt 3 ]; }; then
    echo 'hdiutil: create failed - Resource busy' >&2
    exit 16
fi
if [ "$FAKE_MODE" = fatal ]; then
    echo 'hdiutil: create failed - Permission denied' >&2
    exit 13
fi
printf 'complete' > "$output"
""")

    def command(self, name, body):
        path = self.bin / name
        path.write_text("#!/bin/sh\nset -eu\n" + body)
        path.chmod(0o755)

    def package(self, mode):
        env = {key: value for key, value in os.environ.items()
               if not key.startswith(("MACOS_", "ASC_"))}
        env.update(
            PATH=str(self.bin) + os.pathsep + os.environ.get("PATH", ""),
            TMPDIR=str(self.root / "work"),
            GITHUB_OUTPUT=str(self.root / "outputs"),
            FAKE_MODE=mode, FAKE_STATE=str(self.root / "attempts"),
            FAKE_SLEEPS=str(self.root / "sleeps"), FAKE_ARGS=str(self.root / "args"),
        )
        return subprocess.run(
            ["bash", str(ROOT / "scripts/package-macos-app.sh"), str(self.app), str(self.image)],
            env=env, text=True, capture_output=True, timeout=10)

    def test_success_keeps_the_compressed_image_shape_without_waiting(self):
        result = self.package("success")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("1", (self.root / "attempts").read_text())
        self.assertFalse((self.root / "sleeps").exists())
        self.assertIn("-format\nUDZO\n", (self.root / "args").read_text())
        self.assertEqual("complete", self.image.read_text())
        self.assertEqual([], list((self.root / "work").iterdir()))

    def test_resource_busy_retries_with_backoff_and_removes_partial_images(self):
        result = self.package("recover")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual("3", (self.root / "attempts").read_text())
        self.assertEqual(["5", "10"], (self.root / "sleeps").read_text().splitlines())
        self.assertIn("::warning", result.stderr)
        self.assertEqual("complete", self.image.read_text())

    def test_resource_busy_remains_fatal_after_five_attempts(self):
        result = self.package("busy")
        self.assertEqual(16, result.returncode, result.stderr)
        self.assertEqual("5", (self.root / "attempts").read_text())
        self.assertEqual(["5", "10", "15", "20"], (self.root / "sleeps").read_text().splitlines())
        self.assertFalse(self.image.exists())
        self.assertNotIn("signed=false", result.stdout)
        self.assertEqual([], list((self.root / "work").iterdir()))

    def test_other_failures_are_not_retried_or_left_as_publishable_images(self):
        result = self.package("fatal")
        self.assertEqual(13, result.returncode, result.stderr)
        self.assertEqual("1", (self.root / "attempts").read_text())
        self.assertFalse((self.root / "sleeps").exists())
        self.assertFalse(self.image.exists())
        self.assertIn("Permission denied", result.stderr)
