using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace UrDatabase.Services
{
    /// <summary>
    /// The loopback control interface one VLC launch is given: a port nobody else is on, and a
    /// password nobody can guess.
    /// </summary>
    /// <remarks>
    /// Both are generated fresh for every launch, and that is a security requirement rather than
    /// tidiness. VLC takes its HTTP password as a command line argument, and a process's command
    /// line is readable by every user on the machine — <c>ps</c> on macOS and Linux, the process
    /// list on Windows. A fixed password would therefore be a standing local vulnerability: anyone
    /// with an account on the machine could drive the viewer's player, and, through VLC's own
    /// playlist commands, ask it to open files. A fresh 256-bit secret is worth nothing the moment
    /// the film ends.
    ///
    /// The interface binds to 127.0.0.1 and nothing else. Bound to any address it would be a
    /// remote control for anybody on the same network as the viewer.
    /// </remarks>
    public sealed record VlcControlEndpoint(int Port, string Password)
    {
        /// <summary>The only address this interface may ever listen on.</summary>
        public const string Host = "127.0.0.1";

        /// <summary>
        /// VLC's own account name for the HTTP interface, which is no name at all: it authenticates
        /// with an empty username and the password below.
        /// </summary>
        public const string Username = "";

        /// <summary>Where the status document is. Safe to log; carries no secret.</summary>
        public Uri StatusUri =>
            new($"http://{Host}:{Port.ToString(CultureInfo.InvariantCulture)}/requests/status.xml");
    }

    /// <summary>
    /// Everything about driving VLC's HTTP control interface that can be decided without opening a
    /// socket: the arguments, the secret, and what may be written to a log.
    /// </summary>
    public static class VlcControl
    {
        /// <summary>
        /// Bytes of entropy in the per-launch password. 32 is more than the situation needs and
        /// costs nothing; the interface only has to survive the length of one film.
        /// </summary>
        public const int PasswordBytes = 32;

        /// <summary>
        /// A password for one launch, from the cryptographic generator rather than
        /// <see cref="Random"/>.
        /// </summary>
        /// <remarks>
        /// Hexadecimal, so it is unambiguous to every layer it passes through: a command line, an
        /// HTTP Basic credential and VLC's own argument parsing. A base64 secret would be shorter
        /// and would eventually produce a <c>+</c>, a <c>/</c> or a <c>=</c> in one of those.
        /// </remarks>
        public static string NewPassword() =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(PasswordBytes)).ToLowerInvariant();

        /// <summary>
        /// A loopback port the operating system has just confirmed is free.
        /// </summary>
        /// <remarks>
        /// Asking for port 0 and reading back what was allocated is the only way to be told a free
        /// port rather than to assume one. There is a race in it — the listener is closed before
        /// VLC binds, so something else could take the port in between — and it is deliberately
        /// left: losing it means VLC cannot start its interface, the status never answers, and
        /// nothing is reported for that film. The alternative, holding the port open until VLC
        /// wants it, would mean VLC could never bind it at all.
        ///
        /// The one piece of this feature that touches a socket, kept to four lines for that reason.
        /// </remarks>
        public static int FindFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            try
            {
                return ((IPEndPoint)listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// An endpoint for one launch, or null when a port could not be found. Null is an ordinary
        /// answer: it means this film plays without reporting, not that it does not play.
        /// </summary>
        public static VlcControlEndpoint? TryCreate()
        {
            try
            {
                return new VlcControlEndpoint(FindFreePort(), NewPassword());
            }
            catch (SocketException)
            {
                return null;
            }
        }

        /// <summary>
        /// The arguments that add the control interface to a normal VLC launch.
        /// </summary>
        /// <remarks>
        /// <c>--extraintf</c>, never <c>--intf</c>. The latter replaces VLC's main interface, and
        /// with <c>dummy</c> it is how this endpoint was verified from a terminal — but the person
        /// pressing Play wants to watch the film, and a player with no window is a player that has
        /// silently failed. <c>--extraintf</c> adds the control interface beside the real one.
        /// </remarks>
        public static IReadOnlyList<string> BuildArguments(VlcControlEndpoint endpoint)
        {
            if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));

            return new[]
            {
                "--extraintf", "http",
                "--http-host", VlcControlEndpoint.Host,
                "--http-port", endpoint.Port.ToString(CultureInfo.InvariantCulture),
                "--http-password", endpoint.Password
            };
        }

        /// <summary>
        /// What a launch may say in a log. The port, because it is the useful half when this does
        /// not work; never the password, which is the whole security of the interface.
        /// </summary>
        public static string Describe(string playerName, VlcControlEndpoint? endpoint) =>
            endpoint is null
                ? $"streaming through {playerName}"
                : $"streaming through {playerName}, reporting progress via its control interface on port " +
                  endpoint.Port.ToString(CultureInfo.InvariantCulture);
    }
}
