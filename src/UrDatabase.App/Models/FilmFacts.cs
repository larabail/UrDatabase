using System;
using System.Collections.Generic;

namespace UrDatabase.Models
{
    /// <summary>
    /// What the library's filters ask about a film beyond its genres: how long it runs, how well
    /// it is rated, and who is in it and who directed it.
    /// </summary>
    /// <remarks>
    /// Cast and crew are the same lines the details screen prints — <c>"Name (Character)"</c> and
    /// <c>"Director: Name"</c> — so a local film and a server film describe their people in one
    /// shape, and <see cref="Services.CreditLine"/> is the one place that takes them apart.
    ///
    /// Every part may be absent. Null means "not known", which a filter treats as "cannot claim to
    /// match", never as zero minutes or a rating of nothing.
    /// </remarks>
    public sealed record FilmFacts(
        int? RuntimeMinutes,
        double? Rating,
        IReadOnlyList<string> Cast,
        IReadOnlyList<string> Crew)
    {
        public static FilmFacts None { get; } = new(null, null, Array.Empty<string>(), Array.Empty<string>());
    }
}
