using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns a filename into a title and, when the name carries one, a year.
    ///
    /// Pure by design: no database, no filesystem, no network. A scan is only as good as this
    /// step, and the only way to be confident about the messy shapes real libraries contain
    /// ("The Matrix (1999) 1080p.mkv", "the.matrix.1999.BluRay.x264-GROUP.mkv",
    /// "The Matrix [1999].mp4") is to be able to assert on them directly.
    ///
    /// Known limits, all deliberate: a dotted name loses genuine full stops ("S.W.A.T." becomes
    /// "S W A T"), release noise is only stripped from the end of the title, and nothing here
    /// understands television, so "Show.S01E02" parses as a film with a clumsy title.
    /// </summary>
    public static class FilenameParser
    {
        /// <summary>Roundhay Garden Scene, 1888. Nothing older is a film.</summary>
        private const int EarliestPlausibleYear = 1888;

        /// <summary>
        /// Two years of slack for festival and pre-release copies, and so that a title such as
        /// "Blade Runner 2049" is never mistaken for a year.
        /// </summary>
        private static int LatestPlausibleYear => DateTime.UtcNow.Year + 2;

        private static readonly Regex BracketedYear =
            new(@"[\(\[\{]\s*(?<year>\d{4})\s*[\)\]\}]", RegexOptions.Compiled);

        private static readonly Regex BareYear =
            new(@"(?<!\d)(?<year>\d{4})(?!\d)", RegexOptions.Compiled);

        private static readonly Regex BracketedGroup =
            new(@"[\(\[\{][^\)\]\}]*[\)\]\}]", RegexOptions.Compiled);

        private static readonly Regex AnyBracket =
            new(@"[\(\)\[\]\{\}]", RegexOptions.Compiled);

        private static readonly Regex Resolution =
            new(@"^\d{3,4}[pi]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex VideoCodec =
            new(@"^[hx]\.?26[45]$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex BitDepth =
            new(@"^\d{1,2}bits?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex AudioLayout =
            new(@"^(dd|ddp|dts|aac|ac3|eac3|truehd)\+?\d(\.\d)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Channels =
            new(@"^\d(\.\d)?ch$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Tokens that describe the encode rather than the film. Only ever removed from the end of
        /// a title, because a word that is noise in "The Matrix 1080p" is the film in "4K".
        /// </summary>
        private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
        {
            // source
            "bluray", "blu-ray", "brrip", "bdrip", "bdremux", "remux", "webrip", "web", "webdl",
            "web-dl", "hdtv", "pdtv", "dvdrip", "dvdscr", "dvd", "hdrip", "cam", "hdcam", "telesync",
            "telecine", "workprint", "vhsrip", "r5", "screener",
            // resolution and picture
            "4k", "uhd", "fullhd", "hd", "sd", "hdr", "hdr10", "hdr10plus", "dolbyvision", "dovi",
            "sdr", "imax", "hi10p", "upscaled",
            // codecs and containers
            "hevc", "avc", "xvid", "divx", "av1", "vp9", "mpeg2", "mkv", "mp4", "avi",
            // audio
            "aac", "ac3", "eac3", "dts", "dtshd", "truehd", "atmos", "flac", "mp3", "opus", "dd",
            "ddp", "dual", "multi",
            // edition and release state
            "proper", "repack", "rerip", "extended", "theatrical", "unrated", "uncut", "remastered",
            "restored", "limited", "internal", "readnfo", "subbed", "dubbed", "subs",
            // streaming service tags
            "amzn", "nf", "hulu", "dsnp", "atvp", "hmax", "itunes", "stan", "pcok",
            // release groups seen constantly in the wild
            "yify", "yts", "rarbg", "sparks", "evo", "fgt", "ntb", "ettv", "eztv",
        };

        /// <summary>
        /// Words a title case pass leaves alone when they are not the first word, so a wholly
        /// lower-case filename such as "the.lord.of.the.rings" reads correctly afterwards.
        /// </summary>
        private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
        {
            "a", "an", "and", "as", "at", "but", "by", "for", "from", "in", "into", "nor", "of",
            "on", "or", "over", "the", "to", "vs", "with",
        };

        /// <summary>
        /// Reads a title and optional year out of a path or bare filename. Never returns null and
        /// never returns an empty title for a non-empty name: <c>movies.title</c> is NOT NULL, and
        /// a badly named file still deserves a row a user can find.
        /// </summary>
        public static ParsedMedia Parse(string? pathOrFileName)
        {
            var stem = Stem(pathOrFileName);
            if (stem.Length == 0) return new ParsedMedia("", null);

            var working = RemoveBracketedNoise(stem);

            var (year, titlePart) = SplitOnYear(working);
            var title = CleanTitle(titlePart);

            if (title.Length == 0) title = CleanTitle(working);
            if (title.Length == 0) title = stem.Trim();

            return new ParsedMedia(title, year);
        }

        /// <summary>The filename without its directory or extension, tolerant of either separator.</summary>
        private static string Stem(string? pathOrFileName)
        {
            if (string.IsNullOrWhiteSpace(pathOrFileName)) return "";

            // Windows paths reach a macOS build through configuration and test data alike, and
            // Path.GetFileName only honours the separator of the host OS.
            var lastSeparator = pathOrFileName.LastIndexOfAny(new[] { '/', '\\' });
            var name = lastSeparator >= 0 ? pathOrFileName[(lastSeparator + 1)..] : pathOrFileName;

            var extension = Path.GetExtension(name);
            if (extension.Length > 0 && ScanService.IsVideoFile(name))
            {
                var withoutExtension = name[..^extension.Length];

                // A file called ".mkv" is all extension. Keeping the name is better than returning
                // nothing, because a movie row needs a title and a user needs something to rename.
                if (!string.IsNullOrWhiteSpace(withoutExtension)) name = withoutExtension;
            }

            return name.Trim();
        }

        /// <summary>
        /// Drops bracketed groups that hold no plausible year — "[YTS.MX]", "{edition-Extended}" —
        /// while leaving "(1999)" alone, because that bracket is the most reliable year there is.
        /// </summary>
        private static string RemoveBracketedNoise(string name) =>
            BracketedGroup.Replace(name, match => ContainsPlausibleYear(match.Value) ? match.Value : " ");

        private static bool ContainsPlausibleYear(string text) =>
            BareYear.Matches(text).Any(m => IsPlausibleYear(m.Groups["year"].Value));

        private static bool IsPlausibleYear(string value) =>
            int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) &&
            IsPlausibleYear(year);

        /// <summary>
        /// True for a number that could be a film's release year. Shared with
        /// <see cref="MovieFileMatcher"/>, which reads years out of filenames for a different
        /// purpose: two answers to "is 2049 a year" would let the scanner and the file matcher
        /// disagree about what a filename says.
        /// </summary>
        public static bool IsPlausibleYear(int year) =>
            year >= EarliestPlausibleYear && year <= LatestPlausibleYear;

        /// <summary>
        /// Finds the year and returns everything before it. A bracketed year always wins, which is
        /// what keeps "Blade Runner 2049 (2017)" intact; otherwise the last plausible bare number
        /// wins, so "1917.2019.1080p" is the 2019 release of 1917 rather than the reverse.
        /// </summary>
        private static (int? Year, string TitlePart) SplitOnYear(string working)
        {
            var bracketed = LastPlausible(BracketedYear.Matches(working));
            if (bracketed is not null)
            {
                var before = working[..bracketed.Index];
                var after = working[(bracketed.Index + bracketed.Length)..];
                var titlePart = HasTitleText(before) ? before : after;
                return (int.Parse(bracketed.Groups["year"].Value, CultureInfo.InvariantCulture), titlePart);
            }

            var bare = LastPlausible(BareYear.Matches(working));
            if (bare is not null)
            {
                var before = working[..bare.Index];

                // "2012.1080p.mkv" is the film 2012, not an untitled 2012 release: a leading year
                // with nothing in front of it is the title.
                if (HasTitleText(before))
                    return (int.Parse(bare.Groups["year"].Value, CultureInfo.InvariantCulture), before);
            }

            return (null, working);
        }

        private static Match? LastPlausible(MatchCollection matches)
        {
            Match? found = null;
            foreach (Match match in matches)
                if (IsPlausibleYear(match.Groups["year"].Value))
                    found = match;
            return found;
        }

        private static bool HasTitleText(string candidate) => CleanTitle(candidate).Length > 0;

        /// <summary>
        /// Recovers a readable title: separators become spaces, encode noise is trimmed off the
        /// end, and a wholly lower-case name gets its capitals back.
        /// </summary>
        private static string CleanTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var text = AnyBracket.Replace(raw, " ").Replace('_', ' ');

            // Full stops only separate words in a name that has no spaces of its own. Converting
            // them unconditionally would turn "Mr. Nobody" into "Mr Nobody" for no reason.
            if (!text.Contains(' ')) text = text.Replace('.', ' ');

            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            while (tokens.Count > 0 && IsNoiseToken(tokens[^1]))
                tokens.RemoveAt(tokens.Count - 1);

            var title = string.Join(' ', tokens).Trim(' ', '-', '.', ',', ':', ';', '_');
            return ApplyTitleCase(title);
        }

        /// <summary>
        /// True for a token that describes the encode. A hyphenated token counts when its first
        /// half does, which removes "x264-GROUP" without touching "Spider-Man".
        /// </summary>
        private static bool IsNoiseToken(string token)
        {
            var trimmed = token.Trim('-', '_', '.', ',');
            if (trimmed.Length == 0) return true;

            var hyphen = trimmed.IndexOf('-');
            if (hyphen > 0 && IsNoiseAtom(trimmed[..hyphen])) return true;

            return IsNoiseAtom(trimmed);
        }

        private static bool IsNoiseAtom(string token) =>
            NoiseWords.Contains(token) ||
            Resolution.IsMatch(token) ||
            VideoCodec.IsMatch(token) ||
            BitDepth.IsMatch(token) ||
            AudioLayout.IsMatch(token) ||
            Channels.IsMatch(token);

        /// <summary>
        /// Capitalises a title that arrived entirely in lower case. A name that already carries
        /// capitals is left exactly as its owner wrote it, so "The Lord of the Rings" does not
        /// come back as "The Lord Of The Rings".
        /// </summary>
        private static string ApplyTitleCase(string title)
        {
            if (title.Length == 0 || title.Any(char.IsUpper)) return title;

            var words = title.Split(' ');
            for (var i = 0; i < words.Length; i++)
            {
                if (words[i].Length == 0) continue;
                if (i > 0 && SmallWords.Contains(words[i])) continue;
                words[i] = char.ToUpperInvariant(words[i][0]) + words[i][1..];
            }

            return string.Join(' ', words);
        }
    }
}
