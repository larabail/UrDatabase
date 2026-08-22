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

        /// <summary>Cache buster for the primary image; changes when the artwork does.</summary>
        public string? ImageTag { get; set; }
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
                ImageTag = Lookup(ImageTags, "Primary")
            };
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
