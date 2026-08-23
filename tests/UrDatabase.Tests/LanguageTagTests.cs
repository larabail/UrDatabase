using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Three spellings of the same language reach this app — Jellyfin's ISO 639-2, a filename's
    /// English word, and the two-letter code the badge has room for. The whole point of the table
    /// is that a film does not read as two different languages depending on where its metadata
    /// came from.
    /// </summary>
    public class LanguageTagTests
    {
        [Theory]
        [InlineData("en", "EN")]
        [InlineData("eng", "EN")]
        [InlineData("English", "EN")]
        [InlineData("ENGLISH", "EN")]
        [InlineData("de", "DE")]
        [InlineData("ger", "DE")]
        [InlineData("deu", "DE")]
        [InlineData("Japanese", "JA")]
        [InlineData("jpn", "JA")]
        public void Every_spelling_of_a_language_gives_one_code(string input, string expected)
        {
            Assert.Equal(expected, LanguageTag.Code(input));
        }

        /// <summary>
        /// French has two ISO 639-2 codes and both turn up in real files, depending on which tool
        /// tagged them. A film carrying one must not read as a different language from the same
        /// film carrying the other.
        /// </summary>
        [Fact]
        public void The_two_codes_for_french_are_the_same_language()
        {
            Assert.Equal("FR", LanguageTag.Code("fre"));
            Assert.Equal("FR", LanguageTag.Code("fra"));
            Assert.Equal(LanguageTag.Code("fre"), LanguageTag.Code("french"));
        }

        [Fact]
        public void An_unknown_three_letter_tag_is_abbreviated_rather_than_dropped()
        {
            // Better a slightly wrong badge than silently hiding a track the film has.
            Assert.Equal("XH", LanguageTag.Code("xho"));
        }

        [Fact]
        public void An_unknown_word_is_dropped_because_its_first_two_letters_would_be_a_guess()
        {
            Assert.Null(LanguageTag.Code("gibberish"));
        }

        [Fact]
        public void Nothing_at_all_gives_nothing()
        {
            Assert.Null(LanguageTag.Code(null));
            Assert.Null(LanguageTag.Code(""));
            Assert.Null(LanguageTag.Code("   "));
        }

        [Fact]
        public void An_untagged_track_is_named_as_undetermined_rather_than_shown_as_a_language()
        {
            Assert.Equal(LanguageTag.UnknownCode, LanguageTag.Code("und"));
            Assert.Equal("Undetermined", LanguageTag.Name("und"));
        }

        [Fact]
        public void Names_are_for_the_tooltip_and_fall_back_to_what_the_file_claimed()
        {
            Assert.Equal("Spanish", LanguageTag.Name("spa"));
            Assert.Equal("XHO", LanguageTag.Name("xho"));
            Assert.Equal("", LanguageTag.Name(null));
        }

        [Fact]
        public void Only_recognised_spellings_count_as_known()
        {
            Assert.True(LanguageTag.IsKnown("ita"));
            Assert.False(LanguageTag.IsKnown("x264"));
            Assert.False(LanguageTag.IsKnown(null));
        }
    }
}
