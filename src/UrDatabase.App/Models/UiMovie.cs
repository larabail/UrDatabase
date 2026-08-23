using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

namespace UrDatabase.Models
{
    public class UiMovie : INotifyPropertyChanged
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public string? Genres { get; set; }

        /// <summary>
        /// Which TMDB film this is, when either source has said so: <c>movies.tmdb_id</c> for a
        /// local film, and the server's own provider id for one of its.
        ///
        /// It is what lets the same film from both places be recognised as one. A title cannot do
        /// that job — a library names a file <em>El Drama</em> while the server calls the same film
        /// <em>The Drama</em>, and the two normalise to different keys, so the wall showed it twice.
        /// Null whenever nothing has identified the film, which is why matching on the title stays
        /// as the fallback rather than being replaced.
        /// </summary>
        public int? TmdbId { get; set; }

        /// <summary>
        /// The word for a film that plays with the house network down, on the badge and on the
        /// control in the source row. Those two have to say the same thing: the row is what
        /// explains the badge, and a filter called "On this computer" beside a badge called
        /// something else leaves the reader to guess they are the same fact.
        /// </summary>
        public const string OfflineTag = "Offline";

        /// <summary>The word for a film the server holds.</summary>
        public const string ServerTag = "Server";

        /// <summary>
        /// The word on a television card. Every series carries it and no film does, which is what
        /// makes it safe for series to share the genre shelves with films: the two populations are
        /// mixed, but never silently.
        /// </summary>
        public const string SeriesTag = "Series";

        /// <summary>
        /// The two badge texts, as instance properties because a card template can only bind to
        /// one of those. They are the constants above and nothing else, so the wall and the
        /// source row cannot drift apart by somebody retyping a word in the view.
        /// </summary>
        public string ServerBadge => ServerTag;

        /// <inheritdoc cref="ServerBadge"/>
        public string OfflineBadge => OfflineTag;

        /// <inheritdoc cref="ServerBadge"/>
        public string SeriesBadge => SeriesTag;

        /// <summary>
        /// Whether this is a film, a television series, or one episode of one. Defaults to
        /// <see cref="MediaKind.Film"/> so every row read out of the <c>movies</c> table is right
        /// without the query saying so — nothing on this machine is catalogued as television.
        /// </summary>
        public MediaKind Kind { get; set; } = MediaKind.Film;

        /// <summary>
        /// True when this is a television series rather than a film. A question about what the
        /// card is, not about where it is — <see cref="IsRemote"/> and <see cref="IsOnServer"/>
        /// answer that, and every series happens to be on a server, which is a fact about how
        /// television reaches this app rather than part of what a series means.
        /// </summary>
        public bool IsSeries => Kind == MediaKind.Series;

        /// <summary>
        /// True when this card is one episode. Only ever true in the Continue watching row.
        /// </summary>
        public bool IsEpisode => Kind == MediaKind.Episode;

        public bool IsFilm => Kind == MediaKind.Film;

        /// <summary>
        /// Whether the card says the server holds this.
        /// </summary>
        /// <remarks>
        /// Never on a series, although every series genuinely is on the server. The badge answers
        /// "will this play away from home", and for television the answer is on the card already —
        /// nothing local is ever a series, so the mark would appear on every single one of them
        /// and say nothing that the series badge beside it did not.
        ///
        /// An episode does carry it. It only ever appears in the Continue watching row, beside
        /// films of which some play offline and some do not, and there the badge is the one thing
        /// that separates them — while the series badge would be a lie, because an episode is not
        /// a programme.
        /// </remarks>
        public bool ShowServerBadge => IsOnServer && !IsSeries;

        /// <summary>How many seasons a series has, when the server counted them. Null on a film.</summary>
        public int? SeasonCount { get; set; }

        /// <summary>How many episodes a series has, on the same terms as <see cref="SeasonCount"/>.</summary>
        public int? EpisodeCount { get; set; }

        /// <summary>
        /// The programme an episode card belongs to, so clicking it can open the show. Null on
        /// anything that is not an episode.
        /// </summary>
        /// <remarks>
        /// Kept apart from <see cref="RemoteId"/>, which on an episode card is the episode's own
        /// id — the one that streams, and the one a playback report is about. Folding the two
        /// would mean either playing a series or reporting progress against a programme.
        /// </remarks>
        public string? SeriesId { get; set; }

        /// <summary>Which season an episode is in, when the server numbered it.</summary>
        public int? SeasonNumber { get; set; }

