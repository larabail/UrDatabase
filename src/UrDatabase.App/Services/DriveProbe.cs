using System;
using System.IO;
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
                return new DriveProbeResult(true, label, info.TotalSize, info.AvailableFreeSpace);
            }
            catch
            {
                // A UNC share is not something DriveInfo will describe, and a folder that exists
                // is enough to say the drive is there; the sizes are decoration.
                return new DriveProbeResult(true);
            }
        }
    }
}
