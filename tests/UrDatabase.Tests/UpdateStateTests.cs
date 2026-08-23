using System;
using System.IO;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class UpdateStateTests : IDisposable
    {
        private readonly string _dir;
        private readonly string _path;
        private readonly string _logDir;
        private readonly IDisposable _log;

        public UpdateStateTests()
        {
            var root = Path.Combine(Path.GetTempPath(), "urdb-update-state-" + Guid.NewGuid().ToString("N"));

            // Deliberately a sibling of the directory under test rather than a child of it: one of
            // these tests asserts that saving creates its own folder, so that folder must not
            // already exist.
            _dir = Path.Combine(root, "state");
            _path = Path.Combine(_dir, UpdateState.FileName);
            _logDir = Path.Combine(root, "logs");

            // Saving logs when it cannot write. No test here means to fail that way, but one that
            // starts appending to somebody's real log the day it regresses is the accident
            // AGENTS.md forbids, so the switch is thrown rather than relied upon.
            _log = AppLog.Redirect(_logDir);
        }

        public void Dispose()
        {
            _log.Dispose();
            try { Directory.Delete(Path.GetDirectoryName(_dir)!, recursive: true); } catch { }
        }

        [Fact]
        public void An_install_that_has_never_dismissed_anything_has_nothing_recorded()
        {
            Assert.Null(UpdateState.Load(_path).SkippedVersion);
        }

        [Fact]
        public void Remembers_the_version_that_was_dismissed()
        {
            Assert.True(UpdateState.SaveSkipped("0.11.0", _path));

            Assert.Equal("0.11.0", UpdateState.Load(_path).SkippedVersion);
        }

        [Fact]
        public void Creates_the_folder_it_writes_into()
        {
            // The per-user data directory exists on a real install, but a fresh one that has not
            // started a scan yet may not have been created by anything else.
            Assert.False(Directory.Exists(_dir));

            Assert.True(UpdateState.SaveSkipped("0.11.0", _path));
            Assert.True(File.Exists(_path));
        }

        [Fact]
        public void A_version_written_by_hand_as_a_tag_still_silences_the_release_it_names()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, @"{ ""SkippedVersion"": ""v0.11.0"" }");

            Assert.Equal("0.11.0", UpdateState.Load(_path).SkippedVersion);
        }

        [Fact]
        public void A_value_that_is_not_a_version_records_nothing_rather_than_something_unmatchable()
        {
            UpdateState.SaveSkipped("whenever", _path);

            Assert.Null(UpdateState.Load(_path).SkippedVersion);
        }

        [Fact]
        public void A_file_that_will_not_parse_means_the_banner_shows_which_is_the_safe_way_to_be_wrong()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(_path, "{ this is not json");

            Assert.Null(UpdateState.Load(_path).SkippedVersion);
        }

        [Fact]
        public void The_default_file_lives_beside_the_rest_of_the_app_data_and_not_in_the_config()
        {
            // Deliberately not a field in appsettings.json: that file is the user's own answers,
            // is round-tripped whole by the setup screen, and deletes anything not named in
            // ConfigStore.Serialize.
            Assert.Equal(Path.Combine(PlatformPaths.AppDataRoot, "update-state.json"), UpdateState.DefaultPath);
        }
    }
}
