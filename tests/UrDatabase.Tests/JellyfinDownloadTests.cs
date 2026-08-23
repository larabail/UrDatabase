using System;
using System.Globalization;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The naming rules a downloaded film is saved under.
    ///
    /// These matter more than they look. The local name is what the filename parser reads when the
    /// finished download is catalogued, so a name that loses the year or gains a stray character
    /// does not merely look untidy — it lands the film on the wrong catalogue row, or on a new one
    /// beside the film it is a copy of.
    /// </summary>
    public class JellyfinDownloadTests
    {
        [Fact]
        public void Builds_the_convention_the_parser_reads()
        {
            Assert.Equal("Arrival (2016).mkv", JellyfinDownload.BuildFileName("Arrival", 2016, ".mkv"));
        }

        [Fact]
        public void A_film_with_no_year_simply_has_none()
        {
            Assert.Equal("Arrival.mkv", JellyfinDownload.BuildFileName("Arrival", null, ".mkv"));
        }

        [Theory]
        [InlineData("Face/Off", "Face Off")]
        [InlineData(@"Either\Or", "Either Or")]
        [InlineData("8½: A Film", "8½ A Film")]
        [InlineData("What?", "What")]
        [InlineData("*Batteries Not Included", "Batteries Not Included")]
        [InlineData("A \"Quoted\" Title", "A Quoted Title")]
        public void Strips_characters_a_filesystem_will_not_take(string title, string expected)
        {
            Assert.Equal(expected, JellyfinDownload.SanitizeStem(title));
        }

        [Fact]
        public void An_illegal_character_becomes_a_space_rather_than_vanishing()
        {
            // "FaceOff" would be a different film's name, and a worse thing to find in a folder.
            Assert.Equal("Face Off", JellyfinDownload.SanitizeStem("Face/Off"));
        }

        [Theory]
        [InlineData("NUL")]
        [InlineData("con")]
        [InlineData("Aux")]
        [InlineData("COM1")]
        public void Escapes_the_names_windows_reserves(string title)
        {
            var stem = JellyfinDownload.SanitizeStem(title);

            Assert.StartsWith("_", stem, StringComparison.Ordinal);
            Assert.EndsWith(title, stem, StringComparison.Ordinal);
        }

        [Fact]
        public void A_reserved_name_is_still_escaped_when_it_carries_an_extension()
        {
            Assert.Equal("_NUL.something", JellyfinDownload.SanitizeStem("NUL.something"));
        }

        [Fact]
        public void Trailing_dots_and_spaces_go_because_windows_drops_them_silently()
        {
            // Left on, the file written and the file looked for afterwards are different names.
            Assert.Equal("Movie", JellyfinDownload.SanitizeStem("Movie. "));
            Assert.Equal("Movie", JellyfinDownload.SanitizeStem("Movie..."));
        }

        [Fact]
        public void A_title_that_sanitizes_away_to_nothing_still_gets_a_name()
        {
            Assert.Equal("Untitled", JellyfinDownload.SanitizeStem("???"));
            Assert.Equal("Untitled", JellyfinDownload.SanitizeStem(""));
            Assert.Equal("Untitled", JellyfinDownload.SanitizeStem(null));
        }

        [Fact]
        public void Long_titles_are_cut_to_something_every_filesystem_can_hold()
        {
            var stem = JellyfinDownload.SanitizeStem(new string('a', 500));

            Assert.Equal(JellyfinDownload.MaxStemLength, stem.Length);
        }

        [Theory]
        [InlineData(".mkv", ".mkv")]
        [InlineData("mkv", ".mkv")]
        [InlineData("MKV", ".mkv")]
        [InlineData(" .Mp4 ", ".mp4")]
        [InlineData("film.avi", ".avi")]
        public void Normalizes_an_extension_from_any_shape(string input, string expected)
        {
            Assert.Equal(expected, JellyfinDownload.NormalizeExtension(input));
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData(".")]
        [InlineData(".verylong")]
        [InlineData(".m k v")]
        [InlineData("../../etc")]
        public void Falls_back_rather_than_trusting_a_strange_extension(string? input)
        {
            Assert.Equal(JellyfinDownload.DefaultExtension, JellyfinDownload.NormalizeExtension(input));
        }

        [Fact]
        public void Takes_the_container_from_the_servers_filename()
        {
            Assert.Equal(".mp4", JellyfinDownload.ResolveExtension("Arrival (2016).mp4", container: null));
        }

        [Fact]
        public void Falls_back_to_the_items_container_when_the_response_names_nothing()
        {
            Assert.Equal(".avi", JellyfinDownload.ResolveExtension(null, container: "avi"));
        }

        [Fact]
        public void Falls_back_to_matroska_when_the_server_says_nothing_at_all()
        {
            Assert.Equal(".mkv", JellyfinDownload.ResolveExtension(null, container: null));
        }

        /// <summary>
        /// The remote filename is not trusted for anything but its extension. A server — or
        /// something between this app and one — that answers with a name full of separators must
        /// not be able to steer the write out of the download folder.
        /// </summary>
        [Theory]
        [InlineData("../../../etc/passwd.mp4")]
        [InlineData(@"..\..\windows\system32\evil.mp4")]
        [InlineData("/etc/cron.d/payload.mp4")]
        public void A_hostile_filename_contributes_nothing_but_its_extension(string fileName)
        {
            var folder = Path.Combine(Path.GetTempPath(), "urdb-download-target");
            var extension = JellyfinDownload.ResolveExtension(fileName, container: null);
            var path = JellyfinDownload.BuildPath(folder, "Arrival", 2016, extension);

            Assert.Equal(".mp4", extension);
            Assert.Equal(folder, Path.GetDirectoryName(path));
            Assert.Equal("Arrival (2016).mp4", Path.GetFileName(path));
        }

        [Fact]
        public void A_path_lands_in_the_folder_it_was_given()
        {
            var folder = Path.Combine(Path.GetTempPath(), "urdb-films");
            var path = JellyfinDownload.BuildPath(folder, "Arrival", 2016, ".mkv");

            Assert.Equal(Path.Combine(folder, "Arrival (2016).mkv"), path);
        }

        [Fact]
        public void An_unconfigured_folder_falls_back_to_the_platform_default()
        {
            var path = JellyfinDownload.BuildPath("   ", "Arrival", 2016, ".mkv");

            Assert.Equal(PlatformPaths.DefaultDownloadFolder, Path.GetDirectoryName(path));
        }

        [Fact]
        public void The_partial_name_is_not_a_video_file_as_far_as_a_scan_is_concerned()
        {
            var partial = JellyfinDownload.PartialPathFor("/films/Arrival (2016).mkv");

            Assert.Equal("/films/Arrival (2016).mkv.part", partial);
            Assert.False(ScanService.IsVideoFile(partial));
        }

        [Theory]
        [InlineData(0, "0 B")]
        [InlineData(512, "512 B")]
        [InlineData(1024, "1.0 KB")]
        [InlineData(1536, "1.5 KB")]
        [InlineData(1048576, "1.0 MB")]
        [InlineData(5L * 1024 * 1024 * 1024, "5.0 GB")]
        public void Describes_a_size_the_way_a_file_manager_does(long bytes, string expected)
        {
            // Binary units, to agree with Finder and Explorer rather than with a disk vendor.
            Assert.Equal(expected, JellyfinDownload.DescribeSize(bytes).Replace(',', '.'));
        }

        [Fact]
        public void Progress_reports_no_fraction_when_the_size_is_unknown()
        {
            var progress = new JellyfinDownloadProgress(1024, null);

            Assert.Null(progress.Fraction);
            Assert.Equal("1.0 KB", progress.Describe().Replace(',', '.'));
        }

        [Fact]
        public void Progress_reports_a_fraction_when_the_size_is_known()
        {
            var progress = new JellyfinDownloadProgress(512, 1024);

            Assert.Equal(0.5, progress.Fraction!.Value, 3);
            Assert.Contains("50%", progress.Describe(), StringComparison.Ordinal);
        }

        [Fact]
        public void Progress_never_exceeds_the_whole_however_much_arrives()
        {
            var progress = new JellyfinDownloadProgress(2048, 1024);

            Assert.Equal(1.0, progress.Fraction!.Value, 3);
        }
    }

    /// <summary>
    /// Finding a film that is already downloaded. Separate because it touches the filesystem.
    /// </summary>
    public class JellyfinDownloadLookupTests : IDisposable
    {
        private readonly string _folder;

        public JellyfinDownloadLookupTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "urdb-dl-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try { Directory.Delete(_folder, recursive: true); } catch { }
        }

        [Fact]
        public void Finds_a_downloaded_film_whatever_container_it_turned_out_to_be()
        {
            // The extension is not known until the server answers, so the search is by stem.
            File.WriteAllText(Path.Combine(_folder, "Arrival (2016).mp4"), "x");

            var found = JellyfinDownload.FindExisting(_folder, "Arrival", 2016);

            Assert.Equal(Path.Combine(_folder, "Arrival (2016).mp4"), found);
        }

        [Fact]
        public void A_half_downloaded_film_does_not_count_as_downloaded()
        {
            File.WriteAllText(Path.Combine(_folder, "Arrival (2016).mkv.part"), "x");

            Assert.Null(JellyfinDownload.FindExisting(_folder, "Arrival", 2016));
        }

        [Fact]
        public void A_different_year_is_a_different_film()
        {
            File.WriteAllText(Path.Combine(_folder, "The Thing (1982).mkv"), "x");

            Assert.Null(JellyfinDownload.FindExisting(_folder, "The Thing", 2011));
        }

        [Fact]
        public void A_title_that_merely_starts_the_same_is_not_a_match()
        {
            File.WriteAllText(Path.Combine(_folder, "Arrival (2016) extras.mkv"), "x");

            Assert.Null(JellyfinDownload.FindExisting(_folder, "Arrival", 2016));
        }

        [Fact]
        public void A_folder_that_does_not_exist_means_not_downloaded()
        {
            Assert.Null(JellyfinDownload.FindExisting(
                Path.Combine(_folder, "nothing-here"), "Arrival", 2016));
        }

        [Fact]
        public void The_search_uses_the_sanitized_name_that_was_actually_written()
        {
            var stem = JellyfinDownload.SanitizeStem("Face/Off") +
                       " (" + 1997.ToString(CultureInfo.InvariantCulture) + ")";
            File.WriteAllText(Path.Combine(_folder, stem + ".mkv"), "x");

            Assert.NotNull(JellyfinDownload.FindExisting(_folder, "Face/Off", 1997));
        }
    }
}
