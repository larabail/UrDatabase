using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Choosing and launching a video player. Asserted through an injected probe rather than the
    /// real filesystem, so the result does not depend on what happens to be installed on the
    /// machine running the suite — including CI, where nothing is.
    /// </summary>
    public class MediaPlayerLauncherTests
    {
        private static MediaPlayerLauncher.PlayerCandidate Vlc(string path) => new("VLC", path);
        private static MediaPlayerLauncher.PlayerCandidate Iina(string path) => new("IINA", path);

        [Fact]
        public void The_first_installed_player_wins()
        {
            var candidates = new[] { Vlc("/nowhere/VLC"), Iina("/somewhere/IINA") };

            var found = MediaPlayerLauncher.Find(candidates, path => path == "/somewhere/IINA");

            Assert.NotNull(found);
            Assert.Equal("IINA", found!.Name);
        }

        [Fact]
        public void The_earlier_candidate_wins_when_both_are_installed()
        {
            var candidates = new[] { Vlc("/somewhere/VLC"), Iina("/somewhere/IINA") };

            Assert.Equal("VLC", MediaPlayerLauncher.Find(candidates, _ => true)!.Name);
        }

        [Fact]
        public void Nothing_installed_means_nothing_found_rather_than_a_guess()
        {
            Assert.Null(MediaPlayerLauncher.Find(new[] { Vlc("/nowhere/VLC") }, _ => false));
        }

        [Fact]
        public void An_empty_candidate_list_is_not_an_error()
        {
            Assert.Null(MediaPlayerLauncher.Find(Array.Empty<MediaPlayerLauncher.PlayerCandidate>(), _ => true));
        }

        [Fact]
        public void The_url_is_passed_as_an_argument_rather_than_through_a_shell()
        {
            // It carries an access token and query separators. A shell would split it, and on
            // some platforms log it.
            var url = "http://media.invalid/Videos/item0/stream?static=true&api_key=abc123";

            var psi = MediaPlayerLauncher.BuildStartInfo(Vlc("/somewhere/VLC"), url);

            Assert.Equal("/somewhere/VLC", psi.FileName);
            Assert.False(psi.UseShellExecute);
            Assert.Equal(new[] { url }, psi.ArgumentList.ToArray());
        }

        [Fact]
        public void A_launch_needs_a_url()
        {
            Assert.Throws<ArgumentException>(() => MediaPlayerLauncher.BuildStartInfo(Vlc("/somewhere/VLC"), ""));
            Assert.Throws<ArgumentException>(() => MediaPlayerLauncher.Play("   "));
        }

        [Fact]
        public void The_known_players_are_the_two_that_can_open_a_url()
        {
            var names = MediaPlayerLauncher.KnownPlayers().Select(p => p.Name).Distinct().ToList();

            Assert.Contains("VLC", names);
            Assert.All(MediaPlayerLauncher.KnownPlayers(), p => Assert.False(string.IsNullOrWhiteSpace(p.ExecutablePath)));
        }

        [Fact]
        public void On_a_mac_the_binary_inside_the_bundle_is_used_rather_than_open()
        {
            if (!OperatingSystem.IsMacOS()) return;

            var paths = MediaPlayerLauncher.KnownPlayers().Select(p => p.ExecutablePath).ToList();

            // `open -a VLC http://…` hands an http URL to the browser even when an application is
            // named, and a browser cannot play Matroska.
            Assert.Contains(paths, p => p.EndsWith("VLC.app/Contents/MacOS/VLC", StringComparison.Ordinal));
            Assert.Contains(paths, p => p.EndsWith("IINA.app/Contents/MacOS/IINA", StringComparison.Ordinal));
        }

        [Fact]
        public void The_message_for_a_machine_with_no_player_names_both_and_says_why()
        {
            Assert.Contains("VLC", MediaPlayerLauncher.NotInstalledMessage);
            Assert.Contains("IINA", MediaPlayerLauncher.NotInstalledMessage);
            Assert.Contains("browser", MediaPlayerLauncher.NotInstalledMessage);
        }
    }

    /// <summary>
    /// The identifier the server uses to recognise this install again.
    /// </summary>
    public class JellyfinDeviceIdTests : IDisposable
    {
        private readonly string _dir;

        public JellyfinDeviceIdTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-device-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        [Fact]
        public void The_same_install_keeps_the_same_id()
        {
            // A new id on every launch makes the server's device list grow without bound and
            // throws away the session it just issued a token for.
            var path = Path.Combine(_dir, "device-id");

            var first = JellyfinDeviceId.Resolve(path);
            var second = JellyfinDeviceId.Resolve(path);

            Assert.Equal(first, second);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public void Two_installs_get_different_ids()
        {
            var first = JellyfinDeviceId.Resolve(Path.Combine(_dir, "one"));
            var second = JellyfinDeviceId.Resolve(Path.Combine(_dir, "two"));

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void The_id_identifies_the_install_and_nothing_about_the_person()
        {
            var id = JellyfinDeviceId.Resolve(Path.Combine(_dir, "device-id"));

            Assert.True(Guid.TryParse(id, out _));
            Assert.DoesNotContain(Environment.MachineName, id, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_corrupt_file_is_replaced_rather_than_used()
        {
            var path = Path.Combine(_dir, "device-id");
            File.WriteAllText(path, "not a guid at all");

            var id = JellyfinDeviceId.Resolve(path);

            Assert.True(Guid.TryParse(id, out _));
            Assert.Equal(id, JellyfinDeviceId.Resolve(path));
        }

        [Fact]
        public void A_missing_directory_is_created()
        {
            var path = Path.Combine(_dir, "nested", "deeper", "device-id");

            var id = JellyfinDeviceId.Resolve(path);

            Assert.True(Guid.TryParse(id, out _));
            Assert.True(File.Exists(path));
        }
    }

    /// <summary>
    /// The line under the library. Small, and worth asserting because getting it wrong tells
    /// somebody with several hundred films that they have none.
    /// </summary>
    public class LibraryStatusTests
    {
        [Fact]
        public void With_nothing_anywhere_it_says_where_a_library_would_go()
        {
            var status = LibraryStatus.Describe(0, 0, 0, hasLocalDatabase: false, databasePath: "/tmp/movies.db");

            Assert.Contains("No library yet", status);
            Assert.Contains("/tmp/movies.db", status);
        }

        [Fact]
        public void A_server_library_is_not_no_library()
        {
            // The case this function exists for: nothing scanned, but 396 films on the server.
            var status = LibraryStatus.Describe(0, 0, 396, hasLocalDatabase: false, databasePath: "/tmp/movies.db");

            Assert.DoesNotContain("No library yet", status);
            Assert.Contains("396 films on the Jellyfin server", status);
        }

        [Fact]
        public void With_no_server_the_line_reads_exactly_as_it_did_before()
        {
            var status = LibraryStatus.Describe(10, 4, 0, hasLocalDatabase: true, databasePath: "/tmp/movies.db");

            Assert.Equal("Posters present: 4/10", status);
        }

        [Fact]
        public void Both_libraries_are_counted_separately()
        {
            var status = LibraryStatus.Describe(10, 4, 396, hasLocalDatabase: true, databasePath: "/tmp/movies.db");

            Assert.Contains("Posters present: 4/10", status);
            Assert.Contains("396 films", status);
        }

        [Fact]
        public void One_film_is_not_one_films()
        {
            Assert.Contains("1 film on the Jellyfin server",
                LibraryStatus.Describe(0, 0, 1, hasLocalDatabase: true, databasePath: "/tmp/movies.db"));
        }
    }
}
