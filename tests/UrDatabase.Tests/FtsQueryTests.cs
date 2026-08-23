using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.Sqlite;
using UrDatabase.Models;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The search box, asserted against a real catalogue.
    ///
    /// FTS5 reads its own operators out of the string it is given, so an ordinary film title —
    /// <c>Face/Off</c>, <c>Mission: Impossible</c>, an apostrophe — used to come back as
    /// <c>fts5: syntax error</c>, which the window showed as "Could not read the library". The
    /// escaped string on its own proves nothing about that; only SQLite accepting it does, so
    /// every case here goes through the same query the window runs.
    /// </summary>
    public class FtsQueryTests : IDisposable
    {
        // Both statements are copies of the ones in Views/MainWindow.axaml.cs, so a test failure
        // here means the search box is broken rather than something adjacent to it.
        private const string AllMoviesSql =
            "SELECT id AS Id, title AS Title, year AS Year, genres AS Genres, poster_path AS PosterPath " +
            "FROM movies ORDER BY COALESCE(year,0) DESC, title";

        private const string SearchSql = @"
SELECT m.id AS Id, m.title AS Title, m.year AS Year, m.genres AS Genres, m.poster_path AS PosterPath
FROM movies_fts f
JOIN movies m ON m.id = f.rowid
WHERE movies_fts MATCH @q
ORDER BY rank";

        private static readonly (string Title, int Year)[] Library =
        {
            ("Face/Off", 1997),
            ("Mission: Impossible", 1996),
            ("Dude, Where's My Car?", 2000),
            ("The Matrix", 1999),
            ("The Matrix Reloaded", 2003),
            ("Near Dark", 1987),
            ("Star Wars: Episode V - The Empire Strikes Back", 1980),
            ("Amélie", 2001),
            ("WALL·E", 2008),
        };

        private readonly string _root;
        private readonly string _dbPath;

        public FtsQueryTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "urdb-fts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _dbPath = Path.Combine(_root, "movies.db");

            using var conn = Database.Open(_dbPath);
            foreach (var (title, year) in Library)
                conn.Execute("INSERT INTO movies(title, year) VALUES (@title, @year)", new { title, year });
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { }
        }

        /// <summary>What the window does, once <see cref="FtsQuery"/> is in the way.</summary>
        private List<UiMovie> Search(string? userText)
        {
            using var conn = Database.Open(_dbPath);
            var match = FtsQuery.Build(userText);

            return match is null
                ? conn.Query<UiMovie>(AllMoviesSql).ToList()
                : conn.Query<UiMovie>(SearchSql, new { q = match }).ToList();
        }

        // Every character and bare keyword FTS5 treats as an operator, plus the ordinary
        // punctuation a film title actually contains.
        public static IEnumerable<object[]> AwkwardInput() => new[]
        {
            new object[] { "\"" },
            new object[] { "\"unbalanced" },
            new object[] { "say \"anything" },
            new object[] { "*" },
            new object[] { "Matrix*" },
            new object[] { "NEAR" },
            new object[] { "NEAR(matrix dark)" },
            new object[] { "matrix OR dark" },
            new object[] { "matrix AND dark" },
            new object[] { "matrix NOT dark" },
            new object[] { "-" },
            new object[] { "-matrix" },
            new object[] { "^" },
            new object[] { "^matrix" },
            new object[] { ":" },
            new object[] { "Mission: Impossible" },
            new object[] { "title:matrix" },
            new object[] { "(" },
            new object[] { ")" },
            new object[] { "(matrix)" },
            new object[] { "Face/Off" },
            new object[] { "Dude, Where's My Car?" },
            new object[] { "Where's" },
            new object[] { "'" },
            new object[] { "???" },
            new object[] { "" },
            new object[] { "   " },
            new object[] { "Amélie" },
            new object[] { "WALL·E" },
            new object[] { "Star Wars: Episode V - The Empire Strikes Back" },
        };

        /// <summary>
        /// The bug, stated as SQLite sees it: the raw text of the search box is a query in a
        /// second language, and most film titles are not valid in it. This is what
        /// <c>LoadLocalMovies</c> used to bind, and it is why the status bar said the library
        /// could not be read.
        /// </summary>
        [Theory]
        [InlineData("Mission: Impossible")]
        [InlineData("Face/Off")]
        [InlineData("Dude, Where's My Car?")]
        [InlineData("\"unbalanced")]
        [InlineData("matrix OR")]
        [InlineData("(matrix")]
        [InlineData("^")]
        public void Raw_search_text_is_a_syntax_error_to_fts5(string userText)
        {
            using var conn = Database.Open(_dbPath);

            Assert.ThrowsAny<SqliteException>(
                () => conn.Query<UiMovie>(SearchSql, new { q = userText }).ToList());
        }

        /// <summary>
        /// The same inputs, escaped: SQLite has to accept every one of them. Anything that throws
        /// here is a search the user cannot type.
        /// </summary>
        [Theory]
        [MemberData(nameof(AwkwardInput))]
        public void Escaped_search_text_is_always_a_query_sqlite_accepts(string userText)
        {
            var results = Search(userText);

            Assert.NotNull(results);
        }

        [Theory]
        [InlineData("Face/Off", "Face/Off")]
        [InlineData("face/off", "Face/Off")]
        [InlineData("Mission: Impossible", "Mission: Impossible")]
        [InlineData("mission", "Mission: Impossible")]
        [InlineData("Dude, Where's My Car?", "Dude, Where's My Car?")]
        [InlineData("Where's", "Dude, Where's My Car?")]
        [InlineData("NEAR Dark", "Near Dark")]
        [InlineData("Star Wars: Episode V - The Empire Strikes Back", "Star Wars: Episode V - The Empire Strikes Back")]
        [InlineData("Amélie", "Amélie")]
        [InlineData("WALL·E", "WALL·E")]
        public void A_title_typed_as_it_is_written_finds_that_film(string userText, string expected)
        {
            var titles = Search(userText).Select(m => m.Title).ToList();

            Assert.Contains(expected, titles);
        }

        [Fact]
        public void A_half_typed_word_matches_by_prefix()
        {
            // Nobody waits until they have typed "impossible" to expect a result.
            Assert.Contains("Mission: Impossible", Search("impos").Select(m => m.Title));
            Assert.Contains("The Matrix", Search("matr").Select(m => m.Title));
        }

        [Fact]
        public void Only_the_word_still_being_typed_is_a_prefix()
        {
            // "matrix relo" is someone mid-word; "matr reloaded" is someone who typed a space
            // after "matr" and therefore meant that whole word.
            Assert.Contains("The Matrix Reloaded", Search("matrix relo").Select(m => m.Title));
            Assert.Empty(Search("matr reloaded"));
        }

        [Fact]
        public void More_words_narrow_the_results()
        {
            var one = Search("matrix").Select(m => m.Title).ToList();
            var two = Search("matrix reloaded").Select(m => m.Title).ToList();

            Assert.Equal(2, one.Count);
            Assert.Equal(new[] { "The Matrix Reloaded" }, two);
        }

        [Fact]
        public void A_film_that_is_not_there_is_not_found()
        {
            // The escaping must not quietly widen a search into matching everything.
            Assert.Empty(Search("Zulu"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("???")]
        [InlineData("-")]
        [InlineData("\"")]
        [InlineData("( ) ^ : *")]
        public void Text_with_no_word_in_it_is_not_a_search(string? userText)
        {
            // Deliberate: punctuation alone is the same state as an empty box, so the library
            // stays on screen instead of blanking while someone types an opening quote.
            Assert.Null(FtsQuery.Build(userText));
            Assert.Equal(Library.Length, Search(userText).Count);
        }

        [Theory]
        [InlineData("Matrix", "\"Matrix\"*")]
        [InlineData("  Matrix  ", "\"Matrix\"*")]
        [InlineData("The Matrix", "\"The\" \"Matrix\"*")]
        [InlineData("Mission: Impossible", "\"Mission:\" \"Impossible\"*")]
        [InlineData("Face/Off", "\"Face/Off\"*")]
        [InlineData("NEAR OR AND NOT", "\"NEAR\" \"OR\" \"AND\" \"NOT\"*")]
        [InlineData("-matrix", "\"-matrix\"*")]
        [InlineData("^matrix", "\"^matrix\"*")]
        [InlineData("(matrix)", "\"(matrix)\"*")]
        [InlineData("matrix*", "\"matrix*\"*")]
        [InlineData("say \"anything\"", "\"say\" \"\"\"anything\"\"\"*")]
        [InlineData("??? matrix", "\"matrix\"*")]
        [InlineData("matrix ???", "\"matrix\"*")]
        public void Every_word_becomes_a_quoted_literal(string userText, string expected)
        {
            Assert.Equal(expected, FtsQuery.Build(userText));
        }

        [Fact]
        public void The_prefix_operator_sits_outside_the_closing_quote()
        {
            // Inside it, * is a literal asterisk in the search term and matches nothing.
            var built = FtsQuery.Build("matrix");

            Assert.EndsWith("\"*", built);
            Assert.DoesNotContain("*\"", built);
        }
    }
}