        /// <summary>Its number within that season, on the same terms.</summary>
        public int? EpisodeNumber { get; set; }

        /// <summary>
        /// The episode's own name. Secondary on the card, because on its own it identifies
        /// nothing: a real one from this library is "In throes of increasing wonder … ", which
        /// names no programme, no season and no place in it.
        /// </summary>
        public string? EpisodeTitle { get; set; }

        /// <summary>
        /// Where an episode sits in its programme: <c>"S1E1"</c>, or <c>"E1"</c> when the server
        /// numbered the episode and not the season, or empty when it numbered neither.
        /// </summary>
        /// <remarks>
        /// The same shape the series screen uses, deliberately, so the two places an episode is
        /// named agree. Not zero-padded here: <c>S01E01</c> is right in a list of twenty-four rows
        /// where the numbers have to line up, and merely loud on a single card.
        /// </remarks>
        public string EpisodeLabel
        {
            get
            {
                if (!IsEpisode) return "";

                var season = SeasonNumber is int s ? $"S{s.ToString(CultureInfo.InvariantCulture)}" : "";
                var episode = EpisodeNumber is int e ? $"E{e.ToString(CultureInfo.InvariantCulture)}" : "";

                return season + episode;
            }
        }

        /// <summary>
        /// The line under the title on a card: the year, for a series how many seasons are behind
        /// it, and for an episode where in its programme it is.
        /// </summary>
        /// <remarks>
        /// One property rather than the view choosing between three, because the choice is a rule
        /// about what a card means and rules in a view cannot be tested. A series showing nothing
        /// but a year is the failure mode this exists to prevent: it would read as a film with an
        /// odd date, which is precisely the objection to putting the two on one shelf. An episode
        /// showing nothing but its own name is the same failure again and worse, because the name
        /// is frequently meaningless without the programme.
        ///
        /// An episode's own name is deliberately not here beside the number. There is room on a
        /// 152px card for "S1E1" and how much is left, and nothing else: with the name in as well
        /// the line rendered as "S1E1 · In …", which is a truncation that costs the space and
        /// says nothing. It is on <see cref="CardTooltip"/> in full instead, and on the series
        /// screen the card opens.
        ///
        /// Empty rather than null for a film with no year, so the view has one thing to test.
        /// </remarks>
        public string MetaLine
        {
            get
            {
                var year = Year is int value ? value.ToString(CultureInfo.InvariantCulture) : "";

                if (IsEpisode)
                {
                    // The name only when the server numbered nothing at all, which is the one case
                    // where it is the only thing that distinguishes this card from its siblings.
                    var label = EpisodeLabel;
                    return label.Length > 0 ? label : (EpisodeTitle ?? "").Trim();
                }

                if (!IsSeries) return year;

                var seasons = SeasonCount is int count && count > 0
                    ? count == 1 ? "1 season" : $"{count.ToString(CultureInfo.InvariantCulture)} seasons"
                    : "";

                if (year.Length == 0) return seasons;
                if (seasons.Length == 0) return year;

                return $"{year} · {seasons}";
            }
        }

        /// <summary>
        /// Where the row came from: the local catalogue, or a server. Defaults to
        /// <see cref="MovieSource.Local"/> so every row Dapper materialises from the <c>movies</c>
        /// table is right without the query having to say so.
        ///
        /// Not the whole answer to "where is this film". A film in both places is one card built
        /// from the local row, with <see cref="RemoteId"/> filled in — ask
        /// <see cref="IsOnThisComputer"/> and <see cref="IsOnServer"/> rather than this.
        /// </summary>
        public MovieSource Source { get; set; } = MovieSource.Local;

        /// <summary>
        /// The Jellyfin item id, when the server holds this film. Set on a server film, and on a
        /// local one the server turns out to have a copy of. Null when no server has it.
        /// </summary>
        public string? RemoteId { get; set; }

        /// <summary>
        /// True when the catalogue names at least one file for this film that the last completed
        /// scan still found. Read together with <see cref="HasFileMissing"/>: a film the catalogue
        /// has no file for at all has neither set, and that is a different thing from one whose
        /// copy went away.
        /// </summary>
        public bool HasFileHere { get; set; }

        /// <summary>
        /// True when the catalogue names at least one file for this film that a completed scan
        /// looked for and could not find. Written by the scan into <c>files.missing_since</c>;
        /// a scan that was cancelled, and a folder that was not there to be walked, both leave
        /// this false, because neither may conclude anything from not having found something.
        /// </summary>
        public bool HasFileMissing { get; set; }

