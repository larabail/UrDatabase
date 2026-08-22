using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using UrDatabase.Services;

namespace UrDatabase.Models
{
    /// <summary>
    /// The answers the setup screen collects, and every rule about whether they add up to a
    /// working library. All of it is a plain object on purpose: the window binds to it and calls
    /// <see cref="ToConfig"/>, and none of the decisions below need a UI thread to be tested.
    ///
    /// The two sources are independent and both optional individually — a user may catalogue
    /// files on this machine, browse a Jellyfin server, or do both — but at least one has to be
    /// chosen, because an app pointed at nothing has nothing to show and no way to say why.
    /// </summary>
    public sealed class SetupChoices
    {
        /// <summary>Films kept as files on this computer, found by scanning folders.</summary>
        public bool UseLocalFolders { get; set; }

        /// <summary>
        /// Observable so the window's list updates as folders are added, which is the only
        /// concession in this class to the fact that something binds to it.
        /// </summary>
        public ObservableCollection<string> Folders { get; } = new();

        /// <summary>Films kept on a Jellyfin server, streamed rather than copied here.</summary>
        public bool UseJellyfin { get; set; }

        public string ServerUrl { get; set; } = "";
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public string LibraryName { get; set; } = "";

        /// <summary>Optional. Blank leaves whatever the environment or the build supplies.</summary>
        public string TmdbApiKey { get; set; } = "";

        public string OmdbApiKey { get; set; } = "";

        /// <summary>
        /// Prefills the screen from the user's own configuration file. Takes the raw config
        /// rather than the resolved one so that a key coming from the environment, or compiled
        /// into an official build, shows up as an empty box — filling it in would present a
        /// borrowed value as the user's own and then write it to their disk when they saved.
        /// </summary>
        public static SetupChoices From(AppConfig? stored)
        {
            var config = stored ?? new AppConfig();
            var jellyfin = config.Jellyfin ?? new JellyfinSettings();
            var folders = (config.WatchFolders ?? Array.Empty<string>())
                .Where(folder => !string.IsNullOrWhiteSpace(folder))
                .ToArray();

            var choices = new SetupChoices
            {
                UseLocalFolders = folders.Length > 0,
                UseJellyfin = !string.IsNullOrWhiteSpace(jellyfin.ServerUrl),
                ServerUrl = jellyfin.ServerUrl ?? "",
                Username = jellyfin.Username ?? "",
                Password = jellyfin.Password ?? "",
                ApiKey = jellyfin.ApiKey ?? "",
                LibraryName = jellyfin.LibraryName ?? "",
                TmdbApiKey = config.TmdbApiKey ?? "",
                OmdbApiKey = config.OmdbApiKey ?? ""
            };

            foreach (var folder in folders) choices.Folders.Add(folder);

            return choices;
        }

        /// <summary>
        /// The folders as they would be saved: trimmed, blanks dropped, duplicates collapsed.
        /// Two entries differing only in case are the same folder on Windows and macOS alike,
        /// and scanning one twice would double every film in it.
        /// </summary>
        public IReadOnlyList<string> CleanFolders =>
            Folders
                .Select(folder => (folder ?? "").Trim())
                .Where(folder => folder.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        /// <summary>
        /// The server settings as typed. Deliberately not <see cref="JellyfinSettings.Normalize"/>,
        /// which fills blanks from the environment: what this screen saves has to be what the
        /// person in front of it entered.
        /// </summary>
        public JellyfinSettings ToJellyfinSettings() => new()
        {
            ServerUrl = JellyfinSettings.NormalizeServerUrl(ServerUrl),
            Username = (Username ?? "").Trim(),
            Password = Password ?? "",
            ApiKey = (ApiKey ?? "").Trim(),
            LibraryName = (LibraryName ?? "").Trim()
        };

        public bool HasLocalLibrary => UseLocalFolders && CleanFolders.Count > 0;

        public bool HasJellyfinLibrary => UseJellyfin && ToJellyfinSettings().IsConfigured;

        /// <summary>
        /// Why the answers are not yet usable, phrased for the person to read, or null when they
        /// are. One string rather than a list: a form that reports every fault at once reads as
        /// an accusation, and the first one is the one they can act on.
        /// </summary>
        public string? Problem
        {
            get
            {
                if (UseJellyfin)
                {
                    if (string.IsNullOrWhiteSpace(ServerUrl))
                        return "Enter the address of your Jellyfin server, for example media-box:8096.";

                    if (JellyfinSettings.NormalizeServerUrl(ServerUrl).Length == 0)
                        return "That Jellyfin address is not one this app can reach. It needs a host, optionally with a port.";

                    if (string.IsNullOrWhiteSpace(Username) && string.IsNullOrWhiteSpace(ApiKey))
                        return "Enter the Jellyfin username you sign in with, or an API key.";
                }

                if (UseLocalFolders && CleanFolders.Count == 0)
                    return "Add at least one folder of films, or turn off films on this computer.";

                if (!HasLocalLibrary && !HasJellyfinLibrary)
                    return "Choose where your films are: folders on this computer, a Jellyfin server, or both.";

                return null;
            }
        }

        public bool CanFinish => Problem is null;

        /// <summary>
        /// Folders in the list that are not there any more. Not a reason to refuse the save — a
        /// folder on an external drive is legitimately absent most of the time — but worth
        /// saying, because a typo and an unplugged disk look identical afterwards.
        /// </summary>
        public IReadOnlyList<string> MissingFolders =>
            CleanFolders.Where(folder => !SafeExists(folder)).ToList();

        /// <summary>
        /// The configuration to save, built on top of what the file already had so that settings
        /// this screen never asks about survive being answered. The argument must be a raw config
        /// from <see cref="AppConfig.ReadRaw"/>; a resolved one would drag environment values and
        /// any compiled-in key into the file, and <see cref="ConfigStore.Save"/> rejects it.
        /// </summary>
        public AppConfig ToConfig(AppConfig? existing = null)
        {
            var source = existing ?? new AppConfig();

            return new AppConfig
            {
                DatabasePath = source.DatabasePath,
                PosterCacheDir = source.PosterCacheDir,
                DownloadPosters = source.DownloadPosters,
                TmdbImageSize = source.TmdbImageSize,

                // An unticked source is stored as nothing at all rather than left behind, so that
                // turning Jellyfin off in this screen really does stop the app contacting it.
                WatchFolders = UseLocalFolders ? CleanFolders.ToArray() : Array.Empty<string>(),
                Jellyfin = UseJellyfin ? ToJellyfinSettings() : new JellyfinSettings(),

                TmdbApiKey = (TmdbApiKey ?? "").Trim(),
                OmdbApiKey = (OmdbApiKey ?? "").Trim(),

                SetupCompleted = true
            };
        }

        private static bool SafeExists(string folder)
        {
            try { return Directory.Exists(folder); }
            catch { return false; }
        }
    }
}
