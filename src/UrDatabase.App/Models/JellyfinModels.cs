using System;
using System.Collections.Generic;
using System.Globalization;
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

        /// <summary>
        /// What the server measured about the file itself: picture size, codecs, and the audio and
        /// subtitle languages it carries. Null for a library synced before this was asked for, and
        /// for an item the server reported no streams on.
        /// </summary>
        public MediaInfo? Media { get; set; }
    }

    /// <summary>
    /// One track inside a Jellyfin item — the picture, an audio dub, a subtitle file.
    /// </summary>
    /// <remarks>
    /// This is the only place in the app where resolution and language are measured rather than
    /// guessed. A scanned file offers nothing but its own name, and a name is whatever the person
    /// who encoded it typed; these numbers came from the container.
    /// </remarks>
    public sealed class JellyfinMediaStreamDto
    {
        /// <summary><c>Video</c>, <c>Audio</c>, <c>Subtitle</c>, <c>EmbeddedImage</c>.</summary>
        [JsonPropertyName("Type")] public string? Type { get; set; }

        [JsonPropertyName("Codec")] public string? Codec { get; set; }

        /// <summary>ISO 639-2, usually. Absent on a track nobody tagged.</summary>
        [JsonPropertyName("Language")] public string? Language { get; set; }

        [JsonPropertyName("Channels")] public int? Channels { get; set; }
        [JsonPropertyName("Width")] public int? Width { get; set; }
        [JsonPropertyName("Height")] public int? Height { get; set; }

        /// <summary>The plain answer: <c>HDR</c> or <c>SDR</c>.</summary>
        [JsonPropertyName("VideoRange")] public string? VideoRange { get; set; }

        /// <summary>The specific one: <c>HDR10</c>, <c>DOVI</c>, <c>HLG</c>. Preferred when present.</summary>
        [JsonPropertyName("VideoRangeType")] public string? VideoRangeType { get; set; }

        /// <summary>Jellyfin's own rendering of the track, which is where Atmos is named.</summary>
        [JsonPropertyName("DisplayTitle")] public string? DisplayTitle { get; set; }

        /// <summary>The codec profile. <c>TrueHD</c> writes "Dolby Atmos" here on some servers.</summary>
        [JsonPropertyName("Profile")] public string? Profile { get; set; }

        [JsonPropertyName("IsDefault")] public bool IsDefault { get; set; }

        public bool IsVideo => Is("Video");

        public bool IsAudio => Is("Audio");

        public bool IsSubtitle => Is("Subtitle");

        // Compared case-insensitively for the same reason JellyfinPersonDto does it: Jellyfin has
        // shipped these capitalised and lowercased, and losing every audio track over a capital A
        // is the kind of failure nobody thinks to look for.
        private bool Is(string type) => string.Equals(Type?.Trim(), type, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One television series as Jellyfin describes it.
    /// </summary>
    /// <remarks>
    /// Almost a <see cref="JellyfinMovie"/>, and almost is why it is a type of its own rather than
    /// a flag on that one. A series has no runtime — the number that matters is per episode — and
    /// it has two counts a film cannot have, which are the whole reason a card for one is not
    /// mistakable for a card for a film.
    ///
    /// Its seasons and episodes are deliberately absent. They are fetched when somebody opens the
    /// series, not during a sync: two hundred shows is thousands of episodes, and pulling them all
    /// up front would turn a sync that takes seconds into one nobody waits for.
    /// </remarks>
    public sealed class JellyfinSeries
    {
        public string ItemId { get; set; } = "";
        public string Title { get; set; } = "";

        /// <summary>The year it started. Jellyfin reports the first year, not a range.</summary>
        public int? Year { get; set; }

        /// <summary>Comma separated, matching how <see cref="UiMovie.Genres"/> is stored.</summary>
        public string Genres { get; set; } = "";

        public string Overview { get; set; } = "";

        /// <inheritdoc cref="JellyfinMovie.CommunityRating"/>
        public double? CommunityRating { get; set; }

        public string? ImdbId { get; set; }
        public string? TmdbId { get; set; }

        public List<string> Cast { get; set; } = new();
        public List<string> Crew { get; set; } = new();

        public string? ImageTag { get; set; }

        /// <summary>
        /// How many seasons, when the server said. Null rather than zero for a server that did not
        /// answer with the field: "no seasons" and "nobody counted" are different facts, and only
        /// one of them should be printed on a card.
        /// </summary>
        public int? SeasonCount { get; set; }

        /// <summary>How many episodes across every season, on the same terms as <see cref="SeasonCount"/>.</summary>
        public int? EpisodeCount { get; set; }
    }

    /// <summary>One season of a series. A folder, not something that plays.</summary>
    public sealed class JellyfinSeason
    {
        public string ItemId { get; set; } = "";
        public string SeriesId { get; set; } = "";

        /// <summary>The server's own name for it — usually "Season 1", sometimes "Specials".</summary>
        public string Name { get; set; } = "";

        /// <summary>
        /// Its number, when it has one. Specials are season 0 on most servers and carry no number
        /// at all on some, which is why this is nullable and why nothing sorts on it alone.
        /// </summary>
        public int? Number { get; set; }

        public string? ImageTag { get; set; }

        /// <summary>How many episodes the server says are in it. Null when it did not say.</summary>
        public int? EpisodeCount { get; set; }
    }

    /// <summary>One episode. The only television item in this app that actually plays.</summary>
    public sealed class JellyfinEpisode
    {
        public string ItemId { get; set; } = "";
        public string SeriesId { get; set; } = "";

        /// <summary>Which season folder it belongs to. Empty when the server did not say.</summary>
        public string SeasonId { get; set; } = "";

        public string Name { get; set; } = "";

        /// <summary>The season it is in, from Jellyfin's <c>ParentIndexNumber</c>.</summary>
        public int? SeasonNumber { get; set; }

        /// <summary>Its number within the season, from Jellyfin's <c>IndexNumber</c>.</summary>
        public int? Number { get; set; }

        public string Overview { get; set; } = "";
        public int? RuntimeMinutes { get; set; }
        public double? CommunityRating { get; set; }
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

        /// <summary>
        /// "movies", "tvshows", "music"… This app reads the first two and ignores the rest.
        /// </summary>
        [JsonPropertyName("CollectionType")] public string? CollectionType { get; set; }

        /// <summary>Jellyfin's own name for a library of films.</summary>
        public const string MovieCollection = "movies";

        /// <summary>Jellyfin's own name for a library of television.</summary>
        public const string SeriesCollection = "tvshows";

        public bool IsMovieLibrary => Is(MovieCollection);

        public bool IsSeriesLibrary => Is(SeriesCollection);

        private bool Is(string collectionType) =>
            string.Equals(CollectionType, collectionType, StringComparison.OrdinalIgnoreCase);
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
        /// <c>Movie</c>, <c>Series</c>, <c>Season</c>, <c>Episode</c>… Only read where one request
        /// can return more than one kind of thing; everywhere else the query already said which it
        /// wanted and the server does not send anything else.
        /// </summary>
        [JsonPropertyName("Type")] public string? Type { get; set; }

        /// <summary>The episode's number within its season, or the season's within its series.</summary>
        [JsonPropertyName("IndexNumber")] public int? IndexNumber { get; set; }

        /// <summary>An episode's season number. Jellyfin names it after the parent, not the season.</summary>
        [JsonPropertyName("ParentIndexNumber")] public int? ParentIndexNumber { get; set; }

        [JsonPropertyName("SeriesId")] public string? SeriesId { get; set; }
        [JsonPropertyName("SeasonId")] public string? SeasonId { get; set; }

        /// <summary>
        /// The programme an episode belongs to. Sent with an episode without having to be asked
        /// for, which is what makes a mixed Continue watching row renderable from one request:
        /// nothing caches episodes until a series is opened, so the row would otherwise have an
        /// id and no way to name it.
        /// </summary>
        [JsonPropertyName("SeriesName")] public string? SeriesName { get; set; }

        /// <summary>The season's own name — "Season 1", sometimes "Specials". Rarely useful alone.</summary>
        [JsonPropertyName("SeasonName")] public string? SeasonName { get; set; }

        /// <summary>
        /// Direct children: seasons for a series, episodes for a season. Only sent when
        /// <c>ChildCount</c> is among the requested fields, and not by every server version, which
        /// is why nothing here treats its absence as zero.
        /// </summary>
        [JsonPropertyName("ChildCount")] public int? ChildCount { get; set; }

        /// <summary>Children all the way down: the episode count of a whole series.</summary>
        [JsonPropertyName("RecursiveItemCount")] public int? RecursiveItemCount { get; set; }
        /// Every track in the file, when <c>MediaStreams</c> was among the requested fields. The
        /// only measured description of a copy this app can get: a scanned file offers nothing but
        /// its own name.
        /// </summary>
        [JsonPropertyName("MediaStreams")] public List<JellyfinMediaStreamDto>? MediaStreams { get; set; }

        /// <summary>Picture width as the server records it on the item, independent of the streams.</summary>
        [JsonPropertyName("Width")] public int? Width { get; set; }

        [JsonPropertyName("Height")] public int? Height { get; set; }

        /// <summary>The container — <c>mkv</c>, <c>mp4</c>. Not always populated on a list request.</summary>
        [JsonPropertyName("Container")] public string? Container { get; set; }

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
        /// endpoint is asked for part-watched things, but a server is entitled to include one that
        /// has just been reset, and offering it under that heading would invite somebody to carry
        /// on with a film they never started.
        ///
        /// An episode carries what its card has to say as well as where it got to. Its own name is
        /// often meaningless out of context — "In throes of increasing wonder … " names no
        /// programme — so the series, the season and the number come across with it and the name
        /// is the secondary line rather than the title.
        /// </remarks>
        public JellyfinResumeItem? ToResumeItem(int sortOrder)
        {
            if (string.IsNullOrWhiteSpace(Id)) return null;

            var position = UserData?.PlaybackPositionTicks ?? 0;
            if (position <= 0) return null;

            var isEpisode = string.Equals(Type?.Trim(), JellyfinResumeItem.EpisodeType, StringComparison.OrdinalIgnoreCase);

            return new JellyfinResumeItem
            {
                ItemId = Id.Trim(),
                ItemType = isEpisode ? JellyfinResumeItem.EpisodeType : JellyfinResumeItem.MovieType,
                PositionTicks = position,
                RuntimeTicks = RunTimeTicks is > 0 ? RunTimeTicks : null,
                PlayedPercentage = UserData?.PlayedPercentage,
                SortOrder = sortOrder,

                // Only ever filled for an episode. A film that arrived with a SeriesName — which
                // no server sends, but a proxy or a future field could — would otherwise be given
                // an episode's rendering on the strength of one stray string.
                SeriesId = isEpisode ? (SeriesId ?? "").Trim() : "",
                SeriesName = isEpisode ? (SeriesName ?? "").Trim() : "",
                SeasonNumber = isEpisode ? ParentIndexNumber : null,
                EpisodeNumber = isEpisode ? IndexNumber : null,
                Name = isEpisode ? (Name ?? "").Trim() : ""
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
                ImageTag = Lookup(ImageTags, "Primary"),
                Media = BuildMedia(MediaStreams, Width, Height, Container)
            };
        }

        /// <summary>
        /// The same, for a television series. Kept apart from <see cref="ToMovie"/> rather than
        /// folded into it with a flag, because the two disagree about what is worth carrying: a
        /// series has no runtime of its own and has counts a film cannot have.
        /// </summary>
        public JellyfinSeries? ToSeries()
        {
            if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Name)) return null;

            return new JellyfinSeries
            {
                ItemId = Id.Trim(),
                Title = Name.Trim(),
                Year = ProductionYear is > 0 ? ProductionYear : null,
                Genres = JoinGenres(Genres),
                Overview = (Overview ?? "").Trim(),
                CommunityRating = CommunityRating,
                ImdbId = Lookup(ProviderIds, "Imdb"),
                TmdbId = Lookup(ProviderIds, "Tmdb"),
                Cast = BuildCast(People),
                Crew = BuildCrew(People),
                ImageTag = Lookup(ImageTags, "Primary"),
                SeasonCount = Positive(ChildCount),
                EpisodeCount = Positive(RecursiveItemCount)
            };
        }

        /// <summary>
        /// One season. <paramref name="seriesId"/> is supplied by the caller because
        /// <c>/Shows/{id}/Seasons</c> is asked about one series and does not always repeat which.
        /// </summary>
        public JellyfinSeason? ToSeason(string seriesId)
        {
            if (string.IsNullOrWhiteSpace(Id)) return null;

            var owner = string.IsNullOrWhiteSpace(SeriesId) ? seriesId : SeriesId;
            if (string.IsNullOrWhiteSpace(owner)) return null;

            // A season with no name is ordinary — plenty of servers send an empty one — so it is
            // given the name its number implies rather than being dropped like a nameless film.
            var name = string.IsNullOrWhiteSpace(Name)
                ? (IndexNumber is int number ? $"Season {number.ToString(CultureInfo.InvariantCulture)}" : "Season")
                : Name.Trim();

            return new JellyfinSeason
            {
                ItemId = Id.Trim(),
                SeriesId = owner.Trim(),
                Name = name,
                Number = IndexNumber,
                ImageTag = Lookup(ImageTags, "Primary"),
                EpisodeCount = Positive(ChildCount)
            };
        }

        /// <summary>
        /// One episode. Nameless episodes are kept, unlike nameless films: a server that has not
        /// identified an episode still holds a file that plays, and dropping it would make the
        /// season list disagree with the season's own count.
        /// </summary>
        public JellyfinEpisode? ToEpisode(string seriesId)
        {
            if (string.IsNullOrWhiteSpace(Id)) return null;

            var owner = string.IsNullOrWhiteSpace(SeriesId) ? seriesId : SeriesId;
            if (string.IsNullOrWhiteSpace(owner)) return null;

            return new JellyfinEpisode
            {
                ItemId = Id.Trim(),
                SeriesId = owner.Trim(),
                SeasonId = (SeasonId ?? "").Trim(),
                Name = (Name ?? "").Trim(),
                SeasonNumber = ParentIndexNumber,
                Number = IndexNumber,
                Overview = (Overview ?? "").Trim(),
                RuntimeMinutes = TicksToMinutes(RunTimeTicks),
                CommunityRating = CommunityRating,
                ImageTag = Lookup(ImageTags, "Primary")
            };
        }

        /// <summary>
        /// A count worth printing, or null. Zero is dropped for the same reason a zero runtime is:
        /// a server that answered "0 seasons" about a series it is streaming is not telling the
        /// truth, it is failing to answer, and the card should say nothing rather than something
        /// false.
        /// </summary>
        internal static int? Positive(int? count) => count is > 0 ? count : null;
        /// Folds the server's tracks into the shape the details screen renders, dropping anything
        /// it has no badge for. Returns null when there is nothing worth showing, so a library
        /// synced before this was asked for is indistinguishable from one whose server reported no
        /// streams — in both cases the honest answer is that nobody measured this film.
        /// </summary>
        internal static MediaInfo? BuildMedia(
            IReadOnlyList<JellyfinMediaStreamDto>? streams,
            int? width,
            int? height,
            string? container)
        {
            var info = new MediaInfo
            {
                Width = width is > 0 ? width : null,
                Height = height is > 0 ? height : null,
                Container = string.IsNullOrWhiteSpace(container) ? null : container.Trim().ToLowerInvariant()
            };

            var list = streams ?? new List<JellyfinMediaStreamDto>();

            var video = list.FirstOrDefault(s => s.IsVideo);
            if (video is not null)
            {
                // The stream's own dimensions win over the item's. They are the same number in
                // almost every case, but the item's is a summary and the stream's is the file.
                info.Width = video.Width is > 0 ? video.Width : info.Width;
                info.Height = video.Height is > 0 ? video.Height : info.Height;
                info.VideoCodec = Clean(video.Codec);

                // VideoRangeType names the format; VideoRange only says whether there is one.
                // Older servers send just the latter, so both are read and the specific one wins.
                info.VideoRange = Clean(video.VideoRangeType) ?? Clean(video.VideoRange);
            }

            var audio = list.FirstOrDefault(s => s.IsAudio && s.IsDefault) ?? list.FirstOrDefault(s => s.IsAudio);
            if (audio is not null)
            {
                info.AudioCodec = Clean(audio.Codec);
                info.AudioChannels = audio.Channels is > 0 ? audio.Channels : null;
                info.HasAtmos = MentionsAtmos(audio.Profile) || MentionsAtmos(audio.DisplayTitle);
            }

            info.AudioLanguages = Languages(list.Where(s => s.IsAudio));
            info.SubtitleLanguages = Languages(list.Where(s => s.IsSubtitle));

            return info.HasAnything ? info : null;
        }

        /// <summary>
        /// Every language in a set of tracks, in the server's order, with blanks dropped. Not
        /// deduplicated here — <c>MediaFlags</c> does that by resolved code, which correctly folds
        /// <c>fre</c> and <c>fra</c> together where a comparison of the raw tags would not.
        /// </summary>
        private static List<string> Languages(IEnumerable<JellyfinMediaStreamDto> streams) =>
            streams
                .Select(s => s.Language?.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l!)
                .ToList();

        /// <summary>
        /// Atmos is not a codec and never appears in the codec field; it rides on TrueHD or E-AC-3
        /// and is named only in the profile or the display title.
        /// </summary>
        private static bool MentionsAtmos(string? text) =>
            !string.IsNullOrWhiteSpace(text) && text.Contains("atmos", StringComparison.OrdinalIgnoreCase);

        private static string? Clean(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
