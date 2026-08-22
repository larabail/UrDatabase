using System;
using System.Collections.Generic;
using Xunit;

namespace UrDatabase.Tests
{
    /// <summary>
    /// Tests that mutate the API key environment variables belong here.
    ///
    /// Environment variables are process-wide and xUnit runs collections in parallel, so two test
    /// classes setting and clearing <c>URDATABASE_TMDB_API_KEY</c> raced: one would clear the
    /// variable in the window between the other's set and its <c>AppConfig.Load</c>, failing about
    /// one run in three and passing in isolation. Sharing a collection serialises them against
    /// each other while the rest of the suite still runs in parallel.
    /// </summary>
    [CollectionDefinition(EnvironmentVariables.CollectionName)]
    public class EnvironmentVariableCollection
    {
    }

    public static class EnvironmentVariables
    {
        public const string CollectionName = "Environment variables";
    }

    /// <summary>
    /// Clears the named variables for the life of the scope and puts back whatever was there
    /// before. A test that leaks a key into the process would otherwise change the result of
    /// every test that runs after it, including on a developer machine with a real key exported.
    /// </summary>
    public sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly List<KeyValuePair<string, string?>> _original = new();

        public EnvironmentVariableScope(params string[] names)
        {
            foreach (var name in names)
            {
                _original.Add(new KeyValuePair<string, string?>(name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        public void Dispose()
        {
            foreach (var entry in _original)
                Environment.SetEnvironmentVariable(entry.Key, entry.Value);
        }
    }
}
