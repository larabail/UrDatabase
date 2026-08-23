using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UrDatabase.Models;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns a server's seasons and episodes into the list the series screen shows.
    ///
    /// Out of the window and pure, for the usual reason: everything interesting about a television
    /// library is an edge case, and none of them can be asserted on from inside a view. Specials
    /// with no number, a season the server did not enumerate, an episode whose season id points at
    /// nothing, a show numbered from zero — all of these are ordinary on a real server, and each
    /// one of them is a way for a list of episodes to come out empty or in the wrong order.
    /// </summary>
    public static class SeriesGrouping
    {
        /// <summary>
        /// Jellyfin's convention for episodes that are not part of the run: season zero. They are
        /// listed last rather than first, which is where Jellyfin itself puts them, because
        /// somebody opening a show wants episode one and not a Christmas special from 1994.
        /// </summary>
        public const int SpecialsSeasonNumber = 0;

        /// <summary>
        /// The seasons of a series, in the order they should be read, each carrying its episodes.
        /// </summary>
        /// <param name="seasons">
        /// What <c>/Shows/{id}/Seasons</c> returned. May be empty: some servers answer it with
        /// nothing for a show whose episodes are all loose in one folder.
        /// </param>
        /// <param name="episodes">Every episode of the series, in any order.</param>
        public static IReadOnlyList<SeasonGroup> Group(
            IEnumerable<JellyfinSeason>? seasons,
            IEnumerable<JellyfinEpisode>? episodes)
        {
            var seasonList = (seasons ?? Array.Empty<JellyfinSeason>())
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.ItemId))
                .ToList();

            var episodeList = (episodes ?? Array.Empty<JellyfinEpisode>())
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.ItemId))
                .ToList();

            // Keyed by the season's own id and by its number, because an episode may only know
            // one of the two. Jellyfin sends a SeasonId on most servers and nothing at all on
            // some, and matching on a single key is how half a show's episodes go missing.
            var byId = new Dictionary<string, JellyfinSeason>(StringComparer.OrdinalIgnoreCase);
            var byNumber = new Dictionary<int, JellyfinSeason>();

            foreach (var season in seasonList)
            {
                byId.TryAdd(season.ItemId, season);
                if (season.Number is int number) byNumber.TryAdd(number, season);
            }

            // Every season the server listed gets a group, even an empty one: a season with no
            // episodes is a fact about the library worth seeing, not a reason to pretend the
            // season does not exist.
            var groups = seasonList.ToDictionary(
                season => season.ItemId,
                season => new Builder(season.Name, season.Number),
                StringComparer.OrdinalIgnoreCase);

            // Episodes whose season is not among the listed ones. Keyed by number rather than by
            // id: the whole reason they are here is that their id matched nothing. An episode with
            // no number at all cannot be keyed and gets the one unnumbered group instead — a
            // dictionary keyed on a nullable int would compile to the same thing while claiming
            // null was an ordinary key.
            var orphans = new Dictionary<int, Builder>();
            Builder? unnumbered = null;

            foreach (var episode in episodeList)
            {
                var owner = Resolve(episode, byId, byNumber);

                if (owner is not null && groups.TryGetValue(owner.ItemId, out var group))
                {
                    group.Episodes.Add(episode);
                    continue;
                }

                if (episode.SeasonNumber is not int seasonNumber)
                {
                    unnumbered ??= new Builder(SeasonName(null), null);
                    unnumbered.Episodes.Add(episode);
                    continue;
                }

                if (!orphans.TryGetValue(seasonNumber, out var orphan))
                {
                    orphan = new Builder(SeasonName(seasonNumber), seasonNumber);
                    orphans[seasonNumber] = orphan;
                }

                orphan.Episodes.Add(episode);
            }

            var loose = unnumbered is null ? Array.Empty<Builder>() : new[] { unnumbered };

            return groups.Values
                .Concat(orphans.Values)
                .Concat(loose)
                .OrderBy(Rank)
                .ThenBy(builder => builder.Number ?? int.MaxValue)
                .ThenBy(builder => builder.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(builder => builder.Build())
                .ToList();
        }

        /// <summary>
        /// Which season an episode belongs to: its own season id first, then its season number.
        /// Null when neither matches anything the server listed.
        /// </summary>
        private static JellyfinSeason? Resolve(
            JellyfinEpisode episode,
            IReadOnlyDictionary<string, JellyfinSeason> byId,
            IReadOnlyDictionary<int, JellyfinSeason> byNumber)
        {
            if (!string.IsNullOrWhiteSpace(episode.SeasonId) && byId.TryGetValue(episode.SeasonId, out var byIdMatch))
                return byIdMatch;

            if (episode.SeasonNumber is int number && byNumber.TryGetValue(number, out var byNumberMatch))
                return byNumberMatch;

            return null;
        }

        /// <summary>
        /// Sort bucket. Ordinary seasons first, then specials, then anything unnumbered — the last
        /// two being the ones a viewer is least likely to have opened the show for.
        /// </summary>
        private static int Rank(Builder builder) => builder.Number switch
        {
            null => 2,
            SpecialsSeasonNumber => 1,
            _ => 0
        };

        /// <summary>
        /// One episode as a row. Public because the season screen is not the only thing that has
        /// ever wanted to print an episode, and because it is the piece worth asserting on.
        /// </summary>
        public static EpisodeRow ToRow(JellyfinEpisode episode)
        {
            if (episode is null) throw new ArgumentNullException(nameof(episode));

            return new EpisodeRow
            {
                ItemId = episode.ItemId,
                Label = EpisodeLabel(episode.SeasonNumber, episode.Number),
                Title = EpisodeTitle(episode),
                Runtime = RuntimeLabel(episode.RuntimeMinutes),
                Overview = (episode.Overview ?? "").Trim()
            };
        }

        /// <summary>
        /// <c>S01E02</c>. Zero padded to two digits, which is what makes a column of them line up,
        /// and widened rather than truncated for a show that ran past ninety-nine episodes.
        ///
        /// An episode with no season number still gets <c>E02</c>: half an answer is worth
        /// printing, and the alternative is a row identified by nothing but a title that may
        /// itself be missing.
        /// </summary>
        public static string EpisodeLabel(int? seasonNumber, int? episodeNumber)
        {
            var season = seasonNumber is int s ? $"S{Pad(s)}" : "";
            var episode = episodeNumber is int e ? $"E{Pad(e)}" : "";

            return season + episode;

            static string Pad(int value) =>
                Math.Abs(value).ToString("00", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// What to call an episode. The server's name, or the episode's number spelled out when it
        /// has none — a row reading only "S02E07" with an empty title beside it looks like a
        /// rendering fault rather than like an unnamed episode.
        /// </summary>
        public static string EpisodeTitle(JellyfinEpisode episode)
        {
            if (episode is null) throw new ArgumentNullException(nameof(episode));

            var name = (episode.Name ?? "").Trim();
            if (name.Length > 0) return name;

            return episode.Number is int number
                ? $"Episode {number.ToString(CultureInfo.InvariantCulture)}"
                : "Untitled episode";
        }

        /// <summary>
        /// The name of a season that did not come with one. Season zero is "Specials" because that
        /// is what it is everywhere else in the world, and calling it "Season 0" would be
        /// technically accurate and recognised by nobody.
        /// </summary>
        public static string SeasonName(int? number) => number switch
        {
            null => "Episodes",
            SpecialsSeasonNumber => "Specials",
            _ => $"Season {number.Value.ToString(CultureInfo.InvariantCulture)}"
        };

        /// <summary><c>"48 min"</c>, or empty when the server reported no length.</summary>
        public static string RuntimeLabel(int? minutes)
            => minutes is int value && value > 0
                ? $"{value.ToString(CultureInfo.InvariantCulture)} min"
                : "";

        /// <summary>The count beside a season heading, in the shape the genre shelves already use.</summary>
        public static string CountLabel(int count)
            => count == 1 ? "1 EPISODE" : $"{count.ToString(CultureInfo.InvariantCulture)} EPISODES";

        /// <summary>
        /// Which season the screen should be showing: the one the reader had chosen, or the one
        /// the screen was opened at, or the first.
        /// </summary>
        /// <remarks>
        /// Three claims in a fixed order, and the order is the whole rule. A reader's own choice
        /// wins outright, because the list is rebuilt when the server answers and a refresh that
        /// jumped somewhere else would undo a click made while it was in flight. Then the season
        /// the screen was asked to open at — an episode in the Continue watching row knows which
        /// one it is in, and landing on season one after clicking "S4E7" would answer a question
        /// with a different question. Then the first season, which is what opening a programme has
        /// always meant.
        ///
        /// A wanted season that this programme does not have falls through to the first rather
        /// than to nothing: the caller's number came from a resume entry that can be older than
        /// the episode list beside it.
        /// </remarks>
        public static SeasonGroup? SeasonToShow(
            IReadOnlyList<SeasonGroup>? seasons,
            string? chosenName,
            int? openAtNumber)
        {
            if (seasons is null || seasons.Count == 0) return null;

            if (!string.IsNullOrWhiteSpace(chosenName))
            {
                var chosen = seasons.FirstOrDefault(s =>
                    string.Equals(s.Name, chosenName, StringComparison.OrdinalIgnoreCase));

                if (chosen is not null) return chosen;
            }

            if (openAtNumber is int wanted)
            {
                var asked = seasons.FirstOrDefault(s => s.Number == wanted);
                if (asked is not null) return asked;
            }

            return seasons[0];
        }

        /// <summary>
        /// The line under a series title: how many seasons and how many episodes are actually
        /// here, counted from what was fetched rather than from what the server claimed. Empty
        /// when nothing has been fetched yet, so the screen can say "loading" instead of "0".
        /// </summary>
        public static string Describe(IReadOnlyCollection<SeasonGroup>? seasons)
        {
            if (seasons is null || seasons.Count == 0) return "";

            var episodes = seasons.Sum(s => s.Episodes.Count);

            var seasonPart = seasons.Count == 1
                ? "1 season"
                : $"{seasons.Count.ToString(CultureInfo.InvariantCulture)} seasons";

            var episodePart = episodes == 1
                ? "1 episode"
                : $"{episodes.ToString(CultureInfo.InvariantCulture)} episodes";

            return $"{seasonPart} · {episodePart}";
        }

        /// <summary>
        /// A season under construction. The episodes are sorted once, at the end, rather than kept
        /// ordered as they arrive.
        /// </summary>
        private sealed class Builder
        {
            public Builder(string name, int? number)
            {
                Number = number;
                Name = string.IsNullOrWhiteSpace(name) ? SeasonName(number) : name.Trim();
            }

            public string Name { get; }
            public int? Number { get; }
            public List<JellyfinEpisode> Episodes { get; } = new();

            public SeasonGroup Build() => new()
            {
                Name = Name,
                Number = Number,
                Episodes = Episodes
                    .OrderBy(e => e.Number ?? int.MaxValue)
                    .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(ToRow)
                    .ToList()
            };
        }
    }
}
