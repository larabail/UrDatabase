using System;
using System.Reflection;

namespace UrDatabase.Services
{
    /// <summary>
    /// Which version of the app is running, and which of two versions is newer.
    ///
    /// These are deliberately the same rules <c>web/downloads/releases.js</c> applies to the very
    /// same tags — a <c>MAJOR.MINOR.PATCH</c> triple, a leading <c>v</c> dropped, build metadata
    /// ignored. The downloads page and the app answer one question about one set of releases, and
    /// two implementations that disagreed would mean the page offering a build the app had just
    /// told somebody they already had.
    ///
    /// Compared number by number rather than as text. As text <c>0.9.0</c> sorts after
    /// <c>0.10.0</c>, so everybody on the newest build would be told they were behind and handed
    /// the older one — a fault that first appears at the tenth minor release and looks like the
    /// update check being broken rather than like a sort order.
    /// </summary>
    public static class AppVersion
    {
        /// <summary>
        /// What a version resolves to when the assembly carries nothing usable.
        ///
        /// Deliberately not <c>0.0.0</c>, which would parse. A build that cannot say what it is
        /// must not be told that every release is newer than it — that is a guess dressed as a
        /// fact, and the banner would announce an upgrade over a version nobody established. This
        /// value parses as nothing, so such a build is simply never offered one.
        /// </summary>
        public const string Unknown = "unknown";

        /// <summary>
        /// The running version, as <c>0.11.0</c>.
        ///
        /// Read from the assembly rather than written down here, because
        /// <c>Directory.Build.props</c> holds the only version this repository is allowed to have
        /// and a second copy would be a copy that goes stale on the release it matters for.
        /// </summary>
        public static string Current { get; } = Resolve(typeof(AppVersion).Assembly);

        /// <summary>
        /// The testable form. The informational version is preferred because it is the
        /// <c>&lt;Version&gt;</c> as written, but .NET appends <c>+&lt;commit&gt;</c> to it, and the
        /// assembly version behind it is always a four part number that no release is ever tagged
        /// with. Both are reduced to the three parts a tag actually carries.
        /// </summary>
        internal static string Resolve(Assembly assembly) =>
            Resolve(
                assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                assembly.GetName().Version);

        internal static string Resolve(string? informational, Version? assemblyVersion)
        {
            if (Text(informational) is string fromInformational) return fromInformational;

            // Four parts, always, which Parse refuses on purpose — so it is cut to three here
            // rather than being handed over as something no tag looks like.
            if (assemblyVersion is not null && Text(assemblyVersion.ToString(3)) is string fromAssembly)
                return fromAssembly;

            return Unknown;
        }

        /// <summary>
        /// The three numbers in <paramref name="raw"/>, or null when it is not a version.
        ///
        /// Anything shorter than three parts is padded, so the <c>0.11</c> somebody writes by hand
        /// means <c>0.11.0</c>. Anything longer is refused outright rather than truncated: a four
        /// part number is an assembly version, not a release, and quietly reading <c>0.11.0.4</c>
        /// as <c>0.11.0</c> would compare two different things as equal.
        /// </summary>
        public static (int Major, int Minor, int Patch)? Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            var text = raw.Trim();
            if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];

            // `0.11.0+2f9c1ab` is what the compiler writes and `0.11.0-preview` is what a hand-made
            // tag looks like. Neither tail takes part in ordering, and both are dropped before the
            // numbers are read rather than after, so nothing has to decide what `1ab` means.
            var plus = text.IndexOf('+');
            if (plus >= 0) text = text[..plus];
            var dash = text.IndexOf('-');
            if (dash >= 0) text = text[..dash];

            text = text.Trim();
            if (text.Length == 0) return null;

            var parts = text.Split('.');
            if (parts.Length > 3) return null;

            var numbers = new int[3];
            for (var i = 0; i < parts.Length; i++)
            {
                // Rejected rather than parsed leniently: `int.TryParse` accepts "+2", " 2" and a
                // thousands separator in some cultures, none of which appear in a tag this
                // repository produces, and all of which would make two spellings of one version.
                if (!IsAllDigits(parts[i]) || !int.TryParse(parts[i], out numbers[i])) return null;
            }

            return (numbers[0], numbers[1], numbers[2]);
        }

        /// <summary><paramref name="raw"/> normalised to <c>0.11.0</c>, or null when it is not a version.</summary>
        public static string? Text(string? raw)
        {
            var parsed = Parse(raw);
            return parsed is null ? null : $"{parsed.Value.Major}.{parsed.Value.Minor}.{parsed.Value.Patch}";
        }

        /// <summary>
        /// Orders two versions, oldest first. Anything that does not parse compares equal to
        /// everything, so a caller that has not already discarded such a value gets a stable order
        /// rather than an exception in the middle of a background check.
        /// </summary>
        public static int Compare(string? left, string? right)
        {
            var a = Parse(left);
            var b = Parse(right);
            if (a is null || b is null) return 0;

            if (a.Value.Major != b.Value.Major) return a.Value.Major.CompareTo(b.Value.Major);
            if (a.Value.Minor != b.Value.Minor) return a.Value.Minor.CompareTo(b.Value.Minor);
            return a.Value.Patch.CompareTo(b.Value.Patch);
        }

        /// <summary>
        /// Whether <paramref name="candidate"/> is a version worth telling somebody about.
        ///
        /// False when either side is unreadable, which is the important half: a build with no
        /// usable version of its own must not be told that every release is newer than it, and a
        /// tag nobody can parse must not be offered as an upgrade.
        /// </summary>
        public static bool IsNewer(string? candidate, string? running)
        {
            if (Parse(candidate) is null || Parse(running) is null) return false;
            return Compare(candidate, running) > 0;
        }

        private static bool IsAllDigits(string part)
        {
            if (part.Length == 0) return false;
            foreach (var ch in part)
            {
                if (ch is < '0' or > '9') return false;
            }

            return true;
        }
    }
}
