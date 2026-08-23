using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace UrDatabase.Services
{
    /// <summary>One release asset, already checked to be a build this machine can be handed.</summary>
    /// <param name="Name">The asset's filename, e.g. <c>UrDatabase-0.11.0-osx-arm64.dmg</c>.</param>
    /// <param name="Url">Where GitHub serves it from. Always <c>https</c>.</param>
    /// <param name="Bytes">Its size, or 0 when the API did not say.</param>
    public readonly record struct UpdateAsset(string Name, string Url, long Bytes);

    /// <summary>
    /// A release that is newer than the one running.
    /// </summary>
    /// <param name="Version">Normalised, as <c>0.11.0</c>.</param>
    /// <param name="Tag">The tag exactly as GitHub has it, which is <c>v0.11.0</c>.</param>
    /// <param name="Page">The release page, for somebody who wants to read what changed.</param>
    /// <param name="Asset">
    /// The build for this machine, or null when there is nothing this app can fetch — which is
    /// what turns the banner's button into a link to the downloads site.
    /// </param>
    public sealed record AvailableUpdate(string Version, string Tag, string Page, UpdateAsset? Asset);

    /// <summary>
    /// Turning GitHub's list of releases into the one thing the app needs to know: whether there
    /// is a newer build, and which file on it belongs to this machine.
    ///
    /// Everything here is pure — it takes a decoded API payload and returns plain objects. The
    /// request lives in <see cref="UpdateService"/>. That split is what makes the fiddly parts
    /// testable without a network: which asset belongs to which machine, whether <c>0.9.0</c> or
    /// <c>0.10.0</c> is newer, and whether an Apple silicon Mac running the Intel build should be
    /// offered the Intel build again.
    ///
    /// The rules are the ones <c>web/downloads/releases.js</c> already applies to the same
    /// releases, for the same reason its comments give: one string — the .NET runtime identifier —
    /// identifies a build in the workflow, in the filename and here, so there is no table mapping
    /// one naming scheme onto another to get out of step.
    /// </summary>
    public static class UpdateFeed
    {
        public const string Repo = "larabail/UrDatabase";

        /// <summary>
        /// Newest first, and enough of them that a release which arrived out of version order is
        /// still in the window. Deliberately not <c>/releases/latest</c>, which GitHub defines by
        /// the date a release was created rather than by its version: a fix tagged after a larger
        /// release that was prepared earlier would come back as the latest and offer an upgrade
        /// that is a downgrade.
        /// </summary>
        public const string ReleasesApiUrl = $"https://api.github.com/repos/{Repo}/releases?per_page=30";

        public const string ReleasesPageUrl = $"https://github.com/{Repo}/releases";

        /// <summary>Where somebody is sent when there is nothing this app can fetch for them.</summary>
        public const string DownloadsPageUrl = "https://urdatabase-downloads.web.app";

        public const string OsxArm64 = "osx-arm64";
        public const string OsxX64 = "osx-x64";
        public const string WinX64 = "win-x64";

        /// <summary>The three runtime identifiers the release workflow publishes.</summary>
        public static readonly string[] Platforms = { OsxArm64, OsxX64, WinX64 };

        /// <summary>The containers a build can arrive in. macOS moved from <c>.zip</c> to <c>.dmg</c> at 0.2.1.</summary>
        private static readonly string[] Containers = { ".dmg", ".zip" };

        /// <summary>Files that sit beside the builds and are not themselves downloads.</summary>
        private static readonly string[] NotABuild = { ".sha256", ".sha512", ".md5", ".asc", ".sig", ".txt" };

        /// <summary>
        /// Which build this machine should be offered, or null when the app is running somewhere
        /// no release is published for — a Linux build from source, most likely, where the honest
        /// answer is a link to the downloads page rather than a file.
        /// </summary>
        public static string? CurrentRuntimeIdentifier =>
            RuntimeIdentifier(OperatingSystem.IsMacOS(), OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture);

        /// <summary>
        /// The testable form.
        ///
        /// Keyed on the architecture of the <em>machine</em> and not of this process, which is the
        /// one decision here worth arguing about. An Apple silicon Mac running the Intel build
        /// under Rosetta reports an x64 process and an arm64 OS; asking the process would keep
        /// handing that user the Intel build for ever, so the question asked is the one the
        /// downloads page asks — what is this computer — and the update moves them onto the native
        /// build. Windows on ARM is the mirror image and gets <c>win-x64</c> regardless, because
        /// that is the only Windows build there is and it runs there under emulation.
        /// </summary>
        internal static string? RuntimeIdentifier(bool isMacOS, bool isWindows, Architecture osArchitecture)
        {
            if (isMacOS) return osArchitecture == Architecture.Arm64 ? OsxArm64 : OsxX64;
            if (isWindows) return WinX64;
            return null;
        }

        /// <summary>Which build an asset called <paramref name="name"/> is, or null if it is not one.</summary>
        public static string? AssetPlatform(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var lower = name.Trim().ToLowerInvariant();

            // Checked first, because `UrDatabase-0.2.1-osx-arm64.dmg.sha256` also ends in
            // `-osx-arm64.dmg` as far as a naive search is concerned, and downloading a 90-byte
            // text file as though it were the new version is worse than downloading nothing.
            foreach (var suffix in NotABuild)
            {
                if (lower.EndsWith(suffix, StringComparison.Ordinal)) return null;
            }

            // Matched on the whole `-<rid><container>` tail rather than on the architecture alone.
            // `-x64.zip` would match both the Intel Mac build and the Windows one, and whichever
            // was listed first would win.
            foreach (var rid in Platforms)
            {
                foreach (var container in Containers)
                {
                    if (lower.EndsWith($"-{rid}{container}", StringComparison.Ordinal)) return rid;
                }
            }

            return null;
        }

        /// <summary>
        /// The newest release in <paramref name="payload"/> that is worth telling somebody about,
        /// or null when there is none.
        ///
        /// A release only counts if it carries a build for <paramref name="runtimeIdentifier"/>.
        /// Not merely if it is newer: all three builds come out of one job so they normally arrive
        /// together, but a release can be edited by hand and an asset can be deleted, and
        /// announcing a version this machine cannot have — then sending the user to a page that
        /// offers them the previous one — is a worse answer than saying nothing. When the platform
        /// is unknown the test relaxes to "carries a build at all", so a source build on Linux
        /// still hears about releases and is simply pointed at the website.
        /// </summary>
        internal static AvailableUpdate? Newest(
            IReadOnlyList<GithubRelease?>? payload,
            string? runningVersion,
            string? runtimeIdentifier)
        {
            if (payload is null) return null;

            // A runtime identifier this pipeline does not publish is the same thing as not knowing
            // which build to offer. Left as a match requirement it would mean "announce nothing
            // ever", quietly, rather than "announce it and point at the website".
            if (runtimeIdentifier is not null && Array.IndexOf(Platforms, runtimeIdentifier) < 0)
                runtimeIdentifier = null;

            AvailableUpdate? best = null;

            foreach (var raw in payload)
            {
                var candidate = Normalize(raw, runtimeIdentifier);
                if (candidate is null) continue;
                if (best is not null && AppVersion.Compare(candidate.Version, best.Version) <= 0) continue;

                best = candidate;
            }

            if (best is null || !AppVersion.IsNewer(best.Version, runningVersion)) return null;
            return best;
        }

        /// <summary>
        /// A release from the API as the banner wants it, or null if it is not one.
        ///
        /// Drafts are invisible to anyone without push access, so offering one would be offering a
        /// download that 404s for every user. Pre-releases are excluded for the mirror-image
        /// reason: they are visible, but this pipeline does not publish them, so one appearing is a
        /// hand-made release that was deliberately not meant for everybody.
        /// </summary>
        private static AvailableUpdate? Normalize(GithubRelease? raw, string? runtimeIdentifier)
        {
            if (raw is null || raw.Draft || raw.Prerelease) return null;

            var version = AppVersion.Text(raw.TagName);
            if (version is null) return null;

            var asset = PickAsset(raw.Assets, runtimeIdentifier);
            if (asset is null) return null;

            var tag = string.IsNullOrWhiteSpace(raw.TagName) ? $"v{version}" : raw.TagName.Trim();

            // Only ever open a github.com page. The API is read over https, but `html_url` is still
            // a string from a server, and handing an arbitrary one to the operating system's URL
            // opener is handing it a URL scheme of somebody else's choosing.
            var page = raw.HtmlUrl is not null && raw.HtmlUrl.StartsWith("https://github.com/", StringComparison.Ordinal)
                ? raw.HtmlUrl
                : $"{ReleasesPageUrl}/tag/{Uri.EscapeDataString(tag)}";

            // Null when the platform is unknown, which is what makes the button open the website
            // instead of starting a download this app cannot name a file for.
            return new AvailableUpdate(version, tag, page, runtimeIdentifier is null ? null : asset);
        }

        /// <summary>
        /// The build for <paramref name="runtimeIdentifier"/> among a release's assets, or — when
        /// the platform is unknown — any build at all, which is only used to establish that the
        /// release is a real one rather than a tag with notes attached.
        /// </summary>
        private static UpdateAsset? PickAsset(IReadOnlyList<GithubAsset?>? assets, string? runtimeIdentifier)
        {
            if (assets is null) return null;

            foreach (var asset in assets)
            {
                if (asset is null) continue;

                var platform = AssetPlatform(asset.Name);
                if (platform is null) continue;
                if (runtimeIdentifier is not null && !string.Equals(platform, runtimeIdentifier, StringComparison.Ordinal))
                    continue;

                // Only ever download over https. An asset URL is data from a server like any other,
                // and this one is handed to an HTTP client and then to the operating system.
                var url = asset.BrowserDownloadUrl;
                if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("https://", StringComparison.Ordinal)) continue;

                // First one wins. A release carrying two builds for one runtime identifier is not
                // something this pipeline produces, and guessing between them would be worse than
                // taking the one GitHub listed first.
                return new UpdateAsset(asset.Name!.Trim(), url, asset.Size > 0 ? asset.Size : 0);
            }

            return null;
        }
    }

    /// <summary>
    /// The part of GitHub's release payload this app reads. Named explicitly rather than by a
    /// naming policy, so a rename in the serializer's conventions cannot silently stop the update
    /// check from ever finding a release again.
    /// </summary>
    internal sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GithubAsset?>? Assets { get; set; }
    }

    internal sealed class GithubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
