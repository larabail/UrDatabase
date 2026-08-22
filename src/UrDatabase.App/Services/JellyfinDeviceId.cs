using System;
using System.IO;

namespace UrDatabase.Services
{
    /// <summary>
    /// The stable identifier Jellyfin uses to tell one client install from another. It has to
    /// survive restarts: a fresh id on every launch makes the server's device list grow without
    /// bound and invalidates the session it just issued a token for.
    ///
    /// It is a random GUID and nothing else — not the machine name, not a hardware serial, not
    /// anything that identifies the person — because it is sent to a server on every request.
    /// </summary>
    public static class JellyfinDeviceId
    {
        public const string FileName = "jellyfin-device-id";

        /// <summary>Where the id is kept, alongside the database and logs.</summary>
        public static string DefaultPath => Path.Combine(PlatformPaths.AppDataRoot, FileName);

        /// <summary>
        /// Reads the id, creating one on first use. Never throws: a read-only home directory
        /// yields a fresh id for this session instead of stopping the app from connecting at all.
        /// </summary>
        public static string Resolve(string? path = null)
        {
            var file = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;

            try
            {
                if (File.Exists(file))
                {
                    var existing = File.ReadAllText(file).Trim();
                    if (IsUsable(existing)) return existing;
                }

                var created = Guid.NewGuid().ToString("N");

                var directory = Path.GetDirectoryName(Path.GetFullPath(file));
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                File.WriteAllText(file, created);

                return created;
            }
            catch (Exception ex)
            {
                AppLog.Write("jellyfin.log", $"device id unavailable, using a temporary one: {ex.Message}");
                return Guid.NewGuid().ToString("N");
            }
        }

        private static bool IsUsable(string value) =>
            value.Length is > 0 and <= 64 && Guid.TryParse(value, out _);
    }
}
