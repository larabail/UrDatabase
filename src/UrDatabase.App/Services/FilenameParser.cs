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
    /// "S W A T"). Release noise is cut in two strengths. A token that can only ever describe a
    /// release — "1080p", "x264", "BDRip", "MicroHD", "Castellano" — ends the title, and
    /// everything after it goes whether it is recognised or not. A token that is also a word —
    /// "Web", "Dual", "Spanish", "4K" — is only stripped from the end, so "Charlotte's Web" still
    /// loses its last word but "The Spanish Prisoner" keeps its middle one.
    ///
    /// Nothing here understands television, and that is now a division of labour rather than a
    /// gap: <see cref="EpisodeParser"/> reads a path that names a season and an episode, and a
    /// scan asks it first. What reaches this class is what that one declined, so "Show.S01E02"
    /// still parses as a film with a clumsy title if it ever gets here — which is the right
    /// fallback for a name no episode parser could file.
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

        /// <summary>"5.1", "7.1", "2.0" on their own. Weak, because "2.0" is also a film.</summary>
        private static readonly Regex BareChannels =
            new(@"^[1-9]\.[01]$", RegexOptions.Compiled);

        /// <summary>
        /// A site name stamped onto the file by whoever shared it, as in
        /// "www.DivxTotaL.com - Film.avi". Only with the "www.", because "the.net.1995" is a
        /// dotted name and not a domain.
        /// </summary>
        private static readonly Regex SiteName =
            new(@"(?<![\p{L}\p{N}])www\.[\p{L}\p{N}-]+(\.[\p{L}\p{N}-]+)*\.(com|net|org|info|biz|es|tv|to|me|cc|ws|mx|ar|eu|co|io|nu|la|ch|cx)(?![\p{L}\p{N}])",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Tokens that only ever describe a release and never appear in a film's name. The first
        /// one in a title ends it, and everything after it goes too, however unfamiliar: a
        /// release name puts the film first and the encode after, and the encode part is an open
        /// vocabulary of languages, groups and uploader names ("1080p.Castellano.By.Pepito") that
        /// no list could keep up with.
        /// </summary>
        private static readonly HashSet<string> ReleaseTags = new(StringComparer.OrdinalIgnoreCase)
        {
            // source
            "bluray", "blu-ray", "brrip", "bdrip", "bdremux", "remux", "webrip", "webdl", "web-dl",
            "hdtv", "hdtvrip", "pdtv", "dvdrip", "dvdscr", "dvdfull", "dvd5", "dvd9", "hdrip",
            "hdcam", "microhd", "telesync", "telecine", "workprint", "vhsrip", "bluray-rip",
            // resolution and picture
            "uhd", "fullhd", "hdr", "hdr10", "hdr10plus", "dolbyvision", "dovi", "hi10p", "upscaled",
            // codecs and containers
            "hevc", "avc", "xvid", "divx", "av1", "vp9", "mpeg2", "mkv", "mp4", "avi",
            // audio
            "aac", "ac3", "eac3", "dts", "dtshd", "truehd", "flac",
            // edition and release state
            "rerip", "repack", "readnfo",
            // language and subtitle tags of Spanish releases
            "castellano", "vose", "vosi", "subtitulado", "subtitulada", "subesp",
            // streaming service tags
            "amzn", "dsnp", "atvp", "hmax",
            // release groups seen constantly in the wild
            "yify", "yts", "rarbg", "fgt", "ntb", "ettv", "eztv",
        };

        /// <summary>
        /// Tokens that are noise at the end of a title but can be a word of one: "4K" is a film,
        /// "Charlotte's Web" ends in a source tag and "The Spanish Prisoner" holds a language.
        /// Only ever removed from the end, and never down to nothing.
        /// </summary>
        private static readonly HashSet<string> TrailingNoise = new(StringComparer.OrdinalIgnoreCase)
        {
            // source
            "web", "dvd", "cam", "r5", "screener", "rip", "bd",
            // resolution and picture
            "4k", "hd", "sd", "sdr", "imax",
            // audio
            "atmos", "mp3", "opus", "dd", "ddp", "dual", "multi", "audio",
            // edition and release state
            "proper", "extended", "theatrical", "unrated", "uncut", "remastered", "restored",
            "limited", "internal", "subbed", "dubbed", "subs", "sub",
            // language
            "spanish", "espanol", "español", "latino", "ingles", "inglés", "vo", "vos",
            // streaming service tags
            "nf", "hulu", "itunes", "stan", "pcok",
            // release groups that are also words
            "sparks", "evo",
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

            var working = RemoveBracketedNoise(SiteName.Replace(stem, " "));

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
        /// Recovers readable text from a fragment of a filename, without reading a year out of it.
        /// </summary>
        /// <remarks>
        /// Exposed for <see cref="EpisodeParser"/>, which has a fragment that is a title and needs
        /// exactly the cleaning below — separators to spaces, release noise off the end, capitals
        /// back on a wholly lower-case name — and must not have a year taken out of it. An episode
        /// called "1999" is an episode called "1999", where a film called "The Matrix 1999" is the
        /// 1999 release of The Matrix; <see cref="Parse"/> is right for the second and would
        /// silently empty the first.
        ///
        /// The alternative was a second copy of the noise list, which is how a scanner and a
        /// parser start disagreeing about what "web-dl" means.
        /// </remarks>
        public static string CleanText(string? raw) => CleanTitle(raw ?? "");

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

            var text = AnyBracket.Replace(SiteName.Replace(raw, " "), " ").Replace('_', ' ');

            // Bracket removal and mixed release separators can introduce spaces into a dotted
            // filename. Split internal dots, but keep "Mr. Nobody" and audio tags such as DD5.1.
            text = string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(token => Classify(token) != Noise.None ? token : Regex.Replace(token, @"(?<=\S)\.(?=\S)", " ")));

            var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

            // Release tags in front of the title say nothing about it either.
            while (tokens.Count > 0 && Classify(tokens[0]) == Noise.ReleaseTag)
                tokens.RemoveAt(0);

            // The title ends at the first release tag, and whatever follows it goes with it.
            var cut = tokens.FindIndex(token => Classify(token) == Noise.ReleaseTag);
            if (cut >= 0) tokens.RemoveRange(cut, tokens.Count - cut);

            // Words that are noise only at the end come off one at a time, but never the first:
            // a film called "4K" or "2.0" is still a film.
            while (tokens.Count > 1 && Classify(tokens[^1]) != Noise.None)
                tokens.RemoveAt(tokens.Count - 1);
            if (tokens.Count == 1 && IsPunctuationOnly(tokens[0]))
                tokens.Clear();

            var title = string.Join(' ', tokens).Trim(' ', '-', '.', ',', ':', ';', '_');
            return ApplyTitleCase(title);
        }

        private enum Noise
        {
            None,

            /// <summary>Noise only when it ends a title.</summary>
            Trailing,

            /// <summary>Never part of a title: it and everything after it go.</summary>
            ReleaseTag,
        }

        private static bool IsPunctuationOnly(string token) =>
            token.Trim('-', '_', '.', ',').Length == 0;

        /// <summary>
        /// Says whether a token describes the encode. A hyphenated token counts when its first
        /// half does, which removes "x264-GROUP" without touching "Spider-Man".
        /// </summary>
        private static Noise Classify(string token)
        {
            var trimmed = token.Trim('-', '_', '.', ',');
            if (trimmed.Length == 0) return Noise.Trailing;

            var whole = ClassifyAtom(trimmed);
            if (whole != Noise.None) return whole;

            var hyphen = trimmed.IndexOf('-');
            return hyphen > 0 ? ClassifyAtom(trimmed[..hyphen]) : Noise.None;
        }

        private static Noise ClassifyAtom(string token)
        {
            if (ReleaseTags.Contains(token) ||
                Resolution.IsMatch(token) ||
                VideoCodec.IsMatch(token) ||
                BitDepth.IsMatch(token) ||
                AudioLayout.IsMatch(token) ||
                Channels.IsMatch(token))
                return Noise.ReleaseTag;

            if (TrailingNoise.Contains(token) || BareChannels.IsMatch(token))
                return Noise.Trailing;

            return Noise.None;
        }

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
