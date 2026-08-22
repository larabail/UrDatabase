using System;

namespace UrDatabase.Services
{
    /// <summary>
    /// What is worth keeping in the poster cache, decided before anything is moved into place.
    ///
    /// A cached poster is trusted forever — the file being there is the whole of the check on
    /// the next launch — so whatever is written has to be an image on the way in. The failure
    /// this exists for is not a broken connection but a successful one: a proxy, a captive
    /// portal or TMDB itself answering 200 with an HTML page, which lands on disk as a
    /// perfectly valid <c>.jpg</c> that no image decoder will ever open.
    /// </summary>
    public static class PosterContent
    {
        /// <summary>
        /// How many leading bytes <see cref="LooksLikeImage"/> needs. Twelve, because WebP is
        /// the longest signature here: <c>RIFF</c>, a four byte length, then <c>WEBP</c>.
        /// </summary>
        public const int SignatureLength = 12;

        /// <summary>
        /// True when the server's own description of the body does not rule out an image.
        ///
        /// Deliberately generous. It is a cheap way to abandon an obvious error page before
        /// reading it, not the real check — plenty of perfectly good artwork arrives as
        /// <c>application/octet-stream</c> or with no type at all, and refusing that would
        /// trade a rare corrupt poster for a common missing one. <see cref="LooksLikeImage"/>
        /// is what actually decides.
        /// </summary>
        public static bool IsPlausibleContentType(string? mediaType)
        {
            if (string.IsNullOrWhiteSpace(mediaType)) return true;

            var value = mediaType.Trim();

            return value.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || value.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                || value.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when <paramref name="head"/> opens with the signature of a format the UI can
        /// actually decode. Fewer than <see cref="SignatureLength"/> bytes is allowed — a
        /// truncated response is simply not an image, and says so by failing every test below.
        /// </summary>
        public static bool LooksLikeImage(ReadOnlySpan<byte> head)
        {
            // JPEG: FF D8 FF. Everything TMDB serves as a poster.
            if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return true;

            // PNG: the eight byte signature, spelt out rather than compared to a literal so the
            // trailing CR LF EOF bytes are checked too — they are what catch a transfer that
            // rewrote line endings on the way through.
            if (head.Length >= 8
                && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
                && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return true;

            // GIF87a / GIF89a.
            if (head.Length >= 6
                && head[0] == (byte)'G' && head[1] == (byte)'I' && head[2] == (byte)'F'
                && head[3] == (byte)'8' && (head[4] == (byte)'7' || head[4] == (byte)'9')
                && head[5] == (byte)'a') return true;

            // WebP: a RIFF container whose form type is WEBP. The four length bytes between
            // them are skipped, being the file size rather than a signature.
            if (head.Length >= 12
                && head[0] == (byte)'R' && head[1] == (byte)'I' && head[2] == (byte)'F' && head[3] == (byte)'F'
                && head[8] == (byte)'W' && head[9] == (byte)'E' && head[10] == (byte)'B' && head[11] == (byte)'P') return true;

            // BMP.
            if (head.Length >= 2 && head[0] == (byte)'B' && head[1] == (byte)'M') return true;

            return false;
        }
    }
}
