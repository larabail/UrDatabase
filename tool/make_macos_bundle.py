#!/usr/bin/env python3
"""Turns a `dotnet publish` output directory into a macOS `.app` bundle.

Why this exists at all
----------------------

Until now the macOS download was a folder holding a bare Mach-O executable
called `UrDatabase.App`. That was never merely unconventional:

  * `xcrun stapler` staples a notarization ticket to a **bundle** or a disk
    image. It has nowhere to put one on a loose executable, so the published
    build could not be notarized even if everything else were in place -- and
    without notarization macOS refuses to start it at all.
  * Right-click -> Open, the escape hatch every Mac user has been taught,
    applies to bundles. A loose executable leaves only a terminal command.
  * There is nothing to drag to /Applications, no icon, and no identity in the
    Dock or in Launchpad.

So the bundle is a prerequisite for the signing work rather than a nicety.

Why Python rather than a shell script
-------------------------------------

Two reasons. `plistlib` writes a correct property list without shelling out to
`plutil`, and the whole thing then runs on any platform -- which means the unit
tests beside it run in the `Version` job on Linux rather than only on the macOS
runner that happens to build the release.

The layout it writes
--------------------

    UrDatabase.app/
      Contents/
        Info.plist
        MacOS/         the executable, every .dll, every .dylib, Data/
        Resources/     the icon

Everything from the publish goes in `Contents/MacOS`, including the managed
assemblies and `Data/schema.sql`. That looks wrong to anyone used to Cocoa
conventions, where only executables belong there, and it is nonetheless
correct here: the .NET apphost resolves `UrDatabase.App.dll` and the rest
relative to its own directory, and the application reads `schema.sql` and
`appsettings.json` from `AppContext.BaseDirectory`, which is that same
directory. Moving the data files to `Resources/` would produce a bundle that
signs, notarizes, launches and then cannot find its own schema.
"""

from __future__ import annotations

import argparse
import os
import plistlib
import shutil
import stat
import sys
from pathlib import Path

#: Reverse-DNS name for the bundle. Developer ID signing does not require this
#: to be registered with Apple -- that is an App Store and provisioning profile
#: rule -- but notarization records it, so it should not churn between
#: releases.
DEFAULT_IDENTIFIER = 'com.larabail.urdatabase'

#: The oldest macOS the shipped binaries are supported on. .NET 8 supports
#: macOS 12 and later, so claiming anything older invites a launch that gets
#: as far as a dynamic linker error. LaunchServices enforces this key, so it
#: is also the difference between "this app requires a newer macOS" and a
#: crash with no explanation.
DEFAULT_MINIMUM_SYSTEM_VERSION = '12.0'

#: `Contents/MacOS/<this>` is what LaunchServices starts. It has to match the
#: apphost the publish produced, which is named after the assembly.
DEFAULT_EXECUTABLE = 'UrDatabase.App'

DEFAULT_BUNDLE_NAME = 'UrDatabase'


class BundleError(Exception):
    """Something about the publish or the arguments makes a bundle impossible."""


def _check_version(version: str) -> str:
    """[version] if macOS will accept it as a bundle version, else raises.

    `CFBundleShortVersionString` and `CFBundleVersion` are one to three
    integers separated by full stops. Apple's notary service rejects anything
    else, and it does so *after* a build, an upload and a wait -- which is a
    long way to travel to find out that a version was a git describe string.
    """
    parts = version.split('.')
    if not 1 <= len(parts) <= 3 or not all(part.isdigit() for part in parts):
        raise BundleError(
            f'{version!r} is not a usable bundle version. macOS wants one to '
            'three full-stop separated integers, such as 0.2.1.')
    return version


def build_info_plist(
    *,
    version: str,
    name: str = DEFAULT_BUNDLE_NAME,
    executable: str = DEFAULT_EXECUTABLE,
    identifier: str = DEFAULT_IDENTIFIER,
    icon_file: str | None = None,
    minimum_system_version: str = DEFAULT_MINIMUM_SYSTEM_VERSION,
) -> dict[str, object]:
    """The Info.plist contents, as a dictionary, for review and for testing."""
    _check_version(version)

    info: dict[str, object] = {
        'CFBundleName': name,
        'CFBundleDisplayName': name,
        'CFBundleIdentifier': identifier,
        'CFBundleExecutable': executable,
        'CFBundleInfoDictionaryVersion': '6.0',
        'CFBundlePackageType': 'APPL',
        # Both keys carry the same number. They are allowed to differ -- the
        # short string is what a user sees and CFBundleVersion is the build
        # counter -- but this project releases once per version from a single
        # `<Version>` in Directory.Build.props, so a second number would be a
        # second source of truth with nothing to say.
        'CFBundleShortVersionString': version,
        'CFBundleVersion': version,
        'LSMinimumSystemVersion': minimum_system_version,
        # Without this the window is drawn at 1x and scaled up, which on a
        # Retina display looks like a blurred screenshot of the application
        # rather than the application.
        'NSHighResolutionCapable': True,
        # Keeps the app out of the "this is a background agent" category, so it
        # gets a Dock icon and a menu bar.
        'LSApplicationCategoryType': 'public.app-category.video',
        'NSSupportsAutomaticGraphicsSwitching': True,
    }

    if icon_file:
        # LaunchServices tolerates the extension being present or absent; it is
        # dropped so the key reads the way Apple's own templates write it.
        info['CFBundleIconFile'] = icon_file.removesuffix('.icns')

    return info


