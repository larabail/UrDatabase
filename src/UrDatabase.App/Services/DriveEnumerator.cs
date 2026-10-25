using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// How long reading every drive's type may take, together. Short, because it is normally
        /// instant and a drive still being read after this is a dead network mount.
        /// </summary>
        private static readonly TimeSpan TypeTimeout = TimeSpan.FromSeconds(2);

        public IReadOnlyList<MountedVolume> Enumerate()
        {
            var drives = DriveInfo.GetDrives();
            var images = OperatingSystem.IsMacOS() ? MacDiskImageMounts() : new HashSet<string>();

            var candidates = new List<(string Root, Func<DriveType> TypeOf)>();

            foreach (var drive in drives)
            {
                var root = drive.Name;
                if (!OperatingSystem.IsWindows())
                {
                    root = DriveEnumeration.NormaliseUnixMount(root);
                    if (!DriveEnumeration.IsUnixVolumeMount(root)) continue;
                    if (images.Contains(root)) continue;
                }

                candidates.Add((root, () => drive.DriveType));
            }

            return DriveEnumeration.Classify(candidates, TypeTimeout);
        }

        /// <summary>
        /// The mount points of every disk image attached on this Mac. An installer opened from a
        /// <c>.dmg</c> — this app's own, every time it updates — is a volume under
        /// <c>/Volumes</c> exactly like a disk, and remembering one would raise the alarm the
        /// moment it was ejected. Empty when <c>hdiutil</c> is missing, slow or fails.
        /// </summary>
        private static HashSet<string> MacDiskImageMounts() =>
            DriveEnumeration.ParseDiskImageMounts(RunTool("hdiutil", new[] { "info" }, DiskImageTimeout));

        /// <summary>
        /// Runs a command-line tool and returns what it printed, or null when it is missing,
        /// fails, or is not finished in time — in which case it is killed rather than left behind.
        /// </summary>
        internal static string? RunTool(string file, IEnumerable<string> arguments, TimeSpan timeout)
        {
            try
            {
                var start = new ProcessStartInfo(file)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var argument in arguments) start.ArgumentList.Add(argument);

                using var process = Process.Start(start);
                if (process is null) return null;

                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit((int)timeout.TotalMilliseconds) || !output.Wait(timeout))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                return process.ExitCode == 0 ? output.Result : null;
            }
            catch
            {
                return null;
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

        /// <summary>
        /// Each drive's kind, read with a time limit on all of them together. On a Mac the type
        /// comes from a <c>statfs</c>, and a <c>statfs</c> on a dead SMB mount does not return:
        /// left unguarded, one such mount would cost the whole list, and without the list every
        /// remembered disk looks missing. A drive whose type is not back in time is almost
        /// certainly such a mount, so it is taken for a network drive — listed when it answers,
        /// never remembered. One whose type cannot be read at all is not taken for a disk.
        /// </summary>
        public static IReadOnlyList<MountedVolume> Classify(
            IEnumerable<(string Root, Func<DriveType> TypeOf)> drives, TimeSpan timeout)
        {
            var reads = drives
                .Select(d => (d.Root, Type: Task.Factory.StartNew(
                    d.TypeOf, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)))
                .ToList();

            try { Task.WaitAll(reads.Select(r => (Task)r.Type).ToArray(), timeout); }
            catch (AggregateException) { }

            var volumes = new List<MountedVolume>();
            foreach (var (root, type) in reads)
            {
                DriveKind? kind = !type.IsCompleted ? DriveKind.Network
                    : type.IsCompletedSuccessfully ? KindOf(type.Result)
                    : DriveKind.Virtual;

                if (kind is not null) volumes.Add(new MountedVolume(root, kind.Value));
            }

            return volumes;
        }

        /// <summary>
        /// The volume's real name and its UUID from <c>diskutil info -plist</c>. The name is the one
        /// the disk was given, not the folder macOS mounted it at — which becomes <c>Films 1</c>
        /// when a stale <c>Films</c> folder is in the way — and the UUID survives either. Nulls for
        /// anything that is not such a plist.
        /// </summary>
        public static (string? Name, string? Uuid) ParseDiskutilInfo(string? plist)
        {
            if (string.IsNullOrEmpty(plist)) return (null, null);

            static string? Value(string text, string key)
            {
                var match = Regex.Match(text, $@"<key>{key}</key>\s*<string>(.*?)</string>", RegexOptions.Singleline);
                if (!match.Success) return null;
                var value = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
                return value.Length == 0 ? null : value;
            }

            return (Value(plist, "VolumeName"), Value(plist, "VolumeUUID"));
        }

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
