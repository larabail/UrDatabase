#!/usr/bin/env python3
"""Tests for the macOS bundle assembler.

Run with: python3 -m unittest discover -s tool -p "test_*.py"

The thing this protects is unusually hard to see fail. A bundle with a wrong
`CFBundleExecutable`, a missing `Info.plist` key or a launcher that lost its
executable bit still builds, still signs, still uploads, and is still attached
to a release -- and only stops being fine when somebody double-clicks it, by
which time it is a published download that does nothing.

Every assertion here is about a value macOS reads rather than about the shape
of the code, so the file is deliberately blunt: the plist has these keys, the
files are in these directories, and this is what happens when the input is not
what it claims to be.
"""

import os
import plistlib
import tempfile
import unittest
from pathlib import Path

from make_macos_bundle import (
    BundleError,
    build_info_plist,
    make_bundle,
)


def fake_publish(root, *, executable='UrDatabase.App', executable_bit=True):
    """A directory shaped like `dotnet publish --output` wrote it.

    Not the real thing -- that is two hundred megabytes and needs a macOS SDK
    -- but the same arrangement: a launcher, managed assemblies, a native
    library and the `Data/schema.sql` the application reads at startup.
    """
    publish = Path(root) / 'publish'
    (publish / 'Data').mkdir(parents=True)

    launcher = publish / executable
    launcher.write_bytes(b'\xcf\xfa\xed\xfe not really a Mach-O')
    if executable_bit:
        launcher.chmod(0o755)
    else:
        launcher.chmod(0o644)

    (publish / f'{executable}.dll').write_bytes(b'MZ')
    (publish / 'Avalonia.Base.dll').write_bytes(b'MZ')
    (publish / 'libAvaloniaNative.dylib').write_bytes(b'\xcf\xfa\xed\xfe')
    (publish / 'appsettings.example.json').write_text('{}')
    (publish / 'Data' / 'schema.sql').write_text('-- schema')
    return publish


class InfoPlistTests(unittest.TestCase):
    def test_carries_every_key_launchservices_needs(self):
        info = build_info_plist(version='0.2.1')
        self.assertEqual(info['CFBundleIdentifier'], 'com.larabail.urdatabase')
        self.assertEqual(info['CFBundleName'], 'UrDatabase')
        self.assertEqual(info['CFBundleExecutable'], 'UrDatabase.App')
        self.assertEqual(info['CFBundleShortVersionString'], '0.2.1')
        self.assertEqual(info['CFBundleVersion'], '0.2.1')
        self.assertEqual(info['CFBundlePackageType'], 'APPL')
        self.assertEqual(info['LSMinimumSystemVersion'], '12.0')
        self.assertIs(info['NSHighResolutionCapable'], True)

    def test_names_no_icon_when_there_is_none(self):
        # A CFBundleIconFile pointing at a file the bundle does not contain
        # gives the app the generic placeholder icon, which is the same thing
        # omitting the key does -- except that the key also makes the bundle
        # look correct to anybody reading the plist.
        self.assertNotIn('CFBundleIconFile', build_info_plist(version='1.0.0'))

    def test_drops_the_extension_from_the_icon_name(self):
        info = build_info_plist(version='1.0.0', icon_file='UrDatabase.icns')
        self.assertEqual(info['CFBundleIconFile'], 'UrDatabase')

    def test_refuses_a_version_apple_would_reject(self):
        # notarytool rejects these, but only after a build, an upload and a
        # wait, and the message it gives names the key rather than the value.
        for version in ['', 'v1.2.3', '1.2.3.4', '0.2.1-rc1', '2026-08-22', 'x']:
            with self.assertRaises(BundleError, msg=f'accepted {version!r}'):
                build_info_plist(version=version)

    def test_accepts_one_two_or_three_numbers(self):
        for version in ['1', '1.2', '0.2.1', '12.34.56']:
            build_info_plist(version=version)


class MakeBundleTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def test_builds_the_layout_apple_expects(self):
        publish = fake_publish(self.root)
        app = make_bundle(publish, self.root / 'stage', version='0.2.1')

        self.assertEqual(app, self.root / 'stage' / 'UrDatabase.app')
        self.assertTrue((app / 'Contents' / 'Info.plist').is_file())
        self.assertTrue((app / 'Contents' / 'MacOS' / 'UrDatabase.App').is_file())
        self.assertTrue((app / 'Contents' / 'Resources').is_dir())

    def test_puts_the_whole_publish_next_to_the_launcher(self):
        # The apphost resolves the managed assemblies relative to itself and
        # the application reads Data/schema.sql from AppContext.BaseDirectory,
        # which is this same directory. Tidying either into Resources/ gives a
        # bundle that launches and then cannot find its own schema.
        publish = fake_publish(self.root)
        app = make_bundle(publish, self.root / 'stage', version='0.2.1')

        macos = app / 'Contents' / 'MacOS'
        for relative in ['UrDatabase.App.dll', 'Avalonia.Base.dll',
                         'libAvaloniaNative.dylib', 'appsettings.example.json',
                         'Data/schema.sql']:
            self.assertTrue((macos / relative).is_file(),
                            f'{relative} did not make it into Contents/MacOS')

    def test_keeps_the_launcher_executable(self):
        # A bundle whose launcher lost its executable bit is worse than a
        # broken download: it installs, it has an icon, and double-clicking it
        # produces a dialog about the application being damaged.
        publish = fake_publish(self.root)
        app = make_bundle(publish, self.root / 'stage', version='0.2.1')

        launcher = app / 'Contents' / 'MacOS' / 'UrDatabase.App'
        self.assertTrue(os.access(launcher, os.X_OK))

    def test_writes_a_plist_macos_can_read(self):
        publish = fake_publish(self.root)
        app = make_bundle(publish, self.root / 'stage', version='0.2.1')

        with open(app / 'Contents' / 'Info.plist', 'rb') as handle:
            info = plistlib.load(handle)

        self.assertEqual(info['CFBundleShortVersionString'], '0.2.1')
        self.assertEqual(info['CFBundleExecutable'], 'UrDatabase.App')

    def test_copies_the_icon_into_resources(self):
        publish = fake_publish(self.root)
        icon = self.root / 'UrDatabase.icns'
        icon.write_bytes(b'icns fake')

        app = make_bundle(publish, self.root / 'stage', version='0.2.1',
                          icon=icon)

        self.assertTrue((app / 'Contents' / 'Resources' / 'UrDatabase.icns').is_file())
        with open(app / 'Contents' / 'Info.plist', 'rb') as handle:
            self.assertEqual(plistlib.load(handle)['CFBundleIconFile'],
                             'UrDatabase')

    def test_replaces_a_bundle_left_over_from_a_previous_run(self):
        # A re-run on the same runner would otherwise merge the new publish
        # into the old bundle, leaving assemblies from the previous version
        # beside the current ones. That signs and notarizes perfectly happily.
        stage = self.root / 'stage'
        stale = stage / 'UrDatabase.app' / 'Contents' / 'MacOS'
        stale.mkdir(parents=True)
        (stale / 'GoneInThisVersion.dll').write_bytes(b'MZ')

        publish = fake_publish(self.root)
        app = make_bundle(publish, stage, version='0.2.1')

        self.assertFalse((app / 'Contents' / 'MacOS' / 'GoneInThisVersion.dll').exists())

    def test_refuses_a_publish_with_no_launcher_in_it(self):
        # What a Windows or a library publish looks like from here. Caught now,
        # the message says which file was missing; left alone, `codesign`
        # fails several steps later complaining about a bundle format.
        publish = fake_publish(self.root, executable='Something.Else')
        with self.assertRaises(BundleError) as caught:
            make_bundle(publish, self.root / 'stage', version='0.2.1')
        self.assertIn('UrDatabase.App', str(caught.exception))

    def test_refuses_a_launcher_that_cannot_be_run(self):
        publish = fake_publish(self.root, executable_bit=False)
        with self.assertRaises(BundleError):
            make_bundle(publish, self.root / 'stage', version='0.2.1')

    def test_refuses_a_publish_directory_that_is_not_there(self):
        with self.assertRaises(BundleError):
            make_bundle(self.root / 'nope', self.root / 'stage', version='0.2.1')


if __name__ == '__main__':
    unittest.main()
