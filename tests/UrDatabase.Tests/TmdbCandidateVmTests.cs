using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// A row in the "Wrong film?" picker. The whole point of the screen is that a person can
    /// recognise the right film at a glance, so what each row says about a result — and what it
    /// says when TMDB gave it nothing — is the feature rather than decoration.
    /// </summary>
    public class TmdbCandidateVmTests
    {
        [Fact]
        public void A_row_shows_the_original_title_only_when_it_says_something_new()
        {
            var translated = TmdbCandidateVm.From(
                new TmdbMatch.Candidate { Id = 901, Title = "The Drama", OriginalTitle = "El Drama", ReleaseDate = "2026-03-02" },
                path => "https://image.tmdb.org/t/p/w342" + path);

            var same = TmdbCandidateVm.From(
                new TmdbMatch.Candidate { Id = 550, Title = "Fight Club", OriginalTitle = "Fight Club", ReleaseDate = "1999-10-15" },
                path => path);

            Assert.True(translated.HasOriginalTitle);
            Assert.Equal("El Drama", translated.OriginalTitle);
            Assert.Equal("2026", translated.YearLabel);
            Assert.False(same.HasOriginalTitle);
        }

        [Fact]
        public void A_row_says_what_is_missing_rather_than_showing_a_gap()
        {
            var row = TmdbCandidateVm.From(new TmdbMatch.Candidate { Id = 1 }, path => path);

            Assert.Equal("Untitled", row.Title);
            Assert.Equal("Year unknown", row.YearLabel);
            Assert.Contains("no plot summary", row.Overview);
            Assert.Null(row.PosterUrl);
        }

        [Fact]
        public void A_row_falls_back_to_the_original_title_when_tmdb_has_no_localised_one()
        {
            var row = TmdbCandidateVm.From(
                new TmdbMatch.Candidate { Id = 901, OriginalTitle = "El Drama" },
                path => path);

            Assert.Equal("El Drama", row.Title);

            // Already the title, so repeating it underneath would be noise.
            Assert.False(row.HasOriginalTitle);
        }

        [Fact]
        public void A_row_builds_its_poster_url_at_the_configured_size()
        {
            var row = TmdbCandidateVm.From(
                new TmdbMatch.Candidate { Id = 901, Title = "The Drama", PosterPath = "/right.jpg" },
                path => "https://image.tmdb.org/t/p/w342" + path);

            Assert.Equal("https://image.tmdb.org/t/p/w342/right.jpg", row.PosterUrl);
            Assert.Equal("/right.jpg", row.PosterPath);
        }
    }
}
