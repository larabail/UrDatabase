using System.Collections.Generic;
using System.Globalization;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Builds the row of facts under the title on the details screen.
    ///
    /// This exists as a service, rather than as five conditionally visible panels in the view,
    /// because of the two ratings. <c>IMDb 8.3</c> and <c>Jellyfin 8.2</c> are two different
    /// measurements of two different populations, and a screen that sets them as two identical
    /// pills side by side is telling the user they are the same kind of number. This repository
    /// has already shipped one bug from labelling one service's rating as another's, so the rule
    /// that each number is printed under its own source's name is enforced somewhere it can be
    /// tested rather than somewhere it can be reformatted by accident.
    ///
    /// It also decides which fact is last, because that depends on which ones a given film has,
    /// and a separator hanging off the end of the row is the giveaway that nobody checked.
    /// </summary>
    public static class DetailFacts
    {
        public static IReadOnlyList<DetailFact> For(MovieDetailsVm? vm)
        {
            var facts = new List<DetailFact>();
            if (vm is null) return facts;

            if (vm.Year is int year)
            {
                facts.Add(new DetailFact
                {
                    Label = "YEAR",
                    Value = year.ToString(CultureInfo.InvariantCulture)
                });
            }

            if (vm.Runtime is int runtime && runtime > 0)
            {
                facts.Add(new DetailFact
                {
                    Label = "RUNTIME",
                    Value = $"{runtime.ToString(CultureInfo.InvariantCulture)} min"
                });
            }

            // Named for the service the number came from, never merely "RATING". The star this
            // replaced said neither, which is how the two got confused in the first place.
            if (vm.ImdbRating is double imdb)
            {
                facts.Add(new DetailFact
                {
                    Label = "IMDB",
                    Value = imdb.ToString("0.0", CultureInfo.InvariantCulture),
                    Kind = DetailFactKind.Imdb
                });
            }

            if (vm.CommunityRating is double community)
            {
                facts.Add(new DetailFact
                {
                    Label = "JELLYFIN",
                    Value = community.ToString("0.0", CultureInfo.InvariantCulture),
                    Kind = DetailFactKind.Server
                });
            }

            // Says out loud where the film is, in the same colour the badge and the sync button
            // use. A film in both places says so too: the card carries both badges, and a details
            // screen that mentioned neither would be the one place the app went quiet about it.
            if (vm.IsRemote)
            {
                facts.Add(new DetailFact
                {
                    Label = "WHERE",
                    Value = "On the server",
                    Kind = DetailFactKind.Server
                });
            }
            else if (vm.IsOnServer)
            {
                facts.Add(new DetailFact
                {
                    Label = "WHERE",
                    Value = $"{UiMovie.OfflineTag} and on the server",
                    Kind = DetailFactKind.Server
                });
            }

            for (var i = 0; i < facts.Count; i++)
                facts[i].ShowSeparator = i < facts.Count - 1;

            return facts;
        }

        /// <summary>
        /// The same row for a television series.
        /// </summary>
        /// <remarks>
        /// An overload rather than a branch inside the one above, because what a series has to say
        /// about itself genuinely differs: it has no runtime — that number belongs to an episode —
        /// and it has two counts a film cannot have. It is always on the server, so unlike a film
        /// it has no "where" to establish; saying so on every programme would be a column of
        /// identical text.
        /// </remarks>
        public static IReadOnlyList<DetailFact> For(SeriesDetailsVm? vm)
        {
            var facts = new List<DetailFact>();
            if (vm is null) return facts;

            if (vm.Year is int year)
            {
                facts.Add(new DetailFact
                {
                    Label = "FROM",
                    Value = year.ToString(CultureInfo.InvariantCulture)
                });
            }

            // Both counts are printed only when the server supplied them. A programme headed
            // "0 SEASONS" beside a list of its seasons is the app arguing with itself.
            if (vm.SeasonCount is int seasons && seasons > 0)
            {
                facts.Add(new DetailFact
                {
                    Label = "SEASONS",
                    Value = seasons.ToString(CultureInfo.InvariantCulture)
                });
            }

            if (vm.EpisodeCount is int episodes && episodes > 0)
            {
                facts.Add(new DetailFact
                {
                    Label = "EPISODES",
                    Value = episodes.ToString(CultureInfo.InvariantCulture)
                });
            }

            if (vm.ImdbRating is double imdb)
            {
                facts.Add(new DetailFact
                {
                    Label = "IMDB",
                    Value = imdb.ToString("0.0", CultureInfo.InvariantCulture),
                    Kind = DetailFactKind.Imdb
                });
            }

            if (vm.CommunityRating is double community)
            {
                facts.Add(new DetailFact
                {
                    Label = "JELLYFIN",
                    Value = community.ToString("0.0", CultureInfo.InvariantCulture),
                    Kind = DetailFactKind.Server
                });
            }

            for (var i = 0; i < facts.Count; i++)
                facts[i].ShowSeparator = i < facts.Count - 1;

            return facts;
        }
    }
}
