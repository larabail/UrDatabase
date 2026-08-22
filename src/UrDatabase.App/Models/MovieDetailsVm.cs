using System.Collections.Generic;

namespace UrDatabase.Models
{
    public class MovieDetailsVm
    {
        public long LocalId { get; set; }            // your DB movie id
        public string Title { get; set; } = "";
        public int? Year { get; set; }
        public string Overview { get; set; } = "";
        public string Genres { get; set; } = "";
        public int? Runtime { get; set; }            // minutes
        public double? ImdbRating { get; set; }      // from OMDb, keyed by TMDB's imdb_id
        public string? ImdbId { get; set; }          // tt....
        public string? PosterPath { get; set; }      // local or URL (you already have this)
        public string? BackdropUrl { get; set; }     // URL for big backdrop

        /// <summary>
        /// Which TMDB film this is, when the catalogue has been told. Null for a film nothing has
        /// identified yet, and for anything from a Jellyfin server, which describes its own films.
        ///
        /// Stored so a corrected match survives being reopened. The identification used to be
        /// re-derived from the title every time, which is what made a correction impossible to
        /// keep.
        /// </summary>
        public int? TmdbId { get; set; }

        /// <summary>
        /// The file Play would open, when there is one. Read
        /// <see cref="FileMatch"/> before acting on it: this is not always a file the catalogue
        /// vouches for.
        /// </summary>
        public string? FilePath { get; set; }

        /// <summary>
        /// On what evidence <see cref="FilePath"/> was chosen. A
        /// <see cref="PlayTargetKind.Suggested"/> path was guessed from a filename and has to be
        /// confirmed before it is opened — the app used to make no distinction, and played the
        /// guess.
        /// </summary>
        public PlayTargetKind FileMatch { get; set; } = PlayTargetKind.None;

        public bool HasFile => !string.IsNullOrWhiteSpace(FilePath);

        public List<string> TopCast { get; set; } = new();     // “Actor (Role)”
        public List<string> KeyCrew { get; set; } = new();     // “Director: Name”

        /// <summary>True when the film lives on a Jellyfin server and is streamed, not opened.</summary>
        public bool IsRemote { get; set; }

        /// <summary>
        /// The direct play URL, resolved when the details were opened. Null when the server could
        /// not be reached, which is what lets Play explain itself instead of failing obscurely.
        ///
        /// It carries an access token, so it is never shown, logged or put in a message.
        /// </summary>
        public string? StreamUrl { get; set; }

        /// <summary>
        /// Jellyfin's own community rating, shown under Jellyfin's name and nobody else's. It is a
        /// different measurement, of a different population, from the IMDb rating above, and this
        /// repository has already shipped one bug from labelling one service's number as another's.
        /// </summary>
        public double? CommunityRating { get; set; }

        /// <summary>
        /// Whether a TMDB key was available when this film was opened.
        /// </summary>
        /// <remarks>
        /// Carried so the screen can tell the truth about an empty plot or an empty cast. Both
        /// keys are optional and an install with neither is entirely supported, so "none found"
        /// is the wrong sentence for by far the commonest case: nothing was ever asked.
        /// </remarks>
        public bool TmdbConfigured { get; set; }
    }
}
