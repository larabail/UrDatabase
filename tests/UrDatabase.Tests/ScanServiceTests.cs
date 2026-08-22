using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class ScanServiceTests : IDisposable
    {
        private readonly string _root;

        public ScanServiceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-scan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        [Theory]
        [InlineData("movie.mkv")]
        [InlineData("movie.mp4")]
        [InlineData("movie.avi")]
        [InlineData("movie.mov")]
        [InlineData("movie.wmv")]
        [InlineData("movie.m4v")]
        [InlineData("movie.mpg")]
        [InlineData("movie.mpeg")]
        [InlineData("movie.ts")]
        [InlineData("movie.webm")]
        public void Recognises_video_extensions(string name)
        {
            Assert.True(ScanService.IsVideoFile(name));
        }

        [Theory]
        [InlineData("cover.jpg")]
        [InlineData("subtitles.srt")]
        [InlineData("notes.txt")]
        [InlineData("movie.mkv.part")]
        [InlineData("no-extension")]
        [InlineData("")]
        [InlineData(null)]
        public void Rejects_everything_else(string? name)
        {
            Assert.False(ScanService.IsVideoFile(name));
        }

        [Theory]
        [InlineData("MOVIE.MKV")]
        [InlineData("Movie.Mp4")]
        [InlineData("movie.MOV")]
        public void Extension_matching_is_case_insensitive_on_every_filesystem(string name)
        {
            Assert.True(ScanService.IsVideoFile(name));
        }

        [Fact]
        public void Recognises_video_files_by_full_path_on_either_separator()
        {
            Assert.True(ScanService.IsVideoFile(Path.Combine("/Volumes", "Media", "The Movie (1999).mkv")));
            Assert.True(ScanService.IsVideoFile(@"D:\Movies\The Movie (1999).mkv"));
        }

        [Fact]
        public void Enumerates_files_recursively()
        {
            var nested = Path.Combine(_root, "a", "b");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(_root, "top.mkv"), "x");
            File.WriteAllText(Path.Combine(nested, "deep.mp4"), "x");
            File.WriteAllText(Path.Combine(nested, "cover.jpg"), "x");

            var found = ScanService.EnumerateFilesSafe(_root).ToList();

            Assert.Equal(3, found.Count);
            Assert.Equal(2, found.Count(ScanService.IsVideoFile));
        }

        [Fact]
        public void Enumeration_survives_a_directory_it_cannot_read()
        {
            var readable = Path.Combine(_root, "readable");
            Directory.CreateDirectory(readable);
            File.WriteAllText(Path.Combine(readable, "movie.mkv"), "x");

            var blocked = Path.Combine(_root, "blocked");
            Directory.CreateDirectory(blocked);
            File.WriteAllText(Path.Combine(blocked, "hidden.mkv"), "x");

            var restricted = false;
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(blocked, UnixFileMode.None);
                restricted = true;
            }

            try
            {
                var found = ScanService.EnumerateFilesSafe(_root).ToList();

                Assert.Contains(found, f => f.EndsWith("movie.mkv", StringComparison.Ordinal));
                if (restricted)
                    Assert.DoesNotContain(found, f => f.EndsWith("hidden.mkv", StringComparison.Ordinal));
            }
            finally
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(blocked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        [Fact]
        public void Enumerating_a_missing_directory_yields_nothing_instead_of_throwing()
        {
            var found = ScanService.EnumerateFilesSafe(Path.Combine(_root, "nope")).ToList();

            Assert.Empty(found);
        }

        [Fact]
        public async Task Scan_records_only_video_files()
        {
            var moviesDir = Path.Combine(_root, "movies");
            Directory.CreateDirectory(moviesDir);
            File.WriteAllText(Path.Combine(moviesDir, "The Movie (1999).mkv"), "x");
            File.WriteAllText(Path.Combine(moviesDir, "Another.MP4"), "x");
            File.WriteAllText(Path.Combine(moviesDir, "poster.jpg"), "x");

            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            var updated = await scanner.ScanAsync(conn, new[] { moviesDir });

            Assert.Equal(2, updated);

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(2L, (long)cmd.ExecuteScalar()!);
        }

        [Fact]
        public async Task Rescanning_updates_rather_than_duplicates()
        {
            var moviesDir = Path.Combine(_root, "movies");
            Directory.CreateDirectory(moviesDir);
            File.WriteAllText(Path.Combine(moviesDir, "The Movie.mkv"), "x");

            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            await scanner.ScanAsync(conn, new[] { moviesDir });
            await scanner.ScanAsync(conn, new[] { moviesDir });

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM files";
            Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
        }

        [Fact]
        public async Task Missing_watch_folders_are_skipped_silently()
        {
            using var conn = Database.Open(Path.Combine(_root, "test.db"));
            var scanner = new ScanService();

            var updated = await scanner.ScanAsync(conn, new[] { Path.Combine(_root, "gone"), @"D:\Movies" });

            Assert.Equal(0, updated);
        }
    }
}
