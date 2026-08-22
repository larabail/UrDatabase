using System;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The check that stands between a 200 response and a permanently cached file. The cache
    /// end of this is covered by <see cref="PosterCacheTests"/>; these are the cases that are
    /// awkward to serve through a handler — the formats TMDB does not send but a mirror might,
    /// and the buffers too short to judge.
    /// </summary>
    public class PosterContentTests
    {
        [Theory]
        [InlineData("image/jpeg")]
        [InlineData("image/png")]
        [InlineData("IMAGE/JPEG")]
        [InlineData("image/webp")]
        [InlineData("application/octet-stream")]
        [InlineData("binary/octet-stream")]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void A_type_that_could_be_artwork_is_allowed_through(string? mediaType)
            => Assert.True(PosterContent.IsPlausibleContentType(mediaType));

        [Theory]
        [InlineData("text/html")]
        [InlineData("TEXT/HTML")]
        [InlineData("application/json")]
        [InlineData("text/plain")]
        [InlineData("application/xml")]
        public void A_type_that_is_plainly_a_page_rather_than_a_poster_is_refused(string mediaType)
            => Assert.False(PosterContent.IsPlausibleContentType(mediaType));

        [Fact]
        public void A_jpeg_is_recognised()
            => Assert.True(PosterContent.LooksLikeImage(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));

        [Fact]
        public void A_png_is_recognised()
            => Assert.True(PosterContent.LooksLikeImage(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00 }));

        [Theory]
        [InlineData("GIF87a")]
        [InlineData("GIF89a")]
        public void A_gif_is_recognised(string signature)
            => Assert.True(PosterContent.LooksLikeImage(System.Text.Encoding.ASCII.GetBytes(signature)));

        [Fact]
        public void A_webp_is_recognised()
        {
            var head = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' };

            Assert.True(PosterContent.LooksLikeImage(head));
        }

        [Fact]
        public void A_bitmap_is_recognised()
            => Assert.True(PosterContent.LooksLikeImage(System.Text.Encoding.ASCII.GetBytes("BM______")));

        [Theory]
        [InlineData("<!DOCTYPE html>")]
        [InlineData("<html><body>")]
        [InlineData("{\"status\":404}")]
        [InlineData("Not Found")]
        public void A_page_is_not_mistaken_for_a_poster(string body)
            => Assert.False(PosterContent.LooksLikeImage(System.Text.Encoding.ASCII.GetBytes(body)));

        /// <summary>
        /// A response that stopped early is not artwork, and answering "maybe" for one would put
        /// the fragment back in the cache by another route.
        /// </summary>
        [Fact]
        public void A_body_too_short_to_carry_a_signature_is_refused()
        {
            Assert.False(PosterContent.LooksLikeImage(ReadOnlySpan<byte>.Empty));
            Assert.False(PosterContent.LooksLikeImage(new byte[] { 0xFF }));
            Assert.False(PosterContent.LooksLikeImage(new byte[] { 0xFF, 0xD8 }));
            Assert.False(PosterContent.LooksLikeImage(new byte[] { 0x89, 0x50, 0x4E, 0x47 }));
            Assert.False(PosterContent.LooksLikeImage(System.Text.Encoding.ASCII.GetBytes("RIFF____WEB")));
        }

        /// <summary>
        /// The signature buffer the download fills. Shrinking it would silently stop WebP being
        /// recognised, which would show up as artwork that never caches rather than as a failure.
        /// </summary>
        [Fact]
        public void The_signature_buffer_is_long_enough_for_every_format_checked()
            => Assert.Equal(12, PosterContent.SignatureLength);
    }
}
