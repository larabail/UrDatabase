using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace UrDatabase.Services
{
    /// <summary>
    /// Who a film was watched with: typed as one line of names, stored as a JSON array.
    /// </summary>
    /// <remarks>
    /// Plain names, not accounts. UrActor's "seen with" is a list of UrActor friends, and joining
    /// the two up is the sync this app does not do yet; a name typed here is just a name.
    /// </remarks>
    public static class Companions
    {
        /// <summary>Long enough for any real name, short enough that a pasted paragraph is not one.</summary>
        public const int MaxNameLength = 80;

        private static readonly char[] Separators = { ',', ';', '\n', '\r' };

        /// <summary>
        /// "Ana, Luis;  ana" â†’ ["Ana", "Luis"]. Split on commas, semicolons and line breaks,
        /// trimmed, inner whitespace collapsed, and de-duplicated ignoring case â€” the first
        /// spelling wins, so the list keeps the order and capitalisation the owner typed first.
        /// </summary>
        public static IReadOnlyList<string> Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            return Clean(text.Split(Separators));
        }

        /// <summary>The same cleaning applied to names that are already separate.</summary>
        public static IReadOnlyList<string> Clean(IEnumerable<string?>? names)
        {
            var result = new List<string>();
            if (names is null) return result;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in names)
            {
                var name = Collapse(raw);
                if (name.Length == 0) continue;
                if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
                if (seen.Add(name)) result.Add(name);
            }

            return result;
        }

        /// <summary>The stored form. Null for nobody, so an empty column means watched alone.</summary>
        public static string? ToJson(IEnumerable<string?>? names)
        {
            var cleaned = Clean(names);
            return cleaned.Count == 0 ? null : JsonSerializer.Serialize(cleaned);
        }

        /// <summary>
        /// Reads the stored form back. Anything that is not a JSON array of strings is treated as
        /// nobody rather than thrown: a hand-edited row should not stop a film page opening.
        /// </summary>
        public static IReadOnlyList<string> FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return Array.Empty<string>();

            try
            {
                return Clean(JsonSerializer.Deserialize<List<string?>>(json));
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }

        /// <summary>The names as one editable line: "Ana, Luis".</summary>
        public static string Join(IEnumerable<string>? names) =>
            names is null ? "" : string.Join(", ", names);

        /// <summary>"with Ana", "with Ana and Luis", "with Ana, Luis and Marta", or empty.</summary>
        public static string Describe(IReadOnlyList<string>? names)
        {
            if (names is null || names.Count == 0) return "";
            if (names.Count == 1) return "with " + names[0];

            return "with " + string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
        }

        /// <summary>
        /// Every name used before, most often first, ties broken by whoever was seen most recently.
        /// </summary>
        /// <param name="viewings">Companion lists, newest viewing first.</param>
        public static IReadOnlyList<string> Suggestions(IEnumerable<IReadOnlyList<string>>? viewings)
        {
            if (viewings is null) return Array.Empty<string>();

            var counts = new Dictionary<string, (string Name, int Count, int FirstSeen)>(StringComparer.OrdinalIgnoreCase);
            var order = 0;

            foreach (var viewing in viewings)
            {
                foreach (var name in Clean(viewing))
                {
                    counts[name] = counts.TryGetValue(name, out var found)
                        ? (found.Name, found.Count + 1, found.FirstSeen)
                        : (name, 1, order++);
                }
            }

            return counts.Values
                .OrderByDescending(v => v.Count)
                .ThenBy(v => v.FirstSeen)
                .Select(v => v.Name)
                .ToList();
        }

        /// <summary>Whether the line already names this person, ignoring case.</summary>
        public static bool Contains(string? text, string? name) =>
            !string.IsNullOrWhiteSpace(name)
            && Parse(text).Contains(Collapse(name), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// What clicking a name chip does to the line: adds the person when they are not in it,
        /// takes them out when they are. Everything else typed is kept, cleaned the way
        /// <see cref="Parse"/> cleans it. "Ana" + Luis → "Ana, Luis"; "Ana, Luis" + luis → "Ana".
        /// </summary>
        public static string Toggle(string? text, string? name)
        {
            var names = Parse(text).ToList();
            var person = Collapse(name);
            if (person.Length == 0) return Join(names);

            var removed = names.RemoveAll(n => string.Equals(n, person, StringComparison.OrdinalIgnoreCase));
            if (removed == 0) names.Add(person);

            return Join(names);
        }

        /// <summary>How many people are offered as chips. Enough for a household and its regulars.</summary>
        public const int MaxChips = 8;

        /// <summary>
        /// The chips under the line: the most frequent companions, each marked with whether the
        /// line already names them. Somebody typed into the line who is not among the regulars is
        /// not given a chip — it is already in the box, which is where it can be edited.
        /// </summary>
        public static IReadOnlyList<Models.CompanionChip> Chips(IReadOnlyList<string>? known, string? text, int max = MaxChips)
        {
            if (known is null || known.Count == 0 || max <= 0) return Array.Empty<Models.CompanionChip>();

            var inLine = new HashSet<string>(Parse(text), StringComparer.OrdinalIgnoreCase);
            return Clean(known)
                .Take(max)
                .Select(name => new Models.CompanionChip(name, inLine.Contains(name)))
                .ToList();
        }
        private static string Collapse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
