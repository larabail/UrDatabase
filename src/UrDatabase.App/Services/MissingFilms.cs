using System;
using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// What is left of a film once a completed scan could no longer find the files the catalogue
    /// names for it.
    /// </summary>
    public enum FilmStanding
    {
        /// <summary>
        /// Nothing has changed. Either a file the scan still finds belongs to this film, or the
        /// catalogue names no file for it at all and so has said nothing about one going away.
        /// </summary>
        Kept,

        /// <summary>
        /// Every file this machine had for the film is gone, and a server holds it. The row stays
        /// exactly where it is and the card stops claiming to play from disk.
        /// </summary>
        ServerOnly,

        /// <summary>
        /// Every file this machine had for the film is gone, and nowhere else has it. There is
        /// nothing left to show.
        /// </summary>
        Retired
    }

    /// <summary>
    /// Reads what a scan wrote down about files it could not find, and decides what the library
    /// should do about the films they belonged to.
    ///
    /// <c>ScanService</c> has stamped <c>files.missing_since</c> since the scan learned to notice
    /// a deletion, and until now nothing read it. So a film whose only copy had been deleted still
    /// carried the <c>Offline</c> badge, still answered the "on this computer" filter, and still
    /// offered to play — and only admitted otherwise when somebody pressed Play and the operating
    /// system refused the path.
    ///
    /// Three outcomes from two facts, which is exactly the shape of rule that deserves assertions
    /// rather than a <c>WHERE</c> clause nobody can test, so it lives here rather than in the
    /// query that supplies the facts or in the window that renders the answer.
    /// </summary>
    /// <remarks>
    /// A film with no file rows at all is deliberately <see cref="FilmStanding.Kept"/>. Only a
    /// mark a scan actually wrote is evidence that something went away; a row the catalogue has
    /// never had a file for — one restored from an older library, or one whose file row was
    /// orphaned — has had nothing said about it, and reading silence as a deletion would empty
    /// somebody's library on the strength of a fact nobody recorded.
    /// </remarks>
    public static class MissingFilms
    {
        /// <summary>
        /// The rule itself.
        /// </summary>
        /// <param name="hasFileHere">
        /// The catalogue names at least one file for this film that the last completed scan still
        /// found.
        /// </param>
        /// <param name="hasFileMissing">
        /// The catalogue names at least one file for this film that a completed scan looked for
        /// and could not find.
        /// </param>
        /// <param name="onServer">A Jellyfin server holds the film.</param>
        public static FilmStanding Decide(bool hasFileHere, bool hasFileMissing, bool onServer)
        {
            // A film with two prints keeps both facts, and one surviving copy is enough: the film
            // still plays from this disk, so nothing about it has changed.
            if (hasFileHere) return FilmStanding.Kept;

            if (!hasFileMissing) return FilmStanding.Kept;

            return onServer ? FilmStanding.ServerOnly : FilmStanding.Retired;
        }

        /// <summary>
        /// The same rule for one card, after the two halves of the library have been folded
        /// together — which is the only point at which "does anywhere else have it" has an answer.
        /// </summary>
        /// <remarks>
        /// A card that came from a server is never anything but <see cref="FilmStanding.Kept"/>.
        /// The server is the one describing it, this machine has recorded no files for it, and a
        /// local scan has no standing to conclude anything about a film it has never seen.
        /// </remarks>
        public static FilmStanding Decide(UiMovie? movie)
        {
            if (movie is null) return FilmStanding.Kept;
            if (movie.Source != MovieSource.Local) return FilmStanding.Kept;

            return Decide(movie.HasFileHere, movie.HasFileMissing, movie.IsOnServer);
        }

        /// <summary>
        /// The library with the retired films taken out of it.
        /// </summary>
        /// <remarks>
        /// Nothing is deleted from the database, here or anywhere else. The catalogue row is what
        /// carries a corrected TMDB match and the name the scanner gave a film, and it is the
        /// reason a file that comes back is picked up by the next scan as the film it always was
        /// rather than as a new one — which is the whole argument for not needing a second scan
        /// to confirm a deletion. Deleting would also have to be decided against a cached server
        /// library that may never have been synced on this machine, turning a wrong badge into
        /// lost data.
        ///
        /// A <see cref="FilmStanding.ServerOnly"/> film is not filtered out and needs no work
        /// doing to it: dropping the local claim is what <see cref="UiMovie.IsOnThisComputer"/>
        /// already does from the same two facts, and the card keeps the poster, the genres and the
        /// TMDB id it arrived with.
        /// </remarks>
        public static IReadOnlyList<UiMovie> Retire(IEnumerable<UiMovie>? movies)
        {
            if (movies is null) return Array.Empty<UiMovie>();

            return movies
                .Where(m => m is not null && Decide(m) != FilmStanding.Retired)
                .ToList();
        }
    }
}
