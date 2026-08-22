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
    }
}
