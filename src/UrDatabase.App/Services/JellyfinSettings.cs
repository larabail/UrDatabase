using System;
using System.Text.Json.Serialization;

namespace UrDatabase.Services
{
    /// <summary>
    /// How to reach a Jellyfin server, if there is one. Every field is empty by default and an
    /// empty <see cref="ServerUrl"/> switches the whole feature off, so an install that has never
    /// heard of Jellyfin behaves exactly as it did before: no requests, no panels, no errors.
    ///
    /// The values live in the gitignored <c>appsettings.json</c>, or in environment variables for
    /// anyone who would rather not write a password to a file. Nothing here has a default that
    /// points at a real server.
    /// </summary>
    public sealed class JellyfinSettings
    {
        /// <summary>Base address of the server, for example <c>http://media.example:8096</c>.</summary>
        public string ServerUrl { get; set; } = "";

        /// <summary>Account to sign in as. Preferred over <see cref="ApiKey"/>.</summary>
        public string Username { get; set; } = "";

        public string Password { get; set; } = "";

        /// <summary>
        /// A server-scoped Jellyfin API key, used when no username is given. Jellyfin has no
        /// narrower kind of key — every one of them is administrative — which is why a user
        /// account is the default and this is the alternative rather than the other way round.
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// Which movie library to read, when a server has more than one. Blank means the first
        /// library whose collection type is <c>movies</c>. A library is never identified by id:
        /// ids differ per server and would have to be discovered by hand.
        /// </summary>
        public string LibraryName { get; set; } = "";

        /// <summary>True once there is somewhere to connect to and something to connect with.</summary>
        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(ServerUrl) &&
            (!string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(ApiKey));

        /// <summary>
        /// Which of the two sign-ins to use. A username wins whenever it could work, because a
        /// user token is scoped to one account and every Jellyfin API key is administrative. A key
        /// alongside a username, with no password, is read as "authenticate with the key but read
        /// the library as this user" — which is the only reason to configure both.
        /// </summary>
        [JsonIgnore]
        public bool UsesUserAccount =>
            !string.IsNullOrWhiteSpace(Username) &&
            (string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(Password));

        /// <summary>
        /// Fills blanks from the environment and puts <see cref="ServerUrl"/> into the one shape
        /// the rest of the code expects: an absolute URL with no trailing slash. A host typed
        /// without a scheme gets <c>http://</c>, because a Jellyfin box on a home network
        /// usually has no certificate and "media-box:8096" is what a person types.
        /// </summary>
        public void Normalize()
        {
            ServerUrl = NormalizeServerUrl(FirstNonEmpty(ServerUrl, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinUrlVariable)));
            Username = FirstNonEmpty(Username, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinUsernameVariable));
            Password = FirstNonEmpty(Password, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinPasswordVariable), trim: false);
            ApiKey = FirstNonEmpty(ApiKey, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinApiKeyVariable));
            LibraryName = (LibraryName ?? "").Trim();
        }

        /// <summary>
        /// Trims, supplies a missing scheme and drops any trailing slash. Returns an empty string
        /// for anything that cannot be made into an absolute HTTP URL, which switches the feature
        /// off rather than failing later with a URI parse error the user cannot act on.
        ///
        /// What it deliberately leaves alone is the port and the path. A server behind a reverse
        /// proxy is reached on port 80 at a hostname, or under a path prefix, and "tidying" either
        /// away turns a working address into a 404 the user has no way to explain.
        /// </summary>
        public static string NormalizeServerUrl(string? input)
        {
            // People paste addresses out of a browser, an email or a chat window, which is where
            // the quotes and angle brackets come from.
            var value = (input ?? "").Trim().Trim('"', '\'', '<', '>', '`').Trim();
            if (value.Length == 0) return "";

            // A protocol-relative address is what a copied link sometimes leaves behind.
            if (value.StartsWith("//", StringComparison.Ordinal)) value = "http:" + value;

            if (!value.Contains("://", StringComparison.Ordinal)) value = "http://" + value;

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "";
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return "";
            if (string.IsNullOrEmpty(uri.Host)) return "";

            // Lowercase the scheme and nothing else. Rebuilding from Uri would drop an explicitly
            // typed default port — http://host:80 becomes http://host — and a user who wrote a
            // port wrote it for a reason.
            var separator = value.IndexOf("://", StringComparison.Ordinal);
            return (uri.Scheme + value[separator..]).TrimEnd('/');
        }

        private static string FirstNonEmpty(string? primary, string? fallback, bool trim = true)
        {
            if (!string.IsNullOrWhiteSpace(primary)) return trim ? primary.Trim() : primary;
            if (!string.IsNullOrWhiteSpace(fallback)) return trim ? fallback.Trim() : fallback;
            return "";
        }
    }
}
