using System;
using System.Globalization;
using System.Text;

namespace UrDatabase.Services
{
    /// <summary>
    /// The key a film's rating and viewings are stored under.
    /// </summary>
    /// <remarks>
    /// <c>tmdb:603</c> once the film has been identified, because that is the one name for a film
    /// that survives a correction of its title, is shared by the local copy and the server copy,
    /// and is what UrActor keys its own reviews and calendar on.
    ///
    /// Until then <c>title:the matrix|1999</c>. A scanned film with no TMDB key configured may
    /// never be identified, and the owner should still be able to say they watched it. The title
    /// is case-folded and its whitespace collapsed so that the same film typed or scanned two
    /// slightly different ways is still one film; the year is part of it because a title alone is
    /// not a film — there are four called "A Star Is Born".
    /// </remarks>
    public static class FilmKey
    {
        public const string TmdbPrefix = "tmdb:";
        public const string TitlePrefix = "title:";

        /// <summary>The TMDB key when there is one, otherwise the title key. Null when neither exists.</summary>
        public static string? For(int? tmdbId, string? title, int? year) =>
            tmdbId is > 0
                ? TmdbPrefix + tmdbId.Value.ToString(CultureInfo.InvariantCulture)
                : ForTitle(title, year);

        /// <summary>The title key, or null for a film with no usable title.</summary>
        public static string? ForTitle(string? title, int? year)
        {
            var normalised = NormaliseTitle(title);
            if (normalised.Length == 0) return null;

            var yearPart = year is > 0 ? year.Value.ToString(CultureInfo.InvariantCulture) : "";
            return TitlePrefix + normalised + "|" + yearPart;
        }

        /// <summary>Trimmed, lower-cased in the invariant culture, and with every run of whitespace made one space.</summary>
        public static string NormaliseTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return "";

            var sb = new StringBuilder(title.Length);
            var pendingSpace = false;

            foreach (var ch in title.Trim())
            {
                if (char.IsWhiteSpace(ch))
                {
                    pendingSpace = true;
                    continue;
                }

                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }
    }
}
