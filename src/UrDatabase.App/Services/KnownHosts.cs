using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace UrDatabase.Services
{
    /// <summary>What <c>known_hosts</c> has to say about the key a server just offered.</summary>
    public enum HostKeyVerdict
    {
        /// <summary>The host is listed and this is one of its keys. The only value that connects.</summary>
        Trusted,

        /// <summary>Nothing in the file mentions this host at all.</summary>
        Unknown,

        /// <summary>The host is listed and this is not one of the keys recorded for it.</summary>
        Mismatch,

        /// <summary>The key is listed for this host and marked <c>@revoked</c>.</summary>
        Revoked
    }

    /// <param name="KnownKeyTypes">
    /// The key types already on file for this host, for a message that can say "we have an
    /// ssh-rsa key for that machine and it offered an ssh-ed25519 one" rather than only "wrong".
    /// </param>
    /// <param name="SawCertificateAuthority">
    /// True when the only thing the file says about this host is an <c>@cert-authority</c> line.
    /// That is a legitimate configuration this app cannot check, and saying so is better than
    /// reporting it as an unknown host.
    /// </param>
    public readonly record struct HostKeyCheck(
        HostKeyVerdict Verdict,
        IReadOnlyList<string> KnownKeyTypes,
        bool SawCertificateAuthority)
    {
        public static HostKeyCheck Of(HostKeyVerdict verdict) =>
            new(verdict, Array.Empty<string>(), false);
    }

    /// <summary>
    /// Reads OpenSSH's <c>known_hosts</c> and answers one question: is this the key that machine
    /// offered last time?
    ///
    /// Without it SSH.NET accepts whatever key it is handed. That is worse than what the person
    /// this feature is for was already doing by hand — <c>sftp</c> checks <c>known_hosts</c> and
    /// hard-fails on a mismatch — and shipping something quietly weaker than the command it
    /// replaces is the wrong direction. The private key is never at risk, because public key
    /// authentication does not disclose it; what is at risk is the film, which would be handed to
    /// whatever answered on that address.
    ///
    /// Pure, and kept out of <see cref="SshNetSftpTransport"/>, because the file format has more
    /// in it than it looks: a non-default port is written <c>[host]:port</c>, one line may list
    /// several comma-separated patterns with wildcards and negations, <c>@revoked</c> and
    /// <c>@cert-authority</c> markers change what a line means, an entry may be hashed so that the
    /// hostname cannot be read out of it, and one host legitimately has several keys of different
    /// types. None of that can be tested through a socket.
    /// </summary>
    public static class KnownHosts
    {
        /// <summary>The port that is written bare rather than as <c>[host]:port</c>.</summary>
        private const int DefaultSshPort = 22;

        /// <summary>The only hash OpenSSH has ever written: HMAC-SHA1, salted per entry.</summary>
        private const string HashMarker = "|1|";

        /// <summary>
        /// The name a host is recorded under. OpenSSH brackets the host and appends the port only
        /// when it is not 22, and a hashed entry hashes exactly this string — so getting it wrong
        /// turns every lookup into "unknown host" rather than into an error.
        /// </summary>
        public static string CanonicalName(string? host, int port)
        {
            var name = (host ?? "").Trim();

            return port is DefaultSshPort or 0
                ? name
                : $"[{name}]:{port.ToString(CultureInfo.InvariantCulture)}";
        }

        /// <summary>
        /// A key as <c>ssh-keygen -l</c> prints it: <c>SHA256:</c> and unpadded base64. Not a
        /// secret — a host key's public half is offered to anybody who connects — and the only
        /// thing that makes a mismatch diagnosable, so it belongs in the message and the log.
        /// </summary>
        public static string Fingerprint(byte[]? keyBlob)
        {
            if (keyBlob is null || keyBlob.Length == 0) return "SHA256:(no key)";

            return "SHA256:" + Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');
        }

        /// <summary>
        /// Checks the file at <paramref name="path"/>. A file that is missing, unreadable or not
        /// configured yields <see cref="HostKeyVerdict.Unknown"/> and never
        /// <see cref="HostKeyVerdict.Trusted"/>: verification that switches itself off when it
        /// cannot find its file is not verification.
        /// </summary>
        public static HostKeyCheck CheckFile(string? path, string? host, int port, string? keyType, byte[]? keyBlob)
        {
            string[] lines;

            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return HostKeyCheck.Of(HostKeyVerdict.Unknown);

                lines = File.ReadAllLines(path);
            }
            catch (Exception)
            {
                return HostKeyCheck.Of(HostKeyVerdict.Unknown);
            }

            return Check(lines, host, port, keyType, keyBlob);
        }

        /// <summary>
        /// The same question asked of lines already in hand, which is what the tests drive.
        /// </summary>
        public static HostKeyCheck Check(
            IEnumerable<string>? lines,
            string? host,
            int port,
            string? keyType,
            byte[]? keyBlob)
        {
            if (lines is null || keyBlob is null || keyBlob.Length == 0)
                return HostKeyCheck.Of(HostKeyVerdict.Unknown);

            var name = CanonicalName(host, port);
            if (name.Length == 0) return HostKeyCheck.Of(HostKeyVerdict.Unknown);

            var matchedHost = false;
            var trusted = false;
            var certificateAuthority = false;
            var types = new List<string>();

            foreach (var raw in lines)
            {
                var entry = Parse(raw);
                if (entry is null) continue;
                if (!MatchesHost(entry.Hosts, name)) continue;

                matchedHost = true;

                if (entry.Marker == "@cert-authority")
                {
                    // A certificate authority line says "trust anything this CA signed", which
                    // needs certificate validation this app does not do. Recorded so the message
                    // can name it rather than calling the host unknown.
                    certificateAuthority = true;
                    continue;
                }

                var sameKey = KeysMatch(entry.Key, keyBlob);

                // Checked before anything can be trusted: a key that is both listed and revoked is
                // revoked, and that is the whole point of the marker.
                if (entry.Marker == "@revoked")
                {
                    if (sameKey) return HostKeyCheck.Of(HostKeyVerdict.Revoked);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.KeyType) && !types.Contains(entry.KeyType))
                    types.Add(entry.KeyType);

                if (sameKey) trusted = true;
            }

            if (trusted) return new HostKeyCheck(HostKeyVerdict.Trusted, types, certificateAuthority);

            return matchedHost
                ? new HostKeyCheck(HostKeyVerdict.Mismatch, types, certificateAuthority)
                : HostKeyCheck.Of(HostKeyVerdict.Unknown);
        }

        /// <summary>
        /// Why the connection was refused, and what to do about it. Three genuinely different
        /// situations, so three different sentences: a host nobody has ever vouched for is a
        /// setup step not yet done, while a host whose key has changed is either a rebuilt server
        /// or precisely the thing this check exists to catch, and the message says both.
        /// </summary>
        public static string Describe(
            HostKeyCheck check,
            string? host,
            int port,
            string? offeredFingerprint,
            string? knownHostsPath)
        {
            var where = SftpFailure.Endpoint(host, port);
            var file = string.IsNullOrWhiteSpace(knownHostsPath) ? "~/.ssh/known_hosts" : knownHostsPath;
            var offered = string.IsNullOrWhiteSpace(offeredFingerprint) ? "an unknown key" : offeredFingerprint;
            var name = CanonicalName(host, port);

            if (check.SawCertificateAuthority && check.Verdict != HostKeyVerdict.Revoked)
            {
                return $"{where} is vouched for by a certificate authority in {file}, which this app " +
                       "cannot check. Add the server's own host key to that file to upload to it: " +
                       $"ssh-keyscan -p {port.ToString(CultureInfo.InvariantCulture)} {(host ?? "").Trim()} >> {file}";
            }

            return check.Verdict switch
            {
                HostKeyVerdict.Revoked =>
                    $"The host key {where} offered is marked @revoked in {file}, so this app will not " +
                    $"send anything to it. The key offered was {offered}.",

                HostKeyVerdict.Mismatch =>
                    $"{where} offered a host key that is not the one recorded in {file}. Either that " +
                    "machine was rebuilt, or something else is answering on that address — and until " +
                    "you know which, the film is not going to it." +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    $"It offered {offered}." +
                    (check.KnownKeyTypes.Count > 0
                        ? $" {file} has {string.Join(" and ", check.KnownKeyTypes)} for that address."
                        : "") +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    $"If you rebuilt it yourself: ssh-keygen -R '{name}' and then connect once with sftp.",

                _ =>
                    $"{where} is not in {file}, so this app has no way to tell whether it is really " +
                    "your server. It will not upload to a machine nothing has vouched for." +
                    $"{Environment.NewLine}{Environment.NewLine}" +
                    $"It offered {offered}. To trust it, connect once by hand with " +
                    $"sftp -P {port.ToString(CultureInfo.InvariantCulture)} {(host ?? "").Trim()} and accept " +
                    $"the key, or run ssh-keyscan -p {port.ToString(CultureInfo.InvariantCulture)} " +
                    $"{(host ?? "").Trim()} >> {file}"
            };
        }

        private sealed record Entry(string Marker, string Hosts, string KeyType, byte[]? Key);

        /// <summary>
        /// One line, or null when it is blank, a comment, or too short to mean anything. A
        /// malformed line is skipped rather than thrown on: the file is shared with every other
        /// SSH tool on the machine and may hold entries in formats this app has never heard of.
        /// </summary>
        private static Entry? Parse(string? raw)
        {
            var line = (raw ?? "").Trim();
            if (line.Length == 0 || line.StartsWith('#')) return null;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var at = 0;

            var marker = "";
            if (fields.Length > 0 && fields[0].StartsWith('@'))
            {
                marker = fields[0].ToLowerInvariant();
                at = 1;
            }

            if (fields.Length < at + 3) return null;

            byte[]? key = null;
            try
            {
                key = Convert.FromBase64String(fields[at + 2]);
            }
            catch (FormatException)
            {
                // Not base64, so not a key this can compare. The host still matched, which keeps
                // the answer "mismatch" rather than "unknown" — the safer of the two.
            }

            return new Entry(marker, fields[at], fields[at + 1], key);
        }

        private static bool KeysMatch(byte[]? recorded, byte[] offered) =>
            recorded is not null &&
            recorded.Length == offered.Length &&
            CryptographicOperations.FixedTimeEquals(recorded, offered);

        /// <summary>
        /// Whether a line's host field covers <paramref name="name"/>. Handles the three forms
        /// OpenSSH writes: a hashed entry, a comma-separated list, and patterns with <c>*</c>,
        /// <c>?</c> or a leading <c>!</c> to exclude.
        /// </summary>
        private static bool MatchesHost(string hosts, string name)
        {
            if (hosts.StartsWith(HashMarker, StringComparison.Ordinal))
                return MatchesHashed(hosts, name);

            var matched = false;

            foreach (var pattern in hosts.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var negated = pattern.StartsWith('!');
                var value = negated ? pattern[1..] : pattern;

                if (!Glob(value, name)) continue;

                // A negation wins outright, whatever else on the line matched.
                if (negated) return false;
                matched = true;
            }

            return matched;
        }

        /// <summary>
        /// A hashed entry, which is what a machine with <c>HashKnownHosts yes</c> writes and what
        /// most Linux distributions do by default. The hostname cannot be read back out, so the
        /// only way to use one is to hash the name being looked for the same way and compare:
        /// HMAC-SHA1 over the canonical name, keyed by the salt stored in the entry itself.
        /// </summary>
        private static bool MatchesHashed(string hosts, string name)
        {
            var parts = hosts.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return false;

            try
            {
                var salt = Convert.FromBase64String(parts[1]);
                var expected = Convert.FromBase64String(parts[2]);

                using var hmac = new HMACSHA1(salt);
                var actual = hmac.ComputeHash(Encoding.UTF8.GetBytes(name));

                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch (Exception)
            {
                // A malformed hash is not a match, which refuses rather than trusts.
                return false;
            }
        }

        /// <summary>
        /// OpenSSH's host patterns: <c>*</c> for any run of characters, <c>?</c> for one. Compared
        /// without regard to case, because hostnames are.
        /// </summary>
        private static bool Glob(string pattern, string value)
        {
            if (!pattern.Contains('*') && !pattern.Contains('?'))
                return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);

            var regex = "^" + string.Concat(pattern.Select(ch => ch switch
            {
                '*' => ".*",
                '?' => ".",
                _ => System.Text.RegularExpressions.Regex.Escape(ch.ToString())
            })) + "$";

            try
            {
                return System.Text.RegularExpressions.Regex.IsMatch(
                    value,
                    regex,
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                    TimeSpan.FromMilliseconds(200));
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
