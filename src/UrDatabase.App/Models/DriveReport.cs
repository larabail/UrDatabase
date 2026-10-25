using System;
using System.Collections.Generic;

namespace UrDatabase.Models
{
    /// <summary>Where a drive the library lives on stands right now.</summary>
    public enum DriveState
    {
        /// <summary>The drive answered and its root exists.</summary>
        Connected,

        /// <summary>The drive answered that it is not there: unplugged, unmounted, or no media.</summary>
        Disconnected,

        /// <summary>
        /// The drive did not answer in time. Almost always a network share whose server has gone
        /// away, where the operating system can sit on a single existence check for half a minute.
        /// </summary>
        NotResponding
    }

    /// <summary>
    /// One drive the library depends on, and what checking it found.
    /// </summary>
    /// <param name="Root">The volume, as <see cref="Services.DriveVolumes.VolumeOf"/> names it.</param>
    /// <param name="State">What the probe found.</param>
    /// <param name="WatchFolders">The configured library folders that live on it.</param>
    /// <param name="FilmCount">Distinct catalogued films with a file on it.</param>
    /// <param name="Label">The volume's own name, when the operating system offered one.</param>
    /// <param name="TotalBytes">Capacity, when the drive is connected and reported it.</param>
    /// <param name="FreeBytes">Space left, when the drive is connected and reported it.</param>
    public sealed record DriveReport(
        string Root,
        DriveState State,
        IReadOnlyList<string> WatchFolders,
        int FilmCount,
        string? Label = null,
        long? TotalBytes = null,
        long? FreeBytes = null)
    {
        public bool IsDown => State != DriveState.Connected;

        /// <summary>
        /// Down, and holding a configured library folder. Only these raise the banner. A drive
        /// known only from catalogued files — one whose library folder was removed after it died,
        /// or one holding a copy linked by hand — is never walked by a scan again, so nothing
        /// would ever mark its files missing and the alarm could never be cleared. Such a drive
        /// still appears in the drives dialog.
        /// </summary>
        public bool RaisesAlarm => IsDown && WatchFolders.Count > 0;
    }

    /// <summary>
    /// What a probe of one volume root reported. Unavailable is the only fact the check needs;
    /// the rest is shown when present and quietly left out when it is not.
    /// </summary>
    public sealed record DriveProbeResult(
        bool IsAvailable,
        string? Label = null,
        long? TotalBytes = null,
        long? FreeBytes = null)
    {
        public static DriveProbeResult Unavailable { get; } = new(false);
    }

    /// <summary>A drive the library touches, before anybody has looked at it.</summary>
    /// <param name="Root">The volume.</param>
    /// <param name="WatchFolders">Configured library folders on it.</param>
    /// <param name="FilmCount">Distinct catalogued films with a file on it.</param>
    public sealed record DriveUsage(string Root, IReadOnlyList<string> WatchFolders, int FilmCount);
}
