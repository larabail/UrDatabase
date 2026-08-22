using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace UrDatabase.Services
{
    /// <summary>
    /// What happened when the app tried to reach the configured server. These are separate cases
    /// because they have separate remedies: a name that will not resolve is fixed by typing an
    /// address, a refused connection by starting Jellyfin or correcting the port, and a 404 by
    /// going round the reverse proxy. One message covering all of them helps with none of them.
    /// </summary>
    public enum JellyfinConnectionState
    {
        /// <summary>No server address is set, so nothing was attempted.</summary>
        NotConfigured,

        /// <summary>A Jellyfin server answered.</summary>
        Reachable,

        /// <summary>The hostname could not be turned into an address on this machine.</summary>
        NameNotResolved,

        /// <summary>The machine is there and said no: nothing is listening on that port.</summary>
        ConnectionRefused,

        /// <summary>Neither refused nor answered — the connection was dropped or is being filtered.</summary>
        TimedOut,

        /// <summary>Something answered, but it is not Jellyfin. The reverse-proxy-by-IP case.</summary>
        NotJellyfin,

        /// <summary>Jellyfin answered and rejected the credentials.</summary>
        Unauthorized,

        /// <summary>A transport failure with no more specific cause, or a status nothing can use.</summary>
        Unreachable
    }

    /// <summary>
    /// The outcome of one connection test, with the sentence to show for it. Holds only the
    /// address the user configured and what the server said about itself — never a token, a
    /// password or an API key — so it is safe to write to the log and to put in a dialog.
    /// </summary>
    public sealed class JellyfinConnectionReport
    {
        public JellyfinConnectionState State { get; init; } = JellyfinConnectionState.Unreachable;

        public string Message { get; init; } = "";

        /// <summary>The HTTP status, when the server got far enough to send one.</summary>
        public int? StatusCode { get; init; }

        /// <summary>The server's own name, when it identified itself.</summary>
        public string? ServerName { get; init; }

        /// <summary>The Jellyfin version, when it identified itself.</summary>
        public string? Version { get; init; }

        public bool IsReachable => State == JellyfinConnectionState.Reachable;

        /// <summary>One line for the log.</summary>
        public override string ToString() =>
            StatusCode is null ? $"{State}: {Message}" : $"{State} (HTTP {StatusCode}): {Message}";
    }

    /// <summary>
    /// Turns a transport failure or an HTTP status into a named cause and a sentence that says
    /// what to do about it.
    ///
    /// This exists because of a real evening lost to it: a Tailscale hostname the shell could
    /// resolve and the app could not, and the same server on port 80 behind a reverse proxy that
    /// answered every request with a 404 because it routes by hostname. Both produced "could not
    /// reach the Jellyfin server", which was true of neither — the server was up throughout and
    /// the address worked in a browser.
    /// </summary>
    public static class JellyfinDiagnostics
    {
        /// <summary>The port Jellyfin listens on itself, before anything is put in front of it.</summary>
        public const int DefaultPort = 8096;

        /// <summary>The endpoint that answers without credentials, which is what a test needs.</summary>
        public const string PublicInfoPath = "System/Info/Public";

        /// <summary>
        /// Names the cause of a failed request. Walks the whole exception chain, most specific
        /// evidence first: a socket error, then .NET 8's <see cref="HttpRequestError"/>, then the
        /// message text — because a handler in a test, or a platform that words things its own
        /// way, may only give us the last of those.
        /// </summary>
        public static JellyfinConnectionState Classify(Exception? exception)
        {
            if (exception is null) return JellyfinConnectionState.Unreachable;

            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is SocketException socket) return FromSocketError(socket.SocketErrorCode);
            }

            for (var current = exception; current is not null; current = current.InnerException)
            {
                if (current is HttpRequestException http)
                {
                    var state = FromHttpRequestError(http.HttpRequestError);
                    if (state is not null) return state.Value;
                }

                if (current is TimeoutException) return JellyfinConnectionState.TimedOut;
            }

            // Every message in the chain, because the useful wording is often on the inner one.
            for (var current = exception; current is not null; current = current.InnerException)
            {
                var state = FromMessage(current.Message);
                if (state is not null) return state.Value;
            }

            return JellyfinConnectionState.Unreachable;
        }

        private static JellyfinConnectionState FromSocketError(SocketError error) => error switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain or SocketError.AddressFamilyNotSupported
                => JellyfinConnectionState.NameNotResolved,
            SocketError.ConnectionRefused => JellyfinConnectionState.ConnectionRefused,
            SocketError.TimedOut => JellyfinConnectionState.TimedOut,
            _ => JellyfinConnectionState.Unreachable
        };

        private static JellyfinConnectionState? FromHttpRequestError(HttpRequestError error) => error switch
        {
            HttpRequestError.NameResolutionError => JellyfinConnectionState.NameNotResolved,
            // ConnectionError covers refused, unreachable and reset alike, so it is never a verdict
            // on its own: a socket error decides it above, or the wording decides it below.
            _ => null
        };

        /// <summary>
        /// Last resort. macOS says "nodename nor servname provided, or not known", Linux says
        /// "Name or service not known" and Windows says "No such host is known" for the same
        /// thing, and none of them is a socket error by the time it reaches a caller here.
        /// Returns null when the wording says nothing either way.
        /// </summary>
        private static JellyfinConnectionState? FromMessage(string? message)
        {
            var text = message ?? "";

            if (Mentions(text, "nodename nor servname", "no such host", "name or service not known",
                    "could not resolve", "name resolution", "unknown host"))
                return JellyfinConnectionState.NameNotResolved;

            if (Mentions(text, "connection refused", "actively refused", "refused the connection"))
                return JellyfinConnectionState.ConnectionRefused;

            if (Mentions(text, "timed out", "timeout"))
                return JellyfinConnectionState.TimedOut;

            return null;
        }

        private static bool Mentions(string text, params string[] fragments)
        {
            foreach (var fragment in fragments)
                if (text.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>
        /// Reads an HTTP status as one of these states. Anything from this client's own narrow set
        /// of endpoints coming back 404 means the address is answering but is not Jellyfin: every
        /// path it asks for exists on every Jellyfin server.
        /// </summary>
        public static JellyfinConnectionState FromStatusCode(HttpStatusCode status) => status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => JellyfinConnectionState.Unauthorized,
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                => JellyfinConnectionState.NotJellyfin,
            _ when (int)status >= 200 && (int)status < 300 => JellyfinConnectionState.Reachable,
            _ => JellyfinConnectionState.Unreachable
        };

        /// <summary>
        /// The sentence a person sees. Names the address, says which of the five things went
        /// wrong, and suggests the specific next thing to try.
        /// </summary>
        public static string Describe(JellyfinConnectionState state, string? serverUrl, int? statusCode = null)
        {
            var address = Display(serverUrl);
            var host = HostOf(serverUrl);
            var status = statusCode is null ? "" : $" (HTTP {statusCode})";

            return state switch
            {
                JellyfinConnectionState.NotConfigured =>
                    "No Jellyfin server is configured.",

                JellyfinConnectionState.Reachable =>
                    $"Reached Jellyfin at {address}.",

                JellyfinConnectionState.NameNotResolved =>
                    $"The name \"{host}\" could not be resolved on this machine, so nothing was contacted. " +
                    "That is a name lookup failing, not a server that is down — a Tailscale, VPN or " +
                    "router-local name that works in a browser is not always visible to this app. " +
                    $"Try the server's IP address instead, for example http://192.168.1.10:{DefaultPort}.",

                JellyfinConnectionState.ConnectionRefused =>
                    $"{address} refused the connection. The name resolved and the machine answered, so " +
                    $"check that Jellyfin is running there and that the port is right — Jellyfin's own " +
                    $"port is {DefaultPort}.",

                JellyfinConnectionState.TimedOut =>
                    $"{address} did not answer in time. The connection was neither refused nor completed, " +
                    "which usually means a firewall is dropping it or that network is not reachable from here.",

                JellyfinConnectionState.NotJellyfin =>
                    $"Something is listening at {address}, but it did not answer /{PublicInfoPath}{status}, " +
                    "so it does not look like Jellyfin. A reverse proxy routes by hostname, so an address " +
                    $"that reaches the proxy can land on the wrong site. {TryDirectPort(serverUrl)}",

                JellyfinConnectionState.Unauthorized =>
                    $"Jellyfin at {address} rejected the credentials{status}. Check the username and " +
                    "password, or the API key, in the configuration file.",

                _ when statusCode is not null =>
                    $"{address} answered with HTTP {statusCode}, which is not something this app can use. " +
                    "If a reverse proxy sits in front of Jellyfin, the proxy may be up while Jellyfin is not.",

                _ => $"Could not reach the Jellyfin server at {address}."
            };
        }

        /// <summary>
        /// The advice for the reverse proxy case: go straight at Jellyfin's own port. Pointless
        /// when that is already the configured port, so it says something else instead.
        /// </summary>
        private static string TryDirectPort(string? serverUrl)
        {
            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && uri.Port != DefaultPort)
                return $"Try Jellyfin's own port directly, for example http://{uri.Host}:{DefaultPort}.";

            return "Check that this address really is the Jellyfin server.";
        }

        /// <summary>
        /// The address as configured, minus anything secret. A URL may carry
        /// <c>user:password@</c>, and these strings go into dialogs, the status line and the log —
        /// so the credential is stripped here, at the one place that renders an address for a
        /// person, rather than relied upon not to be there.
        /// </summary>
        internal static string Display(string? serverUrl)
        {
            var value = (serverUrl ?? "").Trim();
            if (value.Length == 0) return "the configured address";

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo))
                return value;

            // Uri.Authority excludes the user information by construction. Rebuilding drops an
            // explicitly written default port, which is a fair price in this one rare case.
            var path = uri.AbsolutePath.TrimEnd('/');
            return $"{uri.Scheme}://{uri.Authority}{path}";
        }

        /// <summary>Just the hostname, for a message about resolving a name.</summary>
        internal static string HostOf(string? serverUrl)
        {
            if (string.IsNullOrWhiteSpace(serverUrl)) return "the configured address";
            return Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
                ? uri.Host
                : serverUrl.Trim();
        }
    }
}
