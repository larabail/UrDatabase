using System.Collections.Generic;
using System.Linq;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    public class CompanionsTests
    {
        [Fact]
        public void A_line_of_names_is_split_trimmed_and_deduplicated()
        {
            Assert.Equal(new[] { "Ana", "Luis Pérez", "Marta" }, Companions.Parse(" Ana, Luis   Pérez; ana\nMarta,, "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData(" , ; ")]
        public void Nobody_is_an_empty_list(string? text)
        {
            Assert.Empty(Companions.Parse(text));
            Assert.Null(Companions.ToJson(Companions.Parse(text)));
        }

        [Fact]
        public void A_name_that_is_really_a_paragraph_is_cut_short()
        {
            var name = Companions.Parse(new string('x', 500))[0];
            Assert.Equal(Companions.MaxNameLength, name.Length);
        }

        [Fact]
        public void Names_survive_the_round_trip_through_json()
        {
            var json = Companions.ToJson(new[] { "Ana", "Luis \"el Gordo\"" });
            Assert.Equal(new[] { "Ana", "Luis \"el Gordo\"" }, Companions.FromJson(json));
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("{\"a\":1}")]
        [InlineData("42")]
        public void A_column_that_is_not_a_list_of_names_reads_as_nobody(string json)
        {
            Assert.Empty(Companions.FromJson(json));
        }

        [Fact]
        public void Companions_are_described_in_a_sentence()
        {
            Assert.Equal("", Companions.Describe(new string[0]));
            Assert.Equal("with Ana", Companions.Describe(new[] { "Ana" }));
            Assert.Equal("with Ana and Luis", Companions.Describe(new[] { "Ana", "Luis" }));
            Assert.Equal("with Ana, Luis and Marta", Companions.Describe(new[] { "Ana", "Luis", "Marta" }));
        }

        [Fact]
        public void Suggestions_put_the_most_frequent_first_and_break_ties_by_recency()
        {
            var viewings = new List<IReadOnlyList<string>>
            {
                new[] { "Marta" },          // newest
                new[] { "Luis", "Ana" },
                new[] { "ana" },
            };

            Assert.Equal(new[] { "Ana", "Marta", "Luis" }, Companions.Suggestions(viewings));
        }

        [Fact]
        public void A_chip_adds_a_name_that_is_not_in_the_line()
        {
            Assert.Equal("Luis", Companions.Toggle("", "Luis"));
            Assert.Equal("Ana, Luis", Companions.Toggle("Ana", "Luis"));
            Assert.Equal("Ana, Marta, Luis", Companions.Toggle("Ana;  Marta,", "Luis"));
        }

        [Fact]
        public void A_chip_takes_out_a_name_that_is_already_there_whatever_its_case()
        {
            Assert.Equal("Ana", Companions.Toggle("Ana, luis", "Luis"));
            Assert.Equal("", Companions.Toggle("Luis", "Luis"));
        }

        [Fact]
        public void A_blank_chip_changes_nothing_but_still_tidies_the_line()
        {
            Assert.Equal("Ana, Luis", Companions.Toggle(" Ana ;Luis", "  "));
        }

        [Fact]
        public void Contains_ignores_case_and_spacing()
        {
            Assert.True(Companions.Contains("Ana, luis  pérez", "Luis Pérez"));
            Assert.False(Companions.Contains("Ana", "Luis"));
            Assert.False(Companions.Contains("Ana", " "));
        }

        [Fact]
        public void Chips_offer_the_regulars_in_order_and_mark_who_is_already_in_the_line()
        {
            var chips = Companions.Chips(new[] { "Ana", "Luis", "Marta" }, "luis, Pedro");

            Assert.Equal(new[] { "Ana", "Luis", "Marta" }, chips.Select(c => c.Name));
            Assert.Equal(new[] { false, true, false }, chips.Select(c => c.IsIncluded));
        }

        [Fact]
        public void Chips_stop_at_the_limit()
        {
            var known = Enumerable.Range(1, 20).Select(i => "Person " + i).ToList();

            Assert.Equal(Companions.MaxChips, Companions.Chips(known, null).Count);
            Assert.Equal(3, Companions.Chips(known, null, max: 3).Count);
        }

        [Fact]
        public void Nobody_known_means_no_chips()
        {
            Assert.Empty(Companions.Chips(null, "Ana"));
            Assert.Empty(Companions.Chips(new string[0], "Ana"));
        }
    }
}