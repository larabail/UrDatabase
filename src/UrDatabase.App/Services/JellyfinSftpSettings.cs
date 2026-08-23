using System;
using System.Globalization;
using System.Text.Json.Serialization;

namespace UrDatabase.Services
{
    /// <summary>
    /// How to put a file on the Jellyfin server's disk, if there is a way in at all.
    ///
    /// Jellyfin has no endpoint that accepts a video file. Its API will take an image or a
    /// subtitle and nothing else; a film only becomes a film by already being on the server's
    /// filesystem when the library is scanned. So uploading means transferring the file by some
    /// other protocol and then asking Jellyfin to look again, and the protocol a media server box
    /// tends to offer is SFTP.
    ///
    /// Every field is empty by default and a blank <see cref="Host"/> switches the whole feature
    /// off, so an install that has never heard of any of this behaves exactly as it did before:
    /// no button, no connection, no error. Nothing here has a default that points at a real
    /// machine, and the private key itself is never held in configuration — only the path to it.
    /// </summary>
    public sealed class JellyfinSftpSettings
    {
        /// <summary>
        /// The port an SSH server listens on when nobody says otherwise. A dedicated upload
        /// account often lives somewhere else, which is why the setting exists at all.
        /// </summary>
        public const int DefaultPort = 22;

        /// <summary>
        /// Where films go, relative to whatever the account lands in. The default suits the usual
        /// arrangement — an SFTP account chrooted so that its root holds one directory per
        /// library — and it is deliberately relative: inside a chroot the server's own
        /// <c>/tank/movies</c> is reachable as <c>movies</c> and not by its real path.
        /// </summary>
        public const string DefaultMoviesPath = "movies";

        /// <summary>Hostname or address of the machine running Jellyfin, not of Jellyfin itself.</summary>
        public string Host { get; set; } = "";

        /// <summary>
        /// SSH port. Left at zero — which is what an absent setting means — the environment is
        /// consulted, then a port carried by <see cref="Host"/>, and finally
        /// <see cref="DefaultPort"/>. Anything outside the range a TCP port can take is read the
        /// same way as zero.
        /// </summary>
        public int Port { get; set; }

        /// <summary>The SSH account to sign in as. Rarely the same name as the Jellyfin user.</summary>
        public string Username { get; set; } = "";

        /// <summary>
        /// Path to the private key half of an SSH key pair, for example
        /// <c>~/.ssh/id_ed25519</c>. Expanded like every other configured path, so a <c>~</c> or
        /// a <c>%USERPROFILE%</c> resolves on the platform it is read on.
        ///
        /// A key rather than a password because an upload account worth having is one that can do
        /// nothing else, and such accounts are usually configured key-only. There is no password
        /// field: adding one would invite a server password into a config file, and the file this
        /// points at stays where its owner put it.
        /// </summary>
        public string PrivateKeyPath { get; set; } = "";

        /// <summary>
        /// The passphrase protecting that key, when it has one. Optional, and left blank for the
        /// ordinary case of a key generated for one purpose with no passphrase on it.
        /// </summary>
        public string PrivateKeyPassphrase { get; set; } = "";

        /// <summary>
        /// The directory films are written into, as the SFTP account sees it. Relative to the
        /// account's landing directory unless it begins with a slash. Blank means
        /// <see cref="DefaultMoviesPath"/>.
        /// </summary>
        public string MoviesPath { get; set; } = "";

        /// <summary>
        /// True once there is a machine to reach, an account to reach it as, and a key to prove
        /// it with. All three or nothing: a partial configuration cannot connect, and offering an
        /// upload that is certain to fail is worse than not offering one.
        /// </summary>
        [JsonIgnore]
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Host) &&
            !string.IsNullOrWhiteSpace(Username) &&
            !string.IsNullOrWhiteSpace(PrivateKeyPath);

        /// <summary>
        /// Fills blanks from the environment and puts every field into the one shape the rest of
        /// the code expects. Safe to call twice: it is written so that normalising an already
        /// normalised instance changes nothing.
        /// </summary>
        public void Normalize()
        {
            // Read before the host is picked apart, so that a username typed into the host is
            // only adopted when no other source supplied one.
            Username = FirstNonEmpty(Username, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinSftpUsernameVariable));

