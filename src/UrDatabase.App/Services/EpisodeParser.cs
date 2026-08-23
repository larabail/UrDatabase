using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Decides whether a path is an episode of a programme, and reads the programme, the season,
    /// the episode number and the episode's own title out of it.
    ///
    /// Pure by design, exactly as <see cref="FilenameParser"/> is, and for a stronger reason: a
    /// television library is nothing but edge cases, and the difference between a scan that files
    /// four hundred episodes correctly and one that scatters them across four hundred film cards
    /// is entirely decided here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This takes a <em>path</em> where <see cref="FilenameParser"/> takes a name, and that is the
    /// whole reason it is a separate class rather than a mode of the other one. A very common
    /// layout names the programme nowhere in the file:
    /// <c>The Sopranos/Season 01/02 - 46 Long.mkv</c>. The season is one directory up and the
    /// programme is two, so a parser handed only the filename cannot see either.
    /// <see cref="FilenameParser.Stem"/> deliberately discards the directory, which is right for a
    /// film and fatal for this.
    /// </para>
    /// <para>
    /// What it recognises, and nothing else:
    /// <list type="bullet">
    ///   <item><description><c>S01E02</c>, in any case, with or without separators between the two halves.</description></item>
    ///   <item><description><c>1x02</c>.</description></item>
    ///   <item><description>A <c>Season 01</c>, <c>S01</c> or <c>Specials</c> directory, which supplies a
    ///   season to a file that carries only an episode number.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// What it deliberately refuses, because guessing would be worse than declining:
    /// <list type="bullet">
    ///   <item><description><b>Absolute numbering</b> — <c>Show - 137.mkv</c> with no season anywhere.
    ///   Ordinary for anime, and indistinguishable from a film with a number in its name without a
    ///   catalogue to check against, which a pure parser has not got.</description></item>
    ///   <item><description><b>Date-based naming</b> — <c>Show.2024.03.11</c> for a daily programme. It
    ///   collides head-on with <see cref="FilenameParser"/>'s year heuristic, which reads the last
    ///   plausible four-digit number as a release year, and the two cannot both be right about the
    ///   same string.</description></item>
    ///   <item><description><b>Three-digit season-and-episode</b> — <c>Show - 102 - Title.mkv</c> meaning
    ///   season one, episode two. Unrecoverable from a bare number that might equally be an
    ///   absolute one.</description></item>
    /// </list>
    /// A path this class declines falls through to <see cref="FilenameParser"/> and is catalogued
    /// as a film, which is what happened to every episode before this existed. That is the point
    /// of declining rather than guessing: the failure is the behaviour people already have, not a
    /// new and more confident kind of wrong.
    /// </para>
    /// <para>
    /// <b>A double episode is read as its first episode.</b> <c>S01E02E03</c> comes back as
    /// episode two, and episode three is simply not represented. One file holding two episodes is
    /// a question about the catalogue's shape rather than about parsing — there is no honest way
    /// for one row to be two episodes — and it is not answered yet. Reading it as episode two
    /// files the thing under a number somebody can find, which beats both refusing it and
    /// inventing a row for an episode that has no file of its own.
    /// </para>
    /// </remarks>
    public static class EpisodeParser
    {
        /// <summary>
        /// <c>S01E02</c>, <c>s1e2</c>, <c>S01.E02</c>. The lookbehind stops it firing inside a
        /// word or a longer number, and the lookahead stops <c>S01E020</c> being read as episode
        /// two with a stray digit after it.
        /// </summary>
        private static readonly Regex SeasonEpisode = new(
            @"(?<![A-Za-z0-9])[Ss](?<season>\d{1,3})[\s._-]*[Ee](?<episode>\d{1,3})(?![0-9])",
            RegexOptions.Compiled);

        /// <summary>
        /// <c>1x02</c>. The episode needs at least two digits, which is the guard that keeps this
        /// away from a resolution: <c>1920x1080</c> cannot match, because a season is at most
        /// three digits and the lookbehind refuses to start part way into <c>1920</c>.
        /// </summary>
        private static readonly Regex CrossNumbered = new(
            @"(?<![A-Za-z0-9])(?<season>\d{1,3})[Xx](?<episode>\d{2,3})(?![0-9])",
            RegexOptions.Compiled);

        /// <summary>A <c>Season 01</c>, <c>Season.1</c> or <c>S01</c> directory.</summary>
        private static readonly Regex SeasonDirectory = new(
            @"^(?:season[\s._-]*(?<number>\d{1,3})|s(?<short>\d{1,3}))$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// A <c>Specials</c> directory. Season zero, because that is Jellyfin's convention and
        /// <see cref="SeriesGrouping.SpecialsSeasonNumber"/> already reads a zero that way — a
        /// second convention here would put a Christmas special at the top of the season list.
        /// </summary>
        private static readonly Regex SpecialsDirectory = new(
            @"^specials?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// A filename that opens with nothing but its episode number: <c>02 - 46 Long.mkv</c>,
        /// <c>02.mkv</c>. Only ever consulted inside a season directory, because on its own a
        /// leading number is far more likely to be a film — <c>1917.mkv</c> — than an episode.
        /// </summary>
        /// <remarks>
        /// One to three digits, so a four-digit year cannot match it. That is what keeps
        /// <c>1917.mkv</c> a film even when somebody has filed it under a season directory by
        /// mistake, and it costs only the ability to read a bare episode number past nine hundred
        /// and ninety-nine, which no programme has.
        /// </remarks>
        private static readonly Regex LeadingEpisodeNumber = new(
            @"^(?<episode>\d{1,3})(?![0-9])(?<rest>.*)$", RegexOptions.Compiled);

        /// <summary>
        /// Reads an episode out of a path, or returns false when the path does not describe one.
        /// </summary>
        /// <param name="pathOrFileName">
        /// A full path where possible. A bare filename still works when it names the programme and
        /// the episode itself — <c>Show.S01E02.mkv</c> — and cannot work when the layout puts the
        /// programme in a directory, which is the common case this class exists for.
        /// </param>
        /// <param name="episode">The episode, when there is one. Undefined when this returns false.</param>
        /// <returns>
        /// False for anything that is not recognisably an episode, and for an episode whose
        /// programme cannot be named. The caller's fallback is <see cref="FilenameParser.Parse"/>,
        /// so declining costs a film card and never costs a row.
        /// </returns>
        public static bool TryParse(string? pathOrFileName, out ParsedEpisode episode)
        {
            episode = default!;
            if (string.IsNullOrWhiteSpace(pathOrFileName)) return false;

            var segments = Split(pathOrFileName);
            if (segments.Count == 0) return false;

            var stem = StripExtension(segments[^1]);
            if (stem.Length == 0) return false;

            var directories = segments.GetRange(0, segments.Count - 1);

            // The filename's own marker first: it is the only source that states both numbers, and
            // a file that disagrees with the directory it sits in is likelier to be filed wrongly
            // than named wrongly.
            if (TryMarker(stem, out var seasonNumber, out var episodeNumber, out var before, out var after))
                return Build(before, after, directories, seasonNumber, episodeNumber, out episode);

            // Otherwise the season has to come from a directory, and the file has to open with the
            // episode number. Both halves are required: without the directory there is no season,
            // and without the leading number there is nothing to say the file is an episode at all.
            var seasonFromDirectory = SeasonFromDirectory(directories, out var seasonDepth);
            if (seasonFromDirectory is null) return false;

            var leading = LeadingEpisodeNumber.Match(stem);
            if (!leading.Success) return false;
            if (!TryNumber(leading.Groups["episode"].Value, out var bareEpisode)) return false;

            // The programme is named above the season directory, never in a file that opens with a
            // number, so what is in front of the episode number is deliberately not offered as a
            // title candidate.
            return Build(
                "",
                leading.Groups["rest"].Value,
                directories.GetRange(0, seasonDepth),
                seasonFromDirectory,
                bareEpisode,
                out episode);
        }

        /// <summary>Splits a path on either separator, dropping the empties a leading slash leaves.</summary>
        /// <remarks>
        /// Both separators, always. Windows paths reach a macOS build through configuration and
        /// test data alike, and <see cref="Path.GetDirectoryName"/> only honours the host's.
        /// </remarks>
        private static List<string> Split(string path)
        {
            var parts = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            var segments = new List<string>(parts.Length);

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (trimmed.Length > 0) segments.Add(trimmed);
            }

            return segments;
        }

        /// <summary>
        /// The filename without its video extension. Anything else is left whole, so a programme
        /// whose episode happens to end in a full stop and three letters is not truncated.
        /// </summary>
        private static string StripExtension(string name)
        {
            if (!ScanService.IsVideoFile(name)) return name.Trim();

            var extension = Path.GetExtension(name);
            if (extension.Length == 0) return name.Trim();

            var withoutExtension = name[..^extension.Length].Trim();
            return withoutExtension.Length > 0 ? withoutExtension : name.Trim();
        }

        /// <summary>
        /// Finds the season and episode marker in a filename, and hands back what sits on either
        /// side of it: the programme in front, the episode's own title behind.
        /// </summary>
        /// <remarks>
        /// The <em>last</em> match wins, which matters for a programme with a number in its name.
        /// "60x60" is not a real show, but "Cell 211" and "9x9" style titles are close enough to
        /// the shape that taking the first match would occasionally read the title as the marker.
        /// The real marker is almost always the rightmost, because the programme comes first.
        /// </remarks>
        private static bool TryMarker(string stem, out int season, out int episode, out string before, out string after)
        {
            season = 0;
            episode = 0;
            before = "";
            after = "";

            var match = LastMatch(SeasonEpisode, stem) ?? LastMatch(CrossNumbered, stem);
            if (match is null) return false;

            if (!TryNumber(match.Groups["season"].Value, out season)) return false;
            if (!TryNumber(match.Groups["episode"].Value, out episode)) return false;

            before = stem[..match.Index];
            after = stem[(match.Index + match.Length)..];
            return true;
        }

        private static Match? LastMatch(Regex pattern, string text)
        {
            Match? found = null;
            foreach (Match match in pattern.Matches(text)) found = match;
            return found;
        }

        /// <summary>
        /// The season a directory names, searched from the file upwards, along with how far up it
        /// was found so the caller knows which directories are above it.
        /// </summary>
        private static int? SeasonFromDirectory(List<string> directories, out int depth)
        {
            for (var i = directories.Count - 1; i >= 0; i--)
            {
                var segment = directories[i];

                if (SpecialsDirectory.IsMatch(segment))
                {
                    depth = i;
                    return SeriesGrouping.SpecialsSeasonNumber;
                }

                var match = SeasonDirectory.Match(segment);
                if (!match.Success) continue;

                var digits = match.Groups["number"].Success
                    ? match.Groups["number"].Value
                    : match.Groups["short"].Value;

                if (!TryNumber(digits, out var season)) continue;

                depth = i;
                return season;
            }

            depth = directories.Count;
            return null;
        }

        /// <summary>
        /// True for a directory that names a season rather than a programme. Consulted when
        /// looking for a programme's name, because "Season 01" is a perfectly good title as far as
        /// <see cref="FilenameParser"/> is concerned and would otherwise be taken for one.
        /// </summary>
        private static bool IsSeasonDirectory(string segment) =>
            SeasonDirectory.IsMatch(segment) || SpecialsDirectory.IsMatch(segment);

        /// <summary>
        /// Assembles the result, naming the programme from the filename when it can and from the
        /// directories when it cannot.
        /// </summary>
        /// <remarks>
        /// The order is the rule. What the filename says in front of the marker is best, because
        /// it sits beside the episode it describes. Failing that, the nearest directory above the
        /// season — <c>The Sopranos/Season 01/</c> — which is how most libraries are actually laid
        /// out. Failing both, there is no programme, and an episode with no programme is refused:
        /// <c>series.title</c> is NOT NULL, and a shelf of episodes filed under a name this class
        /// invented would be worse than the film cards they would otherwise have been.
        ///
        /// A season directory is skipped rather than considered. "Season 01" is a perfectly good
        /// title as far as <see cref="FilenameParser"/> is concerned, so without this the common
        /// <c>The Sopranos/Season 01/S01E02.mkv</c> files every programme in the library under a
        /// handful of shelves called "Season 01", "Season 02" and so on.
        /// </remarks>
        private static bool Build(
            string beforeMarker,
            string afterMarker,
            List<string> directoriesAboveSeason,
            int? season,
            int episode,
            out ParsedEpisode parsed)
        {
            parsed = default!;

            var series = FilenameParser.Parse(beforeMarker);

            if (series.Title.Length == 0)
            {
                for (var i = directoriesAboveSeason.Count - 1; i >= 0; i--)
                {
                    if (IsSeasonDirectory(directoriesAboveSeason[i])) continue;

                    var candidate = FilenameParser.Parse(directoriesAboveSeason[i]);
                    if (candidate.Title.Length == 0) continue;

                    series = candidate;
                    break;
                }
            }

            if (series.Title.Length == 0) return false;

            parsed = new ParsedEpisode(
                series.Title,
                series.Year,
                season,
                episode,
                EpisodeTitle(afterMarker));

            return true;
        }

        /// <summary>
        /// The episode's own title, with the release noise taken off the end.
        /// </summary>
        /// <remarks>
        /// Full stops become spaces before the fragment is cleaned, which
        /// <see cref="FilenameParser"/> pointedly does not do to a name that already contains
        /// spaces. The difference is warranted: that caution is for a whole filename, where
        /// "Mr. Nobody.mkv" is a film whose only full stop is a real one. A fragment sitting
        /// <em>behind a season and episode marker</em> is by definition inside a filename built
        /// out of separated fields, so "46 Long.1080p.BluRay.x264-GROUP" is four fields and not a
        /// sentence. Without this the noise is welded to the title and never stripped, because the
        /// whole tail is one space-delimited token.
        ///
        /// The cost is the same one the other parser documents: an episode genuinely called
        /// "Mr. Monk Takes a Punch" comes back without its full stop.
        /// </remarks>
        private static string EpisodeTitle(string afterMarker) =>
            FilenameParser.CleanText(afterMarker.Replace('.', ' '));

        /// <summary>
        /// Parses a run of digits that the regex has already limited to three. Guards the parse
        /// anyway rather than trusting the pattern from a distance, because the two are edited
        /// separately and only one of them fails loudly.
        /// </summary>
        private static bool TryNumber(string digits, out int value) =>
            int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
