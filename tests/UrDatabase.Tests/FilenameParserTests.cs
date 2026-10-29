using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The shapes below are what film folders actually look like. A scan is only as good as this
    /// step: a title read wrongly here becomes a duplicate or an unfindable row in the catalogue.
    /// </summary>
    public class FilenameParserTests
    {
        [Theory]
        [InlineData("The Matrix (1999) 1080p.mkv")]
        [InlineData("The Matrix [1999].mp4")]
        [InlineData("The Matrix {1999}.avi")]
        [InlineData("The Matrix 1999.avi")]
        [InlineData("the.matrix.1999.BluRay.x264.mkv")]
        [InlineData("The.Matrix.1999.1080p.BluRay.x264-GROUP.mkv")]
        [InlineData("The_Matrix_1999_720p.mp4")]
        [InlineData("The Matrix (1999) [1080p] [YTS.MX].mp4")]
        [InlineData("The.Matrix.1999.2160p.UHD.BluRay.x265.10bit.HDR.DTS-HD.MA.mkv")]
        public void Reads_a_title_and_year_out_of_the_usual_release_names(string name)
        {
            var parsed = FilenameParser.Parse(name);

            Assert.Equal("The Matrix", parsed.Title);
            Assert.Equal(1999, parsed.Year);
        }

        [Theory]
        [InlineData("/Volumes/Media/Films/The Matrix (1999)/The Matrix (1999).mkv")]
        [InlineData(@"D:\Movies\The.Matrix.1999.mkv")]
        public void Reads_the_filename_out_of_a_path_written_for_either_platform(string path)
        {
            var parsed = FilenameParser.Parse(path);

            Assert.Equal("The Matrix", parsed.Title);
            Assert.Equal(1999, parsed.Year);
        }

        [Fact]
        public void A_year_in_brackets_beats_a_year_shaped_number_in_the_title()
        {
            var parsed = FilenameParser.Parse("Blade Runner 2049 (2017).mkv");

            Assert.Equal("Blade Runner 2049", parsed.Title);
            Assert.Equal(2017, parsed.Year);
        }

        [Fact]
        public void A_number_too_far_in_the_future_to_be_a_release_stays_in_the_title()
        {
            var parsed = FilenameParser.Parse("Blade.Runner.2049.2017.1080p.WEB-DL.mkv");

            Assert.Equal("Blade Runner 2049", parsed.Title);
            Assert.Equal(2017, parsed.Year);
        }

        [Fact]
        public void The_last_plausible_year_wins_when_the_title_is_itself_a_year()
        {
            var parsed = FilenameParser.Parse("1917.2019.1080p.BluRay.x264.mkv");

            Assert.Equal("1917", parsed.Title);
            Assert.Equal(2019, parsed.Year);
        }

        [Fact]
        public void A_title_that_is_only_a_year_is_not_mistaken_for_one()
        {
            var parsed = FilenameParser.Parse("2012.1080p.BluRay.x264.mkv");

            Assert.Equal("2012", parsed.Title);
            Assert.Null(parsed.Year);
        }

        [Fact]
        public void A_year_shaped_title_keeps_its_own_release_year()
        {
            var parsed = FilenameParser.Parse("2012 (2009).mkv");

            Assert.Equal("2012", parsed.Title);
            Assert.Equal(2009, parsed.Year);
        }

        [Theory]
        [InlineData("The Matrix.mkv", "The Matrix")]
        [InlineData("the.matrix.mkv", "The Matrix")]
        [InlineData("the.matrix.1080p.bluray.x264-GROUP.mkv", "The Matrix")]
        [InlineData("The Matrix - 1080p.mkv", "The Matrix")]
        [InlineData("The.Matrix.1080p [YTS].mkv", "The Matrix")]
        [InlineData("The.Matrix.1080p.BluRay.x264 [YTS].mkv", "The Matrix")]
        [InlineData("The.Matrix 1080p.BluRay.x264.mkv", "The Matrix")]
        public void A_name_with_no_year_still_yields_a_clean_title(string name, string expected)
        {
            var parsed = FilenameParser.Parse(name);

            Assert.Equal(expected, parsed.Title);
            Assert.Null(parsed.Year);
        }

        [Theory]
        [InlineData("Inception.2010.1080p.BluRay.x264-GRP.mkv", "Inception", 2010)]
        [InlineData("Inception (2010) [HDR] DIVX.avi", "Inception", 2010)]
        [InlineData("Inception [BluRay Rip][AC3 5.1 Castellano][2010].avi", "Inception", 2010)]
        [InlineData("Origen 2010 BDRip Castellano.avi", "Origen", 2010)]
        [InlineData("Origen BDRip Castellano.avi", "Origen", null)]
        [InlineData("Origen.MicroHD.1080p.DUAL.mkv", "Origen", null)]
        [InlineData("Origen MicroHD 1080p Castellano AC3 5.1.mkv", "Origen", null)]
        [InlineData("Avatar 4K UHD HDR Castellano AC3 5.1.mkv", "Avatar", null)]
        [InlineData("El Padrino DVDRip Spanish XviD.avi", "El Padrino", null)]
        [InlineData("Coco SPANISH 1080p.mkv", "Coco", null)]
        [InlineData("Coco 1080p SPANISH.mkv", "Coco", null)]
        [InlineData("Coco.1080p.HEVC.Castellano.Ingles.Subs.By.Pepito.mkv", "Coco", null)]
        [InlineData("Up HDR MULTI VOSE.mkv", "Up", null)]
        [InlineData("Joker.HDRip.XviD.AC3-EVO.2019.avi", "Joker", 2019)]
        [InlineData("Joker 1080p 2019 Dual Audio.mkv", "Joker", 2019)]
        [InlineData("Gladiator 2000 BRRip 720p Dual Audio.mkv", "Gladiator", 2000)]
        [InlineData("Dunkirk 2017 2160p UHD BDRemux HDR10 DTS-HD MA 5.1 Castellano-Ingles.mkv", "Dunkirk", 2017)]
        [InlineData("Seven (1995) [BDrip 1080p] [Castellano DTS-Ingles AC3] [Subs].mkv", "Seven", 1995)]
        [InlineData("Matrix Reloaded [DVDRIP][spanish][www.newpct.com].avi", "Matrix Reloaded", null)]
        [InlineData("www.DivxTotaL.com - El Laberinto del Fauno.avi", "El Laberinto del Fauno", null)]
        [InlineData("www.newpct1.com_Torrente.5.HDRip.avi", "Torrente 5", null)]
        [InlineData("Parasite.2019.KOREAN.1080p.WEB-DL.H264.AAC-RARBG.mp4", "Parasite", 2019)]
        [InlineData("Arrival 1080p WEB-DL DD5.1 H.264-FGT.mkv", "Arrival", null)]
        [InlineData("La La Land 2016 FullHD 1080p Castellano.mkv", "La La Land", 2016)]
        [InlineData("Roma 2018 Subtitulado VOSE 720p.mkv", "Roma", 2018)]
        public void Everything_after_the_first_release_tag_is_trimmed_off_the_title(
            string name, string expectedTitle, int? expectedYear)
        {
            var parsed = FilenameParser.Parse(name);

            Assert.Equal(expectedTitle, parsed.Title);
            Assert.Equal(expectedYear, parsed.Year);
        }

        [Theory]
        [InlineData("Inception.HD.SomeGroup", "Inception")]
        [InlineData("Inception HD-GROUP", "Inception")]
        [InlineData("Inception - HD English By Uploader", "Inception")]
        [InlineData("Inception\tHD\tSomeGroup", "Inception")]
        [InlineData("Inception.SD.SomeGroup", "Inception")]
        [InlineData("Inception.FHD.SomeGroup", "Inception")]
        [InlineData("Inception.SDR.SomeGroup", "Inception")]
        [InlineData("Inception.4K.SomeGroup", "Inception")]
        [InlineData("Inception.8K.SomeGroup", "Inception")]
        [InlineData("Inception.Full.HD.SomeGroup", "Inception")]
        [InlineData("Inception Full-HD SomeGroup", "Inception")]
        [InlineData("Inception.Micro_HD.SomeGroup", "Inception")]
        [InlineData("Inception.HD.Rip.SomeGroup", "Inception")]
        [InlineData("Inception.HD.TV.SomeGroup", "Inception")]
        [InlineData("Inception.Blu.Ray.SomeGroup", "Inception")]
        [InlineData("Inception WEB DL SomeGroup", "Inception")]
        [InlineData("Inception.Web_Rip.SomeGroup", "Inception")]
        [InlineData("Inception.H.264-GROUP", "Inception")]
        [InlineData("Inception.X.265.SomeGroup", "Inception")]
        [InlineData("Spider-Man Full HD SomeGroup", "Spider-Man")]
        public void Quality_and_split_technical_tags_end_the_release_suffix(string stem, string expected)
        {
            Assert.Equal(expected, FilenameParser.Parse(stem + ".mkv").Title);
            Assert.Equal(expected, FilenameParser.CleanText(stem));
            Assert.True(FilenameParser.CarriesReleaseTag(stem));
        }

        [Theory]
        [InlineData("HD")]
        [InlineData("4K")]
        [InlineData("HD Memories")]
        [InlineData("Life in HD")]
        [InlineData("The HD Movie")]
        [InlineData("Full Metal Jacket")]
        [InlineData("The Rip")]
        [InlineData("Pump Up the Audio")]
        [InlineData("Web of Lies")]
        public void Quality_cleanup_does_not_cut_title_phrases_or_substrings(string title)
        {
            Assert.Equal(title, FilenameParser.Parse(title + " (2000).mkv").Title);
            Assert.Equal(title, FilenameParser.CleanText(title));
            Assert.False(FilenameParser.CarriesReleaseTag(title));
        }

        [Theory]
        [InlineData("Inception HD SomeGroup (2010).mkv")]
        [InlineData("Inception.Full.HD.SomeGroup.2010.mkv")]
        [InlineData("Inception 2010 HD SomeGroup.mkv")]
        public void Quality_cleanup_keeps_the_release_year(string name)
        {
            var parsed = FilenameParser.Parse(name);
            Assert.Equal("Inception", parsed.Title);
            Assert.Equal(2010, parsed.Year);
        }

        [Theory]
        [InlineData("Johnny English (2003).mkv", "Johnny English")]
        [InlineData("Johnny English 1080p.mkv", "Johnny English")]
        [InlineData("The Spanish Prisoner (1997) 1080p.mkv", "The Spanish Prisoner")]
        [InlineData("4K.mkv", "4K")]
        [InlineData("The Net (1995).mkv", "The Net")]
        [InlineData("the.net.1995.1080p.mkv", "The Net")]
        [InlineData("2.0 (2018).mkv", "2.0")]
        [InlineData("Apollo 13 (1995) Dual 1080p.mkv", "Apollo 13")]
        public void A_word_that_can_be_part_of_a_title_only_goes_when_it_ends_one(string name, string expected)
        {
            Assert.Equal(expected, FilenameParser.Parse(name).Title);
        }

        [Theory]
        [InlineData("www.site.com.Back.to.the.Future.1985.mkv", "Back to the Future", 1985)]
        [InlineData("www.newpct1.com.La.Casa.de.Papel.HDRip.avi", "La Casa de Papel", null)]
        [InlineData("www.DivxTotaL.com.El.Laberinto.de.la.Muerte.avi", "El Laberinto de la Muerte", null)]
        [InlineData("www.site.com.es.Mar.Adentro.2004.avi", "Mar Adentro", 2004)]
        [InlineData("www.site.net - Me Before You (2016).mkv", "Me Before You", 2016)]
        public void A_site_name_is_removed_without_eating_title_words_that_look_like_domains(
            string name, string expectedTitle, int? expectedYear)
        {
            var parsed = FilenameParser.Parse(name);

            Assert.Equal(expectedTitle, parsed.Title);
            Assert.Equal(expectedYear, parsed.Year);
        }

        [Theory]
        [InlineData("The Rip (2026).mkv", "The Rip")]
        [InlineData("The Rip 1080p.mkv", "The Rip")]
        [InlineData("The Sub (2023).mkv", "The Sub")]
        [InlineData("The Sub 2023 1080p.mkv", "The Sub")]
        [InlineData("Pump Up the Audio (2001).mkv", "Pump Up the Audio")]
        [InlineData("Pump.Up.the.Audio.720p.mkv", "Pump Up the Audio")]
        [InlineData("The Latino (2010).mkv", "The Latino")]
        [InlineData("The Web 1080p.mkv", "The Web")]
        public void A_word_that_is_only_sometimes_a_tag_stays_when_nothing_says_it_is_one(string name, string expected)
        {
            Assert.Equal(expected, FilenameParser.Parse(name).Title);
        }

        [Theory]
        [InlineData("Gladiator BD Rip.mkv", "Gladiator")]
        [InlineData("Gladiator Dual Audio.mkv", "Gladiator")]
        [InlineData("Gladiator Castellano Latino.mkv", "Gladiator")]
        public void A_word_that_is_only_sometimes_a_tag_goes_when_another_tag_vouches_for_it(string name, string expected)
        {
            Assert.Equal(expected, FilenameParser.Parse(name).Title);
        }

        [Theory]
        [InlineData("Origen MicroHD", true)]
        [InlineData("Coco 1080p", true)]
        [InlineData("www.site.com Coco", true)]
        [InlineData("The Rip", false)]
        [InlineData("Pump Up the Audio", false)]
        [InlineData("Charlotte's Web", false)]
        [InlineData("4K", false)]
        public void Only_a_title_holding_an_unambiguous_release_tag_is_said_to_carry_one(string title, bool expected)
        {
            Assert.Equal(expected, FilenameParser.CarriesReleaseTag(title));
        }

        [Fact]
        public void A_title_already_carrying_release_tags_cleans_down_to_the_film()
        {
            // Catalogue rows scanned by an older build still hold the noisy name. The TMDB search
            // retries with CleanText, so improving it also rescues those rows without a rescan.
            Assert.Equal("Origen", FilenameParser.CleanText("Origen MicroHD 1080p Castellano"));
            Assert.Equal("Coco", FilenameParser.CleanText("Coco SPANISH HDR Version Extendida"));
        }

        [Theory]
        [InlineData("Spider-Man.mkv", "Spider-Man")]
        [InlineData("Spider-Man.Far.From.Home.2019.1080p.x264-GROUP.mkv", "Spider-Man Far From Home")]
        public void A_hyphen_inside_a_title_survives_release_group_stripping(string name, string expected)
        {
            Assert.Equal(expected, FilenameParser.Parse(name).Title);
        }

        [Theory]
        [InlineData("Mr. Nobody (2009).mkv", "Mr. Nobody")]
        [InlineData("Ocean's Eleven (2001).mkv", "Ocean's Eleven")]
        [InlineData("Amélie (2001).mkv", "Amélie")]
        [InlineData("WALL·E (2008).mkv", "WALL·E")]
        public void Punctuation_a_user_typed_is_left_alone(string name, string expected)
        {
            Assert.Equal(expected, FilenameParser.Parse(name).Title);
        }

        [Fact]
        public void A_lower_case_name_gets_its_capitals_back_without_shouting_small_words()
        {
            var parsed = FilenameParser.Parse("the.lord.of.the.rings.the.fellowship.of.the.ring.2001.extended.mkv");

            Assert.Equal("The Lord of the Rings the Fellowship of the Ring", parsed.Title);
            Assert.Equal(2001, parsed.Year);
        }

        [Fact]
        public void A_title_that_already_has_capitals_is_never_recased()
        {
            Assert.Equal("REC", FilenameParser.Parse("REC (2007).mkv").Title);
            Assert.Equal("The Lord of the Rings", FilenameParser.Parse("The Lord of the Rings (2001).mkv").Title);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Nothing_in_yields_an_empty_title_rather_than_an_exception(string? name)
        {
            var parsed = FilenameParser.Parse(name);

            Assert.Equal("", parsed.Title);
            Assert.Null(parsed.Year);
        }

        [Fact]
        public void A_name_that_is_nothing_but_noise_still_produces_a_title()
        {
            // movies.title is NOT NULL, and a file the parser cannot read still deserves a row a
            // user can see and rename.
            var parsed = FilenameParser.Parse("1080p.x264.mkv");

            Assert.False(string.IsNullOrWhiteSpace(parsed.Title));
        }

        [Theory]
        [InlineData(".mkv")]
        [InlineData("/Volumes/Media/.mkv")]
        [InlineData("+++.mkv")]
        [InlineData("_.mp4")]
        public void A_file_that_is_all_extension_or_all_punctuation_still_produces_a_title(string name)
        {
            // Anything blank here would be rejected by the scan and left out of the library
            // entirely, and the file would never appear however many times it was scanned.
            Assert.False(string.IsNullOrWhiteSpace(FilenameParser.Parse(name).Title));
        }

        [Fact]
        public void A_video_extension_is_stripped_and_anything_else_is_left_where_it_is()
        {
            Assert.Equal("The Matrix", FilenameParser.Parse("The Matrix.mkv").Title);
            Assert.Equal("The Matrix", FilenameParser.Parse("The Matrix (1999).part1.mkv").Title);
        }

        [Fact]
        public void A_title_ending_in_a_word_that_is_also_a_release_tag_loses_it_which_is_the_accepted_trade()
        {
            // "WEB" ends far more release names than titles; keeping it would leave "Film WEB" rows.
            Assert.Equal("Charlotte's", FilenameParser.Parse("Charlotte's Web (2006).mkv").Title);
        }

        [Fact]
        public void A_dotted_name_loses_genuine_full_stops_which_is_the_accepted_trade()
        {
            // Dots are how release names separate words, and there is no way to tell "S.W.A.T."
            // from "the.matrix" without a title database. Losing the stops is the lesser harm:
            // the alternative is a library full of "the.matrix" entries.
            Assert.Equal("S W A T", FilenameParser.Parse("S.W.A.T.2003.1080p.mkv").Title);
        }
    }
}
