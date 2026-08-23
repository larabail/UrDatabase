using System.Globalization;

namespace UrDatabase.Services
{
    /// <summary>
    /// Bytes as a person reads them.
    ///
    /// Binary units, because that is what a file manager on either platform shows and a transfer
    /// that disagreed with Finder about the size of the file it had just written would look wrong
    /// rather than merely different.
    ///
    /// Its own class because two unrelated features now need it — copying a film off a Jellyfin
    /// server, and fetching a new build of the app — and the second borrowing the first one's
    /// formatter would have tied the update check to the Jellyfin code for the sake of a string.
    /// </summary>
    public static class ByteSize
    {
        public static string Describe(long bytes)
        {
            if (bytes < 0) return "0 B";
            if (bytes < 1024) return $"{bytes} B";

            string[] units = { "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = -1;

            do
            {
                value /= 1024;
                unit++;
            }
            while (value >= 1024 && unit < units.Length - 1);

            return value < 10
                ? $"{value.ToString("0.0", CultureInfo.CurrentCulture)} {units[unit]}"
                : $"{value.ToString("0", CultureInfo.CurrentCulture)} {units[unit]}";
        }
    }
}
