using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The two halves of Wrong series? that are not the catalogue: what the picker says and
    /// searches in each mode, and what the series screen shows once a programme has been chosen.
    /// No network but a <see cref="FakeHttpMessageHandler"/>, and no files at all.
    /// </summary>
    public class SeriesPickerTests : IDisposable
    {
        private readonly TempLog _log = new();

        public void Dispose() => _log.Dispose();

        private static TmdbService Service(FakeHttpMessageHandler handler) =>
            new(apiKey: "not-a-real-key", posterCacheDir: "", imageSize: "w342", downloadPosters: false,
                handler: handler, requests: new TmdbRequestScheduler(TimeSpan.Zero));

        // ---------- the picker ----------

        [Theory]
        [InlineData("Celda 211 BDR1080 www newpct1 com", "Celda 211")]
        [InlineData("Casi imposible BDR1080", "Casi imposible")]
        [InlineData("Charlotte's Web", "Charlotte's Web")]
        [InlineData("HD", "HD")]
        [InlineData("1080p", "1080p")]
        public void The_initial_picker_query_cleans_release_tags_but_keeps_real_titles(string title, string expected)
        {
            Assert.Equal(expected, TmdbMatchWording.InitialQuery(title));
        }

        [Fact]
        public async Task The_series_picker_searches_television_and_never_the_film_catalogue()
        {
            using var handler = FakeHttpMessageHandler.Routed(
                ("/search/tv", HttpStatusCode.OK, TmdbTelevisionTests.FargoSearch),
                ("/search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 275, ""title"": ""Fargo"", ""release_date"": ""1996-03-08"" } ] }"));
            using var tmdb = Service(handler);

            var found = Assert.Single(await TmdbMatchWording.Series.SearchAsync(tmdb, "Fargo", null, CancellationToken.None));

            Assert.Equal(60622, found.Id);
            Assert.Contains("/search/tv", Assert.Single(handler.Requests), StringComparison.Ordinal);
        }

        [Fact]
        public async Task The_film_picker_still_searches_films()
        {
            using var handler = FakeHttpMessageHandler.Routed(
                ("/search/movie", HttpStatusCode.OK, @"{ ""results"": [ { ""id"": 275, ""title"": ""Fargo"", ""release_date"": ""1996-03-08"" } ] }"));
            using var tmdb = Service(handler);

            var found = await TmdbMatchWording.Film.SearchAsync(tmdb, "Fargo", null, CancellationToken.None);

            Assert.Contains(found, candidate => candidate.Id == 275);
            Assert.DoesNotContain(handler.Requests, request => request.Contains("/search/tv", StringComparison.Ordinal));
        }

        /// <summary>A programme picker that said "film" anywhere would be asking the wrong question.</summary>
        [Fact]
        public void The_series_picker_never_calls_a_programme_a_film()
        {
            var wording = TmdbMatchWording.For(TmdbMatchKind.Series);

            foreach (var text in new[]
                     {
                         wording.WindowTitle, wording.Explanation, wording.Watermark, wording.ChooseLabel,
                         wording.Results(0, "Fargo"), wording.Results(1, "Fargo"), wording.Results(3, "Fargo")
                     })
                Assert.DoesNotContain("film", text, StringComparison.OrdinalIgnoreCase);

            Assert.Equal("Use this programme", wording.ChooseLabel);
            Assert.Equal("1 result. Pick the one that is this programme.", wording.Results(1, "Fargo"));
        }

        [Fact]
        public void The_film_picker_reads_exactly_as_it_did()
        {
            var wording = TmdbMatchWording.For(TmdbMatchKind.Film);

            Assert.Equal("Which film is this?", wording.WindowTitle);
            Assert.Equal("Use this film", wording.ChooseLabel);
            Assert.Equal("Film title", wording.Watermark);
            Assert.Equal("2 results. Pick the one that is this film.", wording.Results(2, "Heat"));
            Assert.Equal(
                "TMDB has nothing for “Heat”. Try the title in its own language, or without a subtitle.",
                wording.Results(0, " Heat "));
        }

        [Fact]
        public void A_television_result_becomes_a_row_named_and_dated_by_the_programme()
        {
            var row = TmdbCandidateVm.From(new TmdbMatch.Candidate
            {
                Id = 2316,
                Title = "The Office",
                OriginalTitle = "The Office",
                ReleaseDate = "2005-03-24",
                PosterPath = "/office-us.jpg"
            }, path => "https://image.example" + path);

            Assert.Equal(2316, row.TmdbId);
            Assert.Equal("The Office", row.TmdbTitle);
            Assert.False(row.HasOriginalTitle);
            Assert.Equal("2005", row.YearLabel);
            Assert.Equal("/office-us.jpg", row.PosterPath);
            Assert.Equal("https://image.example/office-us.jpg", row.PosterUrl);
        }

        // ---------- the screen ----------

        [Fact]
        public void Only_a_programme_on_this_disk_offers_Wrong_series()
        {
            Assert.True(LocalSeriesPage.CanCorrect(new SeriesDetailsVm { LocalId = 4 }));
            Assert.False(LocalSeriesPage.CanCorrect(new SeriesDetailsVm { RemoteId = "abc" }));
        }

        /// <summary>
        /// The card the screen was opened from predates the correction, so its name, poster and
        /// genres are the wrong programme's. The corrected screen reads all three off the catalogue.
        /// </summary>
        [Fact]
        public void A_corrected_screen_takes_its_name_poster_and_genres_from_the_catalogue_not_the_card()
        {
            var before = new SeriesDetailsVm
            {
                LocalId = 1, Title = "Office US", Genres = "Documentary",
                PosterPath = "https://image.tmdb.org/t/p/w342/wrong.jpg", SeasonCount = 2, EpisodeCount = 12,
                TmdbConfigured = true, TmdbId = 1, Overview = "The wrong programme.", ImdbId = "tt0000001",
                ImdbRating = 3.1
            };
            var details = new LocalSeriesDetails(
                1, "The Office", null, 2316, "Comedy", "https://image.tmdb.org/t/p/w342/office-us.jpg",
                "A mockumentary.", "/office-backdrop.jpg", 8.6, "tt0386676",
                new List<string> { "Steve Carell (Michael Scott)" }, new List<string> { "Creator: Greg Daniels" },
                Described: true);

            var after = LocalSeriesPage.Corrected(before, details);

            Assert.Equal(1, after.LocalId);
            Assert.Equal("The Office", after.Title);
            Assert.Equal("Comedy", after.Genres);
            Assert.Equal("https://image.tmdb.org/t/p/w342/office-us.jpg", after.PosterPath);
            Assert.Equal(2316, after.TmdbId);
            Assert.True(after.IsIdentified);
            Assert.Equal("A mockumentary.", after.Overview);
            Assert.Equal("tt0386676", after.ImdbId);
            Assert.Null(after.ImdbRating);
            Assert.Equal(new[] { "Steve Carell (Michael Scott)" }, after.TopCast);
            Assert.Equal(2, after.SeasonCount);
            Assert.Equal(12, after.EpisodeCount);
            Assert.Equal(LocalSeriesPage.TmdbAttribution, LocalSeriesPage.Attribution(after));
        }

        [Fact]
        public void A_correction_TMDB_could_not_describe_shows_nothing_of_the_old_programme()
        {
            var before = new SeriesDetailsVm
            {
                LocalId = 1, Title = "Office US", Genres = "Documentary", TmdbConfigured = true,
                TmdbId = 1, Overview = "The wrong programme.", ImdbId = "tt0000001",
                TopCast = new List<string> { "Somebody Else" }
            };
            var details = new LocalSeriesDetails(
                1, "The Office", null, 2316, null, null, null, null, null, null,
                Array.Empty<string>(), Array.Empty<string>(), Described: false);

            var after = LocalSeriesPage.Corrected(before, details);

            Assert.Equal("The Office", after.Title);
            Assert.Equal("", after.Genres);
            Assert.Null(after.PosterPath);
            Assert.Null(after.TmdbId);
            Assert.Equal("", after.Overview);
            Assert.Null(after.ImdbId);
            Assert.Empty(after.TopCast);

            var note = LocalSeriesPage.CorrectionNote(new SeriesCorrection(details, "The Office", Described: false));
            Assert.Contains("did not return its description", note, StringComparison.Ordinal);
        }
    }
}
