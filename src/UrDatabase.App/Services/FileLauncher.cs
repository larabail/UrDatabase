using System;
using System.Diagnostics;
using System.IO;

namespace UrDatabase.Services
{
    /// <summary>
    /// Opens a file in the OS default application. <c>UseShellExecute</c> alone is not enough:
    /// on macOS and Linux it cannot open arbitrary documents, so those platforms shell out to
    /// their own opener instead.
    /// </summary>
    public static class FileLauncher
    {
        /// <summary>Builds the launch descriptor without starting anything, so it can be asserted in tests.</summary>
        public static ProcessStartInfo BuildStartInfo(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A file path is required.", nameof(filePath));

            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("open") { UseShellExecute = false };
                psi.ArgumentList.Add(filePath);
                return psi;
            }

            if (OperatingSystem.IsWindows())
            {
                return new ProcessStartInfo(filePath) { UseShellExecute = true };
            }

            var linux = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            linux.ArgumentList.Add(filePath);
            return linux;
        }

        /// <summary>Launches <paramref name="filePath"/>. Throws when the file is missing or the OS refuses.</summary>
        public static void Open(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("The file to play no longer exists.", filePath);

            Process.Start(BuildStartInfo(filePath));
        }

        /// <summary>
        /// Whether this is a web address the app is willing to hand to the operating system.
        ///
        /// Only <c>http</c> and <c>https</c>, and only an absolute URL. Every address the app opens
        /// originates in a GitHub API response, and the launcher below runs whatever a scheme is
        /// registered to: <c>file:</c> would open something on this disk and a scheme installed by
        /// another application would start that application, neither on the user's initiative.
        /// </summary>
        public static bool IsWebUrl(string? url) =>
            Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var parsed) &&
            (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps);

        /// <summary>Builds the launch descriptor for a web address without opening anything.</summary>
        public static ProcessStartInfo BuildUrlStartInfo(string url)
        {
            if (!IsWebUrl(url))
                throw new ArgumentException("Only http and https addresses can be opened.", nameof(url));

            var target = url.Trim();

            if (OperatingSystem.IsMacOS())
            {
                var mac = new ProcessStartInfo("open") { UseShellExecute = false };
                mac.ArgumentList.Add(target);
                return mac;
            }

            if (OperatingSystem.IsWindows())
            {
                // The shell is what knows which browser is the default one; started without it,
                // Windows tries to execute the URL as though it were a program.
                return new ProcessStartInfo(target) { UseShellExecute = true };
            }

            var linux = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
            linux.ArgumentList.Add(target);
            return linux;
        }

        /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
        public static void OpenUrl(string url) => Process.Start(BuildUrlStartInfo(url));
    }
}
