using System;
using System.Collections.Generic;

namespace UrDatabase.Models
{
    /// <summary>
    /// Something the owner has noticed is wrong with their copy of a film.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="MediaFlag"/> on purpose. A media flag is what a file says about
    /// itself — measured by Jellyfin or ffprobe, or claimed by a filename — and is recomputed
    /// every time the film is opened. A problem is a person's judgement after watching it, which
    /// nothing can measure and nothing may overwrite: a 1080p file can still have the aspect ratio
    /// squashed, and a 5.1 track can still be out of sync.
    /// </remarks>
    public enum FilmProblemKind
    {
        Audio,
        LowResolution,
        AspectRatio,
        Subtitles,
        Picture,
        Other
    }

    /// <summary>
    /// What has been reported about one film: which problems, and anything the owner wrote down.
    /// </summary>
    public sealed class FilmProblemReport
    {
        public FilmProblemReport(string filmKey, IReadOnlyList<FilmProblemKind> kinds, string? note, DateTime? flaggedAt = null)
        {
            FilmKey = filmKey ?? "";
            Kinds = kinds ?? Array.Empty<FilmProblemKind>();
            Note = (note ?? "").Trim();
            FlaggedAt = flaggedAt;
        }

        /// <summary>The film's identity, in the same form as <see cref="UiMovie.Key"/>.</summary>
        public string FilmKey { get; }

        /// <summary>The problems ticked, in the order they are listed in <see cref="FilmProblemKind"/>.</summary>
        public IReadOnlyList<FilmProblemKind> Kinds { get; }

        /// <summary>Free text, trimmed. Empty when nothing was written.</summary>
        public string Note { get; }

        /// <summary>When the report was last saved. Null on one that has not been.</summary>
        public DateTime? FlaggedAt { get; }

        /// <summary>
        /// True when there is anything to report. A note with no box ticked still counts: "the
        /// last ten minutes are missing" is a problem that fits none of the boxes.
        /// </summary>
        public bool IsEmpty => Kinds.Count == 0 && Note.Length == 0;
    }
}
