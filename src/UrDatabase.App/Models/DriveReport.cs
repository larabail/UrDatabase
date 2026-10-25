using System;
using System.Collections.Generic;

namespace UrDatabase.Models
{
    /// <summary>Where a drive stands right now.</summary>
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
    /// What sort of volume the operating system says a drive is. Decides whether the app
    /// remembers it, and so whether it is missed when it goes.
    /// </summary>
    public enum DriveKind
    {
        /// <summary>A disk: internal, or external in an enclosure that says it is fixed.</summary>
        Fixed,

        /// <summary>A USB stick or a card reader slot.</summary>
        Removable,

        /// <summary>A mapped network drive or a mounted share.</summary>
        Network,

        /// <summary>A CD, DVD or Blu-ray drive, or a mounted image of one.</summary>
        Optical,

        /// <summary>
        /// A drive that is not a disk at all: a cloud drive such as Google Drive, a RAM disk, or a
        /// filesystem the operating system could not name.
        /// </summary>
        Virtual
    }

    /// <summary>
    /// One drive, and what checking it found. A drive is listed because it is plugged in now, because
    /// the app remembers it from an earlier launch, or because the library lives on it.
    /// </summary>
    /// <param name="Root">
    /// The volume, as <see cref="Services.DriveVolumes.VolumeOf"/> names it. For a remembered drive
    /// that is missing, the root it was last seen at.
    /// </param>
    /// <param name="State">What the probe found.</param>
    /// <param name="WatchFolders">The configured library folders that live on it.</param>
    /// <param name="FilmCount">Distinct catalogued films with a file on it.</param>
    /// <param name="Label">The volume's own name, when the operating system offered one.</param>
    /// <param name="TotalBytes">Capacity, when the drive is connected and reported it.</param>
    /// <param name="FreeBytes">Space left, when the drive is connected and reported it.</param>
    /// <param name="Id">
    /// What tells this disk from every other, independent of the letter it is mounted at. Null
    /// when the drive could not be identified, which also means it cannot be remembered.
    /// </param>
    /// <param name="Kind">What the operating system says it is; null when it is known only from a path.</param>
    /// <param name="Remembered">The app has this disk on record and will miss it if it goes.</param>
    /// <param name="LastSeenUtc">When a remembered disk was last plugged in.</param>
    public sealed record DriveReport(
        string Root,
        DriveState State,
        IReadOnlyList<string> WatchFolders,
        int FilmCount,
        string? Label = null,
        long? TotalBytes = null,
        long? FreeBytes = null,
        string? Id = null,
        DriveKind? Kind = null,
        bool Remembered = false,
        DateTime? LastSeenUtc = null)
    {
        public bool IsDown => State != DriveState.Connected;

        /// <summary>
        /// Down, and either holding a configured library folder or remembered from an earlier
        /// launch. Only these raise the banner, and every one of them has a way to clear it:
        /// remove the library folder in Settings, or forget the disk in the drives dialog.
        ///
        /// A drive known only from catalogued files — one whose library folder was removed after it
        /// died, or one holding a copy linked by hand — has neither, so it would hold the banner up
        /// on every launch with nothing to press. It still appears in the drives dialog.
        /// </summary>
        public bool RaisesAlarm => IsDown && (WatchFolders.Count > 0 || Remembered);

        /// <summary>A remembered disk that is not here, which the owner may have retired.</summary>
        public bool CanForget => IsDown && Remembered && Id is not null;
    }

    /// <summary>
    /// What a probe of one volume root reported. Unavailable is the only fact the check needs;
    /// the rest is shown or used to identify the disk when present, and quietly left out when not.
    /// </summary>
    /// <param name="Serial">
    /// The volume serial number, where the operating system has one to give (Windows). Survives the
    /// disk being given a different letter, which is why it is preferred to anything else.
    /// </param>
    /// <param name="Format">The filesystem, such as <c>NTFS</c> or <c>apfs</c>.</param>
    public sealed record DriveProbeResult(
        bool IsAvailable,
        string? Label = null,
        long? TotalBytes = null,
        long? FreeBytes = null,
        string? Serial = null,
        string? Format = null)
    {
        public static DriveProbeResult Unavailable { get; } = new(false);
    }

    /// <summary>A drive the library touches, before anybody has looked at it.</summary>
    /// <param name="Root">The volume.</param>
    /// <param name="WatchFolders">Configured library folders on it.</param>
    /// <param name="FilmCount">Distinct catalogued films with a file on it.</param>
    public sealed record DriveUsage(string Root, IReadOnlyList<string> WatchFolders, int FilmCount);

    /// <summary>
    /// A volume the operating system lists right now. Listing it is cheap and says nothing about
    /// whether it will answer; that is the probe's job.
    /// </summary>
    public sealed record MountedVolume(string Root, DriveKind Kind);

    /// <summary>
    /// A disk the app has seen plugged in, kept in the catalogue so that it can be missed when it
    /// stops appearing. A dead disk is not reported missing by the operating system — it is simply
    /// not in the list any more — so remembering it is the only way to notice.
    /// </summary>
    /// <param name="Id">See <see cref="DriveReport.Id"/>.</param>
    /// <param name="Label">Its volume name when last seen.</param>
    /// <param name="LastRoot">Where it was mounted when last seen.</param>
    /// <param name="TotalBytes">Its capacity when last seen.</param>
    /// <param name="LastSeenUtc">When it was last seen.</param>
    public sealed record KnownDrive(
        string Id,
        string? Label,
        string LastRoot,
        long? TotalBytes,
        DateTime LastSeenUtc);
}
