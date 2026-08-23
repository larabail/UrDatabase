using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns what is known about a copy of a film into the row of badges beside the year and the
    /// runtime: how big the picture is, how it sounds, and which languages it plays in.
    /// </summary>
    /// <remarks>
    /// A service rather than a stack of conditionally visible panels in the view, for the same
    /// reason <see cref="DetailFacts"/> is one, plus a sharper one of its own: the resolution
    /// ladder is a judgement call with no single right answer, and it has to give the same answer
    /// for a film measured by Jellyfin and for the identical film read off a filename. Two ladders
    /// would put "4K" on one copy and "2160p" on another and leave the user to work out whether
    /// those are the same thing.
    /// </remarks>
    public static class MediaFlags
    {
        /// <summary>
        /// How many languages are named before the badges stop and a count takes over. A film with
        /// thirty audio tracks is a real thing a server holds, and thirty chips is not a row.
        /// </summary>
        private const int MaxLanguages = 5;

        /// <summary>
        /// Names a picture size the way people say it out loud.
        /// </summary>
        /// <remarks>
        /// Classified on the wider of the two dimensions, scaled to 16:9, rather than on height.
        /// Height alone is wrong for the commonest case there is: a 2.39:1 film mastered at 1080p
        /// is 1920x800, and a ladder reading 800 pixels of height calls it standard definition.
        ///
        /// The rungs are named as release listings and streaming services name them, so "4K" and
        /// "2K" appear where a user expects them and the pixel counts underneath are the ones the
        /// formats actually use — 3840 and 4096 wide for 4K, 2560 for 2K, 1920 for 1080p. DCI's
        /// 2048x1080 lands on 1080p deliberately: it is a 1080-line picture and calling it 2K
        /// would put it above a 2560-wide one that has more of everything.
        /// </remarks>
        public static string? Quality(int? width, int? height)
        {
            var w = width is > 0 ? width.Value : 0;
            var h = height is > 0 ? height.Value : 0;
            if (w == 0 && h == 0) return null;

            var effective = Math.Max(w, (int)Math.Round(h * 16.0 / 9.0));

            return effective switch
            {
                >= 3400 => "4K",
                >= 2400 => "2K",
                >= 1700 => "1080p",
                >= 1100 => "720p",
                _ => "SD"
            };
        }

        /// <summary>
        /// The badges for a film, in reading order: what the picture is, then what it sounds like,
        /// then what it can be heard and read in. Never null, and empty for a film nothing has
        /// measured — which is every scanned film whose name says nothing.
        /// </summary>
        public static IReadOnlyList<MediaFlag> For(MediaInfo? info)
        {
            var flags = new List<MediaFlag>();
            if (info is null) return flags;

            // A measurement beats a claim. Jellyfin counted the pixels; a filename is repeating
            // whatever the person who encoded it typed, and they are wrong often enough that the
            // two are never averaged or shown side by side.
            var quality = Quality(info.Width, info.Height) ?? Normalise(info.ClaimedQuality);
            if (quality is not null)
            {
                flags.Add(new MediaFlag
                {
                    Text = quality,
                    Kind = MediaFlagKind.Picture,
                    Tip = QualityTip(quality, info)
                });
            }

            var range = DynamicRange(info.VideoRange);
            if (range is not null)
            {
                flags.Add(new MediaFlag
                {
                    Text = range,
                    Kind = MediaFlagKind.Picture,
                    Tip = range == "DV" ? "Dolby Vision" : "High dynamic range"
                });
            }

            var codec = VideoCodecName(info.VideoCodec);
            if (codec is not null)
            {
                flags.Add(new MediaFlag { Text = codec, Kind = MediaFlagKind.Picture, Tip = "Video codec" });
            }

            if (!string.IsNullOrWhiteSpace(info.Source))
            {
                flags.Add(new MediaFlag
                {
                    Text = info.Source.Trim().ToUpperInvariant(),
                    Kind = MediaFlagKind.Picture,
                    Tip = "Where this copy came from"
                });
            }

            var audio = AudioLabel(info);
            if (audio is not null)
            {
                flags.Add(new MediaFlag { Text = audio, Kind = MediaFlagKind.Sound, Tip = "Audio track" });
            }

            var size = FileSize(info.SizeBytes);
            if (size is not null)
            {
                flags.Add(new MediaFlag { Text = size, Kind = MediaFlagKind.Picture, Tip = "Size on disk" });
            }

            AddLanguages(flags, info.AudioLanguages, MediaFlagKind.Language, "Audio", "HEARD IN");
            AddLanguages(flags, info.SubtitleLanguages, MediaFlagKind.Subtitle, "Subtitles", "SUBS");

            return flags;
        }

        private static void AddLanguages(
            List<MediaFlag> flags,
            IEnumerable<string>? languages,
            MediaFlagKind kind,
            string what,
            string groupLabel)
        {
            var codes = Codes(languages);
            if (codes.Count == 0) return;

            var first = true;

            foreach (var code in codes.Take(MaxLanguages))
            {
                flags.Add(new MediaFlag
                {
                    Text = code.Code,
                    Kind = kind,
                    Tip = $"{what}: {code.Name}",
                    GroupLabel = first ? groupLabel : ""
                });

                first = false;
            }

            var remaining = codes.Count - MaxLanguages;
            if (remaining > 0)
            {
                flags.Add(new MediaFlag
                {
                    Text = $"+{remaining.ToString(CultureInfo.InvariantCulture)}",
                    Kind = kind,
                    Tip = $"{what}: {string.Join(", ", codes.Skip(MaxLanguages).Select(c => c.Name))}"
                });
            }
        }

        /// <summary>
        /// The distinct languages in a list, in the order the source gave them. Distinct by code
        /// rather than by tag, because <c>fre</c> and <c>fra</c> are one language and a film with
        /// both would otherwise wear the same badge twice.
        /// </summary>
        internal static List<(string Code, string Name)> Codes(IEnumerable<string>? languages)
        {
            var result = new List<(string Code, string Name)>();
            if (languages is null) return result;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var language in languages)
            {
                var code = LanguageTag.Code(language);
                if (code is null || !seen.Add(code)) continue;

                result.Add((code, LanguageTag.Name(language)));
            }

            return result;
        }

        /// <summary>
        /// Folds a filename's resolution token onto the same ladder the measured sizes use, so
        /// "2160p" and a 3840-pixel-wide picture both read as 4K.
        /// </summary>
        internal static string? Normalise(string? claimed)
        {
            var value = claimed?.Trim();
            if (string.IsNullOrEmpty(value)) return null;

            return value.ToLowerInvariant() switch
            {
                "2160p" or "2160i" or "4k" or "uhd" => "4K",
                "1440p" or "2k" or "qhd" => "2K",
                "1080p" or "1080i" or "fullhd" => "1080p",
                "720p" or "720i" or "hd" => "720p",
                "576p" or "576i" or "480p" or "480i" or "360p" or "sd" => "SD",
                _ => null
            };
        }

        /// <summary>
        /// The badge for the dynamic range, or null for a picture that is plain SDR. "SDR" is
        /// every film that has ever been made until recently and says nothing worth a chip.
        /// </summary>
        internal static string? DynamicRange(string? range)
        {
            var value = range?.Trim();
            if (string.IsNullOrEmpty(value)) return null;

            // Jellyfin reports "DOVI", "HDR10", "HDR10Plus", "HLG" and "SDR" on VideoRangeType,
            // and the plainer "HDR"/"SDR" on VideoRange. Both reach here.
            return value.ToLowerInvariant() switch
            {
                "dovi" or "dv" or "dolbyvision" or "dolby vision" => "DV",
                "hdr10plus" or "hdr10+" => "HDR10+",
                "hdr10" => "HDR10",
                "hlg" => "HLG",
                "hdr" => "HDR",
                _ => null
            };
        }

        internal static string? VideoCodecName(string? codec)
        {
            var value = codec?.Trim();
            if (string.IsNullOrEmpty(value)) return null;

            return value.ToLowerInvariant() switch
            {
                "hevc" or "h265" or "h.265" or "x265" => "HEVC",
                "h264" or "h.264" or "x264" or "avc" or "avc1" => "H.264",
                "av1" => "AV1",
                "vp9" => "VP9",
                "mpeg2video" or "mpeg2" => "MPEG-2",
                "vc1" or "vc-1" => "VC-1",
                "xvid" or "divx" or "mpeg4" => "MPEG-4",
                _ => null
            };
        }

        /// <summary>
        /// The sound, as one badge: the codec, then the layout. Atmos replaces the codec because
        /// it is the thing a person is choosing the track for, and "TRUEHD ATMOS 7.1" is three
        /// facts in a space meant for one.
        /// </summary>
        internal static string? AudioLabel(MediaInfo info)
        {
            var codec = info.HasAtmos ? "ATMOS" : AudioCodecName(info.AudioCodec);
            var layout = ChannelLayout(info.AudioChannels);

            if (codec is null && layout is null) return null;
            if (codec is null) return layout;
            if (layout is null) return codec;

            return $"{codec} {layout}";
        }

        internal static string? AudioCodecName(string? codec)
        {
            var value = codec?.Trim();
            if (string.IsNullOrEmpty(value)) return null;

            return value.ToLowerInvariant() switch
            {
                "truehd" or "mlp" => "TRUEHD",
                "dts" => "DTS",
                "dtshd" or "dts-hd" or "dtshd_ma" or "dts-hd ma" => "DTS-HD",
                "eac3" or "ddp" or "ec-3" => "DDP",
                "ac3" or "dd" => "DD",
                "aac" => "AAC",
                "flac" => "FLAC",
                "opus" => "OPUS",
                "mp3" => "MP3",
                "pcm" or "lpcm" => "PCM",
                "vorbis" => "VORBIS",
                _ => null
            };
        }

        /// <summary>
        /// Channels as the layout people say — 6 channels is "5.1", because one of them is the
        /// subwoofer and nobody calls it six channel sound.
        /// </summary>
        internal static string? ChannelLayout(int? channels) => channels switch
        {
            1 => "1.0",
            2 => "2.0",
            3 => "2.1",
            6 => "5.1",
            7 => "6.1",
            8 => "7.1",
            _ => null
        };

        /// <summary>
        /// The size on disk, to one decimal place, in whichever unit keeps it readable. Powers of
        /// 1024 and named GB rather than GiB, because that is what every file manager on both of
        /// this app's platforms shows and a second convention would only look like a bug.
        /// </summary>
        internal static string? FileSize(long? bytes)
        {
            if (bytes is not > 0) return null;

            double value = bytes.Value;
            string[] units = { "B", "KB", "MB", "GB", "TB" };

            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            // Whole numbers below a megabyte: "0.9 KB" is noise where "900 B" is a fact.
            var format = unit <= 1 ? "0" : "0.0";
            return $"{value.ToString(format, CultureInfo.InvariantCulture)} {units[unit]}";
        }

        /// <summary>
        /// The real pixel dimensions behind a resolution badge, when they were measured. "4K" is a
        /// bracket, and a person looking closely wants to know which side of it they got.
        /// </summary>
        private static string QualityTip(string quality, MediaInfo info)
        {
            if (info.Width is > 0 && info.Height is > 0)
            {
                return $"{quality} — {info.Width.Value.ToString(CultureInfo.InvariantCulture)}" +
                       $"×{info.Height.Value.ToString(CultureInfo.InvariantCulture)}";
            }

            // No measurement means this came off a filename, and a filename is a claim.
            return $"{quality}, according to the filename";
        }
    }
}
