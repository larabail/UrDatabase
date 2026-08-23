using System;
using System.IO;
using System.Linq;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The rules behind the setup screen: what counts as a usable answer, what the user is told
    /// when it is not one, and what gets written when it is.
    ///
    /// None of this needs a window, which is the point — the screen itself only reads controls
    /// and calls into here, so the behaviour a user actually depends on is testable without a
    /// UI thread.
    /// </summary>
    public class SetupChoicesTests : IDisposable
    {
        private readonly string _dir;

        public SetupChoicesTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static SetupChoices WithServer() => new()
        {
            UseJellyfin = true,
            ServerUrl = "http://media.invalid:8096",
            Username = "viewer",
            Password = "hunter2"
        };

        private SetupChoices WithFolder()
        {
            var choices = new SetupChoices { UseLocalFolders = true };
            choices.Folders.Add(_dir);
            return choices;
        }

        // ---------- at least one library ----------

        [Fact]
        public void Choosing_neither_source_is_refused()
        {
            var choices = new SetupChoices();

            Assert.False(choices.CanFinish);
            Assert.Contains("Choose where your films are", choices.Problem);
        }

        [Fact]
        public void Folders_on_this_computer_are_enough_on_their_own()
        {
            var choices = WithFolder();

            Assert.True(choices.CanFinish);
            Assert.True(choices.HasLocalLibrary);
            Assert.False(choices.HasJellyfinLibrary);
        }

        [Fact]
        public void A_jellyfin_server_is_enough_on_its_own()
        {
            var choices = WithServer();

            Assert.True(choices.CanFinish);
            Assert.True(choices.HasJellyfinLibrary);
            Assert.False(choices.HasLocalLibrary);
        }

        [Fact]
        public void Both_at_once_is_a_perfectly_good_answer()
        {
            var choices = WithServer();
            choices.UseLocalFolders = true;
            choices.Folders.Add(_dir);

            Assert.True(choices.CanFinish);
            Assert.True(choices.HasLocalLibrary);
            Assert.True(choices.HasJellyfinLibrary);
        }

        // ---------- saying what is wrong ----------

        [Fact]
        public void Ticking_local_films_without_naming_a_folder_asks_for_one()
        {
            var choices = new SetupChoices { UseLocalFolders = true };

            Assert.False(choices.CanFinish);
            Assert.Contains("at least one folder", choices.Problem);
        }

        [Fact]
        public void A_server_with_no_address_asks_for_one()
        {
            var choices = WithServer();
            choices.ServerUrl = "   ";

            Assert.Contains("address of your Jellyfin server", choices.Problem);
        }

        [Fact]
        public void An_address_that_is_not_a_web_address_is_rejected_before_anything_is_dialled()
        {
            var choices = WithServer();
            choices.ServerUrl = "ftp://media.invalid";

            Assert.False(choices.CanFinish);
            Assert.Contains("not one this app can reach", choices.Problem);
        }

        [Fact]
        public void A_server_with_no_way_to_sign_in_asks_for_one()
        {
            var choices = WithServer();
            choices.Username = "";
            choices.Password = "";

            Assert.False(choices.CanFinish);
            Assert.Contains("username", choices.Problem);
        }

        [Fact]
        public void An_api_key_stands_in_for_a_username()
        {
            var choices = WithServer();
            choices.Username = "";
            choices.Password = "";
            choices.ApiKey = "not-a-real-key";

            Assert.True(choices.CanFinish);
        }

        [Fact]
        public void A_bare_host_is_given_a_scheme()
        {
            var choices = WithServer();
            choices.ServerUrl = "media-box:8096/";

            Assert.Equal("http://media-box:8096", choices.ToJellyfinSettings().ServerUrl);
        }

        // ---------- folders ----------

        [Fact]
        public void Blank_and_repeated_folders_are_dropped_before_they_are_saved()
        {
            var choices = new SetupChoices { UseLocalFolders = true };
            choices.Folders.Add(_dir);
            choices.Folders.Add("   ");
            choices.Folders.Add(_dir.ToUpperInvariant());

            // Scanning the same folder twice would put every film in it on screen twice.
            Assert.Equal(new[] { _dir }, choices.CleanFolders.ToArray());
        }

        [Fact]
        public void A_folder_that_is_not_there_is_reported_but_does_not_block_the_save()
        {
            var absent = Path.Combine(_dir, "an-unplugged-drive");

            var choices = new SetupChoices { UseLocalFolders = true };
            choices.Folders.Add(_dir);
            choices.Folders.Add(absent);

            Assert.Equal(new[] { absent }, choices.MissingFolders.ToArray());
            Assert.True(choices.CanFinish);
        }

        // ---------- what gets written ----------

        [Fact]
        public void Finishing_marks_setup_as_answered()
        {
            Assert.True(WithFolder().ToConfig().SetupCompleted);
        }

        [Fact]
        public void An_unticked_source_is_cleared_rather_than_left_behind()
        {
            // Unticking Jellyfin has to actually stop the app contacting the server. Leaving the
            // old address in the file would keep it syncing while the screen said it was off.
            var previous = new AppConfig
            {
                WatchFolders = new[] { _dir },
                Jellyfin = new JellyfinSettings { ServerUrl = "http://old.invalid:8096", Username = "viewer" }
            };

            var config = new SetupChoices
            {
                UseLocalFolders = false,
                UseJellyfin = true,
                ServerUrl = "http://new.invalid:8096",
                Username = "viewer"
            }.ToConfig(previous);

            Assert.Empty(config.WatchFolders);
            Assert.Equal("http://new.invalid:8096", config.Jellyfin.ServerUrl);

            var off = WithFolder().ToConfig(previous);

            Assert.Equal("", off.Jellyfin.ServerUrl);
            Assert.False(off.Jellyfin.IsConfigured);
        }

        [Fact]
        public void Settings_this_screen_never_asks_about_survive_being_saved()
        {
            var previous = new AppConfig
            {
                DatabasePath = "/somewhere/else/movies.db",
                PosterCacheDir = "/somewhere/else/posters",
                DownloadPosters = true,
                TmdbImageSize = "original",
                CheckForUpdates = false
            };

            var config = WithFolder().ToConfig(previous);

            Assert.Equal("/somewhere/else/movies.db", config.DatabasePath);
            Assert.Equal("/somewhere/else/posters", config.PosterCacheDir);
            Assert.True(config.DownloadPosters);
            Assert.Equal("original", config.TmdbImageSize);

            // The one with no control anywhere in the app: editing the file is the only way to say
            // it, so putting it back to the default here would undo the only place it was said.
            Assert.False(config.CheckForUpdates);
        }

        [Fact]
        public void An_install_that_has_never_said_otherwise_keeps_the_update_check_on()
        {
            Assert.True(WithFolder().ToConfig().CheckForUpdates);
            Assert.True(WithFolder().ToConfig(new AppConfig()).CheckForUpdates);
        }

        [Fact]
        public void Keys_are_trimmed_because_a_pasted_one_carries_whitespace()
        {
            var choices = WithFolder();
            choices.TmdbApiKey = "  pasted-with-a-newline\n";
            choices.OmdbApiKey = " ";

            var config = choices.ToConfig();

            Assert.Equal("pasted-with-a-newline", config.TmdbApiKey);
            Assert.Equal("", config.OmdbApiKey);
        }

        [Fact]
        public void What_is_saved_can_be_written_because_it_is_not_a_resolved_configuration()
        {
            Assert.False(WithFolder().ToConfig().IsResolved);
        }

        // ---------- prefilling ----------

        [Fact]
        public void The_screen_opens_showing_what_the_file_already_said()
        {
            var choices = SetupChoices.From(new AppConfig
            {
                WatchFolders = new[] { _dir },
                TmdbApiKey = "typed-by-the-user",
                Jellyfin = new JellyfinSettings
                {
                    ServerUrl = "http://media.invalid:8096",
                    Username = "viewer",
                    LibraryName = "Films"
                }
            });

            Assert.True(choices.UseLocalFolders);
            Assert.Equal(new[] { _dir }, choices.Folders.ToArray());
            Assert.True(choices.UseJellyfin);
            Assert.Equal("http://media.invalid:8096", choices.ServerUrl);
            Assert.Equal("viewer", choices.Username);
            Assert.Equal("Films", choices.LibraryName);
            Assert.Equal("typed-by-the-user", choices.TmdbApiKey);
        }

        [Fact]
        public void A_fresh_install_opens_with_neither_source_chosen()
        {
            var choices = SetupChoices.From(AppConfig.ReadRaw(Path.Combine(_dir, "nothing-here.json")));

            Assert.False(choices.UseLocalFolders);
            Assert.False(choices.UseJellyfin);
            Assert.False(choices.CanFinish);
        }
    }
}
