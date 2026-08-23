using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class MediaFlagsTests
    {
        /// <summary>
        /// The rung a picture lands on. Classified on the wider dimension scaled to 16:9, because
        /// height alone calls a 1920x800 scope film standard definition — which is most of the
        /// blockbusters anybody owns.
        /// </summary>
        [Theory]
        [InlineData(3840, 2160, "4K")]
        [InlineData(4096, 1716, "4K")]      // DCI 4K, scope
        [InlineData(3840, 1600, "4K")]      // UHD, scope
        [InlineData(2560, 1440, "2K")]
        [InlineData(2048, 1080, "1080p")]   // DCI 2K is a 1080-line picture, not a 2K one
        [InlineData(1920, 1080, "1080p")]
        [InlineData(1920, 800, "1080p")]    // 2.39:1 at 1080p — the case a height ladder fails
        [InlineData(1280, 720, "720p")]
        [InlineData(1024, 576, "SD")]
        [InlineData(720, 576, "SD")]
        public void Resolution_is_named_the_way_people_say_it(int width, int height, string expected)
        {
            Assert.Equal(expected, MediaFlags.Quality(width, height));
        }

        [Fact]
        public void An_unmeasured_picture_has_no_quality()
        {
            Assert.Null(MediaFlags.Quality(null, null));
            Assert.Null(MediaFlags.Quality(0, 0));
        }

        /// <summary>
        /// A filename's claim has to land on the same rung as the equivalent measurement, or the
        /// same film reads as "2160p" from a scan and "4K" from a server.
        /// </summary>
        [Theory]
        [InlineData("2160p", "4K")]
        [InlineData("4k", "4K")]
        [InlineData("UHD", "4K")]
        [InlineData("1440p", "2K")]
        [InlineData("1080p", "1080p")]
        [InlineData("720p", "720p")]
        [InlineData("480p", "SD")]
        public void A_claimed_resolution_lands_on_the_same_ladder(string claimed, string expected)
        {
            Assert.Equal(expected, MediaFlags.Normalise(claimed));
        }

        [Fact]
        public void A_measurement_beats_a_filenames_claim()
        {
            // The name says 4K and the container says 1080p. The container wins, because one of
            // them counted the pixels.
            var info = new MediaInfo { Width = 1920, Height = 1080, ClaimedQuality = "2160p" };

            Assert.Equal("1080p", MediaFlags.For(info).First().Text);
        }

        [Fact]
        public void A_claim_is_labelled_as_a_claim_and_a_measurement_is_not()
        {
            var claimed = MediaFlags.For(new MediaInfo { ClaimedQuality = "1080p" }).First();
            var measured = MediaFlags.For(new MediaInfo { Width = 1920, Height = 1080 }).First();

            Assert.Contains("according to the filename", claimed.Tip);
            Assert.Contains("1920×1080", measured.Tip);
        }

        [Fact]
        public void Plain_sdr_is_not_worth_a_badge()
        {
            // Every film ever made until recently is SDR. A chip saying so on all of them is noise.
            Assert.Null(MediaFlags.DynamicRange("SDR"));
            Assert.Null(MediaFlags.DynamicRange(null));
        }

        [Theory]
        [InlineData("HDR10", "HDR10")]
        [InlineData("DOVI", "DV")]
        [InlineData("HDR", "HDR")]
        [InlineData("HLG", "HLG")]
        public void Dynamic_range_is_shown_when_there_is_one(string reported, string expected)
        {
            Assert.Equal(expected, MediaFlags.DynamicRange(reported));
        }

        [Theory]
        [InlineData(6, "5.1")]
        [InlineData(8, "7.1")]
        [InlineData(2, "2.0")]
        public void Channels_are_shown_as_the_layout_people_say(int channels, string expected)
        {
            Assert.Equal(expected, MediaFlags.ChannelLayout(channels));
        }

        [Fact]
        public void Atmos_replaces_the_codec_it_rides_on()
        {
            // "TRUEHD ATMOS 7.1" is three facts in a space meant for one, and Atmos is the fact
            // somebody is choosing the track for.
            var info = new MediaInfo { AudioCodec = "truehd", AudioChannels = 8, HasAtmos = true };

            Assert.Equal("ATMOS 7.1", MediaFlags.AudioLabel(info));
        }

        [Fact]
        public void The_codec_is_shown_when_there_is_no_atmos()
        {
            Assert.Equal("DTS-HD 5.1", MediaFlags.AudioLabel(new MediaInfo
            {
                AudioCodec = "dtshd",
                AudioChannels = 6
            }));
        }

        [Fact]
        public void The_same_language_under_two_codes_earns_one_badge()
        {
            var info = new MediaInfo { AudioLanguages = { "fre", "fra", "eng" } };

            var languages = MediaFlags.For(info).Where(f => f.IsLanguage).Select(f => f.Text).ToList();

            Assert.Equal(new[] { "FR", "EN" }, languages);
        }

        [Fact]
        public void Audio_and_subtitle_languages_are_told_apart()
        {
            var info = new MediaInfo
            {
                AudioLanguages = { "eng" },
                SubtitleLanguages = { "fre" }
            };

            var flags = MediaFlags.For(info);

            var audio = Assert.Single(flags, f => f.IsLanguage);
            var subtitle = Assert.Single(flags, f => f.IsSubtitle);

            Assert.Equal("EN", audio.Text);
            Assert.StartsWith("Audio:", audio.Tip);
            Assert.Equal("FR", subtitle.Text);
            Assert.StartsWith("Subtitles:", subtitle.Tip);
        }

        /// <summary>
        /// Without a label the row ends "EN FR ES DE EN FR" — heard in four languages, readable in
        /// two — as six chips differing only by a fill, which reads as the app having printed the
        /// same thing twice.
        /// </summary>
        [Fact]
        public void Each_run_of_language_badges_is_labelled_once()
        {
            var info = new MediaInfo
            {
                AudioLanguages = { "eng", "fra" },
                SubtitleLanguages = { "eng", "spa" }
            };

            var labels = MediaFlags.For(info).Select(f => f.GroupLabel).ToList();

            Assert.Equal(new[] { "HEARD IN", "", "SUBS", "" }, labels);
        }

        [Fact]
        public void The_picture_and_sound_badges_are_not_labelled()
        {
            var info = new MediaInfo { Width = 1920, Height = 1080, AudioCodec = "dts", AudioChannels = 6 };

            Assert.All(MediaFlags.For(info), f => Assert.False(f.HasGroupLabel));
        }

        [Fact]
        public void A_film_with_more_languages_than_fit_gets_a_count_and_keeps_the_rest_in_the_tooltip()
        {
            var info = new MediaInfo
            {
                AudioLanguages = { "eng", "fre", "deu", "spa", "ita", "jpn", "kor" }
            };

            var languages = MediaFlags.For(info).Where(f => f.IsLanguage).ToList();

            Assert.Equal(6, languages.Count);
            Assert.Equal("+2", languages[^1].Text);
            Assert.Contains("Japanese", languages[^1].Tip);
            Assert.Contains("Korean", languages[^1].Tip);
        }

        [Theory]
        [InlineData(26_500_000_000L, "24.7 GB")]
        [InlineData(1_400_000_000L, "1.3 GB")]
        [InlineData(900L, "900 B")]
        public void Size_is_shown_in_the_unit_that_keeps_it_readable(long bytes, string expected)
        {
            Assert.Equal(expected, MediaFlags.FileSize(bytes));
        }

        [Fact]
        public void Size_is_formatted_invariantly_regardless_of_the_current_locale()
        {
            var original = Thread.CurrentThread.CurrentCulture;
            try
            {
                // A comma-decimal locale must not produce "24,7 GB" on one machine and "24.7 GB"
                // on another.
                Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

                Assert.Equal("24.7 GB", MediaFlags.FileSize(26_500_000_000L));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Fact]
        public void An_unmeasured_file_has_no_size()
        {
            Assert.Null(MediaFlags.FileSize(null));
            Assert.Null(MediaFlags.FileSize(0));
        }

        [Fact]
        public void A_film_nothing_has_described_gets_no_badges_at_all()
        {
            Assert.Empty(MediaFlags.For(null));
            Assert.Empty(MediaFlags.For(new MediaInfo()));
        }

        [Fact]
        public void The_picture_comes_before_the_sound_and_the_sound_before_the_languages()
        {
            var info = new MediaInfo
            {
                Width = 3840,
                Height = 2160,
                VideoRange = "HDR10",
                VideoCodec = "hevc",
                AudioCodec = "truehd",
                AudioChannels = 8,
                HasAtmos = true,
                AudioLanguages = { "eng" }
            };

            var texts = MediaFlags.For(info).Select(f => f.Text).ToList();

            Assert.Equal(new List<string> { "4K", "HDR10", "HEVC", "ATMOS 7.1", "EN" }, texts);
        }
    }
}
