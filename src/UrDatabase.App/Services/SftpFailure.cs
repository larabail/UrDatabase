using System;
using System.IO;
using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace UrDatabase.Services
{
    /// <summary>
    /// Turns whatever SSH.NET threw into a sentence a person can act on.
    ///
    /// Kept pure, and kept out of <see cref="SshNetSftpTransport"/>, for the reason AGENTS.md
    /// gives: a message only reachable by making a real connection fail is a message nobody can
    /// test, and these are the messages that matter most. An upload fails on somebody's evening,
    /// against a server they set up months ago, and "Renci.SshNet.Common.SshAuthenticationException:
    /// Permission denied (publickey)." tells them nothing about which of the five things they
    /// configured is the wrong one.
    ///
    /// Each branch therefore names the specific thing to go and look at. Which failure it was is
    /// the whole value: a refused connection, a rejected key and a read-only directory send a
    /// person to three different places.
    /// </summary>
    public static class SftpFailure
    {
        /// <summary>
        /// The sentence for a failure while connecting. <paramref name="host"/> and
        /// <paramref name="port"/> are the user's own server and safe to show; the key path is
        /// theirs too, and naming it is usually what solves this.
        /// </summary>
        public static string Describe(Exception? error, string? host, int port, string? keyPath = null)
        {
            var where = Endpoint(host, port);
            var key = string.IsNullOrWhiteSpace(keyPath) ? "the configured private key" : keyPath.Trim();

            return error switch
            {
                SshPassPhraseNullOrEmptyException =>
                    $"{key} is protected by a passphrase. Put it in JellyfinSftp.PrivateKeyPassphrase, " +
                    "or use a key with no passphrase.",

                SshAuthenticationException =>
                    $"{where} refused the key in {key}. Check that the username is right and that the " +
                    "matching public key is in that account's authorized_keys on the server.",

                SftpPermissionDeniedException =>
                    $"That account is not allowed to write there. Check what {where} lets it into, " +
                    "and that JellyfinSftp.MoviesPath names a directory inside that.",

                SftpPathNotFoundException =>
                    $"That path does not exist on {where}. Check JellyfinSftp.MoviesPath — inside a " +
                    "chrooted upload account it is relative to what that account lands in, not the " +
                    "server's own path to its library.",

                SshOperationTimeoutException =>
                    $"{where} did not answer in time. It may be asleep, or on a network this machine " +
                    "cannot currently reach.",

                SshConnectionException =>
                    $"The connection to {where} dropped. Nothing was added to your film library — " +
                    "starting again uploads the film from the beginning.",

                ProxyException =>
                    $"The proxy in front of {where} refused the connection.",

                SocketException socket => DescribeSocket(socket, where),

                // Everything SSH.NET does not map to a type of its own arrives as the base
                // SftpException, including the one the server sends when a write fails because
                // its disk is full. Without this branch that lands in the key-file case below and
                // tells somebody their SSH key is wrong ninety minutes into a film.
                SftpException sftp => DescribeSftp(sftp, where),

                FileNotFoundException or DirectoryNotFoundException =>
                    $"There is no private key at {key}. Point JellyfinSftp.PrivateKeyPath at the " +
                    "private half of the key pair — the file without the .pub.",

                UnauthorizedAccessException =>
                    $"This app is not allowed to read {key}.",

                // SSH.NET reports an unreadable or unsupported key file this way, and it is by
                // far the likeliest reason for one: the .pub was configured instead of the key.
                SshException or FormatException or InvalidOperationException =>
                    $"Could not use {key} to sign in to {where}. Check it is an unencrypted OpenSSH " +
                    "private key and not the .pub half of the pair.",

                IOException =>
                    $"The transfer to {where} failed part way through. Nothing was left under the " +
                    "film's own name on the server.",

                null => $"Could not reach {where}.",

                _ => $"Could not reach {where}: {error.Message}"
            };
        }

        /// <summary>
        /// A failure the server reported about the file itself rather than about the connection.
        ///
        /// SFTP version 3, which is what OpenSSH speaks, has only a handful of status codes and
        /// no code for "the disk is full" — that, a read-only filesystem, a quota, and a rename
        /// onto a name that already exists all come back as the same <c>Failure</c>. So the
        /// message for it names the likely causes rather than asserting one, which is still far
        /// more use than the key-file advice this used to fall through to.
        /// </summary>
        private static string DescribeSftp(SftpException error, string where) => error.StatusCode switch
        {
            StatusCode.NoSuchFile =>
                $"That path does not exist on {where}. Check JellyfinSftp.MoviesPath.",

            StatusCode.PermissionDenied =>
                $"That account is not allowed to write there. Check what {where} lets it into, " +
                "and that JellyfinSftp.MoviesPath names a directory inside that.",

            StatusCode.OperationUnsupported =>
                $"The SFTP service on {where} refused an operation this app needs. It may be a " +
                "restricted or non-OpenSSH server.",

            StatusCode.NoConnection or StatusCode.ConnectionLost =>
                $"The connection to {where} dropped. Nothing was added to your film library — " +
                "starting again uploads the film from the beginning.",

            _ =>
                $"{where} would not finish writing the film. The commonest causes are the server's " +
                "disk being full and the library directory being read-only for that account."
        };

        private static string DescribeSocket(SocketException error, string where) => error.SocketErrorCode switch        {
            SocketError.HostNotFound or SocketError.NoData =>
                $"No machine called {where} could be found. Check the address in JellyfinSftp.Host.",

            SocketError.ConnectionRefused =>
                $"{where} refused the connection. Check the port — an upload account often listens " +
                "somewhere other than 22.",

            SocketError.TimedOut =>
                $"{where} did not answer in time. It may be asleep, or on a network this machine " +
                "cannot currently reach.",

            SocketError.NetworkUnreachable or SocketError.HostUnreachable =>
                $"{where} cannot be reached from this network. It is usually only reachable from home.",

            _ => $"Could not reach {where}."
        };

        /// <summary>
        /// The server as a person would write it. The port is included only when it is not the
        /// default, because "box:22" reads like a detail that matters and it does not.
        /// </summary>
        public static string Endpoint(string? host, int port)
        {
            var name = (host ?? "").Trim();
            if (name.Length == 0) return "the SFTP server";

            return port is JellyfinSftpSettings.DefaultPort or 0 ? name : $"{name}:{port}";
        }
    }
}
