using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UrDatabase.Services
{
    /// <summary>
    /// One setting in the file that this app has no such thing as, and the name it was most
    /// likely meant to be. Holds only key names — never a value — so it is safe to write to the
    /// log and to put on screen even when the line it came from was a password.
    /// </summary>
    public sealed class UnknownSetting
    {
        /// <summary>The key as written, qualified by its parent: <c>Jellyfin.Url</c>.</summary>
        public string Key { get; init; } = "";

        /// <summary>
        /// What it probably meant, empty when nothing in the model is close enough to be worth
        /// guessing at. More than one when the evidence genuinely does not choose between them:
        /// a bare <c>ApiKey</c> at the top level could be either metadata key.
        /// </summary>
        public IReadOnlyList<string> Suggestions { get; init; } = Array.Empty<string>();

        /// <summary>One line for the log, in the shape a person can search the README for.</summary>
        public string Describe() =>
            Suggestions.Count == 0
                ? $"unknown setting \"{Key}\" — ignored"
                : $"unknown setting \"{Key}\" — did you mean {ConfigDiagnostics.Quote(Suggestions)}?";

        public override string ToString() => Describe();
    }

    /// <summary>
    /// Reads a configuration file for keys the app does not have, so that a typo says so instead
    /// of doing nothing.
    ///
    /// This exists because of one evening lost to <c>"Jellyfin": { "Url": ... }</c>, where the
    /// field is <c>ServerUrl</c>. The wrong name deserialised to nothing, the server was never
    /// contacted, and the app started perfectly with an empty library and no thread to pull. Every
    /// other setting has the same trap in it.
    ///
    /// What it deliberately does not do is refuse to start.
    /// <see cref="JsonUnmappedMemberHandling.Disallow"/> would turn one mistyped key into a file
    /// that cannot be read at all, and would mean a configuration written by a newer version
    /// bricking an older one — which is a worse failure than the one being fixed. Unknown keys are
    /// collected, reported and then ignored exactly as before.
    ///
    /// It says nothing about malformed JSON either. A file that will not parse has no key names to
    /// report, and the broader silence around a file that fails to load is issue #25.
    /// </summary>
    public static class ConfigDiagnostics
    {
        /// <summary>
        /// How deep to follow nested settings objects. Three is already one more than the model
        /// has; the limit is only here so a type that somehow refers to itself cannot spin.
        /// </summary>
        private const int MaxDepth = 3;

        /// <summary>
        /// Beyond this many equally good candidates the guess stops being a help, and the bare
        /// "unknown setting" is the more honest answer.
        /// </summary>
        private const int MaxSuggestions = 3;

        /// <summary>
        /// How alike two names have to be before one is offered as the other's intent. Set by the
        /// two cases that matter: <c>Url</c> for <c>ServerUrl</c> has to clear it, and <c>Url</c>
        /// against <c>Username</c> — the nearest wrong answer in the same object — must not.
        /// </summary>
        private const double SuggestionFloor = 0.6;

        /// <summary>
        /// Every key in <paramref name="json"/> that <see cref="AppConfig"/> has no property for,
        /// in the order they appear. Empty for an absent, empty or unparseable file, which is the
        /// ordinary case: a fresh install has no configuration at all and must start silently.
        /// </summary>
        public static IReadOnlyList<UnknownSetting> Inspect(string? json) => Inspect(json, typeof(AppConfig));

        internal static IReadOnlyList<UnknownSetting> Inspect(string? json, Type model)
        {
            var found = new List<UnknownSetting>();
            if (string.IsNullOrWhiteSpace(json)) return found;

            try
            {
                // The same leniencies the deserialiser is given. Reading the document more
                // strictly than the app reads it would report a file that loads fine as broken.
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

                if (document.RootElement.ValueKind != JsonValueKind.Object) return found;

                Walk(document.RootElement, model, prefix: "", found, depth: 0);
            }
            catch (Exception)
            {
                // A file that does not parse was not loaded either, so the app is already running
                // on defaults. Naming keys it does not have would be a guess about a file nobody
                // has read.
            }

            return found;
        }

        /// <summary>
        /// Writes every unrecognised setting to <c>startup.log</c> and returns the single line to
        /// put in front of a person, or null when there is nothing to say — which is the usual
        /// answer, and the point: an install with no configuration warns about nothing.
        /// </summary>
        public static string? Report(AppConfig? config)
        {
            var unknown = config?.UnknownSettings;
            if (config is null || unknown is null || unknown.Count == 0) return null;

            var file = config.SourcePath ?? AppConfig.FileName;
            foreach (var setting in unknown)
                AppLog.Write("startup.log", $"{file}: {setting.Describe()}");

            return Summarize(unknown, config.SourcePath);
        }

        /// <summary>
        /// The one sentence for a status line or a dialog, or null when nothing was wrong. Names
        /// the file, because the app reads from three places and the one it read is the only one
        /// worth editing.
        /// </summary>
        public static string? Summarize(IReadOnlyList<UnknownSetting>? unknown, string? sourcePath)
        {
            if (unknown is null || unknown.Count == 0) return null;

            var file = FileLabel(sourcePath);
            var first = unknown[0];
            var hint = first.Suggestions.Count == 0 ? "" : $" Did you mean {Quote(first.Suggestions)}?";

            return unknown.Count == 1
                ? $"{file}: \"{first.Key}\" is not a setting UrDatabase recognises, so it is being ignored.{hint}"
                : $"{file}: {unknown.Count} settings are not ones UrDatabase recognises and are being " +
                  $"ignored, starting with \"{first.Key}\".{hint}";
        }

        private static string FileLabel(string? sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) return AppConfig.FileName;

            try { return Path.GetFileName(sourcePath) is { Length: > 0 } name ? name : sourcePath; }
            catch { return AppConfig.FileName; }
        }

        private static void Walk(JsonElement element, Type model, string prefix, List<UnknownSetting> found, int depth)
        {
            var mappable = Mappable(model);
            var names = mappable.Select(NameOf).ToArray();

            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";

                // Case-insensitively, because that is how the app deserialises: "tmdbapikey" is
                // read, so it is not a mistake and must not be reported as one.
                var match = mappable.FirstOrDefault(candidate =>
                    string.Equals(NameOf(candidate), property.Name, StringComparison.OrdinalIgnoreCase));

                if (match is null)
                {
                    found.Add(new UnknownSetting { Key = key, Suggestions = Suggest(property.Name, names) });
                    continue;
                }

                if (depth < MaxDepth &&
                    property.Value.ValueKind == JsonValueKind.Object &&
                    IsNestedSettings(match.PropertyType))
                    Walk(property.Value, match.PropertyType, key, found, depth + 1);
            }
        }

        /// <summary>
        /// The properties the deserialiser would actually fill in. Read off the model rather than
        /// listed here on purpose: a second list of key names is a list that drifts, and drifting
        /// would mean reporting a real setting as a typo, which is worse than the original bug.
        ///
        /// A computed property is not one of these. <c>Jellyfin.IsConfigured</c> looks like a
        /// setting and is not; writing it does nothing, so it is worth being told about.
        /// </summary>
        internal static IReadOnlyList<PropertyInfo> Mappable(Type model) =>
            model.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.SetMethod is { IsPublic: true })
                .Where(property => !IsIgnored(property))
                .OrderBy(NameOf, StringComparer.Ordinal)
                .ToArray();

        private static bool IsIgnored(PropertyInfo property)
        {
            var ignore = property.GetCustomAttribute<JsonIgnoreAttribute>();

            // WhenWritingNull and WhenWritingDefault only affect what gets written out; a property
            // carrying one of those is still read back in, so it is still a real setting.
            return ignore is not null && ignore.Condition == JsonIgnoreCondition.Always;
        }

        private static string NameOf(PropertyInfo property) =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? property.Name;

        /// <summary>
        /// Whether a property holds a nested group of settings worth looking inside — the
        /// <c>Jellyfin</c> object being the only one so far. Restricted to this assembly's own
        /// types: a framework type with an object shape is a value, not a section of the file.
        /// </summary>
        private static bool IsNestedSettings(Type type) =>
            type.IsClass &&
            type != typeof(string) &&
            !typeof(IEnumerable).IsAssignableFrom(type) &&
            type.Assembly == typeof(AppConfig).Assembly;

        /// <summary>
        /// What the key was probably meant to be. Returns everything tied for closest, because
        /// choosing arbitrarily between two equally good answers is how a suggestion sends
        /// somebody to correct a key that was already right.
        /// </summary>
        internal static IReadOnlyList<string> Suggest(string name, IReadOnlyList<string> known)
        {
            var best = 0.0;
            var matches = new List<string>();

            foreach (var candidate in known)
            {
                var score = Similarity(name, candidate);
                if (score < SuggestionFloor) continue;

                if (score > best + 0.0001)
                {
                    best = score;
                    matches.Clear();
                    matches.Add(candidate);
                }
                else if (Math.Abs(score - best) <= 0.0001)
                {
                    matches.Add(candidate);
                }
            }

            return matches.Count > MaxSuggestions ? Array.Empty<string>() : matches;
        }

        /// <summary>
        /// How alike two key names are, from 0 to 1. Edit distance on its own is not enough for
        /// the case this was written for: <c>Url</c> is six edits from <c>ServerUrl</c> and could
        /// not have meant anything else, so a name contained in another scores on how much of it
        /// is there rather than on how much is missing.
        ///
        /// Punctuation and case are removed first, which makes <c>tmdb_api_key</c> an exact match
        /// for <c>TmdbApiKey</c> — a key the deserialiser does not read but a person plainly meant.
        /// </summary>
        internal static double Similarity(string? left, string? right)
        {
            var a = Normalize(left);
            var b = Normalize(right);

            if (a.Length == 0 || b.Length == 0) return 0;
            if (string.Equals(a, b, StringComparison.Ordinal)) return 1;

            var longest = Math.Max(a.Length, b.Length);
            var shortest = Math.Min(a.Length, b.Length);
            var score = 1.0 - (double)Distance(a, b) / longest;

            // Two letters inside a longer word is a coincidence; three is a name.
            if (shortest >= 3 && (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal)))
                score = Math.Max(score, SuggestionFloor + (1 - SuggestionFloor) * shortest / longest);

            return score;
        }

        /// <summary>Levenshtein distance, two rows at a time: these are single words.</summary>
        private static int Distance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (var j = 0; j <= b.Length; j++) previous[j] = j;

            for (var i = 1; i <= a.Length; i++)
            {
                current[0] = i;

                for (var j = 1; j <= b.Length; j++)
                {
                    var substitute = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitute);
                }

                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }

        private static string Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return "";

            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
                if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));

            return builder.ToString();
        }

        /// <summary>Renders one or more names as a person would read them out.</summary>
        internal static string Quote(IReadOnlyList<string> names)
        {
            var quoted = names.Select(name => $"\"{name}\"").ToArray();

            return quoted.Length switch
            {
                0 => "",
                1 => quoted[0],
                _ => string.Join(", ", quoted[..^1]) + " or " + quoted[^1]
            };
        }
    }
}
