using System;
using System.IO;
using System.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Where a film goes on the server and what it is called when it gets there. All of it pure,
    /// none of it needing a connection.
    ///
    /// The separator cases are the ones that matter. These are remote paths, and a backslash in
    /// one does not fail — it succeeds, creating a single file on the server literally named
    /// <c>Arrival (2016)\Arrival (2016).mkv</c> inside the movies directory, which no Jellyfin
    /// scan will ever match and no listing will explain. That bug can only be caught here,
    /// because it only appears on Windows and the suite has to catch it from anywhere.
    /// </summary>
    public class JellyfinUploadTests
    {
        [Fact]
        public void A_film_gets_the_folder_and_name_Jellyfin_libraries_use()
        {
            var path = JellyfinUpload.BuildRemotePath("movies", "Arrival", 2016, "/home/someone/arrival.2016.1080p.mkv");

            Assert.Equal("movies/Arrival (2016)/Arrival (2016).mkv", path);
        }

        /// <summary>
        /// The whole point of building the path by hand rather than with
        /// <see cref="Path.Combine"/>, which produces a backslash on Windows.
        /// </summary>
        [Fact]
        public void Remote_paths_use_forward_slashes_on_every_platform()
        {
            var path = JellyfinUpload.BuildRemotePath("movies", "Arrival", 2016, "C:\\Films\\arrival.mkv");
            var folder = JellyfinUpload.BuildRemoteFolder("movies", "Arrival", 2016);

            Assert.DoesNotContain('\\', path);
            Assert.DoesNotContain('\\', folder);
            Assert.Equal("movies/Arrival (2016)/Arrival (2016).mkv", path);
        }

        [Fact]
        public void The_extension_comes_from_the_local_file_and_the_name_does_not()
        {
            var path = JellyfinUpload.BuildRemotePath("movies", "Arrival", 2016, "/films/Arrival.2016.WEB-DL.x265.MP4");

            Assert.Equal("movies/Arrival (2016)/Arrival (2016).mp4", path);
        }

        [Fact]
        public void A_film_with_no_year_simply_has_none_in_its_name()
        {
            var path = JellyfinUpload.BuildRemotePath("movies", "Arrival", null, "/films/arrival.mkv");

            Assert.Equal("movies/Arrival/Arrival.mkv", path);
        }

        /// <summary>
        /// The same sanitiser the download side uses, so a film that came down as
        /// <c>Face Off (1997).mkv</c> goes back up into <c>Face Off (1997)/</c>.
        /// </summary>
        [Fact]
        public void A_title_with_a_slash_in_it_does_not_become_a_directory()
        {
            var path = JellyfinUpload.BuildRemotePath("movies", "Face/Off", 1997, "/films/faceoff.mkv");

            Assert.Equal("movies/Face Off (1997)/Face Off (1997).mkv", path);
            Assert.Equal("Face Off", JellyfinDownload.SanitizeStem("Face/Off"));
        }

        [Theory]
        [InlineData("", "movies/Arrival (2016)/Arrival (2016).mkv")]
        [InlineData(null, "movies/Arrival (2016)/Arrival (2016).mkv")]
        [InlineData("movies/", "movies/Arrival (2016)/Arrival (2016).mkv")]
        [InlineData("/tank/movies", "/tank/movies/Arrival (2016)/Arrival (2016).mkv")]
        [InlineData("films\\hd", "films/hd/Arrival (2016)/Arrival (2016).mkv")]
        public void The_movies_directory_is_taken_as_configured(string? moviesPath, string expected)
        {
            Assert.Equal(expected, JellyfinUpload.BuildRemotePath(moviesPath, "Arrival", 2016, "/films/a.mkv"));
        }

        /// <summary>
        /// An account chrooted so its root holds <c>movies/</c> reaches the server's own
        /// <c>/tank/movies</c> as <c>movies</c>. Prefixing a slash would look right and land
        /// somewhere else entirely, so a relative path has to stay relative.
        /// </summary>
        [Fact]
        public void A_relative_movies_path_stays_relative()
        {
            Assert.Equal("movies", JellyfinUpload.NormalizeRemoteRoot("movies"));
            Assert.StartsWith("movies/", JellyfinUpload.BuildRemoteFolder("movies", "Arrival", 2016), StringComparison.Ordinal);
        }

        [Fact]
        public void An_absolute_movies_path_stays_absolute()
        {
            Assert.Equal("/tank/movies", JellyfinUpload.NormalizeRemoteRoot("/tank/movies/"));
            Assert.Equal("/tank/movies/Arrival (2016)", JellyfinUpload.BuildRemoteFolder("/tank/movies", "Arrival", 2016));
        }

        [Fact]
        public void The_partial_name_is_not_something_a_scan_reads_as_a_film()
        {
            var partial = JellyfinUpload.PartialPathFor("movies/Arrival (2016)/Arrival (2016).mkv");

            Assert.Equal("movies/Arrival (2016)/Arrival (2016).mkv.uploading", partial);
            Assert.False(ScanService.IsVideoFile(partial));
        }

        [Fact]
        public void Every_directory_that_has_to_be_made_is_listed_outermost_first()
        {
            var folder = JellyfinUpload.BuildRemoteFolder("films/hd", "Arrival", 2016);

            Assert.Equal(
                new[] { "films", "films/hd", "films/hd/Arrival (2016)" },
                JellyfinUpload.AncestorsOf(folder));
        }

        [Fact]
        public void An_absolute_folder_keeps_its_leading_slash_all_the_way_up()
        {
            Assert.Equal(
                new[] { "/tank", "/tank/movies", "/tank/movies/Arrival (2016)" },
                JellyfinUpload.AncestorsOf("/tank/movies/Arrival (2016)"));
        }

        [Fact]
        public void Nothing_has_to_be_made_for_an_empty_folder()
        {
            Assert.Empty(JellyfinUpload.AncestorsOf(""));
            Assert.Empty(JellyfinUpload.AncestorsOf("/"));
            Assert.Empty(JellyfinUpload.AncestorsOf(null));
        }

        [Fact]
        public void A_copy_already_on_the_server_is_recognised_whatever_its_extension()
        {
            var names = new[] { "Arrival (2016).mp4", "poster.jpg" };

            Assert.Equal("Arrival (2016).mp4", JellyfinUpload.FindExisting(names, "Arrival", 2016));
        }

        [Fact]
        public void A_different_film_in_the_same_directory_is_not_a_copy()
        {
            var names = new[] { "Arrival (2015).mkv", "Arrival Two (2016).mkv", "arrival.mkv" };

            Assert.Null(JellyfinUpload.FindExisting(names, "Arrival", 2016));
        }

        /// <summary>
        /// The file a failed attempt leaves behind. Reading it as the film would make a retry
        /// impossible — the app would insist the server already had something it does not.
        /// </summary>
        [Fact]
        public void A_leftover_partial_upload_is_not_a_copy()
        {
            var names = new[] { "Arrival (2016).mkv.uploading" };

            Assert.Null(JellyfinUpload.FindExisting(names, "Arrival", 2016));
        }

        [Fact]
        public void A_copy_is_matched_without_regard_to_case()
        {
            Assert.Equal("ARRIVAL (2016).MKV", JellyfinUpload.FindExisting(new[] { "ARRIVAL (2016).MKV" }, "Arrival", 2016));
        }

        [Fact]
        public void An_empty_directory_holds_no_copy()
        {
            Assert.Null(JellyfinUpload.FindExisting(Array.Empty<string>(), "Arrival", 2016));
            Assert.Null(JellyfinUpload.FindExisting(null, "Arrival", 2016));
        }

        /// <summary>
        /// This app hands paths to an operating system that will happily run a script, and it is
        /// about to copy one into somebody else's film library. The rule is the same one that
        /// governs linking a file, deliberately: two answers to "is this a film?" would eventually
        /// disagree.
        /// </summary>
        [Theory]
        [InlineData("/films/arrival.mkv", true)]
        [InlineData("/films/arrival.MP4", true)]
        [InlineData("/films/arrival.txt", false)]
        [InlineData("/films/arrival.sh", false)]
        [InlineData("/films/arrival", false)]
        public void Only_a_video_file_may_be_uploaded(string path, bool allowed)
        {
            var refusal = JellyfinUpload.DescribeRefusal(path, _ => true);

            Assert.Equal(allowed, refusal is null);
            if (!allowed) Assert.Contains("not a video file", refusal!, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_file_that_is_no_longer_there_is_refused_by_name()
        {
            var refusal = JellyfinUpload.DescribeRefusal("/films/arrival.mkv", _ => false);

            Assert.NotNull(refusal);
            Assert.Contains("arrival.mkv", refusal!, StringComparison.Ordinal);
        }

        [Fact]
        public void Joining_collapses_the_empty_and_doubled_separators_configuration_produces()
        {
            Assert.Equal("movies/Arrival (2016)", JellyfinUpload.JoinRemote("movies/", "/Arrival (2016)"));
            Assert.Equal("movies", JellyfinUpload.JoinRemote("movies", "", null));
            Assert.Equal("/a/b/c", JellyfinUpload.JoinRemote("/a/", "b", "/c/"));
        }

        [Fact]
        public void A_long_title_is_shortened_the_same_way_on_both_sides_of_the_transfer()
        {
            var title = new string('a', 400);
            var folder = JellyfinUpload.BuildRemoteFolder("movies", title, 2016);
            var name = folder.Split('/').Last();

            Assert.Equal(JellyfinDownload.SanitizeStem(title) + " (2016)", name);
            Assert.True(name.Length < 200);
        }
    }
}
