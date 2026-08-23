using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// What the library does about a film a scan can no longer find on this disk.
    ///
    /// <c>files.missing_since</c> has been written since the scan learned to notice a deletion and
    /// was read by nothing at all, so a film whose only copy had been deleted still carried the
    /// <c>Offline</c> badge, still answered the "on this computer" filter, and still offered to
    /// play — and only admitted otherwise when the operating system refused the path. Every test
    /// here fails against that library.
    ///
    /// Written against a real scan over real files in a temporary directory, the way
    /// <see cref="ScanLifecycleTests"/> is, because the interesting cases are all about what a
    /// scan actually concludes: a cancelled one and an absent watch folder must change nothing,
    /// and a file that comes back must restore the film rather than duplicate it. A stubbed scan
    /// would only assert those rules back at itself.
    /// </summary>
    public class MissingFilmTests : IDisposable
    {
        private readonly string _root;

        public MissingFilmTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-missing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        // ---- the rule itself -------------------------------------------------------------------

        [Theory]
        // A copy is still here, so nothing else matters — including a second print that went away.
        [InlineData(true, false, false, FilmStanding.Kept)]
        [InlineData(true, false, true, FilmStanding.Kept)]
        [InlineData(true, true, false, FilmStanding.Kept)]
        [InlineData(true, true, true, FilmStanding.Kept)]
        // Nothing here and nothing marked: the catalogue has never had a file for this film and
        // has therefore said nothing about one going away.
        [InlineData(false, false, false, FilmStanding.Kept)]
        [InlineData(false, false, true, FilmStanding.Kept)]
        // Every copy gone. Where it lands turns entirely on whether anywhere else has it.
        [InlineData(false, true, true, FilmStanding.ServerOnly)]
        [InlineData(false, true, false, FilmStanding.Retired)]
        public void Three_outcomes_from_two_facts(bool here, bool missing, bool onServer, FilmStanding expected)
            => Assert.Equal(expected, MissingFilms.Decide(here, missing, onServer));

        [Fact]
        public void A_film_the_catalogue_has_no_file_for_is_left_alone()
        {
            // The dangerous reading. A row with no files at all is not evidence of a deletion —
            // it is a catalogue restored from elsewhere, or a row whose file was orphaned — and
            // treating silence as a deletion would empty a library on a fact nobody recorded.
            var neverHadAFile = new UiMovie { Id = 1, Title = "Stalker" };

            Assert.Equal(FilmStanding.Kept, MissingFilms.Decide(neverHadAFile));
            Assert.True(neverHadAFile.IsOnThisComputer);
            Assert.Single(MissingFilms.Retire(new[] { neverHadAFile }));
        }

        [Fact]
        public void A_server_film_is_never_retired_by_a_local_scan()
        {
            // A scan of this machine has no standing to conclude anything about a film it has
            // never seen a file for, and every server film is one of those.
            var server = JellyfinLibrary.ToUiMovie(new JellyfinMovie { ItemId = "a", Title = "Stalker", Year = 1979 });

            Assert.Equal(FilmStanding.Kept, MissingFilms.Decide(server));
            Assert.Single(MissingFilms.Retire(new[] { server }));
        }

        // ---- a film that is only here -----------------------------------------------------------

        [Fact]
        public async Task A_local_film_whose_file_is_deleted_leaves_the_library()
        {
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            var heat = Write(films, "Heat (1995).mkv");

            await Scan(films);

            var before = Load();
            Assert.Equal(2, before.All.Count);

            File.Delete(heat);
            await Scan(films);

            var after = Load();
            Assert.DoesNotContain(after.All, m => m.Title == "Heat");
            Assert.Equal(new[] { "The Matrix" }, after.All.Select(m => m.Title));

            // Gone from the view, still in the catalogue. The row carries the film's identity, and
            // this process cannot tell a deletion from a drive that is not plugged in.
            using var conn = Database.Connect(DbPath);
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies WHERE title = 'Heat'"));
        }

        [Fact]
        public async Task A_film_that_left_the_library_is_not_in_the_search_either()
        {
            // Two paths read the catalogue and only one of them is the grouped view. A film that
            // vanished from the wall and could still be typed back into existence would be worse
            // than one that never left.
            var films = MakeFolder("Films");
            var heat = Write(films, "Heat (1995).mkv");

            await Scan(films);
            Assert.Single(Load("heat").All);

            File.Delete(heat);
            await Scan(films);

            Assert.Empty(Load("heat").All);
        }

        [Fact]
        public async Task A_film_that_left_the_library_is_not_counted_under_it()
        {
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            var heat = Write(films, "Heat (1995).mkv");

            await Scan(films);
            Assert.Equal("Posters present: 0/2", Load().Status);

            File.Delete(heat);
            await Scan(films);

            Assert.Equal("Posters present: 0/1", Load().Status);
        }

        // ---- a film that is in both places -------------------------------------------------------

        [Fact]
        public async Task A_film_in_both_places_degrades_to_a_server_film_rather_than_vanishing()
        {
            var films = MakeFolder("Films");
            var path = Write(films, "El Drama (2026).mkv");

            await Scan(films);

            // The state the owner's library is actually in: a hand-corrected match, a poster the
            // catalogue fetched, and a server that also has the film.
            Correct("El Drama", tmdbId: 1325734, poster: "/tmp/el-drama.jpg");
            var server = new[] { Server("srv-1", "The Drama", 2026, tmdbId: "1325734", genres: "Drama, Thriller") };

            var before = Load(remote: server);
            Assert.True(Assert.Single(before.All).IsInBothPlaces);

            File.Delete(path);
            await Scan(films);

            var after = Load(remote: server);
            var film = Assert.Single(after.All);

            // Still there, and now a server film: the badge, the filter and Play all agree.
            Assert.False(film.IsOnThisComputer);
            Assert.False(film.IsInBothPlaces);
            Assert.True(film.IsOnServer);
            Assert.True(film.IsRemote);
            Assert.Equal("srv-1", film.RemoteId);

            // The row is the point of keeping it. Everything hung off it survives.
            Assert.Equal(1325734, film.TmdbId);
            Assert.Equal("/tmp/el-drama.jpg", film.PosterPath);
            Assert.Equal("/tmp/el-drama.jpg", film.DisplayPosterPath);
            Assert.Contains("Thriller", film.GenresList);

            // And it survives in the catalogue, which is what makes the correction outlast the
            // file: movies.scan_title is how the next scan finds this row again.
            using var conn = Database.Connect(DbPath);
            var row = conn.QuerySingle<(long Id, string Title, int? TmdbId, string? ScanTitle)>(
                "SELECT id AS Id, title AS Title, tmdb_id AS TmdbId, scan_title AS ScanTitle FROM movies");
            Assert.Equal(1325734, row.TmdbId);
            Assert.Equal("El Drama", row.ScanTitle);
        }

        [Fact]
        public async Task A_degraded_film_is_counted_by_the_server_control_and_not_by_the_offline_one()
        {
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            var path = Write(films, "Heat (1995).mkv");

            await Scan(films);
            var server = new[] { Server("srv-heat", "Heat", 1995) };

            Assert.Equal(2, LibraryFilter.Count(Load(remote: server).All, LibrarySource.ThisComputer));

            File.Delete(path);
            await Scan(films);

            var all = Load(remote: server).All;
            Assert.Equal(2, LibraryFilter.Count(all, LibrarySource.Everywhere));
            Assert.Equal(1, LibraryFilter.Count(all, LibrarySource.ThisComputer));
            Assert.Equal(1, LibraryFilter.Count(all, LibrarySource.Server));
            Assert.Equal(new[] { "The Matrix" }, LibraryFilter.Apply(all, LibrarySource.ThisComputer).Select(m => m.Title));
        }

        [Fact]
        public async Task A_film_the_server_has_lost_too_leaves_the_library()
        {
            // The server is asked afresh on every read, so a film that degraded yesterday because
            // the server had it goes altogether once the server stops reporting it.
            var films = MakeFolder("Films");
            var path = Write(films, "Heat (1995).mkv");

            await Scan(films);
            File.Delete(path);
            await Scan(films);

            Assert.Single(Load(remote: new[] { Server("srv-heat", "Heat", 1995) }).All);
            Assert.Empty(Load().All);
        }

        [Fact]
        public async Task A_search_that_only_matches_this_half_of_the_film_does_not_retire_it()
        {
            // The two sources disagree about the name — "El Drama" here, "The Drama" there,
            // folded onto each other by TMDB id. Narrowing the server library before folding meant
            // a search for "el" found the local row, failed to find the server's copy of it, and
            // concluded that nowhere had the film. Whether a film is in the library must not
            // depend on what was typed.
            var films = MakeFolder("Films");
            var path = Write(films, "El Drama (2026).mkv");

            await Scan(films);
            Correct("El Drama", tmdbId: 1325734, poster: "/tmp/el-drama.jpg");
            using (var conn = Database.Open(DbPath))
                conn.Execute("UPDATE movies SET title = 'El Drama'");

            var server = new[] { Server("srv-1", "The Drama", 2026, tmdbId: "1325734") };

            File.Delete(path);
            await Scan(films);

            var searched = Assert.Single(Load("el", server).All);
            Assert.Equal("El Drama", searched.Title);
            Assert.True(searched.IsOnServer);
            Assert.False(searched.IsOnThisComputer);

            // And the unsearched view says exactly the same thing, which is the point.
            Assert.Single(Load(remote: server).All);
        }

        [Fact]
        public async Task A_search_still_leaves_out_the_server_films_it_does_not_match()
        {
            // The other half of the fix: folding against the whole server library must not smuggle
            // films the query never matched into the results.
            var films = MakeFolder("Films");
            Write(films, "Heat (1995).mkv");

            await Scan(films);
            var server = new[] { Server("srv-heat", "Heat", 1995), Server("srv-stalker", "Stalker", 1979) };

            Assert.Equal(new[] { "Heat" }, Load("heat", server).All.Select(m => m.Title));
            Assert.Equal(new[] { "Stalker" }, Load("stalker", server).All.Select(m => m.Title));
            Assert.Equal(2, Load(remote: server).All.Count);
        }

        [Fact]
        public async Task A_degraded_film_is_still_the_same_card_it_was()
        {
            // Its identity is the local row's, and has to stay that way: the merged list is
            // deduplicated on this key, and a card that changed key as its file went away could
            // collide with the server copy that was folded onto it.
            var films = MakeFolder("Films");
            var path = Write(films, "Heat (1995).mkv");

            await Scan(films);
            var server = new[] { Server("srv-heat", "Heat", 1995) };
            var before = Assert.Single(Load(remote: server).All);

            File.Delete(path);
            await Scan(films);

            var after = Assert.Single(Load(remote: server).All);
            Assert.Equal(before.Key, after.Key);
            Assert.Equal($"local:{after.Id}", after.Key);
        }

        [Fact]
        public async Task A_catalogue_row_with_no_file_at_all_stays_in_the_library()
        {
            // Against a real catalogue rather than a hand-built card, because the two facts this
            // turns on come out of SQL and a film with no file rows has to produce false for both.
            var films = MakeFolder("Films");
            Write(films, "Heat (1995).mkv");

            await Scan(films);

            using (var conn = Database.Open(DbPath))
                conn.Execute("INSERT INTO movies (title, year) VALUES ('Stalker', 1979)");

            var orphan = Assert.Single(Load().All, m => m.Title == "Stalker");
            Assert.False(orphan.HasFileHere);
            Assert.False(orphan.HasFileMissing);
            Assert.False(orphan.FileIsGone);
            Assert.True(orphan.IsOnThisComputer);
        }

        // ---- what must not change ----------------------------------------------------------------

        [Fact]
        public async Task A_film_with_two_prints_keeps_its_place_when_one_of_them_goes()
        {
            var films = MakeFolder("Films");
            var hd = Write(films, "Heat (1995) 1080p.mkv", "shorter");
            Write(films, "Heat (1995) 2160p.mkv", "a much longer file");

            await Scan(films);
            var film = Assert.Single(Load().All);
            Assert.Equal("Heat", film.Title);

            File.Delete(hd);
            var second = await Scan(films);
            Assert.Equal(1, second.Missing);

            var after = Assert.Single(Load().All);
            Assert.True(after.IsOnThisComputer);
            Assert.False(after.FileIsGone);

            // And Play opens the print that is still there, not the one that went.
            using var conn = Database.Connect(DbPath);
            var target = PlayTargetResolver.Resolve(conn, after.Id, after.Title, after.Year);
            Assert.Equal(PlayTargetKind.Linked, target.Kind);
            Assert.Equal(Path.Combine(films, "Heat (1995) 2160p.mkv"), target.FilePath);
        }

        [Fact]
        public async Task A_cancelled_scan_takes_nothing_out_of_the_library()
        {
            // A cancelled scan stops somewhere arbitrary, so everything it had not reached yet is
            // indistinguishable from everything that is gone. Concluding the second would empty
            // most of a large library on a scan somebody stopped early.
            var films = MakeFolder("Films");
            Write(films, "The Matrix (1999).mkv");
            var heat = Write(films, "Heat (1995).mkv");

            await Scan(films);
            File.Delete(heat);

            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var cancelled = await Scan(films, cts.Token);

            Assert.Equal(ScanStatus.Cancelled, cancelled.Status);

            var after = Load();
            Assert.Equal(2, after.All.Count);
            Assert.All(after.All, m => Assert.True(m.IsOnThisComputer));
        }

        [Fact]
        public async Task An_unplugged_drive_takes_nothing_out_of_the_library()
        {
            // The folder is not there, so it was not searched, so nothing under it may be called
            // gone. This is the case that makes an external drive survivable.
            var onDisk = MakeFolder("Films");
            var removable = MakeFolder("Removable");
            Write(onDisk, "The Matrix (1999).mkv");
            Write(removable, "Heat (1995).mkv");

            await Scan(onDisk, removable);
            Directory.Delete(removable, recursive: true);

            var second = await Scan(onDisk, removable);
            Assert.Equal(ScanStatus.Completed, second.Status);
            Assert.Equal(0, second.Missing);

            var after = Load();
            Assert.Equal(2, after.All.Count);
            Assert.Contains(after.All, m => m.Title == "Heat" && m.IsOnThisComputer);
        }

        [Fact]
        public async Task A_scan_of_one_folder_takes_nothing_out_of_another()
        {
            var films = MakeFolder("Films");
            var others = MakeFolder("Others");
            Write(films, "The Matrix (1999).mkv");
            Write(others, "Heat (1995).mkv");

            await Scan(films, others);
            await Scan(films);

            Assert.Equal(2, Load().All.Count);
        }

        // ---- coming back ---------------------------------------------------------------------------

        [Fact]
        public async Task A_film_whose_file_comes_back_returns_once_and_not_twice()
        {
            // The reason the mark alone is enough and a "two consecutive scans" rule was not
            // needed: the row never went anywhere, so the next scan simply finds it again.
            var films = MakeFolder("Films");
            var path = Write(films, "Heat (1995).mkv");

            await Scan(films);
            File.Delete(path);
            await Scan(films);
            Assert.Empty(Load().All);

            Write(films, "Heat (1995).mkv");
            await Scan(films);

            var back = Assert.Single(Load().All);
            Assert.Equal("Heat", back.Title);
            Assert.True(back.IsOnThisComputer);

            using var conn = Database.Connect(DbPath);
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM files"));
        }

        [Fact]
        public async Task A_hand_corrected_film_whose_file_comes_back_is_still_the_film_you_corrected()
        {
            var films = MakeFolder("Films");
            var path = Write(films, "El Drama (2026).mkv");

            await Scan(films);
            Correct("El Drama", tmdbId: 1325734, poster: "/tmp/el-drama.jpg");

            File.Delete(path);
            await Scan(films);
            Assert.Empty(Load().All);

            Write(films, "El Drama (2026).mkv");
            await Scan(films);

            var back = Assert.Single(Load().All);
            Assert.Equal("The Drama", back.Title);
            Assert.Equal(1325734, back.TmdbId);
            Assert.Equal("/tmp/el-drama.jpg", back.PosterPath);

            using var conn = Database.Connect(DbPath);
            Assert.Equal(1L, conn.QuerySingle<long>("SELECT COUNT(*) FROM movies"));
        }

        [Fact]
        public async Task Linking_a_file_by_hand_brings_a_film_back_without_a_scan()
        {
            // The way out for somebody who moved a film somewhere the scan does not walk. The
            // link has to clear the mark, or it would be recorded and then ignored.
            var films = MakeFolder("Films");
            var path = Write(films, "Heat (1995).mkv");

            await Scan(films);
            var movieId = Assert.Single(Load().All).Id;

            File.Delete(path);
            await Scan(films);
            Assert.Empty(Load().All);

            var elsewhere = Write(MakeFolder("Elsewhere"), "Heat (1995).mkv");
            using (var conn = Database.Open(DbPath))
                PlayTargetResolver.LinkFile(conn, movieId, elsewhere);

            var back = Assert.Single(Load().All);
            Assert.True(back.IsOnThisComputer);
        }

        // ---- play ------------------------------------------------------------------------------------

        [Fact]
        public async Task Play_never_offers_a_file_the_catalogue_says_is_gone()
        {
            var films = MakeFolder("Films");
            var path = Write(films, "Heat (1995).mkv");

            await Scan(films);

            using var conn = Database.Open(DbPath);
            var movieId = conn.QuerySingle<long>("SELECT id FROM movies");
            Assert.Equal(PlayTargetKind.Linked, PlayTargetResolver.Resolve(conn, movieId, "Heat", 1995).Kind);

            File.Delete(path);
            await new ScanService().ScanAsync(conn, new[] { films });

            // Refused on the catalogue's own account, before the filesystem is consulted: the
            // resolver is handed a filesystem that still claims the file is there.
            var target = PlayTargetResolver.Resolve(conn, movieId, "Heat", 1995, _ => true);
            Assert.Equal(PlayTargetKind.None, target.Kind);
            Assert.Null(target.FilePath);
        }

        [Fact]
        public async Task A_missing_file_is_not_offered_as_a_suggestion_for_another_film()
        {
            // The suggestion pool is unclaimed files, and a film whose own copy went away used to
            // be handed the nearest lookalike — including one that was equally gone.
            var films = MakeFolder("Films");
            var path = Write(films, "Heat (1995).mkv");

            await Scan(films);

            using var conn = Database.Open(DbPath);
            conn.Execute("UPDATE files SET movie_id = NULL");
            var orphan = conn.ExecuteScalar<long>(
                "INSERT INTO movies (title, year) VALUES ('Heat', 1995) RETURNING id");

            Assert.Equal(PlayTargetKind.Suggested, PlayTargetResolver.Resolve(conn, orphan, "Heat", 1995).Kind);

            File.Delete(path);
            await new ScanService().ScanAsync(conn, new[] { films });

            Assert.Equal(
                PlayTargetKind.None,
                PlayTargetResolver.Resolve(conn, orphan, "Heat", 1995, _ => true).Kind);
        }

        // ---- helpers ---------------------------------------------------------------------------------

        private string DbPath => Path.Combine(_root, "movies.db");

        private Task<ScanResult> Scan(params string[] folders) => Scan(folders, CancellationToken.None);

        private async Task<ScanResult> Scan(string[] folders, CancellationToken ct)
        {
            using var conn = Database.Open(DbPath);
            return await new ScanService().ScanAsync(conn, folders, null, ct);
        }

        private Task<ScanResult> Scan(string folder, CancellationToken ct) => Scan(new[] { folder }, ct);

        private LibraryView Load(string? query = null, IReadOnlyList<UiMovie>? remote = null) =>
            new LibraryLoader(new MovieRepository(DbPath)).Load(query, remote);

        /// <summary>
        /// What <c>Wrong film?</c> writes: the film is renamed to what TMDB calls it, the id is
        /// recorded, and the name the scanner gave it is kept so the next scan finds this row.
        /// </summary>
        private void Correct(string scanTitle, int tmdbId, string poster)
        {
            using var conn = Database.Open(DbPath);
            conn.Execute(
                @"UPDATE movies
                     SET title = 'The Drama', tmdb_id = @tmdbId, scan_title = @scanTitle, poster_path = @poster
                   WHERE title = @scanTitle",
                new { tmdbId, scanTitle, poster });
        }

        private static UiMovie Server(string itemId, string title, int? year, string? tmdbId = null, string genres = "") =>
            JellyfinLibrary.ToUiMovie(new JellyfinMovie
            {
                ItemId = itemId,
                Title = title,
                Year = year,
                TmdbId = tmdbId,
                Genres = genres
            });

        private string MakeFolder(string relative)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(path);
            return path;
        }

        private static string Write(string folder, string name, string body = "x")
        {
            var path = Path.Combine(folder, name);
            File.WriteAllText(path, body);
            return path;
        }
    }
}