            var host = NormalizeHost(FirstNonEmpty(Host, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinSftpHostVariable)));
            Host = host.Host;

            if (string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(host.Username))
                Username = host.Username;

            // A dedicated setting outranks a port carried along by the host, which in turn
            // outranks the environment: each is more specific about this server than the last.
            Port = FirstPort(
                IsPort(Port) ? Port : null,
                host.Port,
                ParsePort(Environment.GetEnvironmentVariable(PlatformPaths.JellyfinSftpPortVariable)));

            // Expanded here rather than at the point of use so that everything downstream — the
            // reader, the log line, the "no key at that path" message — is talking about the same
            // file the user meant.
            PrivateKeyPath = PlatformPaths.Expand(
                FirstNonEmpty(PrivateKeyPath, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinSftpKeyVariable)));

            // Not trimmed: leading and trailing spaces are legal in a passphrase, and silently
            // removing one would produce an authentication failure nobody could explain.
            PrivateKeyPassphrase = FirstNonEmpty(
                PrivateKeyPassphrase,
                Environment.GetEnvironmentVariable(PlatformPaths.JellyfinSftpPassphraseVariable),
                trim: false);

            MoviesPath = JellyfinUpload.NormalizeRemoteRoot(
                FirstNonEmpty(MoviesPath, Environment.GetEnvironmentVariable(PlatformPaths.JellyfinSftpMoviesPathVariable)));
        }

        /// <summary>
        /// Reads a host the way people write one. An address is copied out of an SSH command, a
        /// config file or a chat message, so it arrives as <c>sftp://box:2223</c>,
        /// <c>uploader@box</c> or <c>box:2223/</c> at least as often as it arrives bare.
        ///
        /// The port and username are pulled out rather than discarded, because a value that
        /// carries them was written by somebody who meant them, and connecting to a host literally
        /// named "uploader@box:2223" fails with a DNS error that explains nothing.
        /// </summary>
        internal static (string Host, int? Port, string Username) NormalizeHost(string? input)
        {
            var value = (input ?? "").Trim().Trim('"', '\'', '<', '>', '`').Trim();
            if (value.Length == 0) return ("", null, "");

            var scheme = value.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0) value = value[(scheme + 3)..];

            value = value.TrimEnd('/');

            var username = "";
            var at = value.LastIndexOf('@');
            if (at >= 0)
            {
                username = value[..at].Trim();
                value = value[(at + 1)..];
            }

            // A path after the host is not something this app can use, and keeping it would make
            // the hostname unresolvable.
            var slash = value.IndexOf('/');
            if (slash >= 0) value = value[..slash];

            int? port = null;

            // One colon only. Two or more means a bare IPv6 address, where every colon belongs to
            // the address itself; the bracketed form is handled below because there the port is
            // unambiguous.
            if (value.StartsWith('[') && value.Contains(']', StringComparison.Ordinal))
            {
                var close = value.IndexOf(']');
                var tail = value[(close + 1)..];
                if (tail.StartsWith(':') && TryPort(tail[1..], out var bracketed)) port = bracketed;
                value = value[1..close];
            }
            else if (value.IndexOf(':') >= 0 && value.IndexOf(':') == value.LastIndexOf(':'))
            {
                var colon = value.IndexOf(':');
                if (TryPort(value[(colon + 1)..], out var parsed))
                {
                    port = parsed;
                    value = value[..colon];
                }
            }

            return (value.Trim(), port, username);
        }

        /// <summary>
        /// The first of these that is a usable port, or <see cref="DefaultPort"/>. Anything
        /// outside the range a TCP port can take never reaches a connection attempt.
        /// </summary>
        internal static int FirstPort(params int?[] candidates)
        {
            foreach (var candidate in candidates)
                if (candidate is int value && IsPort(value)) return value;

            return DefaultPort;
        }

        private static int? ParsePort(string? text) =>
            int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && IsPort(value)
                ? value
                : null;

        private static bool TryPort(string? text, out int port)
        {
            port = ParsePort(text) ?? 0;
            return port != 0;
        }

        private static bool IsPort(int value) => value is > 0 and <= 65535;

        private static string FirstNonEmpty(string? primary, string? fallback, bool trim = true)
        {
            if (!string.IsNullOrWhiteSpace(primary)) return trim ? primary.Trim() : primary;
            if (!string.IsNullOrWhiteSpace(fallback)) return trim ? fallback.Trim() : fallback;
            return "";
        }
    }
}
