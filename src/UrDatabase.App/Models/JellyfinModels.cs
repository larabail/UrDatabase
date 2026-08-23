using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace UrDatabase.Models
{
    /// <summary>Where a film in the library came from.</summary>
    public enum MovieSource
    {
        /// <summary>A file on this machine, found by scanning a watch folder.</summary>
        Local = 0,

        /// <summary>An item on a Jellyfin server. Nothing about it is on this disk.</summary>
        Jellyfin = 1
    }

    /// <summary>
    /// One film as Jellyfin describes it. Jellyfin has already done the identification work — the
    /// title is curated rather than parsed out of a filename, and the genres and provider ids come
    /// straight from its own metadata — so nothing here is guessed at and no second lookup is
    /// needed to fill it in.
    /// </summary>
    public sealed class JellyfinMovie
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public int? Year { get; set; }

        /// <summary>Comma separated, matching how <see cref="UiMovie.Genres"/> is stored.</summary>
        public string Genres { get; set; } = "";

        public string Overview { get; set; } = "";
        public int? RuntimeMinutes { get; set; }

        /// <summary>
        /// Jellyfin's own community rating. Emphatically not an IMDb rating: it is a different
        /// number from a different population, and showing it under IMDb's name would be a lie.
        /// </summary>
        public double? CommunityRating { get; set; }

        /// <summary>The <c>tt…</c> id, when Jellyfin knows it. What OMDb needs for a real rating.</summary>
        public string? ImdbId { get; set; }

        public string? TmdbId { get; set; }

        /// <summary>
        /// The billed cast, as <c>"Name (Role)"</c> — the same shape the TMDB path produces, so
        /// the details screen takes both apart with the same parser and neither source needs a
        /// second code path.
        /// </summary>
        public List<string> Cast { get; set; } = new();

        /// <summary>Directors and writers, as <c>"Director: Name"</c>.</summary>
        public List<string> Crew { get; set; } = new();

        /// <summary>Cache buster for the primary image; changes when the artwork does.</summary>
        public string? ImageTag { get; set; }
    }

    /// <summary>
    /// One person on a Jellyfin item: an actor with a part, or a member of the crew with a job.
    /// </summary>
    public sealed class JellyfinPersonDto
    {
        [JsonPropertyName("Name")] public string? Name { get; set; }

        /// <summary>The part played. Empty for crew, and for an uncredited actor.</summary>
        [JsonPropertyName("Role")] public string? Role { get; set; }

        /// <summary><c>Actor</c>, <c>Director</c>, <c>Writer</c>, <c>Producer</c>, and so on.</summary>
        [JsonPropertyName("Type")] public string? Type { get; set; }

        // Compared case-insensitively rather than against a literal. Jellyfin has shipped these
        // capitalised and, in places, lowercased, and a film losing its whole cast over a capital
        // A is the kind of failure nobody thinks to look for.
        public bool IsActor => Is("Actor");

        public bool IsDirector => Is("Director");

        /// <summary>
        /// Jellyfin uses a single "Writer" type where TMDB distinguishes screenplay from story,
        /// so this matches on the substring the way the local path already does.
        /// </summary>
        public bool IsWriter =>
            Type is not null && Type.Contains("Writer", StringComparison.OrdinalIgnoreCase);

        private bool Is(string type) => string.Equals(Type?.Trim(), type, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The shape of a Jellyfin user as <c>/Users</c> returns it.</summary>
    public sealed class JellyfinUserDto
    {
        [JsonPropertyName("Id")] public string Id { get; set; } = "";
        [JsonPropertyName("Name")] public string Name { get; set; } = "";
    }

    /// <summary>The response from <c>/Users/AuthenticateByName</c>.</summary>
    public sealed class JellyfinAuthResult
    {
        [JsonPropertyName("AccessToken")] public string? AccessToken { get; set; }
        [JsonPropertyName("User")] public JellyfinUserDto? User { get; set; }
    }

    /// <summary>One of a user's top level libraries.</summary>
    public sealed class JellyfinViewDto
    {
        [JsonPropertyName("Id")] public string Id { get; set; } = "";
        [JsonPropertyName("Name")] public string Name { get; set; } = "";

        /// <summary>"movies", "tvshows", "music"… The only thing this app looks at is "movies".</summary>
        [JsonPropertyName("CollectionType")] public string? CollectionType { get; set; }
    }

    /// <summary>A page of items, with the total so the caller knows when to stop asking.</summary>
    public sealed class JellyfinItemsDto
    {
        [JsonPropertyName("Items")] public List<JellyfinItemDto> Items { get; set; } = new();
        [JsonPropertyName("TotalRecordCount")] public int TotalRecordCount { get; set; }
    }

    /// <summary>
    /// What <c>/System/Info/Public</c> returns. The one endpoint Jellyfin answers without a
    /// credential, which makes it the only thing a connection test can ask before a sign-in has
    /// ever worked. An address that answers this is Jellyfin; an address that 404s it is not.
    /// </summary>
    public sealed class JellyfinPublicInfoDto
    {
        [JsonPropertyName("ServerName")] public string? ServerName { get; set; }
        [JsonPropertyName("Version")] public string? Version { get; set; }
        [JsonPropertyName("Id")] public string? Id { get; set; }
        [JsonPropertyName("ProductName")] public string? ProductName { get; set; }
    }

    /// <summary>A single item from <c>/Users/{id}/Items</c>, before it is folded into a movie.</summary>
    public sealed class JellyfinItemDto
    {
        [JsonPropertyName("Id")] public string Id { get; set; } = "";
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("ProductionYear")] public int? ProductionYear { get; set; }
        [JsonPropertyName("Genres")] public List<string>? Genres { get; set; }
        [JsonPropertyName("Overview")] public string? Overview { get; set; }
        [JsonPropertyName("RunTimeTicks")] public long? RunTimeTicks { get; set; }
        [JsonPropertyName("CommunityRating")] public double? CommunityRating { get; set; }
        [JsonPropertyName("ProviderIds")] public Dictionary<string, string>? ProviderIds { get; set; }
        [JsonPropertyName("ImageTags")] public Dictionary<string, string>? ImageTags { get; set; }

        /// <summary>
        /// Cast and crew, when <c>People</c> was among the requested fields. Jellyfin has always
        /// been able to report these; for a long time nothing asked, and every film from a server
        /// showed an empty cast list as though it had none.
        /// </summary>
        [JsonPropertyName("People")] public List<JellyfinPersonDto>? People { get; set; }

        /// <summary>
        /// What this user has done with the item: how far in they are, and whether they finished
        /// it. Absent unless <c>UserData</c> was among the requested fields, and absent anyway for
        /// an item nobody has touched.
        /// </summary>
        [JsonPropertyName("UserData")] public JellyfinUserDataDto? UserData { get; set; }

        /// <summary>
        /// This item as one entry of the Continue watching row, or null when it is not one.
        /// </summary>
        /// <remarks>
        /// An item with no id, or with no position in it, is not something to continue: the
        /// endpoint is asked for films that are part-watched, but a server is entitled to include
        /// one that has just been reset, and offering it under that heading would invite somebody
        /// to carry on with a film they never started.
        /// </remarks>
        public JellyfinResumeItem? ToResumeItem(int sortOrder)
        {
            if (string.IsNullOrWhiteSpace(Id)) return null;

            var position = UserData?.PlaybackPositionTicks ?? 0;
            if (position <= 0) return null;

            return new JellyfinResumeItem
            {
                ItemId = Id.Trim(),
                PositionTicks = position,
                RuntimeTicks = RunTimeTicks is > 0 ? RunTimeTicks : null,
                PlayedPercentage = UserData?.PlayedPercentage,
                SortOrder = sortOrder
            };
        }

        /// <summary>
        /// Turns the wire shape into the app's own, dropping anything unusable. Returns null for
        /// an item with no id or no title, which cannot be shown or played either way.
        /// </summary>
        public JellyfinMovie? ToMovie()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) return null;

            return new JellyfinMovie
            {
                ItemId = Id.Trim(),
                Title = Name.Trim(),
                Year = ProductionYear is > 0 ? ProductionYear : null,
                Genres = JoinGenres(Genres),
                Overview = (Overview ?? "").Trim(),
                RuntimeMinutes = TicksToMinutes(RunTimeTicks),
                CommunityRating = CommunityRating,
                ImdbId = Lookup(ProviderIds, "Imdb"),
                TmdbId = Lookup(ProviderIds, "Tmdb"),
                Cast = BuildCast(People),
                Crew = BuildCrew(People),
                ImageTag = Lookup(ImageTags, "Primary")
            };
        }

        /// <summary>
        /// The billed cast, in Jellyfin's own order, capped at ten to match what TMDB supplies for
        /// a local film. A person with no name is dropped rather than shown as a blank row.
        /// </summary>
        internal static List<string> BuildCast(IEnumerable<JellyfinPersonDto>? people)
        {
            if (people is null) return new List<string>();

            return people
                .Where(p => p.IsActor && !string.IsNullOrWhiteSpace(p.Name))
                .Take(10)
                .Select(p => string.IsNullOrWhiteSpace(p.Role)
                    ? p.Name!.Trim()
                    : $"{p.Name!.Trim()} ({p.Role!.Trim()})")
                .ToList();
        }

        /// <summary>
        /// Up to three directors and three writers, in that order, matching the local path. A
        /// server lists everyone from the gaffer down, and a details screen is not a call sheet.
        /// </summary>
        internal static List<string> BuildCrew(IEnumerable<JellyfinPersonDto>? people)
        {
            if (people is null) return new List<string>();

            var materialised = people.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();

            var crew = new List<string>();

            crew.AddRange(materialised
                .Where(p => p.IsDirector)
                .Take(3)
                .Select(p => $"Director: {p.Name!.Trim()}"));

            crew.AddRange(materialised
                .Where(p => p.IsWriter)
                .Take(3)
                .Select(p => $"Writer: {p.Name!.Trim()}"));

            return crew;
        }

        /// <summary>
        /// Genres arrive as a real array, so unlike a scanned film they need no enrichment and
        /// never land in the "Uncategorised" bucket.
        /// </summary>
        internal static string JoinGenres(IEnumerable<string>? genres) =>
            genres is null
                ? ""
                : string.Join(", ", genres.Where(g => !string.IsNullOrWhiteSpace(g)).Select(g => g.Trim()));

        /// <summary>
        /// Jellyfin reports runtime in 100-nanosecond ticks. Rounded to the nearest minute, and
        /// null for zero, because "0 min" reads as a broken file rather than an unknown length.
        /// </summary>
        internal static int? TicksToMinutes(long? ticks)
        {
            if (ticks is null || ticks <= 0) return null;

            var minutes = (int)Math.Round(ticks.Value / (double)TimeSpan.TicksPerMinute, MidpointRounding.AwayFromZero);
            return minutes > 0 ? minutes : null;
        }

        private static string? Lookup(Dictionary<string, string>? map, string key)
        {
            if (map is null) return null;

            foreach (var pair in map)
            {
                if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(pair.Value))
                    return pair.Value.Trim();
            }

            return null;
        }
    }
}
