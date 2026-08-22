using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// There are three different reasons a film shows no plot and no cast, and the screen used to
    /// print the same sentence for all three. On an install with no TMDB key — the default, since
    /// both keys are optional — "None found for this film" is simply untrue: it blames the film
    /// for a question nobody asked, and hides the one thing the user could do about it.
    /// </summary>
    public class MissingMetadataTests
    {
        [Fact]
        public void A_local_film_with_no_key_is_told_a_key_would_fix_it()
        {
            var notice = MissingMetadata.CreditsNotice(isRemote: false, tmdbConfigured: false);

            Assert.Contains("TMDB key", notice);
            Assert.Contains("Settings", notice);
        }

        /// <summary>
        /// The point of the distinction: with a key, TMDB really was asked and really does not
        /// have it, and telling the user to add a key they already have would be nonsense.
        /// </summary>
        [Fact]
        public void A_local_film_with_a_key_is_simply_told_nothing_was_found()
        {
            var notice = MissingMetadata.CreditsNotice(isRemote: false, tmdbConfigured: true);

            Assert.DoesNotContain("key", notice, System.StringComparison.OrdinalIgnoreCase);
            Assert.Contains("None found", notice);
        }

        /// <summary>
        /// A server describes its own films, so a TMDB key is irrelevant to one and must never be
        /// suggested — the app deliberately makes no TMDB call for a server film.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void A_server_film_is_never_told_to_add_a_TMDB_key(bool tmdbConfigured)
        {
            var credits = MissingMetadata.CreditsNotice(isRemote: true, tmdbConfigured);
            var overview = MissingMetadata.OverviewNotice(isRemote: true, tmdbConfigured);

            Assert.DoesNotContain("TMDB", credits);
            Assert.DoesNotContain("TMDB", overview);
            Assert.Contains("server", credits);
            Assert.Contains("server", overview);
        }

        [Fact]
        public void The_plot_notice_makes_the_same_distinction_as_the_credits_one()
        {
            var without = MissingMetadata.OverviewNotice(isRemote: false, tmdbConfigured: false);
            var with = MissingMetadata.OverviewNotice(isRemote: false, tmdbConfigured: true);

            Assert.Contains("TMDB key", without);
            Assert.DoesNotContain("TMDB key", with);
            Assert.NotEqual(without, with);
        }

        [Fact]
        public void Every_notice_is_a_real_sentence_rather_than_a_blank()
        {
            foreach (var remote in new[] { true, false })
            foreach (var configured in new[] { true, false })
            {
                Assert.False(string.IsNullOrWhiteSpace(MissingMetadata.CreditsNotice(remote, configured)));
                Assert.False(string.IsNullOrWhiteSpace(MissingMetadata.OverviewNotice(remote, configured)));
            }
        }
    }
}
