using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Lists the volumes the operating system has mounted right now. An interface for the same
    /// reason as <see cref="IDriveProbe"/>: a test says which disks are plugged in rather than
    /// depending on what the machine running the suite happens to have.
    /// </summary>
    public interface IDriveEnumerator
    {
        /// <summary>May block, and may throw; the caller puts a time limit on it and copes.</summary>
        IReadOnlyList<MountedVolume> Enumerate();
    }

    /// <summary>
    /// The real list, from <see cref="DriveInfo.GetDrives"/>. Nothing here touches a drive's
    /// contents — that can hang on a dead share — so listing stays quick and every slow question is
    /// left to the probe, which runs under a time limit.
    /// </summary>
    public sealed class SystemDriveEnumerator : IDriveEnumerator
    {
        public static SystemDriveEnumerator Instance { get; } = new();

        /// <summary>How long <c>hdiutil</c> gets to say which volumes are disk images.</summary>
        private static readonly TimeSpan DiskImageTimeout = TimeSpan.FromSeconds(3);

        public IReadOnlyList<MountedVolume> Enumerate()
        {
            var drives = DriveInfo.GetDrives();
            var images = OperatingSystem.IsMacOS() ? MacDiskImageMounts() : new HashSet<string>();

            var volumes = new List<MountedVolume>();

            foreach (var drive in drives)
            {
                var root = drive.Name;
                if (!OperatingSystem.IsWindows())
                {
                    root = DriveEnumeration.NormaliseUnixMount(root);
                    if (!DriveEnumeration.IsUnixVolumeMount(root)) continue;
                    if (images.Contains(root)) continue;
                }

                var kind = DriveEnumeration.KindOf(drive.DriveType);
                if (kind is null) continue;

                volumes.Add(new MountedVolume(root, kind.Value));
            }

            return volumes;
        }

        /// <summary>
        /// The mount points of every disk image attached on this Mac. An installer opened from a
        /// <c>.dmg</c> — this app's own, every time it updates — is a volume under
        /// <c>/Volumes</c> exactly like a disk, and remembering one would raise the alarm the
        /// moment it was ejected. Empty when <c>hdiutil</c> is missing, slow or fails.
        /// </summary>
        private static HashSet<string> MacDiskImageMounts()
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo("hdiutil", "info")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                if (process is null) return new HashSet<string>();

                var output = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit((int)DiskImageTimeout.TotalMilliseconds) || !output.Wait(DiskImageTimeout))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return new HashSet<string>();
                }

                return DriveEnumeration.ParseDiskImageMounts(output.Result);
            }
            catch
            {
                return new HashSet<string>();
            }
        }
    }

    /// <summary>
    /// The decisions about which mounted volumes count as drives, kept out of
    /// <see cref="SystemDriveEnumerator"/> so they can be asserted on from any platform.
    /// </summary>
    public static class DriveEnumeration
    {
        /// <summary>
        /// What a <see cref="DriveType"/> means here, or null for a volume that is not worth
        /// listing at all — a drive letter with nothing behind it.
        /// </summary>
        public static DriveKind? KindOf(DriveType type) => type switch
        {
            DriveType.Fixed => DriveKind.Fixed,
            DriveType.Removable => DriveKind.Removable,
            DriveType.Network => DriveKind.Network,
            DriveType.CDRom => DriveKind.Optical,
            DriveType.Ram => DriveKind.Virtual,
            DriveType.Unknown => DriveKind.Virtual,
            _ => null
        };

        /// <summary><c>/Volumes/Films/</c> and <c>/Volumes/Films</c> are one mount; <c>/</c> stays <c>/</c>.</summary>
        public static string NormaliseUnixMount(string mount)
        {
            var trimmed = mount.TrimEnd('/');
            return trimmed.Length == 0 ? "/" : trimmed;
        }

        /// <summary>
        /// Whether a Unix mount point is a disk somebody plugged in, or the boot volume, rather than
        /// one of the dozens of system mounts a Mac or a Linux desktop carries — <c>/dev</c>,
        /// <c>/System/Volumes/Data</c>, <c>/boot/efi</c>, Time Machine's local snapshots. The test is
        /// the same one the library uses: a mount counts when it is the volume its own path lives
        /// on, so the two can never disagree about what a drive is.
        /// </summary>
        public static bool IsUnixVolumeMount(string mount)
        {
            if (string.IsNullOrWhiteSpace(mount)) return false;
            var root = NormaliseUnixMount(mount);
            if (!string.Equals(DriveVolumes.VolumeOf(root), root, StringComparison.Ordinal)) return false;
            if (root == "/") return true;

            var name = root[(root.LastIndexOf('/') + 1)..];
            return !name.StartsWith('.') && !name.StartsWith("com.apple.", StringComparison.Ordinal);
        }

        /// <summary>
        /// The <c>/Volumes/…</c> mount points in the text <c>hdiutil info</c> prints. Each attached
        /// image lists its devices one per line, tab-separated, with the mount point last.
        /// </summary>
        public static HashSet<string> ParseDiskImageMounts(string? output)
        {
            var mounts = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(output)) return mounts;

            foreach (var line in output.Split('\n'))
            {
                foreach (var field in line.Split('\t').Select(f => f.Trim()))
                {
                    if (field.StartsWith("/Volumes/", StringComparison.Ordinal))
                        mounts.Add(NormaliseUnixMount(field));
                }
            }

            return mounts;
        }

        /// <summary>
        /// Whether a volume that calls itself a disk is really a cloud drive. Google Drive for
        /// desktop mounts a whole drive letter on Windows and reports it as a fixed disk; it is there
        /// only while the Google app is running and signed in, so remembering it would raise the
        /// alarm every time somebody quit that app.
        /// </summary>
        public static bool LooksVirtual(string? label, string? format)
        {
            static bool Has(string? s, string what) =>
                s is not null && s.Contains(what, StringComparison.OrdinalIgnoreCase);

            return Has(format, "GoogleDrive") || Has(format, "fuse")
                || Has(label, "Google Drive") || Has(label, "GoogleDrive")
                || Has(label, "pCloud") || Has(label, "Dropbox") || Has(label, "OneDrive");
        }
    }
}
