using System;
using System.Collections.Generic;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns what someone typed into the search box into an expression FTS5 will accept.
    ///
    /// Binding the text as a parameter stops SQL injection, but FTS5 has a second query language
    /// layered inside that one parameter: <c>"</c>, <c>*</c>, <c>:</c>, <c>^</c>, <c>-</c>,
    /// <c>(</c>, <c>)</c> and the bare words AND, OR, NOT and NEAR are all operators there. Film
    /// titles are full of those — <c>Face/Off</c>, <c>Mission: Impossible</c>,
    /// <c>Dude, Where's My Car?</c> — so passing the raw text through returned
    /// <c>fts5: syntax error</c> and the window reported it as the library being unreadable.
    ///
    /// The fix is to hand FTS5 no operators at all: every word becomes a quoted string literal,
    /// which is the one construct whose contents are never parsed. Out of the window and pure,
    /// because a rule reachable only from a text-changed handler needs a UI thread to test, and
    /// that is why this bug shipped untested.
    /// </summary>
    public static class FtsQuery
    {
        /// <summary>
        /// The MATCH expression for <paramref name="userText"/>, or <c>null</c> when there is
        /// nothing in it to search for — empty, whitespace, or punctuation like <c>???</c> that
        /// contains no word at all. A caller treats <c>null</c> as "not searching" and lists the
        /// whole library rather than running a query that cannot match anything.
        /// </summary>
        public static string? Build(string? userText)
        {
            if (string.IsNullOrWhiteSpace(userText)) return null;

            var terms = new List<string>();
            foreach (var token in userText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                // A token of pure punctuation contains no term for the tokenizer to index, and
                // quoting it would produce an empty phrase. Drop it instead.
                if (!HasWordCharacter(token)) continue;

                // Doubling is FTS5's own escape for a quote inside a string literal, so an
                // unbalanced " typed by hand survives as a literal character.
                terms.Add('"' + token.Replace("\"", "\"\"") + '"');
            }

            if (terms.Count == 0) return null;

            // Prefix match on the last word only: the search box filters as you type, so that
            // word is the one still being typed, while every earlier word was finished with a
            // space. The * has to sit outside the closing quote to be the prefix operator —
            // inside it, it would be a literal asterisk that matches nothing.
            terms[terms.Count - 1] += "*";

            // Space-separated phrases are an implicit AND, so more words narrow the results.
            return string.Join(" ", terms);
        }

        private static bool HasWordCharacter(string token)
        {
            foreach (var c in token)
                if (char.IsLetterOrDigit(c)) return true;

            return false;
        }
    }
}
