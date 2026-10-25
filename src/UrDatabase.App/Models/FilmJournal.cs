using System;
using System.Collections.Generic;
using System.Globalization;
using UrDatabase.Services;

namespace UrDatabase.Models
{
    /// <summary>
    /// Which film a rating or a viewing is about, independent of where the film is stored.
    /// </summary>
    /// <remarks>
    /// Not a catalogue row id: a rating has to outlive the row (a rescan can delete it) and has to
    /// work for a film that only exists on the Jellyfin server and so has no row at all. See
    /// <see cref="FilmKey"/> for how the key is made.
    /// </remarks>
    public sealed class FilmIdentity
    {
        public FilmIdentity(int? tmdbId, string? title, int? year)
        {
            TmdbId = tmdbId is > 0 ? tmdbId : null;
            Title = (title ?? "").Trim();
            Year = year is > 0 ? year : null;
        }

        public int? TmdbId { get; }
        public string Title { get; }
        public int? Year { get; }

        /// <summary>The key the film's rows are written under. Null when the film cannot be named.</summary>
        public string? Key => FilmKey.For(TmdbId, Title, Year);

        /// <summary>
        /// The key the film would have had before it was identified. Rows written then are still
        /// found under it, and moved onto <see cref="Key"/> the next time anything is saved.
        /// </summary>
        public string? TitleKey => FilmKey.ForTitle(Title, Year);

        public bool CanBeJournalled => Key is not null;
    }

    /// <summary>The owner's rating and review of one film.</summary>
    public sealed class FilmRating
    {
        public string Key { get; init; } = "";
        public int? TmdbId { get; init; }
        public string Title { get; init; } = "";
        public int? Year { get; init; }

        /// <summary>One to ten, or null for a review with no stars.</summary>
        public int? Stars { get; init; }

        public string Review { get; init; } = "";
        public DateTime UpdatedAtUtc { get; init; }
    }

    /// <summary>One time the owner watched a film.</summary>
    public sealed class FilmViewing
    {
        public long Id { get; init; }
        public string Key { get; init; } = "";
        public DateOnly WatchedOn { get; init; }
        public IReadOnlyList<string> Companions { get; init; } = Array.Empty<string>();
        public string Note { get; init; } = "";

        /// <summary>"3 Oct 2026", in the invariant culture so it reads the same on every machine.</summary>
        public string WatchedOnText => WatchedOn.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        /// <summary>"with Ana and Luis", or empty when it was watched alone.</summary>
        public string CompanionsText => Services.Companions.Describe(Companions);

        public bool HasCompanions => Companions.Count > 0;
        public bool HasNote => Note.Length > 0;
    }

    /// <summary>One name offered under the "who with" line, and whether the line already has it.</summary>
    public sealed record CompanionChip(string Name, bool IsIncluded);

    /// <summary>Everything the film page shows about the owner's history with one film.</summary>
    public sealed class FilmJournalEntry
    {
        public static readonly FilmJournalEntry Empty = new();

        public FilmRating? Rating { get; init; }
        public IReadOnlyList<FilmViewing> Viewings { get; init; } = Array.Empty<FilmViewing>();

        /// <summary>Names from earlier viewings of any film, most used first, for suggestions.</summary>
        public IReadOnlyList<string> KnownCompanions { get; init; } = Array.Empty<string>();
    }
}