        /// <summary>
        /// True when every file this machine had for the film is one a scan could not find.
        ///
        /// False for a film with two prints where only one went away — one surviving copy still
        /// plays — and false for a film the catalogue names no file for at all, which is an
        /// ordinary "nothing linked yet" rather than a film that has gone.
        /// </summary>
        public bool FileIsGone => HasFileMissing && !HasFileHere;

        /// <summary>
        /// True when the film has to be streamed, because nothing on this disk will play it.
        ///
        /// A film from a server, and also a catalogued film whose own copy is gone and which the
        /// server still holds — that one degrades to being a server film rather than disappearing,
        /// so that the row carrying its corrected TMDB match, its poster and its genres survives
        /// the file being deleted.
        /// </summary>
        public bool IsRemote => Source == MovieSource.Jellyfin || (FileIsGone && IsOnServer);

        /// <summary>
        /// True when a film on this machine will actually open: the catalogue has the film
        /// <em>and</em> the last scan could still find a file for it.
        ///
        /// It used to be the source alone, which made it a claim about where a row came from
        /// rather than about where the film is. A film somebody had deleted kept the
        /// <see cref="OfflineTag"/> badge and answered the "on this computer" filter until the
        /// moment Play failed on it.
        /// </summary>
        public bool IsOnThisComputer => Source == MovieSource.Local && !FileIsGone;

        /// <summary>True when a server has the film, whether or not this machine does too.</summary>
        public bool IsOnServer => !string.IsNullOrWhiteSpace(RemoteId);

        /// <summary>
        /// True when the film is in both places. The card says so with both badges, because the
        /// two facts are different promises: the server copy is the one with metadata, and the
        /// local copy is the one that plays on a train.
        /// </summary>
        public bool IsInBothPlaces => IsOnThisComputer && IsOnServer;

        /// <summary>
        /// Identity across both sources. Local rows have an autoincrement id and remote ones a
        /// GUID from Jellyfin, so neither alone can deduplicate a mixed list — every remote film
        /// carries id 0 and would collapse into a single entry if grouped by that.
        ///
        /// A film in both places keeps its local key. It is one film, counted once, and it is the
        /// local copy that everything else about it — the file, the TMDB match — hangs off. That
        /// holds even once the file is gone: keyed on <see cref="Source"/> rather than on
        /// <see cref="IsRemote"/>, so a film degrading to a server film cannot change identity
        /// underneath a list that has already deduplicated on it.
        ///
        /// Television is keyed separately from film. Jellyfin does not reuse an id between the
        /// two, so this buys nothing today; it is here so that the day something does — a second
        /// server, an import, a fixture — a series and a film cannot silently become one card.
        /// An episode is keyed separately again, for the same reason and one more: an episode card
        /// and its programme's card can be on screen at once, with the row above the shelves.
        /// </summary>
        public string Key => Source == MovieSource.Jellyfin
            ? Kind switch
            {
                MediaKind.Series => $"jellyfin:series:{RemoteId}",
                MediaKind.Episode => $"jellyfin:episode:{RemoteId}",
                _ => $"jellyfin:{RemoteId}"
            }
            : $"local:{Id.ToString(CultureInfo.InvariantCulture)}";

        private string? _posterPath;

