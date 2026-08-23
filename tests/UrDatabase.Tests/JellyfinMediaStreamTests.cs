using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using UrDatabase.Models;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Folding a server's track list into the app's own description. This is the only path in the
    /// app where a resolution and a language are measured rather than claimed, which is the whole
    /// reason the sync pays for the extra field.
    /// </summary>
    public class JellyfinMediaStreamTests
    {
        private static JellyfinMediaStreamDto Video(
            string codec = "hevc",
            int width = 3840,
            int height = 2160,
            string? rangeType = "HDR10",
            string? range = "HDR") => new()
        {
            Type = "Video",
            Codec = codec,
            Width = width,
            Height = height,
            VideoRange = range,
            VideoRangeType = rangeType
        };

        private static JellyfinMediaStreamDto Audio(
            string language,
            string codec = "eac3",
            int channels = 6,
            bool isDefault = false,
            string? profile = null,
            string? displayTitle = null) => new()
        {
            Type = "Audio",
            Codec = codec,
            Language = language,
            Channels = channels,
            IsDefault = isDefault,
            Profile = profile,
            DisplayTitle = displayTitle
        };

        /// <summary>
        /// A film is asked about its tracks; a series is not. A series is a folder — it has no
        /// track list and no picture size of its own — so carrying MediaStreams into the series
        /// request would buy a programme no badges and cost the payload anyway. The two constants
        /// share a description and differ in exactly that.
        /// </summary>
        [Fact]
        public void Only_films_are_asked_for_their_tracks()
        {
            Assert.Contains("MediaStreams", UrDatabase.Services.JellyfinClient.ItemFields);
            Assert.Contains("Width", UrDatabase.Services.JellyfinClient.ItemFields);

            Assert.DoesNotContain("MediaStreams", UrDatabase.Services.JellyfinClient.SeriesFields);
            Assert.DoesNotContain("Width", UrDatabase.Services.JellyfinClient.SeriesFields);

            // Still a description, though: a series needs its plot, genres and cast like anything
            // else, and its own two counts on top.
            foreach (var field in new[] { "Genres", "Overview", "People", "ProviderIds" })
                Assert.Contains(field, UrDatabase.Services.JellyfinClient.SeriesFields);

            Assert.Contains("ChildCount", UrDatabase.Services.JellyfinClient.SeriesFields);
        }

        /// <summary>
        /// An episode list is a list of titles. A season of twenty-four would drag twenty-four
        /// track lists across the network to render it.
        /// </summary>
        [Fact]
        public void An_episode_list_stays_cheap()
        {
            Assert.DoesNotContain("MediaStreams", UrDatabase.Services.JellyfinClient.EpisodeFields);
            Assert.DoesNotContain("People", UrDatabase.Services.JellyfinClient.EpisodeFields);
        }

        [Fact]
        public void The_picture_is_taken_from_the_video_stream()
        {
            var media = JellyfinItemDto.BuildMedia(new[] { Video() }, null, null, "mkv");

            Assert.NotNull(media);
            Assert.Equal(3840, media!.Width);
            Assert.Equal(2160, media.Height);
            Assert.Equal("hevc", media.VideoCodec);
            Assert.Equal("mkv", media.Container);
        }

        [Fact]
        public void The_specific_range_beats_the_plain_one()
        {
            // Older servers send only VideoRange ("HDR"), newer ones also send VideoRangeType
            // ("HDR10"). The specific answer is the useful one.
            var media = JellyfinItemDto.BuildMedia(new[] { Video(rangeType: "DOVI", range: "HDR") }, null, null, null);

            Assert.Equal("DOVI", media!.VideoRange);
        }

        [Fact]
        public void An_older_server_that_only_says_hdr_is_still_heard()
        {
            var media = JellyfinItemDto.BuildMedia(new[] { Video(rangeType: null, range: "HDR") }, null, null, null);

            Assert.Equal("HDR", media!.VideoRange);
        }

        [Fact]
        public void The_streams_dimensions_beat_the_items_summary()
        {
            var media = JellyfinItemDto.BuildMedia(new[] { Video(width: 1920, height: 1080) }, 3840, 2160, null);

            Assert.Equal(1920, media!.Width);
        }

        [Fact]
        public void The_items_dimensions_are_used_when_no_stream_carries_them()
        {
            var media = JellyfinItemDto.BuildMedia(new[] { Video(width: 0, height: 0) }, 1920, 1080, null);

            Assert.Equal(1920, media!.Width);
            Assert.Equal(1080, media.Height);
        }

        [Fact]
        public void The_default_audio_track_describes_the_sound()
        {
            // Not the first track. A film whose first track is a commentary in 2.0 must not be
            // badged as 2.0 when the track that plays is 7.1.
            var media = JellyfinItemDto.BuildMedia(
                new[]
                {
                    Video(),
                    Audio("eng", codec: "aac", channels: 2),
                    Audio("eng", codec: "truehd", channels: 8, isDefault: true)
                },
                null, null, null);

            Assert.Equal("truehd", media!.AudioCodec);
            Assert.Equal(8, media.AudioChannels);
        }

        [Fact]
        public void The_first_audio_track_is_used_when_none_is_marked_default()
        {
            var media = JellyfinItemDto.BuildMedia(
                new[] { Video(), Audio("eng", codec: "dts", channels: 6) },
                null, null, null);

            Assert.Equal("dts", media!.AudioCodec);
        }

        [Fact]
        public void Atmos_is_found_where_jellyfin_actually_names_it()
        {
            // It is not a codec and never appears in the codec field; it rides on TrueHD or E-AC-3
            // and is named only in the profile or the display title.
            var byProfile = JellyfinItemDto.BuildMedia(
                new[] { Audio("eng", codec: "truehd", isDefault: true, profile: "Dolby Atmos") },
                null, null, null);

            var byTitle = JellyfinItemDto.BuildMedia(
                new[] { Audio("eng", codec: "eac3", isDefault: true, displayTitle: "Dolby Digital+ Atmos 5.1") },
                null, null, null);

            Assert.True(byProfile!.HasAtmos);
            Assert.True(byTitle!.HasAtmos);
        }

        [Fact]
        public void Audio_and_subtitle_languages_are_kept_apart()
        {
            var media = JellyfinItemDto.BuildMedia(
                new[]
                {
                    Video(),
                    Audio("eng", isDefault: true),
                    Audio("fra"),
                    new JellyfinMediaStreamDto { Type = "Subtitle", Language = "spa" },
                    new JellyfinMediaStreamDto { Type = "Subtitle", Language = "eng" }
                },
                null, null, null);

            Assert.Equal(new[] { "eng", "fra" }, media!.AudioLanguages);
            Assert.Equal(new[] { "spa", "eng" }, media.SubtitleLanguages);
        }

        [Fact]
        public void A_track_with_no_language_tag_is_dropped_rather_than_shown_blank()
        {
            var media = JellyfinItemDto.BuildMedia(
                new[] { Video(), Audio("eng", isDefault: true), new JellyfinMediaStreamDto { Type = "Audio" } },
                null, null, null);

            Assert.Equal(new[] { "eng" }, media!.AudioLanguages);
        }

        /// <summary>
        /// Jellyfin has shipped these type names capitalised and, in places, lowercased. A film
        /// losing every track over a capital A is the kind of failure nobody thinks to look for.
        /// </summary>
        [Fact]
        public void Stream_types_are_matched_without_regard_to_case()
        {
            var media = JellyfinItemDto.BuildMedia(
                new[]
                {
                    new JellyfinMediaStreamDto { Type = "video", Codec = "hevc", Width = 3840, Height = 2160 },
                    new JellyfinMediaStreamDto { Type = "AUDIO", Codec = "dts", Language = "eng", Channels = 6 }
                },
                null, null, null);

            Assert.Equal("hevc", media!.VideoCodec);
            Assert.Equal("dts", media.AudioCodec);
        }

        [Fact]
        public void An_item_the_server_measured_nothing_about_has_no_media_at_all()
        {
            // Null rather than an empty description, so a library synced before this was asked for
            // is indistinguishable from a server that reported nothing — which is the truth in
            // both cases: nobody measured this film.
            Assert.Null(JellyfinItemDto.BuildMedia(null, null, null, null));
            Assert.Null(JellyfinItemDto.BuildMedia(new List<JellyfinMediaStreamDto>(), null, null, null));
        }

        [Fact]
        public void A_whole_item_carries_its_media_through_to_the_movie()
        {
            var json = @"{
                ""Id"": ""abc"",
                ""Name"": ""F1"",
                ""ProductionYear"": 2025,
                ""Width"": 3840,
                ""Height"": 1600,
                ""MediaStreams"": [
                    { ""Type"": ""Video"", ""Codec"": ""hevc"", ""Width"": 3840, ""Height"": 1600, ""VideoRangeType"": ""DOVI"" },
                    { ""Type"": ""Audio"", ""Codec"": ""truehd"", ""Language"": ""eng"", ""Channels"": 8, ""IsDefault"": true, ""Profile"": ""Dolby Atmos"" },
                    { ""Type"": ""Subtitle"", ""Language"": ""fre"" }
                ]
            }";

            var item = JsonSerializer.Deserialize<JellyfinItemDto>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            var movie = item!.ToMovie();

            Assert.NotNull(movie?.Media);
            Assert.Equal(3840, movie!.Media!.Width);
            Assert.Equal("DOVI", movie.Media.VideoRange);
            Assert.True(movie.Media.HasAtmos);
            Assert.Equal(new[] { "fre" }, movie.Media.SubtitleLanguages);
        }

        /// <summary>
        /// The badges are the point of all of the above, so the round trip is asserted end to end
        /// rather than only at the mapping.
        /// </summary>
        [Fact]
        public void A_server_film_ends_up_with_the_badges_a_user_would_expect()
        {
            var media = JellyfinItemDto.BuildMedia(
                new[]
                {
                    Video(codec: "hevc", width: 3840, height: 1600, rangeType: "HDR10"),
                    Audio("eng", codec: "truehd", channels: 8, isDefault: true, profile: "Dolby Atmos"),
                    Audio("fra", codec: "eac3"),
                    new JellyfinMediaStreamDto { Type = "Subtitle", Language = "spa" }
                },
                null, null, "mkv");

            var texts = UrDatabase.Services.MediaFlags.For(media).Select(f => f.Text).ToList();

            Assert.Equal(new[] { "4K", "HDR10", "HEVC", "ATMOS 7.1", "EN", "FR", "ES" }, texts);
        }
    }
}