def _clear(path: Path) -> None:
    if path.is_symlink() or path.is_file():
        path.unlink()
    elif path.is_dir():
        shutil.rmtree(path)


def make_bundle(
    publish_dir: Path,
    output_dir: Path,
    *,
    version: str,
    name: str = DEFAULT_BUNDLE_NAME,
    executable: str = DEFAULT_EXECUTABLE,
    identifier: str = DEFAULT_IDENTIFIER,
    icon: Path | None = None,
    minimum_system_version: str = DEFAULT_MINIMUM_SYSTEM_VERSION,
) -> Path:
    """Assembles `<output_dir>/<name>.app` from [publish_dir]. Returns its path.

    The publish directory is *moved* rather than copied. A self-contained .NET
    publish is a couple of hundred megabytes, it is a build artifact with no
    other reader, and copying it would double both the time and the disk a
    release run needs for no benefit.
    """
    publish_dir = Path(publish_dir)
    output_dir = Path(output_dir)

    if not publish_dir.is_dir():
        raise BundleError(f'{publish_dir} is not a directory, so there is '
                          'nothing to put in a bundle.')

    launcher = publish_dir / executable
    if not launcher.is_file():
        raise BundleError(
            f'{launcher} does not exist. The publish produced no {executable} '
            'launcher, so either the runtime identifier was not a macOS one or '
            'the project no longer builds an executable of that name.')

    if not os.access(launcher, os.X_OK):
        raise BundleError(
            f'{launcher} is not executable. Something between `dotnet publish` '
            'and here dropped the executable bit, and a bundle whose launcher '
            'cannot be run is worse than no bundle: it looks installed.')

    app = output_dir / f'{name}.app'
    _clear(app)

    contents = app / 'Contents'
    macos = contents / 'MacOS'
    resources = contents / 'Resources'

    macos.parent.mkdir(parents=True, exist_ok=True)
    # `shutil.move` rather than `Path.rename`, because the publish directory
    # and the staging directory can be on different filesystems -- they are
    # whenever somebody points `--output` at a temporary directory -- and
    # rename fails across devices with an errno that names none of that.
    shutil.move(str(publish_dir), str(macos))
    resources.mkdir(parents=True, exist_ok=True)

    icon_file = None
    if icon is not None:
        icon = Path(icon)
        if not icon.is_file():
            raise BundleError(f'{icon} does not exist, so there is no icon to '
                              'put in the bundle.')
        icon_file = icon.name
        shutil.copy2(icon, resources / icon_file)

    info = build_info_plist(
        version=version,
        name=name,
        executable=executable,
        identifier=identifier,
        icon_file=icon_file,
        minimum_system_version=minimum_system_version,
    )
    with open(contents / 'Info.plist', 'wb') as handle:
        plistlib.dump(info, handle)

    # Restored rather than assumed. `shutil.move` preserves the mode when it
    # can rename, and re-creates files when it cannot; the second path is the
    # one that quietly produces a bundle macOS refuses to launch.
    bundled_launcher = macos / executable
    mode = bundled_launcher.stat().st_mode
    bundled_launcher.chmod(mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

    return app


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description='Assemble a macOS .app bundle from a dotnet publish '
                    'directory.')
    parser.add_argument('--publish-dir', required=True, type=Path,
                        help='The directory `dotnet publish --output` wrote. '
                             'It is moved into the bundle, not copied.')
    parser.add_argument('--output', required=True, type=Path,
                        help='Where to create <name>.app.')
    parser.add_argument('--version', required=True,
                        help='The version from Directory.Build.props.')
    parser.add_argument('--name', default=DEFAULT_BUNDLE_NAME)
    parser.add_argument('--executable', default=DEFAULT_EXECUTABLE)
    parser.add_argument('--identifier', default=DEFAULT_IDENTIFIER)
    parser.add_argument('--icon', type=Path, default=None)
    parser.add_argument('--minimum-system-version',
                        default=DEFAULT_MINIMUM_SYSTEM_VERSION)
    args = parser.parse_args(argv)

    try:
        app = make_bundle(
            args.publish_dir,
            args.output,
            version=args.version,
            name=args.name,
            executable=args.executable,
            identifier=args.identifier,
            icon=args.icon,
            minimum_system_version=args.minimum_system_version,
        )
    except BundleError as error:
        print(f'error: {error}', file=sys.stderr)
        return 1

    print(app)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
