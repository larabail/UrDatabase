using System.Collections.Generic;
using System.Linq;
using UrDatabase.Models;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Cast and crew from a Jellyfin server.
    ///
    /// The server has always been able to report these; for a long time nothing asked for them,
    /// so every film from a server showed an empty cast list as though it genuinely had none.
    /// These cover the mapping from the wire shape, and in particular the case handling — a film
    /// losing its whole cast over a capital A is the kind of failure nobody thinks to look for.
    /// </summary>
    public class JellyfinPeopleTests
    {
        private static JellyfinPersonDto Person(string name, string type, string? role = null) =>
            new() { Name = name, Type = type, Role = role };

        [Fact]
        public void Actors_become_a_cast_list_in_the_same_shape_the_local_path_uses()
        {
            var people = new[]
            {
                Person("Keir Dullea", "Actor", "Dave Bowman"),
                Person("Gary Lockwood", "Actor", "Frank Poole"),
            };

            var cast = JellyfinItemDto.BuildCast(people);

            Assert.Equal(new[] { "Keir Dullea (Dave Bowman)", "Gary Lockwood (Frank Poole)" }, cast);
        }

        [Fact]
        public void An_actor_with_no_part_is_listed_by_name_alone()
        {
            var cast = JellyfinItemDto.BuildCast(new[] { Person("Douglas Rain", "Actor") });

            Assert.Equal(new[] { "Douglas Rain" }, cast);
        }

        /// <summary>
        /// Jellyfin has shipped these types capitalised and, in places, lowercased.
        /// </summary>
        [Theory]
        [InlineData("Actor")]
        [InlineData("actor")]
        [InlineData("ACTOR")]
        public void The_person_type_is_matched_without_regard_to_case(string type)
        {
            var cast = JellyfinItemDto.BuildCast(new[] { Person("Keir Dullea", type, "Dave Bowman") });

            Assert.Single(cast);
        }

        [Fact]
        public void Directors_and_writers_become_crew_and_everyone_else_is_dropped()
        {
            var people = new[]
            {
                Person("Keir Dullea", "Actor", "Dave Bowman"),
                Person("Stanley Kubrick", "Director"),
                Person("Arthur C. Clarke", "Writer"),
                Person("Victor Lyndon", "Producer"),
                Person("Geoffrey Unsworth", "Cinematographer"),
            };

            var crew = JellyfinItemDto.BuildCrew(people);

            Assert.Equal(new[] { "Director: Stanley Kubrick", "Writer: Arthur C. Clarke" }, crew);
        }

        [Fact]
        public void Directors_come_before_writers_however_the_server_ordered_them()
        {
            var people = new[]
            {
                Person("Arthur C. Clarke", "Writer"),
                Person("Stanley Kubrick", "Director"),
            };

            var crew = JellyfinItemDto.BuildCrew(people);

            Assert.StartsWith("Director:", crew[0]);
            Assert.StartsWith("Writer:", crew[1]);
        }

        /// <summary>
        /// A server lists everyone from the gaffer down, and a details screen is not a call sheet.
        /// The caps match what the local TMDB path already applies.
        /// </summary>
        [Fact]
        public void The_lists_are_capped_the_same_way_the_local_path_caps_them()
        {
            var people = new List<JellyfinPersonDto>();
            for (var i = 0; i < 40; i++) people.Add(Person($"Actor {i}", "Actor", $"Part {i}"));
            for (var i = 0; i < 8; i++) people.Add(Person($"Director {i}", "Director"));
            for (var i = 0; i < 8; i++) people.Add(Person($"Writer {i}", "Writer"));

            Assert.Equal(10, JellyfinItemDto.BuildCast(people).Count);

            var crew = JellyfinItemDto.BuildCrew(people);
            Assert.Equal(3, crew.Count(c => c.StartsWith("Director:")));
            Assert.Equal(3, crew.Count(c => c.StartsWith("Writer:")));
        }

        [Fact]
        public void A_person_with_no_name_is_dropped_rather_than_shown_as_a_blank_row()
        {
            var people = new[]
            {
                Person("", "Actor", "Someone"),
                Person("   ", "Director"),
                Person("Keir Dullea", "Actor", "Dave Bowman"),
            };

            Assert.Equal(new[] { "Keir Dullea (Dave Bowman)" }, JellyfinItemDto.BuildCast(people));
            Assert.Empty(JellyfinItemDto.BuildCrew(people));
        }

        [Fact]
        public void An_item_with_no_people_at_all_maps_to_empty_lists_rather_than_null()
        {
            Assert.Empty(JellyfinItemDto.BuildCast(null));
            Assert.Empty(JellyfinItemDto.BuildCrew(null));

            var movie = new JellyfinItemDto { Id = "abc", Name = "Ran" }.ToMovie();

            Assert.NotNull(movie);
            Assert.Empty(movie!.Cast);
            Assert.Empty(movie.Crew);
        }

        /// <summary>
        /// TMDB distinguishes screenplay from story; Jellyfin uses one "Writer" type, and some
        /// servers report "Screenplay Writer". Matching on the substring covers both.
        /// </summary>
        [Fact]
        public void A_writer_variant_is_still_a_writer()
        {
            var crew = JellyfinItemDto.BuildCrew(new[] { Person("Arthur C. Clarke", "Screenplay Writer") });

            Assert.Equal(new[] { "Writer: Arthur C. Clarke" }, crew);
        }

        [Fact]
        public void A_whole_item_carries_its_people_through_to_the_movie()
        {
            var movie = new JellyfinItemDto
            {
                Id = "abc",
                Name = "2001: A Space Odyssey",
                People = new List<JellyfinPersonDto>
                {
                    Person("Keir Dullea", "Actor", "Dave Bowman"),
                    Person("Stanley Kubrick", "Director"),
                }
            }.ToMovie();

            Assert.NotNull(movie);
            Assert.Equal(new[] { "Keir Dullea (Dave Bowman)" }, movie!.Cast);
            Assert.Equal(new[] { "Director: Stanley Kubrick" }, movie.Crew);
        }
    }
}
