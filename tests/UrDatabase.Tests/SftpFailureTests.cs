using System;
using System.IO;
using System.Net.Sockets;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// The sentences somebody meets when an upload will not start.
    ///
    /// These are the messages that matter most and the ones a live test could never produce
    /// reliably: an upload fails on somebody's evening, against a server they configured months
    /// ago, and it has to say which of the five things they configured is the wrong one. Keeping
    /// the mapping pure is what makes that assertable at all — see AGENTS.md on logic reachable
    /// only from a window, which applies just as much to logic reachable only from a socket.
    /// </summary>
    public class SftpFailureTests
    {
        private const string Host = "media.invalid";
        private const string Key = "/keys/id_ed25519";

        [Fact]
        public void A_rejected_key_says_which_key_and_where_to_put_the_public_half()
        {
            var message = SftpFailure.Describe(
                new SshAuthenticationException("Permission denied (publickey)."), Host, 2223, Key);

            Assert.Contains(Key, message, StringComparison.Ordinal);
            Assert.Contains("authorized_keys", message, StringComparison.Ordinal);
            Assert.Contains("media.invalid:2223", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The commonest mistake of all: pointing the setting at the <c>.pub</c>, which is the
        /// half that is supposed to be shared and the half that cannot sign anything.
        /// </summary>
        [Fact]
        public void An_unreadable_key_says_it_may_be_the_public_half()
        {
            var message = SftpFailure.Describe(new SshException("invalid private key file"), Host, 22, Key + ".pub");

            Assert.Contains(".pub", message, StringComparison.Ordinal);
            Assert.Contains("private key", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_key_that_is_not_there_names_the_path_that_was_looked_at()
        {
            var message = SftpFailure.Describe(new FileNotFoundException("no such file"), Host, 22, Key);

            Assert.Contains(Key, message, StringComparison.Ordinal);
            Assert.Contains("PrivateKeyPath", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_key_needing_a_passphrase_names_the_setting_that_holds_one()
        {
            var message = SftpFailure.Describe(new SshPassPhraseNullOrEmptyException("passphrase"), Host, 22, Key);

            Assert.Contains("PrivateKeyPassphrase", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The port is the thing to check here, because an account set up only for uploads is
        /// routinely put somewhere other than 22.
        /// </summary>
        [Fact]
        public void A_refused_connection_points_at_the_port()
        {
            var message = SftpFailure.Describe(new SocketException((int)SocketError.ConnectionRefused), Host, 2223, Key);

            Assert.Contains("port", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("media.invalid:2223", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_name_that_will_not_resolve_points_at_the_address_setting()
        {
            var message = SftpFailure.Describe(new SocketException((int)SocketError.HostNotFound), Host, 22, Key);

            Assert.Contains("JellyfinSftp.Host", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_network_that_cannot_reach_the_server_says_so_rather_than_blaming_the_key()
        {
            var message = SftpFailure.Describe(new SocketException((int)SocketError.NetworkUnreachable), Host, 22, Key);

            Assert.Contains("network", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Key, message, StringComparison.Ordinal);
        }

        /// <summary>
        /// The chroot trap, and the likeliest thing to get wrong when setting this up: inside a
        /// chrooted account the server's own <c>/tank/movies</c> is reached as <c>movies</c>.
        /// </summary>
        [Fact]
        public void A_missing_directory_explains_that_the_path_is_the_accounts_own()
        {
            var message = SftpFailure.Describe(new SftpPathNotFoundException("no such path"), Host, 22, Key);

            Assert.Contains("MoviesPath", message, StringComparison.Ordinal);
            Assert.Contains("chroot", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_directory_that_cannot_be_written_to_says_which_setting_names_it()
        {
            var message = SftpFailure.Describe(new SftpPermissionDeniedException("denied"), Host, 22, Key);

            Assert.Contains("MoviesPath", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_dropped_connection_does_not_promise_more_than_it_can_keep()
        {
            var message = SftpFailure.Describe(new SshConnectionException("lost"), Host, 22, Key);

            Assert.Contains("dropped", message, StringComparison.OrdinalIgnoreCase);

            // Removing the partial file needs the connection that just died, so the promise is
            // about the library — which the rename never reached — and not about the disk.
            Assert.Contains("Nothing was added to your film library", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// SFTP version 3, which is what OpenSSH speaks, has no status code for a full disk: it
        /// comes back as the generic <c>Failure</c>, on the base <see cref="SftpException"/> type.
        /// Without a branch of its own that lands in the key-file case, and tells somebody their
        /// SSH key is wrong an hour and a half into a film.
        /// </summary>
        [Fact]
        public void A_write_the_server_refuses_does_not_blame_the_key()
        {
            var message = SftpFailure.Describe(new SftpException(StatusCode.Failure, "failure"), Host, 22, Key);

            Assert.DoesNotContain(Key, message, StringComparison.Ordinal);
            Assert.DoesNotContain(".pub", message, StringComparison.Ordinal);
            Assert.Contains("disk being full", message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void A_server_that_refuses_the_write_outright_names_the_account_not_the_key()
        {
            var message = SftpFailure.Describe(new SftpException(StatusCode.PermissionDenied, "denied"), Host, 22, Key);

            Assert.Contains("MoviesPath", message, StringComparison.Ordinal);
            Assert.DoesNotContain(".pub", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Whatever it was, it must read as a sentence rather than as a type name. This is the
        /// property the whole class exists for.
        /// </summary>
        [Theory]
        [InlineData(typeof(SshAuthenticationException))]
        [InlineData(typeof(SshConnectionException))]
        [InlineData(typeof(SshOperationTimeoutException))]
        [InlineData(typeof(SshException))]
        public void No_message_ever_shows_an_exception_type(Type type)
        {
            var error = (Exception)Activator.CreateInstance(type, "raw detail")!;
            var message = SftpFailure.Describe(error, Host, 22, Key);

            Assert.DoesNotContain("Exception", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Renci", message, StringComparison.Ordinal);
            Assert.EndsWith(".", message.Trim(), StringComparison.Ordinal);
        }

        [Fact]
        public void An_unconfigured_server_is_still_described_without_a_blank_where_its_name_goes()
        {
            Assert.Equal("the SFTP server", SftpFailure.Endpoint("", 22));
            Assert.Equal("the SFTP server", SftpFailure.Endpoint(null, 2223));
            Assert.Contains("the SFTP server", SftpFailure.Describe(null, null, 22), StringComparison.Ordinal);
        }

        /// <summary>
        /// A port worth mentioning is one that is not the default; "box:22" reads like a detail
        /// that matters and it does not.
        /// </summary>
        [Fact]
        public void The_default_port_is_not_mentioned()
        {
            Assert.Equal("media.invalid", SftpFailure.Endpoint(Host, 22));
            Assert.Equal("media.invalid:2223", SftpFailure.Endpoint(Host, 2223));
        }
    }
}
