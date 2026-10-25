using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>What probing one root found, with the time limit already applied.</summary>
    public sealed record DriveProbeOutcome(DriveState State, DriveProbeResult Result)
    {
        public static DriveProbeOutcome Away { get; } = new(DriveState.Disconnected, DriveProbeResult.Unavailable);
    }

    /// <summary>The drives to show, and the disks to record as seen.</summary>
    public sealed record DriveInventoryResult(IReadOnlyList<DriveReport> Reports, IReadOnlyList<KnownDrive> Seen);

    /// <summary>
    /// Puts together the three things that can put a drive on the list — the volumes plugged in
    /// now, the disks remembered from earlier launches, and the drives the library lives on — into
    /// one report per drive, and says which disks to remember.
    ///
    /// Pure, so that every decision about which disk is which can be tested with no disks at all.
    /// </summary>
    public static class DriveInventory
    {
        /// <summary>
        /// What tells one disk from another regardless of the letter it was given: the volume
        /// serial number where the operating system has one, otherwise the volume's name together
        /// with its capacity. Null when there is neither, and such a drive is never remembered —
        /// identifying it by its letter would mistake whatever is plugged in there next for it.
        /// </summary>
        public static string? IdentityOf(DriveProbeResult? probe)
        {
            if (probe is null || !probe.IsAvailable) return null;
            if (!string.IsNullOrWhiteSpace(probe.Serial)) return "serial:" + probe.Serial.Trim();
            if (!string.IsNullOrWhiteSpace(probe.Label) && probe.TotalBytes is > 0)
                return $"volume:{BaseName(probe.Label)}|{probe.TotalBytes.Value}";
            return null;
        }

        /// <summary>
        /// A volume name without the " 1", " 2" macOS appends to the folder it mounts a disk at
        /// when one of that name is already there. Only matters when the real name could not be
        /// read; the capacity alongside it keeps two different disks apart.
        /// </summary>
        private static string BaseName(string label)
        {
            var name = label.Trim();
            var stripped = Regex.Replace(name, @" \d+$", "");
            return stripped.Length == 0 ? name : stripped;
        }

        /// <param name="mounted">What the operating system lists right now.</param>
        /// <param name="probes">What probing each root found, keyed by root.</param>
        /// <param name="usages">The drives the library lives on.</param>
        /// <param name="known">The disks remembered from earlier.</param>
        /// <param name="canRemember">
        /// False when there is no catalogue to remember anything in. Nothing is then marked as
        /// remembered, so nothing claims it will be missed.
        /// </param>
        /// <param name="nowUtc">When this check ran.</param>
        /// <param name="listingComplete">
        /// False when the operating system's list of drives could not be had, and the volumes passed
        /// in are only the roots worth probing directly. A remembered disk that was not found is then
        /// not evidence of anything — it may be plugged in at a letter nobody could list — so only a
        /// disk absent from its own last root is reported missing.
        /// </param>
        public static DriveInventoryResult Build(
            IEnumerable<MountedVolume>? mounted,
            IReadOnlyDictionary<string, DriveProbeOutcome>? probes,
            IEnumerable<DriveUsage>? usages,
            IEnumerable<KnownDrive>? known,
            bool canRemember,
            DateTime nowUtc,
            bool listingComplete = true)
        {
            var volumes = (mounted ?? Array.Empty<MountedVolume>())
                .Where(v => !string.IsNullOrWhiteSpace(v.Root))
                .GroupBy(v => v.Root, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var usageByRoot = new Dictionary<string, DriveUsage>(StringComparer.OrdinalIgnoreCase);
            foreach (var usage in usages ?? Array.Empty<DriveUsage>())
                usageByRoot.TryAdd(usage.Root, usage);

            var knownList = (known ?? Array.Empty<KnownDrive>()).Where(k => !string.IsNullOrWhiteSpace(k.Id)).ToList();
            var knownById = new Dictionary<string, KnownDrive>(StringComparer.Ordinal);
            foreach (var k in knownList) knownById.TryAdd(k.Id, k);

            DriveProbeOutcome OutcomeOf(string root) =>
                probes is not null && probes.TryGetValue(root, out var o) ? o : DriveProbeOutcome.Away;

            // First every volume that answered, identified. Only then can a volume that did not
            // answer be guessed at from the letter a remembered disk used to have: the disk may
            // simply have moved to another letter, and that has to be found out first.
            var ids = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var volume in volumes)
            {
                var outcome = OutcomeOf(volume.Root);
                var id = outcome.State == DriveState.Connected ? IdentityOf(outcome.Result) : null;

                // The same disk under two letters — a SUBST, or a folder mounted as a drive — is one
                // disk. The first letter keeps the identity; the alias is listed but not remembered.
                if (id is not null && !matched.Add(id)) id = null;
                ids[volume.Root] = id;
            }

            KnownDrive? LastSeenAt(string root) =>
                knownList.FirstOrDefault(k => !matched.Contains(k.Id)
                    && string.Equals(k.LastRoot, root, StringComparison.OrdinalIgnoreCase));

            var reports = new List<DriveReport>();
            var seen = new List<KnownDrive>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var volume in volumes)
            {
                var outcome = OutcomeOf(volume.Root);
                var connected = outcome.State == DriveState.Connected;
                var probe = outcome.Result;
                var id = ids[volume.Root];

                var kind = volume.Kind;
                if (kind == DriveKind.Fixed && connected && DriveEnumeration.LooksVirtual(probe.Label, probe.Format))
                    kind = DriveKind.Virtual;

                usageByRoot.TryGetValue(volume.Root, out var usage);
                if (usage is not null) claimed.Add(volume.Root);
                var holdsLibrary = usage is not null && (usage.WatchFolders.Count > 0 || usage.FilmCount > 0);

                var record = id is not null && knownById.TryGetValue(id, out var byId) ? byId : null;
                if (record is null && !connected)
                {
                    // Listed but not answering: most likely the remembered disk that lives here.
                    record = LastSeenAt(volume.Root);
                    if (record is not null) matched.Add(record.Id);
                }

                var worthRemembering = kind == DriveKind.Fixed || (kind == DriveKind.Removable && holdsLibrary);
                var remember = canRemember && connected && id is not null && (worthRemembering || record is not null);

                // A stick, a share or a cloud drive that is not answering is not worth a line: an
                // empty card reader is not news. A disk is listed whatever its state.
                var listed = kind == DriveKind.Fixed || connected || usage is not null || record is not null;
                if (!listed) continue;

                if (remember)
                    seen.Add(new KnownDrive(id!, probe.Label, volume.Root, probe.TotalBytes, nowUtc));

                reports.Add(new DriveReport(
                    volume.Root,
                    outcome.State,
                    usage?.WatchFolders ?? Array.Empty<string>(),
                    usage?.FilmCount ?? 0,
                    connected ? probe.Label : record?.Label,
                    connected ? probe.TotalBytes : record?.TotalBytes,
                    connected ? probe.FreeBytes : null,
                    id ?? record?.Id,
                    kind,
                    canRemember && (remember || record is not null),
                    connected ? null : record?.LastSeenUtc));
            }

            // The library's drives the operating system did not list: a network share reached by
            // its UNC path, or a letter with nothing behind it any more.
            foreach (var usage in usageByRoot.Values.Where(u => !claimed.Contains(u.Root)))
            {
                var outcome = OutcomeOf(usage.Root);
                var connected = outcome.State == DriveState.Connected;
                var record = connected ? null : LastSeenAt(usage.Root);
                if (record is not null) matched.Add(record.Id);

                reports.Add(new DriveReport(
                    usage.Root,
                    outcome.State,
                    usage.WatchFolders,
                    usage.FilmCount,
                    connected ? outcome.Result.Label : record?.Label,
                    connected ? outcome.Result.TotalBytes : record?.TotalBytes,
                    connected ? outcome.Result.FreeBytes : null,
                    record?.Id,
                    null,
                    canRemember && record is not null,
                    record?.LastSeenUtc));
            }

            // And the point of remembering: a disk that is simply not there any more.
            foreach (var record in knownList.Where(k => listingComplete && !matched.Contains(k.Id)))
            {
                reports.Add(new DriveReport(
                    record.LastRoot,
                    DriveState.Disconnected,
                    Array.Empty<string>(),
                    0,
                    record.Label,
                    record.TotalBytes,
                    null,
                    record.Id,
                    null,
                    canRemember,
                    record.LastSeenUtc));
            }

            var ordered = reports
                .OrderBy(r => r.Root, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.IsDown ? 1 : 0)
                .ToList();

            return new DriveInventoryResult(ordered, seen);
        }
    }
}
