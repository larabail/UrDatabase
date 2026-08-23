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
        /// True when a server holds this film as well as this machine. Set for a film that is in
        /// both places, which <see cref="IsRemote"/> is deliberately not: that one is streamed and
        /// this one is opened from disk, and only the facts row mentions the server copy at all.
        /// </summary>
        public bool IsOnServer { get; set; }

        /// <summary>
        /// The Jellyfin item id, for a film the server holds. What a download asks for, and set
        /// for a film in both places too — a local copy does not stop the server copy having an id.
        /// </summary>
        public string? RemoteId { get; set; }

        /// <summary>Where a downloaded copy is written. From configuration, not from the film.</summary>
        public string? DownloadFolder { get; set; }

        /// <summary>
        /// The catalogue a finished download is written into, so the copy is playable and
        /// searchable without waiting for a scan.
        /// </summary>
        public string? DatabasePath { get; set; }

        /// <summary>
        /// A copy of this film already on this disk, found when the details were opened or written
        /// by a download since. Set means the film plays with the server switched off, which is
        /// the entire point of downloading it.
        /// </summary>
        public string? DownloadedPath { get; set; }

        /// <summary>
        /// True when there is something to download and nothing downloaded yet. A film already on
        /// this disk needs no copy, and a server film whose id never reached the cache cannot be
        /// asked for.
        /// </summary>
        public bool CanDownload =>
            IsRemote &&
            !string.IsNullOrWhiteSpace(RemoteId) &&
            string.IsNullOrWhiteSpace(DownloadedPath);

        /// <summary>
        /// True when there is something to send to the server: a film on this disk, with a file
        /// behind it, that the server does not already have.
        ///
        /// The mirror image of <see cref="CanDownload"/>, and deliberately silent about whether an
        /// SFTP account is configured — that is a property of the install rather than of the film,
        /// and the screen adds it.
        /// </summary>
        public bool CanUpload => !IsRemote && !IsOnServer && HasFile;

        /// <summary>
        /// The direct play URL, resolved when the details were opened. Null when the server could
        /// not be reached, which is what lets Play explain itself instead of failing obscurely.
        ///
        /// It carries an access token, so it is never shown, logged or put in a message.
        /// </summary>
        public string? StreamUrl { get; set; }

        /// <summary>
        /// Where the server says this film was left, in ticks. Zero for one that is not
        /// part-watched, which is nearly every film.
        /// </summary>
        /// <remarks>
        /// What <b>Continue watching</b> seeks to. Taken from the card the library already built
        /// rather than fetched again, so the details screen and the shelf behind it can never
        /// disagree about where you were.
        /// </remarks>
        public long ResumePositionTicks { get; set; }

        /// <summary>How much is left, exactly as the card says it: <c>"42 MIN LEFT"</c>.</summary>
        public string? ResumeNote { get; set; }

        /// <summary>
        /// True when there is a position worth returning to. A second is the floor, for the same
        /// reason it is in the row: a player that was opened and shut reports a position before
        /// anybody watched anything.
        /// </summary>
        public bool HasResumePosition =>
            ResumePositionTicks >= Services.PlaybackPosition.MinimumMeaningfulTicks;

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

        /// <summary>
        /// What is known about the copy itself — picture size, codecs, audio and subtitle
        /// languages. Measured by the server for a Jellyfin film and read off the filename for a
        /// scanned one, which is a claim rather than a measurement; <c>MediaFlags</c> knows the
        /// difference and says so in the tooltip. Null for a film nothing has described.
        /// </summary>
        public MediaInfo? Media { get; set; }

        /// <summary>
        /// What the Academy made of the film. Empty for the great majority of films, for an
        /// install with no UrActor key, and whenever the archive could not be reached — the screen
        /// shows nothing in all three cases rather than distinguishing between them, because
        /// "no awards" is the truthful reading of all three from the user's side.
        /// </summary>
        public OscarHonours Awards { get; set; } = OscarHonours.None;
    }
}
