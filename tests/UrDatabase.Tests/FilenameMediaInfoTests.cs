using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Reading a release filename. The shapes here are taken from real libraries, because that is
    /// the only way to be confident about a parser whose input nobody controls.
    /// </summary>
    public class FilenameMediaInfoTests
    {
        [Fact]
        public void Reads_everything_a_full_release_name_carries()
        {
            var info = FilenameMediaInfo.Parse("The.Matrix.1999.2160p.UHD.BluRay.REMUX.HDR10.HEVC.TrueHD.7.1.Atmos-GROUP.mkv");

            Assert.Equal("2160p", info.ClaimedQuality);
            Assert.Equal("hevc", info.VideoCodec);
            Assert.Equal("HDR10", info.VideoRange);
            Assert.Equal("truehd", info.AudioCodec);
            Assert.Equal(8, info.AudioChannels);
            Assert.True(info.HasAtmos);
            Assert.Equal("Remux", info.Source);
            Assert.Equal("mkv", info.Container);
        }

        [Fact]
        public void Reads_a_codec_and_channels_welded_into_one_token()
        {
            var info = FilenameMediaInfo.Parse("Arrival (2016) 1080p WEB-DL DDP5.1 x264.mkv");

            Assert.Equal("1080p", info.ClaimedQuality);
            Assert.Equal("h264", info.VideoCodec);
            Assert.Equal("eac3", info.AudioCodec);
            Assert.Equal(6, info.AudioChannels);
            Assert.Equal("WEB-DL", info.Source);
        }

        /// <summary>
        /// The failure this parser exists to avoid. "Italian" is a language word and also half of
        /// a very famous title, and a whole-filename scan tags Michael Caine's heist film as an
        /// Italian-language release with no way for anybody to guess why.
        /// </summary>
        [Fact]
        public void A_language_word_in_the_title_is_not_a_language()
        {
            var info = FilenameMediaInfo.Parse("The Italian Job (1969) 1080p BluRay x264.mkv");

            Assert.Empty(info.AudioLanguages);
            Assert.Equal("1080p", info.ClaimedQuality);
        }

        [Fact]
        public void A_language_after_the_year_is_a_language()
        {
            var info = FilenameMediaInfo.Parse("Amelie.2001.1080p.BluRay.FRENCH.ENGLISH.x264.mkv");

            Assert.Equal(new[] { "FRENCH", "ENGLISH" }, info.AudioLanguages);
        }

        [Fact]
        public void A_film_whose_name_says_nothing_claims_nothing()
        {
            var info = FilenameMediaInfo.Parse("Casablanca.mkv");

            Assert.Null(info.ClaimedQuality);
            Assert.Null(info.VideoCodec);
            Assert.Empty(info.AudioLanguages);

            // The container is read from the extension and needs no tag region to find.
            Assert.Equal("mkv", info.Container);
        }

        /// <summary>
        /// Without a year the region has to start somewhere, and it starts at the first token that
        /// could not be part of a title. No film is called "1080p".
        /// </summary>
        [Fact]
        public void A_name_with_no_year_cuts_at_the_first_token_no_title_could_contain()
        {
            var info = FilenameMediaInfo.Parse("Some Film 1080p BluRay x265 GERMAN.mkv");

            Assert.Equal("1080p", info.ClaimedQuality);
            Assert.Equal("hevc", info.VideoCodec);
            Assert.Equal(new[] { "GERMAN" }, info.AudioLanguages);
        }

        [Fact]
        public void The_last_plausible_year_is_the_boundary()
        {
            // "2049" is part of the title. The release year behind it is where the tags start.
            var info = FilenameMediaInfo.Parse("Blade Runner 2049 (2017) 2160p DV HEVC.mkv");

            Assert.Equal("2160p", info.ClaimedQuality);
            Assert.Equal("DOVI", info.VideoRange);
            Assert.Equal("hevc", info.VideoCodec);
        }

        [Fact]
        public void A_codec_token_is_never_read_as_a_language()
        {
            // "DD" is Dolby Digital. There is no language whose code is DD, and it must not become
            // one by being two letters long.
            var info = FilenameMediaInfo.Parse("Heat.1995.1080p.BluRay.DD5.1.x264.mkv");

            Assert.Empty(info.AudioLanguages);
            Assert.Equal("ac3", info.AudioCodec);
            Assert.Equal(6, info.AudioChannels);
        }

        [Fact]
        public void A_bare_channel_layout_is_read()
        {
            var info = FilenameMediaInfo.Parse("Dune.2021.2160p.WEBRip.DTS-HD.5.1.x265.mkv");

            Assert.Equal("dtshd", info.AudioCodec);
            Assert.Equal(6, info.AudioChannels);
            Assert.Equal("WEBRip", info.Source);
        }

        [Fact]
        public void The_more_specific_source_wins_where_a_release_claims_two()
        {
            // A remux is a Blu-ray too, and "Remux" is the informative half of that claim.
            Assert.Equal("Remux", FilenameMediaInfo.Parse("Alien.1979.2160p.REMUX.BluRay.mkv").Source);
        }

        [Fact]
        public void A_windows_path_parses_on_a_unix_host()
        {
            // Windows paths reach a macOS build through configuration and test data alike, and
            // Path.GetFileName only honours the host's separator.
            var info = FilenameMediaInfo.Parse(@"D:\Films\Heat.1995.1080p.BluRay.x264.mkv");

            Assert.Equal("1080p", info.ClaimedQuality);
            Assert.Equal("h264", info.VideoCodec);
        }

        [Fact]
        public void Nothing_at_all_is_not_a_failure()
        {
            var info = FilenameMediaInfo.Parse(null);

            Assert.False(info.HasAnything);
            Assert.Equal(new string[0], info.AudioLanguages.ToArray());
        }

        [Fact]
        public void The_same_language_twice_earns_one_entry()
        {
            var info = FilenameMediaInfo.Parse("Film.2015.1080p.ENGLISH.english.x264.mkv");

            Assert.Single(info.AudioLanguages);
        }

        /// <summary>
        /// The size is the one thing about a local copy that is not a claim, so it is measured
        /// rather than read — through a seam, so this needs no real file.
        /// </summary>
        [Fact]
        public void A_local_file_is_measured_as_well_as_read()
        {
            var info = LocalMedia.Describe("Heat.1995.1080p.BluRay.x264.mkv", _ => 8_000_000_000L);

            Assert.NotNull(info);
            Assert.Equal(8_000_000_000L, info!.SizeBytes);
            Assert.Equal("1080p", info.ClaimedQuality);
        }

        [Fact]
        public void A_file_that_cannot_be_measured_still_reports_what_its_name_claims()
        {
            var info = LocalMedia.Describe("Heat.1995.1080p.BluRay.x264.mkv", _ => null);

            Assert.NotNull(info);
            Assert.Null(info!.SizeBytes);
            Assert.Equal("1080p", info.ClaimedQuality);
        }

        [Fact]
        public void A_film_with_no_file_is_described_as_nothing_rather_than_as_an_empty_row()
        {
            Assert.Null(LocalMedia.Describe(null));
            Assert.Null(LocalMedia.Describe("   "));
        }

        [Fact]
        public void A_file_whose_name_and_size_say_nothing_is_described_as_nothing()
        {
            // No extension this app recognises, no tags, no size: there is nothing to print, and
            // an empty badge row would only look like a failed lookup.
            Assert.Null(LocalMedia.Describe("Casablanca", _ => null));
        }

        [Fact]
        public void A_real_file_on_disk_is_measured()
        {
            var dir = Path.Combine(Path.GetTempPath(), "urdb-media-" + Path.GetRandomFileName());
            Directory.CreateDirectory(dir);

            try
            {
                var path = Path.Combine(dir, "Heat.1995.1080p.BluRay.x264.mkv");
                File.WriteAllBytes(path, new byte[2048]);

                var info = LocalMedia.Describe(path);

                Assert.NotNull(info);
                Assert.Equal(2048, info!.SizeBytes);
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}
