using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Reads what a release filename claims about the copy it names: how big the picture is, what
    /// encoded it, how it sounds and which languages it carries.
    /// </summary>
    /// <remarks>
    /// Pure, like <see cref="FilenameParser"/> beside it, and for the same reason — the shapes
    /// real libraries contain can only be got right by asserting on them directly.
    ///
    /// The one idea worth understanding here is the tag region. Every token this looks for is also
    /// a word that appears in film titles: "The Italian Job", "4K", "Dual", "The Danish Girl",
    /// "Atmos". Scanning a whole filename for the word "italian" tags Michael Caine's heist film
    /// as an Italian-language release, and nobody would ever guess why. So nothing is read until
    /// the film's own name is behind us: the region starts after the year, or — for a name with no
    /// year — at the first token that could not possibly be part of a title, such as "1080p" or
    /// "x264". A filename with neither yields nothing at all, which is the honest answer for
    /// "Casablanca.mkv".
    ///
    /// Everything it returns is a claim rather than a measurement. <see cref="MediaFlags"/> knows
    /// that, and prefers real dimensions from a server whenever it has them.
    /// </remarks>
    public static class FilenameMediaInfo
    {
        private static readonly Regex Resolution =
            new(@"^(?<res>\d{3,4}[pi])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex Year =
            new(@"(?<!\d)(?<year>\d{4})(?!\d)", RegexOptions.Compiled);

        /// <summary>An audio codec with its channel count welded on: "DDP5.1", "DTS-HD.7.1", "AAC2.0".</summary>
        private static readonly Regex AudioWithChannels =
            new(@"^(?<codec>dd|ddp|dts|dtshd|aac|ac3|eac3|truehd|flac|opus|mp3)\+?(?<channels>\d)(\.(?<sub>\d))?$",
                RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A bare channel layout: "5.1", "7.1", "2.0", "5.1ch".</summary>
        private static readonly Regex Channels =
            new(@"^(?<main>\d)\.(?<sub>\d)(ch)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Where a copy came from, mapped to what the badge says, with how specific each claim is.
        /// </summary>
        /// <remarks>
        /// The rank exists because a release name states two things that are both true:
        /// <c>2160p.REMUX.BluRay</c> and <c>2160p.BluRay.REMUX</c> are the same copy, and taking
        /// whichever word came first would badge them differently. A remux <em>is</em> a Blu-ray,
        /// so "Remux" is the informative half of that claim and always wins. Everything else is
        /// mutually exclusive — nothing is both a web download and a cinema recording — so among
        /// equals the first word found is as good an answer as any.
        /// </remarks>
        private static readonly (string Token, string Label, int Rank)[] Sources =
        {
            ("bdremux", "Remux", 0),
            ("remux", "Remux", 0),
            ("bluray", "BluRay", 1),
            ("blu-ray", "BluRay", 1),
            ("brrip", "BluRay", 1),
            ("bdrip", "BluRay", 1),
            ("web-dl", "WEB-DL", 1),
            ("webdl", "WEB-DL", 1),
            ("webrip", "WEBRip", 1),
            ("hdtv", "HDTV", 1),
            ("pdtv", "HDTV", 1),
            ("dvdrip", "DVD", 1),
            ("dvdscr", "DVD", 1),
            ("hdrip", "HDRip", 1),
            ("cam", "CAM", 1),
            ("hdcam", "CAM", 1),
            ("telesync", "TS", 1),
            ("screener", "Screener", 1),
        };

        private static readonly Dictionary<string, string> VideoCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["x265"] = "hevc",
            ["h265"] = "hevc",
            ["h.265"] = "hevc",
            ["hevc"] = "hevc",
            ["x264"] = "h264",
            ["h264"] = "h264",
            ["h.264"] = "h264",
            ["avc"] = "h264",
            ["av1"] = "av1",
            ["vp9"] = "vp9",
            ["xvid"] = "xvid",
            ["divx"] = "divx",
            ["mpeg2"] = "mpeg2",
            ["vc1"] = "vc1",
        };

        private static readonly Dictionary<string, string> AudioCodecs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["truehd"] = "truehd",
            ["dtshd"] = "dtshd",
            ["dts-hd"] = "dtshd",
            ["dtsma"] = "dtshd",
            ["dts"] = "dts",
            ["eac3"] = "eac3",
            ["ddp"] = "eac3",
            ["dd+"] = "eac3",
            ["ac3"] = "ac3",
            ["dd"] = "ac3",
            ["aac"] = "aac",
            ["flac"] = "flac",
            ["opus"] = "opus",
            ["mp3"] = "mp3",
            ["pcm"] = "pcm",
            ["lpcm"] = "pcm",
        };

        private static readonly Dictionary<string, string> Ranges = new(StringComparer.OrdinalIgnoreCase)
        {
            ["hdr"] = "HDR",
            ["hdr10"] = "HDR10",
            ["hdr10plus"] = "HDR10Plus",
            ["hdr10+"] = "HDR10Plus",
            ["dv"] = "DOVI",
            ["dovi"] = "DOVI",
            ["dolbyvision"] = "DOVI",
            ["hlg"] = "HLG",
        };

        /// <summary>
        /// Tokens that mean "the encode", never "the film". Used to find where the title stops on
        /// a filename that carries no year, so they have to be words no title would contain — a
        /// release group name or a stray "extended" is not good enough evidence to cut on.
        /// </summary>
        private static readonly HashSet<string> RegionStarters = new(StringComparer.OrdinalIgnoreCase)
        {
            "bluray", "blu-ray", "brrip", "bdrip", "bdremux", "remux", "webrip", "webdl", "web-dl",
            "hdtv", "dvdrip", "dvdscr", "hdrip", "x264", "x265", "h264", "h265", "h.264", "h.265",
            "hevc", "avc", "xvid", "divx", "av1", "uhd", "hdr", "hdr10", "dovi", "dolbyvision",
            "atmos", "truehd", "dtshd", "ddp", "eac3", "ac3", "aac",
        };

        /// <summary>
        /// Reads a filename. Never null; every field is empty for a name that says nothing, which
        /// is most of them.
        /// </summary>
        public static MediaInfo Parse(string? pathOrFileName)
        {
            var info = new MediaInfo();
            if (string.IsNullOrWhiteSpace(pathOrFileName)) return info;

            var lastSeparator = pathOrFileName.LastIndexOfAny(new[] { '/', '\\' });
            var name = lastSeparator >= 0 ? pathOrFileName[(lastSeparator + 1)..] : pathOrFileName;
            name = name.Trim();
            if (name.Length == 0) return info;

            var extension = Path.GetExtension(name);
            if (extension.Length > 1 && ScanService.IsVideoFile(name))
            {
                info.Container = extension[1..].ToLowerInvariant();
                name = name[..^extension.Length];
            }

            var tokens = Tokenise(name);
            var region = TagRegion(tokens);
            if (region.Count == 0) return info;

            ReadTokens(region, info);
            return info;
        }

        /// <summary>
        /// Splits on every separator a release name uses at once, except where a full stop is
        /// part of what it separates.
        /// </summary>
        /// <remarks>
        /// Full stops always separate here, unlike in <see cref="FilenameParser"/> — nothing in
        /// this file is shown to a user, so there is no title to protect from losing "Mr.
        /// Nobody"'s full stop. Two things do have to be protected, and both were found by a test
        /// rather than by reading the code:
        ///
        /// <c>DDP5.1</c>, <c>TrueHD.7.1</c> and a bare <c>5.1</c> all carry a channel layout in
        /// which the full stop is the notation. Split naively, "5.1" becomes a five and a one, and
        /// every 5.1 film in a library reads as five channel sound.
        ///
        /// <c>H.264</c> and <c>H.265</c> are codec names with a full stop inside them, and split
        /// naively they become an "H" and a number that is not a year.
        ///
        /// Neither can be fixed by simply keeping full stops between digits: <c>1999.2160p</c>
        /// has one, and welding a year to a resolution loses both.
        /// </remarks>
        internal static List<string> Tokenise(string name)
        {
            var protectedName = Layout.Replace(name, m => $"{m.Groups[1].Value}{Placeholder}{m.Groups[2].Value}");
            protectedName = CodecName.Replace(protectedName, m => $"{m.Groups[1].Value}{Placeholder}{m.Groups[2].Value}");

            return protectedName
                .Split(new[] { ' ', '.', '_', '(', ')', '[', ']', '{', '}', ',' },
                       StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Replace(Placeholder, '.').Trim('-'))
                .Where(t => t.Length > 0)
                .ToList();
        }

        /// <summary>
        /// Stands in for a full stop that is part of a token rather than between two. Never
        /// appears in a filename, so nothing can be mistaken for it.
        /// </summary>
        private const char Placeholder = '\u0001';

        /// <summary>A channel layout: the digit, the stop and the digit, with no other digit touching it.</summary>
        private static readonly Regex Layout =
            new(@"(?<!\d)(\d)\.(\d)(?!\d)", RegexOptions.Compiled);

        /// <summary>A codec written with a stop in it: <c>H.264</c>, <c>x.265</c>.</summary>
        private static readonly Regex CodecName =
            new(@"(?<![a-z0-9])([hx])\.(26[45])(?![0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// The part of a filename that describes the encode rather than the film. See the remarks
        /// on the class for why nothing outside it is ever read.
        /// </summary>
        internal static List<string> TagRegion(IReadOnlyList<string> tokens)
        {
            // The year is the reliable boundary, and the last one wins so "Blade Runner 2049 2017
            // 2160p" cuts after 2017 rather than after the title.
            var cut = -1;
            for (var i = 0; i < tokens.Count; i++)
            {
                if (Year.IsMatch(tokens[i]) &&
                    int.TryParse(Year.Match(tokens[i]).Groups["year"].Value, out var year) &&
                    FilenameParser.IsPlausibleYear(year))
                    cut = i;
            }

            if (cut >= 0) return tokens.Skip(cut + 1).ToList();

            // No year. Fall back to the first token that could not be part of a title. A
            // resolution counts, because no film is called "1080p".
            for (var i = 0; i < tokens.Count; i++)
            {
                if (RegionStarters.Contains(tokens[i]) || Resolution.IsMatch(tokens[i]))
                    return tokens.Skip(i).ToList();
            }

            return new List<string>();
        }

        private static void ReadTokens(IReadOnlyList<string> tokens, MediaInfo info)
        {
            // The source is decided across the whole region rather than token by token, because
            // its rank matters more than its position. See the remarks on Sources.
            var sourceRank = int.MaxValue;

            foreach (var raw in tokens)
            {
                var token = raw.Trim('-', '+');
                if (token.Length == 0) continue;

                if (Read(token, info, ref sourceRank)) continue;

                // A release group is welded onto the codec with a hyphen — "x264-GROUP" — so a
                // token nothing recognised is retried without its tail. Whole first, because
                // "DTS-HD" is a name with a hyphen in it rather than a name with a group after it,
                // and cutting at the hyphen would demote it to plain DTS.
                var hyphen = token.IndexOf('-');
                if (hyphen > 0) Read(token[..hyphen], info, ref sourceRank);
            }
        }

        /// <summary>
        /// Reads one token into the description. Returns true when it was recognised as anything,
        /// which is what stops the hyphen retry above running on a token already understood.
        /// </summary>
        private static bool Read(string token, MediaInfo info, ref int sourceRank)
        {
            if (info.ClaimedQuality is null && TryQuality(token, out var quality))
            {
                info.ClaimedQuality = quality;
                return true;
            }

            if (info.VideoCodec is null && VideoCodecs.TryGetValue(token, out var video))
            {
                info.VideoCodec = video;
                return true;
            }

            if (info.VideoRange is null && Ranges.TryGetValue(token, out var range))
            {
                info.VideoRange = range;
                return true;
            }

            if (string.Equals(token, "atmos", StringComparison.OrdinalIgnoreCase))
            {
                info.HasAtmos = true;
                return true;
            }

            var combined = AudioWithChannels.Match(token);
            if (combined.Success)
            {
                info.AudioCodec ??= AudioCodecs.TryGetValue(combined.Groups["codec"].Value, out var mapped)
                    ? mapped
                    : null;
                info.AudioChannels ??= ChannelCount(combined.Groups["channels"].Value, combined.Groups["sub"].Value);
                return true;
            }

            if (info.AudioCodec is null && AudioCodecs.TryGetValue(token, out var audio))
            {
                info.AudioCodec = audio;
                return true;
            }

            var layout = Channels.Match(token);
            if (info.AudioChannels is null && layout.Success)
            {
                info.AudioChannels = ChannelCount(layout.Groups["main"].Value, layout.Groups["sub"].Value);
                return true;
            }

            if (TrySource(token, out var source, out var rank) && rank < sourceRank)
            {
                info.Source = source;
                sourceRank = rank;
                return true;
            }

            // Last, so a token that is also a codec or a source is never read as a language.
            // "DD" is Dolby Digital, and there is no language whose code is DD.
            if (LanguageTag.IsKnown(token))
            {
                var code = LanguageTag.Code(token);
                if (code is not null &&
                    code != LanguageTag.UnknownCode &&
                    !info.AudioLanguages.Contains(token, StringComparer.OrdinalIgnoreCase))
                    info.AudioLanguages.Add(token);

                return true;
            }

            return false;
        }

        /// <summary>
        /// Turns "5" and "1" into six channels, the way a release name writes 5.1. A layout with
        /// no subwoofer digit is just its main count.
        /// </summary>
        private static int? ChannelCount(string main, string sub)
        {
            if (!int.TryParse(main, out var channels) || channels is < 1 or > 9) return null;

            if (int.TryParse(sub, out var lfe) && lfe is > 0 and < 3) channels += lfe;

            return channels;
        }

        private static bool TryQuality(string token, out string? quality)
        {
            quality = null;

            var match = Resolution.Match(token);
            if (match.Success)
            {
                quality = match.Groups["res"].Value.ToLowerInvariant();
                return MediaFlags.Normalise(quality) is not null;
            }

            if (MediaFlags.Normalise(token) is not null &&
                token.ToLowerInvariant() is "4k" or "uhd" or "2k" or "qhd" or "fullhd")
            {
                quality = token.ToLowerInvariant();
                return true;
            }

            return false;
        }

        private static bool TrySource(string token, out string? source, out int rank)
        {
            foreach (var (candidate, label, candidateRank) in Sources)
            {
                if (string.Equals(token, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    source = label;
                    rank = candidateRank;
                    return true;
                }
            }

            source = null;
            rank = int.MaxValue;
            return false;
        }
    }
}
