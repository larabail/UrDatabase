using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Asks the operating system whether a volume is there. An interface so the check can be
    /// tested without unplugging anything: a test hands in a probe that says what it is told to.
    /// </summary>
    public interface IDriveProbe
    {
        /// <summary>Must not throw. May block — the caller puts a time limit on it.</summary>
        DriveProbeResult Probe(string root);
    }

    /// <summary>The real filesystem.</summary>
    public sealed class FileSystemDriveProbe : IDriveProbe
    {
        public static FileSystemDriveProbe Instance { get; } = new();

        /// <summary>Well inside the time limit the caller puts on the whole probe.</summary>
        private static readonly TimeSpan DiskutilTimeout = TimeSpan.FromSeconds(4);

        public DriveProbeResult Probe(string root)
        {
            try
            {
                // The existence check is the whole verdict. On macOS an unplugged disk takes its
                // /Volumes entry with it; on Windows an empty card reader or a removed USB disk
                // answers false here; a dead share answers false, eventually.
                if (!Directory.Exists(root)) return DriveProbeResult.Unavailable;
            }
            catch
            {
                return DriveProbeResult.Unavailable;
            }

            try
            {
                var info = new DriveInfo(root);
                if (!info.IsReady) return DriveProbeResult.Unavailable;

                var label = string.IsNullOrWhiteSpace(info.VolumeLabel) ? null : info.VolumeLabel;

                // On Unix .NET answers the label question with the mount point, which is not a
                // name anybody gave the disk. The last segment of /Volumes/Films is.
                if (label is not null && label.StartsWith('/'))
                {
                    var name = label.TrimEnd('/');
                    var cut = name.LastIndexOf('/');
                    label = cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : null;
                }

                string? format = null;
                try { format = info.DriveFormat; } catch { }

                var serial = SerialOf(root);

                // The folder macOS mounted a disk at is not its name: a stale "Films" folder left by
                // an unclean eject puts the disk at "Films 1". diskutil knows the real name and the
                // volume UUID, which survives either. Skipped for network mounts, which have neither.
                if (OperatingSystem.IsMacOS() && info.DriveType != DriveType.Network)
                {
                    var (name, uuid) = DriveEnumeration.ParseDiskutilInfo(
                        SystemDriveEnumerator.RunTool("diskutil", new[] { "info", "-plist", root }, DiskutilTimeout));
                    if (name is not null) label = name;
                    serial ??= uuid;
                }

                return new DriveProbeResult(
                    true, label, info.TotalSize, info.AvailableFreeSpace, serial, format);
            }
            catch
            {
                // A UNC share is not something DriveInfo will describe, and a folder that exists
                // is enough to say the drive is there; the sizes are decoration.
                return new DriveProbeResult(true);
            }
        }

        /// <summary>
        /// The volume serial number Windows writes when a disk is formatted, as Explorer prints it.
        /// Null anywhere else, and on any failure: identity then falls back to the label and size.
        /// </summary>
        private static string? SerialOf(string root)
        {
            if (!OperatingSystem.IsWindows()) return null;

            try
            {
                var path = root.EndsWith('\\') ? root : root + "\\";
                if (!GetVolumeInformationW(path, null, 0, out var serial, out _, out _, null, 0)) return null;
                return serial == 0 ? null : $"{serial >> 16:X4}-{serial & 0xFFFF:X4}";
            }
            catch
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetVolumeInformationW(
            string rootPathName,
            StringBuilder? volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder? fileSystemNameBuffer,
            int fileSystemNameSize);
    }
}