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
        /// The two badge texts, as instance properties because a card template can only bind to
        /// one of those. They are the constants above and nothing else, so the wall and the
        /// source row cannot drift apart by somebody retyping a word in the view.
        /// </summary>
        public string ServerBadge => ServerTag;

        /// <inheritdoc cref="ServerBadge"/>
        public string OfflineBadge => OfflineTag;

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
        /// True when the film is <em>only</em> on the server, so playing it needs the server
        /// reachable. A film held in both places is not remote: it plays from this disk.
        /// </summary>
        public bool IsRemote => Source == MovieSource.Jellyfin;

        /// <summary>True when the catalogue on this machine has the film.</summary>
        public bool IsOnThisComputer => Source == MovieSource.Local;

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
        /// local copy that everything else about it — the file, the TMDB match — hangs off.
        /// </summary>
        public string Key => IsRemote
            ? $"jellyfin:{RemoteId}"
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
