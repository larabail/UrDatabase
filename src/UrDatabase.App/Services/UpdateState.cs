using System;
using System.IO;
using System.Text.Json;

namespace UrDatabase.Services
{
    /// <summary>
    /// What the app remembers about update checks between launches, which is one thing: the
    /// version somebody pressed <b>Later</b> on.
    ///
    /// Its own small file rather than a field in <c>appsettings.json</c>. That file is a
    /// hand-editable list of the user's answers, it is round-tripped whole by the setup screen, and
    /// every setting in it has to be named in <see cref="ConfigStore.Serialize"/> or it is deleted
    /// on the next save. A value the app writes to itself, that no user would ever set and that
    /// changes on its own, does not belong in there.
    ///
    /// Best effort in both directions. A read-only home directory means the banner reappears next
    /// launch, which is a mild annoyance; it must never be a failed start or a lost click.
    /// </summary>
    public sealed class UpdateState
    {
        public const string FileName = "update-state.json";

        /// <summary>
        /// The version the user has said they do not want to be told about, normalised. Null when
        /// they have never dismissed one, which is every install until they do.
        /// </summary>
        public string? SkippedVersion { get; set; }

        public static string DefaultPath => Path.Combine(PlatformPaths.AppDataRoot, FileName);

        public static UpdateState Load(string? path = null)
        {
            try
            {
                var target = path ?? DefaultPath;
                if (!File.Exists(target)) return new UpdateState();

                var state = JsonSerializer.Deserialize<UpdateState>(
                    File.ReadAllText(target),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (state is null) return new UpdateState();

                // Normalised on the way in, so a file edited by hand to `v0.11.0` still silences
                // the release it names rather than silencing nothing.
                state.SkippedVersion = AppVersion.Text(state.SkippedVersion);
                return state;
            }
            catch
            {
                // Malformed, unreadable, written by a newer version: the banner shows, which is the
                // safe way to be wrong.
                return new UpdateState();
            }
        }

        /// <summary>
        /// Records that this version has been dismissed, and returns whether it stuck. A version
        /// that does not parse clears the record instead of storing a value nothing will match.
        /// </summary>
        public static bool SaveSkipped(string? version, string? path = null)
        {
            var state = new UpdateState { SkippedVersion = AppVersion.Text(version) };

            try
            {
                var target = path ?? DefaultPath;

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

                File.WriteAllText(target, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("update.log", $"could not remember the dismissed version: {ex.Message}");
                return false;
            }
        }
    }
}
