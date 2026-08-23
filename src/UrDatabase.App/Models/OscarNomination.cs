using System.Collections.Generic;
using System.Linq;

namespace UrDatabase.Models
{
    /// <summary>
    /// One Academy Award nomination naming a film, as the archive records it.
    /// </summary>
    public sealed class OscarNomination
    {
        /// <summary>
        /// The year the ceremony was held, not the year the film came out. The 1976 ceremony
        /// honoured 1975's films, and showing the film's own year here would put every award a
        /// year earlier than the Academy did.
        /// </summary>
        public int Ceremony { get; set; }

        /// <summary>
        /// The category as the Academy named it that year. Deliberately not shortened: the leading
        /// acting award has been called both "Best Actor in a Leading Role" and "Best Performance
        /// by an Actor in a Leading Role", and rewriting one into the other would misquote a
        /// ceremony.
        /// </summary>
        public string Category { get; set; } = "";

        /// <summary>
        /// Who or what was nominated. The film itself for Best Picture; a person for the acting
        /// and craft awards.
        /// </summary>
        public string Nominee { get; set; } = "";

        /// <summary>
        /// The context: the film, for a person's nomination, or the producers, for Best Picture.
        /// Empty when the archive recorded none.
        /// </summary>
        public string Detail { get; set; } = "";

        public bool Won { get; set; }
    }

    /// <summary>
    /// What the Academy made of one film: every nomination it received, and how many it converted.
    /// </summary>
    public sealed class OscarHonours
    {
        public IReadOnlyList<OscarNomination> Nominations { get; init; } = new List<OscarNomination>();

        public int Wins => Nominations.Count(n => n.Won);

        public int Total => Nominations.Count;

        public bool Any => Nominations.Count > 0;

        /// <summary>
        /// The ceremony these came from, when they all came from one — which is the normal case,
        /// a film's awards all being decided on the same night. Null for the rare film honoured
        /// across two ceremonies, where naming one year would be wrong.
        /// </summary>
        public int? Ceremony
        {
            get
            {
                if (Nominations.Count == 0) return null;

                var first = Nominations[0].Ceremony;
                return Nominations.All(n => n.Ceremony == first) ? first : null;
            }
        }

        public static readonly OscarHonours None = new();
    }
}
