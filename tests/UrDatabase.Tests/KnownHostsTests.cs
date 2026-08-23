using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UrDatabase.Services;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Whether the machine an upload is about to go to is the machine it went to last time.
    ///
    /// Without this check SSH.NET accepts whatever host key it is handed, which is weaker than the
    /// <c>sftp</c> command this feature replaces — that one reads <c>known_hosts</c> and hard-fails
    /// on a mismatch. None of it can be tested through a socket, and the file format has more edges
    /// than it looks: the bracketed <c>[host]:port</c> form, comma-separated patterns, markers that
    /// change what a line means, and hashed entries whose hostname cannot be read back out.
    ///
    /// Every fixture here is written to a temporary directory. Nothing reads the real
    /// <c>~/.ssh/known_hosts</c>, and no key below belongs to a real machine.
    /// </summary>
    public class KnownHostsTests : IDisposable
    {
        private const string Host = "192.0.2.20";
        private const int Port = 2222;

        /// <summary>An ed25519-shaped blob. Invented, and never sent anywhere.</summary>
        private static readonly byte[] ServerKey = Blob(1);

        /// <summary>What something else answering on that address would offer.</summary>
        private static readonly byte[] ImpostorKey = Blob(2);

        private readonly string _dir;

        public KnownHostsTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "urdb-kh-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static byte[] Blob(byte seed)
        {
            var bytes = new byte[51];
            for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed * 31 + i);
            return bytes;
        }

        private static string Base64(byte[] key) => Convert.ToBase64String(key);

        private static string Line(string hosts, byte[] key, string type = "ssh-ed25519", string marker = "") =>
            (marker.Length > 0 ? marker + " " : "") + $"{hosts} {type} {Base64(key)} comment@somewhere";

        private string WriteFile(params string[] lines)
        {
            var path = Path.Combine(_dir, "known_hosts");
            File.WriteAllLines(path, lines);
            return path;
        }

        private static HostKeyCheck Check(string line, byte[] offered) =>
            KnownHosts.Check(new[] { line }, Host, Port, "ssh-ed25519", offered);

        // ---------- the name a host is recorded under ----------

        /// <summary>
        /// Getting this wrong turns every lookup into "unknown host" rather than into an error,
        /// which would refuse every upload with the wrong explanation.
        /// </summary>
        [Theory]
        [InlineData("media.invalid", 22, "media.invalid")]
        [InlineData("media.invalid", 0, "media.invalid")]
        [InlineData("media.invalid", 2222, "[media.invalid]:2222")]
        [InlineData("192.0.2.20", 2222, "[192.0.2.20]:2222")]
        public void A_non_default_port_is_bracketed_the_way_openssh_writes_it(string host, int port, string expected)
        {
            Assert.Equal(expected, KnownHosts.CanonicalName(host, port));
        }

        // ---------- the three outcomes ----------

        [Fact]
        public void The_recorded_key_for_that_host_is_trusted()
        {
            var check = Check(Line("[192.0.2.20]:2222", ServerKey), ServerKey);

            Assert.Equal(HostKeyVerdict.Trusted, check.Verdict);
        }

        [Fact]
        public void A_different_key_for_a_known_host_is_a_mismatch()
        {
            var check = Check(Line("[192.0.2.20]:2222", ServerKey), ImpostorKey);

            Assert.Equal(HostKeyVerdict.Mismatch, check.Verdict);
            Assert.Contains("ssh-ed25519", check.KnownKeyTypes);
        }

        [Fact]
        public void A_host_the_file_never_mentions_is_unknown()
        {
            var check = Check(Line("[10.0.0.5]:2222", ServerKey), ServerKey);

            Assert.Equal(HostKeyVerdict.Unknown, check.Verdict);
        }

        /// <summary>
        /// The same key on the same machine but reached on another port is a different entry to
        /// OpenSSH, and has to be here too — otherwise a bare line would silently vouch for a
        /// service on a port nobody recorded.
        /// </summary>
        [Fact]
        public void A_bare_entry_does_not_vouch_for_a_non_default_port()
        {
            var check = Check(Line("192.0.2.20", ServerKey), ServerKey);

            Assert.Equal(HostKeyVerdict.Unknown, check.Verdict);
        }

        [Fact]
        public void A_bare_entry_does_vouch_for_the_default_port()
        {
            var check = KnownHosts.Check(new[] { Line("192.0.2.20", ServerKey) }, Host, 22, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Trusted, check.Verdict);
        }

        // ---------- the shapes the file actually comes in ----------

        [Fact]
        public void One_line_may_list_several_hosts()
        {
            var check = Check(Line("media-box,[192.0.2.20]:2222,other.invalid", ServerKey), ServerKey);

            Assert.Equal(HostKeyVerdict.Trusted, check.Verdict);
        }

        [Fact]
        public void A_host_may_have_several_keys_of_different_types()
        {
            var rsa = Blob(3);

            var check = KnownHosts.Check(
                new[]
                {
                    Line("[192.0.2.20]:2222", rsa, "ssh-rsa"),
                    Line("[192.0.2.20]:2222", ServerKey)
                },
                Host, Port, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Trusted, check.Verdict);
        }

        /// <summary>
        /// A machine with only an ssh-rsa entry that offers an ed25519 key is refused — safely —
        /// but the message has to be able to say what is on file, or it reads as an attack when it
        /// is a stale entry.
        /// </summary>
        [Fact]
        public void A_key_type_that_is_not_on_file_says_which_types_are()
        {
            var check = KnownHosts.Check(
                new[] { Line("[192.0.2.20]:2222", Blob(3), "ssh-rsa") },
                Host, Port, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Mismatch, check.Verdict);
            Assert.Equal(new[] { "ssh-rsa" }, check.KnownKeyTypes);
        }

        [Fact]
        public void Comments_and_blank_lines_are_not_entries()
        {
            var check = KnownHosts.Check(
                new[] { "", "   ", "# a comment", Line("[192.0.2.20]:2222", ServerKey) },
                Host, Port, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Trusted, check.Verdict);
        }

        [Fact]
        public void A_malformed_line_is_skipped_rather_than_thrown_on()
        {
            var check = KnownHosts.Check(
                new[] { "nonsense", "[192.0.2.20]:2222 ssh-ed25519", "@marker only", Line("[192.0.2.20]:2222", ServerKey) },
                Host, Port, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Trusted, check.Verdict);
        }

        [Fact]
        public void A_wildcard_pattern_covers_the_hosts_it_names()
        {
            Assert.Equal(
                HostKeyVerdict.Trusted,
                KnownHosts.Check(new[] { Line("192.0.2.*", ServerKey) }, Host, 22, "ssh-ed25519", ServerKey).Verdict);

            Assert.Equal(
                HostKeyVerdict.Unknown,
                KnownHosts.Check(new[] { Line("10.0.0.*", ServerKey) }, Host, 22, "ssh-ed25519", ServerKey).Verdict);
        }

        [Fact]
        public void A_negated_pattern_excludes_the_host_it_names()
        {
            var check = KnownHosts.Check(
                new[] { Line("192.0.2.*,!192.0.2.20", ServerKey) },
                Host, 22, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Unknown, check.Verdict);
        }

        // ---------- markers ----------

        [Fact]
        public void A_revoked_key_is_refused_even_though_it_is_listed()
        {
            var check = Check(Line("[192.0.2.20]:2222", ServerKey, marker: "@revoked"), ServerKey);

            Assert.Equal(HostKeyVerdict.Revoked, check.Verdict);
        }

        /// <summary>
        /// A revocation has to win over the ordinary entry whichever order they appear in, or the
        /// marker would mean nothing on a file where the old line was never removed.
        /// </summary>
        [Fact]
        public void A_revocation_wins_over_a_line_that_would_have_trusted_it()
        {
            var check = KnownHosts.Check(
                new[]
                {
                    Line("[192.0.2.20]:2222", ServerKey),
                    Line("[192.0.2.20]:2222", ServerKey, marker: "@revoked")
                },
                Host, Port, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Revoked, check.Verdict);
        }

        /// <summary>
        /// A legitimate configuration this app cannot check, because validating it means
        /// validating a certificate. Refused, but reported as itself rather than as an unknown
        /// host, which would send somebody off to add a key that is already covered.
        /// </summary>
        [Fact]
        public void A_certificate_authority_is_reported_as_something_this_app_cannot_check()
        {
            var check = Check(Line("[192.0.2.20]:2222", Blob(4), marker: "@cert-authority"), ServerKey);

            Assert.NotEqual(HostKeyVerdict.Trusted, check.Verdict);
            Assert.True(check.SawCertificateAuthority);

            var message = KnownHosts.Describe(check, Host, Port, "SHA256:abc", "/tmp/known_hosts");
            Assert.Contains("certificate authority", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cannot check", message, StringComparison.OrdinalIgnoreCase);
        }

        // ---------- hashed entries ----------

        /// <summary>
        /// What a machine with <c>HashKnownHosts yes</c> writes, which is the default on most
        /// Linux distributions. The hostname cannot be read out of one, so a reader that compares
        /// strings sees a file full of entries for no host at all and refuses everything.
        /// </summary>
        [Fact]
        public void A_hashed_entry_is_matched_by_hashing_the_name_the_same_way()
        {
            var check = Check(Line(Hashed("[192.0.2.20]:2222"), ServerKey), ServerKey);

            Assert.Equal(HostKeyVerdict.Trusted, check.Verdict);
        }

        [Fact]
        public void A_hashed_entry_for_another_host_does_not_match()
        {
            var check = Check(Line(Hashed("[10.0.0.5]:2222"), ServerKey), ServerKey);

            Assert.Equal(HostKeyVerdict.Unknown, check.Verdict);
        }

        [Fact]
        public void A_hashed_entry_with_the_wrong_key_is_still_a_mismatch_not_an_unknown_host()
        {
            var check = Check(Line(Hashed("[192.0.2.20]:2222"), ServerKey), ImpostorKey);

            Assert.Equal(HostKeyVerdict.Mismatch, check.Verdict);
        }

        [Fact]
        public void A_malformed_hash_refuses_rather_than_trusts()
        {
            var check = Check(Line("|1|not-base64|also-not", ServerKey), ServerKey);

            Assert.Equal(HostKeyVerdict.Unknown, check.Verdict);
        }

        /// <summary>Exactly what <c>ssh-keygen -H</c> writes: HMAC-SHA1, keyed by a per-entry salt.</summary>
        private static string Hashed(string name)
        {
            var salt = new byte[20];
            for (var i = 0; i < salt.Length; i++) salt[i] = (byte)(i * 7 + 3);

            using var hmac = new HMACSHA1(salt);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(name));

            return $"|1|{Convert.ToBase64String(salt)}|{Convert.ToBase64String(hash)}";
        }

        // ---------- reading the file ----------

        [Fact]
        public void The_file_on_disk_is_read_the_same_way_as_lines_in_hand()
        {
            var path = WriteFile("# my servers", Line("[192.0.2.20]:2222", ServerKey));

            Assert.Equal(
                HostKeyVerdict.Trusted,
                KnownHosts.CheckFile(path, Host, Port, "ssh-ed25519", ServerKey).Verdict);
        }

        /// <summary>
        /// The property the whole class exists for. Verification that switches itself off when it
        /// cannot find its file is not verification, and a missing file is the ordinary state of a
        /// machine that has never used SSH.
        /// </summary>
        [Theory]
        [InlineData("missing")]
        [InlineData("")]
        [InlineData(null)]
        public void A_file_that_is_not_there_never_trusts_anything(string? which)
        {
            var path = which == "missing" ? Path.Combine(_dir, "no-such-file") : which;

            var check = KnownHosts.CheckFile(path, Host, Port, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Unknown, check.Verdict);
        }

        [Fact]
        public void A_directory_where_the_file_should_be_never_trusts_anything()
        {
            var check = KnownHosts.CheckFile(_dir, Host, Port, "ssh-ed25519", ServerKey);

            Assert.Equal(HostKeyVerdict.Unknown, check.Verdict);
        }

        [Fact]
        public void An_empty_file_trusts_nothing()
        {
            Assert.Equal(
                HostKeyVerdict.Unknown,
                KnownHosts.CheckFile(WriteFile(), Host, Port, "ssh-ed25519", ServerKey).Verdict);
        }

        [Fact]
        public void A_server_that_offers_no_key_is_never_trusted()
        {
            var path = WriteFile(Line("[192.0.2.20]:2222", ServerKey));

            Assert.Equal(HostKeyVerdict.Unknown, KnownHosts.CheckFile(path, Host, Port, "ssh-ed25519", null).Verdict);
            Assert.Equal(HostKeyVerdict.Unknown, KnownHosts.CheckFile(path, Host, Port, "ssh-ed25519", Array.Empty<byte>()).Verdict);
        }

        [Fact]
        public void A_host_that_was_never_configured_is_never_trusted()
        {
            var path = WriteFile(Line("[192.0.2.20]:2222", ServerKey));

            Assert.Equal(HostKeyVerdict.Unknown, KnownHosts.CheckFile(path, "", Port, "ssh-ed25519", ServerKey).Verdict);
            Assert.Equal(HostKeyVerdict.Unknown, KnownHosts.CheckFile(path, null, Port, "ssh-ed25519", ServerKey).Verdict);
        }

        // ---------- fingerprints ----------

        /// <summary>
        /// The form <c>ssh-keygen -l</c> prints, so the number in the dialog is the number the
        /// user can compare against the server. Not a secret: a host key's public half is offered
        /// to anybody who connects.
        /// </summary>
        [Fact]
        public void A_fingerprint_reads_the_way_ssh_keygen_prints_one()
        {
            var fingerprint = KnownHosts.Fingerprint(ServerKey);

            Assert.StartsWith("SHA256:", fingerprint, StringComparison.Ordinal);
            Assert.DoesNotContain("=", fingerprint, StringComparison.Ordinal);
            Assert.NotEqual(fingerprint, KnownHosts.Fingerprint(ImpostorKey));
        }

        [Fact]
        public void A_missing_key_still_has_something_printable()
        {
            Assert.StartsWith("SHA256:", KnownHosts.Fingerprint(null), StringComparison.Ordinal);
            Assert.StartsWith("SHA256:", KnownHosts.Fingerprint(Array.Empty<byte>()), StringComparison.Ordinal);
        }

        // ---------- what a person is told ----------

        /// <summary>
        /// The interesting case. Refusing is safe, but refusing without saying how to fix it makes
        /// the feature unusable for anybody who has not connected by hand — which is most people
        /// who would configure this at all.
        /// </summary>
        [Fact]
        public void An_unknown_host_is_told_how_to_become_a_known_one()
        {
            var message = KnownHosts.Describe(
                HostKeyCheck.Of(HostKeyVerdict.Unknown), Host, Port, "SHA256:abc", "/home/someone/.ssh/known_hosts");

            Assert.Contains("/home/someone/.ssh/known_hosts", message, StringComparison.Ordinal);
            Assert.Contains("ssh-keyscan -p 2222 192.0.2.20", message, StringComparison.Ordinal);
            Assert.Contains("sftp -P 2222", message, StringComparison.Ordinal);
            Assert.Contains("SHA256:abc", message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Either the server was rebuilt or something else is answering, and the message has to
        /// say both — one of them is routine and the other is the reason this check exists.
        /// </summary>
        [Fact]
        public void A_mismatch_names_both_explanations_and_the_way_out_of_the_harmless_one()
        {
            var check = new HostKeyCheck(HostKeyVerdict.Mismatch, new[] { "ssh-ed25519" }, false);
            var message = KnownHosts.Describe(check, Host, Port, "SHA256:abc", "/home/someone/.ssh/known_hosts");

            Assert.Contains("rebuilt", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("something else is answering", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("ssh-keygen -R '[192.0.2.20]:2222'", message, StringComparison.Ordinal);
            Assert.Contains("ssh-ed25519", message, StringComparison.Ordinal);
        }

        [Fact]
        public void A_revoked_key_says_so_rather_than_suggesting_a_fix()
        {
            var message = KnownHosts.Describe(
                HostKeyCheck.Of(HostKeyVerdict.Revoked), Host, Port, "SHA256:abc", "/home/someone/.ssh/known_hosts");

            Assert.Contains("@revoked", message, StringComparison.Ordinal);
            Assert.DoesNotContain("ssh-keyscan", message, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_refusal_names_the_file_it_read()
        {
            foreach (var verdict in new[] { HostKeyVerdict.Unknown, HostKeyVerdict.Mismatch, HostKeyVerdict.Revoked })
            {
                var message = KnownHosts.Describe(
                    HostKeyCheck.Of(verdict), Host, Port, "SHA256:abc", "/home/someone/.ssh/known_hosts");

                Assert.Contains("/home/someone/.ssh/known_hosts", message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void A_message_falls_back_to_the_usual_path_rather_than_a_blank()
        {
            var message = KnownHosts.Describe(HostKeyCheck.Of(HostKeyVerdict.Unknown), Host, Port, null, null);

            Assert.Contains("~/.ssh/known_hosts", message, StringComparison.Ordinal);
            Assert.Contains("192.0.2.20:2222", message, StringComparison.Ordinal);

            // A null that reached the screen as the word "null", or as a gap where the key should
            // be, would read as a bug in the app rather than as a server worth checking.
            Assert.DoesNotContain("null", message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("an unknown key", message, StringComparison.Ordinal);
        }

        // ---------- where the file lives ----------

        /// <summary>
        /// A literal <c>~</c> resolves to nothing on Windows, which this app also builds for and
        /// where OpenSSH keeps the same file under the user profile.
        /// </summary>
        [Fact]
        public void The_default_path_is_resolved_rather_than_a_literal_tilde()
        {
            var path = PlatformPaths.KnownHostsPath;

            Assert.DoesNotContain("~", path, StringComparison.Ordinal);
            Assert.EndsWith(Path.Combine(".ssh", "known_hosts"), path, StringComparison.Ordinal);
            Assert.StartsWith(PlatformPaths.HomeDirectory, path, StringComparison.Ordinal);
        }
    }
}
