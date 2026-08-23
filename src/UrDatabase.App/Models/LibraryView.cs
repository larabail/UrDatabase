using System;
using System.Collections.Generic;

namespace UrDatabase.Models
{
    /// <summary>
    /// Everything one library read produced: the films, and the sentence describing them.
    ///
    /// A single value rather than several fields written one after another, because the window
    /// applies the result of a search that may already have been superseded by the next keystroke.
    /// One object either reaches the screen whole or does not reach it at all; separate
    /// assignments could interleave two searches and leave the list from one beside the status
    /// line of the other.
    /// </summary>
    public sealed class LibraryView
    {
        public static LibraryView Empty { get; } = new LibraryView(
            query: null,
            local: Array.Empty<UiMovie>(),
            remote: Array.Empty<UiMovie>(),
            all: Array.Empty<UiMovie>(),
            status: "");

        public LibraryView(
            string? query,
            IReadOnlyList<UiMovie> local,
            IReadOnlyList<UiMovie> remote,
            IReadOnlyList<UiMovie> all,
            string status)
        {
            Query = query;
            Local = local ?? Array.Empty<UiMovie>();
            Remote = remote ?? Array.Empty<UiMovie>();
            All = all ?? Array.Empty<UiMovie>();
            Status = status ?? "";
        }

        /// <summary>What was typed, exactly as typed. Null when nothing was.</summary>
        public string? Query { get; }

        /// <summary>
        /// Whether this is a search, and so whether the flat result list is what the user should
        /// be looking at. Decided by the raw text, not by whether it produced a usable FTS
        /// expression: typing "???" searches for nothing findable, but the person doing it is
        /// still searching and must not be dropped back into the grouped view mid-word.
        /// </summary>
        public bool IsSearch => !string.IsNullOrWhiteSpace(Query);

        /// <summary>Films from the local catalogue.</summary>
        public IReadOnlyList<UiMovie> Local { get; }

        /// <summary>Films from a Jellyfin server, already narrowed to the query.</summary>
        public IReadOnlyList<UiMovie> Remote { get; }

        /// <summary>Both halves in one ordering, deduplicated by identity. What the view binds to.</summary>
        public IReadOnlyList<UiMovie> All { get; }

        /// <summary>The line under the library, including the wording for a read that failed.</summary>
        public string Status { get; }
    }
}