        /// <summary>
        /// The artwork the catalogue on this machine holds: a cached file, or a TMDB URL. Null
        /// until something enriches the film, which is most of a freshly scanned library.
        /// </summary>
        public string? PosterPath
        {
            get => _posterPath;
            set
            {
                if (_posterPath == value) return;
                _posterPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayPosterPath));
            }
        }

        private string? _remotePosterPath;

        /// <summary>
        /// The artwork the server offers for this film. Kept apart from <see cref="PosterPath"/>
        /// rather than written into it, because the local column is what survives the server going
        /// away: filling it with a server URL would both make the card lie once the server is gone
        /// and stop the poster loader ever fetching a real one, since it skips films that already
        /// look illustrated.
        /// </summary>
        public string? RemotePosterPath
        {
            get => _remotePosterPath;
            set
            {
                if (_remotePosterPath == value) return;
                _remotePosterPath = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DisplayPosterPath));
            }
        }

        /// <summary>
        /// What the card actually shows: this machine's artwork, or the server's until this
        /// machine has any. Without the fallback, folding a server film into a scanned one would
        /// replace two cards — one of them illustrated — with a single blank plate, which is
        /// exactly what an install with no TMDB key would see for every film it shares.
        /// </summary>
        public string? DisplayPosterPath =>
            string.IsNullOrWhiteSpace(PosterPath) ? RemotePosterPath : PosterPath;

        private double? _resumeFraction;

        /// <summary>
        /// How far through this film the server says the viewer is, between 0 and 1, or null for a
        /// film that is not part-watched — which is nearly every card.
        /// </summary>
        /// <remarks>
        /// Stamped onto the card by <see cref="Services.ResumeRow"/> rather than read from the
        /// catalogue, because it is the one fact about a film that belongs to a person and a
        /// moment rather than to the film. It is left set wherever the card appears, not only in
        /// the Continue watching row: it is the same film and the same fact, and a mark that
        /// vanished as soon as you looked at the Drama shelf would be answering "where was I" only
        /// in the one place you already knew.
        /// </remarks>
        public double? ResumeFraction
        {
            get => _resumeFraction;
            set
            {
                var clamped = value is double f && !double.IsNaN(f) ? Math.Clamp(f, 0d, 1d) : (double?)null;
                if (_resumeFraction == clamped) return;

                _resumeFraction = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasResume));
                OnPropertyChanged(nameof(ResumePercent));
            }
        }

        private string? _resumeNote;

        /// <summary>How much of the film is left, as printed on the card: <c>"42 MIN LEFT"</c>.</summary>
        public string? ResumeNote
        {
            get => _resumeNote;
            set
            {
                if (_resumeNote == value) return;
                _resumeNote = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CardTooltip));
            }
        }

        /// <summary>True when this film is part-watched, and so carries a progress mark.</summary>
        public bool HasResume => _resumeFraction is > 0;

        /// <summary>
        /// The same fraction on the 0–100 scale a <c>ProgressBar</c> wants, so the view binds a
        /// number rather than reaching for a converter.
        /// </summary>
        public double ResumePercent => (_resumeFraction ?? 0) * 100d;

        /// <summary>
        /// What hovering the card says. The title, where an episode sits in its programme and what
        /// that episode is called, and how far through it is when that is known — a 3px rule along
        /// the bottom of a poster is legible at a glance and says nothing to somebody meeting it
        /// for the first time, and this is where it explains itself.
        /// </summary>
        /// <remarks>
        /// The only place an episode's own name is readable in full. The card has room for the
        /// number and how much is left and nothing else, which is the right trade at 152 pixels
        /// wide, and this is where the rest of it lives.
        /// </remarks>
        public string CardTooltip
        {
            get
            {
                var parts = new List<string> { Title };

                if (IsEpisode)
                {
                    var label = EpisodeLabel;
                    var name = (EpisodeTitle ?? "").Trim();

                    var episode = label.Length > 0 && name.Length > 0 ? $"{label} · {name}"
                                : label.Length > 0 ? label
                                : name;

                    if (episode.Length > 0) parts.Add(episode);
                }

                if (!string.IsNullOrWhiteSpace(_resumeNote)) parts.Add(_resumeNote!);

                return string.Join(" — ", parts);
            }
        }

        /// <summary>
        /// Records that the server holds this film too, folding its copy into this card.
        /// </summary>
        /// <remarks>
        /// Only what the library view itself needs is taken across. Genres, because a scanned film
        /// has none and would drop out of every shelf it belongs on the moment its server twin
        /// stopped being a card of its own; and the poster, as a fallback. Nothing is written to
        /// the catalogue: the server is asked afresh on every sync, and a copy of its metadata in
        /// the <c>movies</c> table would be one more thing to keep true.
        /// </remarks>
        public void AdoptServerCopy(UiMovie server)
        {
            if (server is null) return;

            RemoteId = server.RemoteId;
            RemotePosterPath = server.DisplayPosterPath;

            if (string.IsNullOrWhiteSpace(Genres)) Genres = server.Genres;

            // A film the server has identified and the catalogue has not. Taking the id here is
            // what lets a fold that had to be made on the title hold on identity next time, and it
            // only ever fills a blank — a local answer, including a corrected one, is never
            // overwritten by the server's.
            TmdbId ??= server.TmdbId;
        }

        public IEnumerable<string> GenresList =>
            (Genres ?? "")
            .Replace('|', ',')
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(g => g.Trim())
            .Where(g => g.Length > 0);

        public bool HasGenre(string g) =>
            !string.IsNullOrWhiteSpace(g) &&
            GenresList.Any(x => string.Equals(x, g, StringComparison.OrdinalIgnoreCase));

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
